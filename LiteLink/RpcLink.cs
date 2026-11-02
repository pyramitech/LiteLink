using LiteLink.Shared;
using LiteLink.Shared.Func;
using LiteLink.Shared.Serializer;

namespace LiteLink
{
    public class RpcLink
    {
        public IConnection Connection;
        public PermissionBitmask Permissions;

        public static Dictionary<string, uint> ClientFunctions = new Dictionary<string, uint>();

        //pending Invoke<TResult> calls awaiting the client's response, one slot per request id
        private TaskCompletionSource<byte[]>[] _invokeRequests;
        private byte _lastInvokeId;
        private readonly object _invokeLock = new object();

        /// <summary>how long an Invoke&lt;TResult&gt; waits for the client's response before failing.
        /// A field on purpose: RpcLink properties would be picked up by the serializer generator</summary>
        public TimeSpan InvokeResponseTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// whether this client may issue multichannel requests, i.e. run calls in parallel on its
        /// connection; denied requests fail with <see cref="ErrorCodes.Unauthorized"/>. Seeded from
        /// <see cref="RpcServer.AllowMultiChannelByDefault"/> when the connection is added; flip it
        /// per client at runtime, e.g. grant after a successful login. The grant dies with the
        /// connection, a reconnecting client starts from the server default again.
        /// A field on purpose: RpcLink properties would be picked up by the serializer generator
        /// </summary>
        public bool AllowMultiChannel = true;

        public void GrantPermission(string permissionGroup)
        {
            Permissions.SetBitmask(RpcServer.Instance.GetPermissionGroupBitmask(permissionGroup));
        }

        public void GrantPermission(PermissionBitmask bitmask)
        {
            Permissions.SetBitmask(bitmask);
        }

        /// <summary>
        /// If enabled will disable, if disabled will enable
        /// </summary>
        /// <param name="bitmask"></param>
        public void TogglePermission(PermissionBitmask bitmask)
        {
            Permissions.FlipBitmask(bitmask);
        }

        public void SetPermission(string permissionGroup)
        {
            Permissions = RpcServer.Instance.GetPermissionGroupBitmask(permissionGroup);
        }

        public void SetPermission(PermissionBitmask bitmask)
        {
            Permissions = bitmask;
        }

        /// <summary>
        /// Deprecated fire-and-forget push, kept non-generic so it can't collide with
        /// <see cref="Invoke{TResult}"/>: calls with an explicit type argument, e.g.
        /// Invoke&lt;Model&gt;(name, model), now resolve to the result-returning Invoke.
        /// </summary>
        [Obsolete("Use Send<T> for fire-and-forget pushes, or Invoke<TResult> to call the client and await its response")]
        public void Invoke(string function, object model)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model), "The deprecated Invoke cannot push null, use Send<T>(function, null) instead");

            var writer = new LiteWriter();
            writer.WriteByte(0); //isCompressed
            writer.WriteUInt32(CRCFunctions.CalculateCRC32(function));
            WriteTypeIndex(ref writer, GetRegisteredTypeIndex(model.GetType()));
            LiteSerializer.SerializeObject(ref writer, model);
            Connection.Invoke(writer.Close(1));
        }

        /// <summary>
        /// Pushes a one-way message to this client. The client receives it via
        /// RegisterFunction(name, Action&lt;T&gt;); there is no response.
        /// </summary>
        public void Send<T>(string function, T model)
        {
            if (!ClientFunctions.ContainsKey(function))
            {
                ClientFunctions.Add(function, CRCFunctions.CalculateCRC32(function));
            }
            var hash = ClientFunctions[function];
            var writer = new LiteWriter();
            writer.WriteByte(0); //isCompressed
            writer.WriteUInt32(hash);
            WriteTypeIndex(ref writer, GetRegisteredTypeIndex(typeof(T)));
            LiteSerializer.Serialize(ref writer, model);
            Connection.Invoke(writer.Close(1));
        }

        /// <summary>
        /// Calls a function on this client and awaits its response. The client must have a
        /// responder registered via RegisterFunction(name, Func&lt;TArg, TResult&gt;); the call
        /// fails fast when it doesn't, and faults if the client's responder throws. Up to 256
        /// invokes per connection can be in flight at once.
        /// </summary>
        /// <param name="function">the invocation name the client registered</param>
        /// <param name="model">optional argument; null means no argument. Serialized by its runtime type,
        /// which must be part of the generated type map</param>
        /// <param name="timeout">overrides <see cref="InvokeResponseTimeout"/> for this call</param>
        public async Task<TResult> Invoke<TResult>(string function, object model = null, TimeSpan? timeout = null)
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestId = ScheduleInvokeId(tcs);

            try
            {
                var request = BuildInvokeRequest(typeof(TResult), function, model, requestId);
                await Connection.Invoke(request);

                var payload = await tcs.Task.WaitAsync(timeout ?? InvokeResponseTimeout);
                return DeserializeResult<TResult>(payload);
            }
            catch
            {
                //release the slot unless a response claimed it in the meantime
                Interlocked.CompareExchange(ref _invokeRequests[requestId], null, tcs);
                throw;
            }
        }

        //separate sync helpers because LiteWriter/LiteReader are ref structs and can't appear in async methods
        private static byte[] BuildInvokeRequest(Type resultType, string function, object model, byte requestId)
        {
            var writer = new LiteWriter();
            writer.WriteByte(0); //isCompressed
            writer.WriteUInt32(0); //system hash
            writer.WriteByte(2); //push type 2: server-initiated invoke
            writer.WriteByte(requestId);
            WriteTypeIndex(ref writer, GetRegisteredTypeIndex(resultType)); //the client serializes its response as this type
            writer.WriteUInt32(CRCFunctions.CalculateCRC32(function));
            if (model == null)
            {
                writer.WriteByte(0); //no argument
            }
            else
            {
                writer.WriteByte(1);
                WriteTypeIndex(ref writer, GetRegisteredTypeIndex(model.GetType()));
                LiteSerializer.SerializeObject(ref writer, model);
            }
            return writer.Close(1);
        }

        private static TResult DeserializeResult<TResult>(byte[] payload)
        {
            var reader = new LiteReader(payload);
            return LiteSerializer.Deserialize<TResult>(ref reader);
        }

        private byte ScheduleInvokeId(TaskCompletionSource<byte[]> tcs)
        {
            lock (_invokeLock)
            {
                _invokeRequests ??= new TaskCompletionSource<byte[]>[256];
                unchecked
                {
                    byte id = _lastInvokeId;
                    for (int i = 0; i < 256; i++, id++)
                    {
                        if (_invokeRequests[id] == null)
                        {
                            _invokeRequests[id] = tcs;
                            _lastInvokeId = id;
                            return id;
                        }
                    }
                }
            }
            throw new InvalidOperationException("All 256 invoke slots of this connection are occupied");
        }

        /// <summary>resolves an incoming invoke response (system opcode 0 type 3) to its pending call</summary>
        internal void CompleteInvoke(byte requestId, byte status, byte[] payload)
        {
            TaskCompletionSource<byte[]> tcs = null;
            if (_invokeRequests != null)
                tcs = Interlocked.Exchange(ref _invokeRequests[requestId], null);

            if (tcs == null)
            {
                RpcServer.Instance?.OnError?.Invoke(new Exception(
                    "Received an invoke response for request " + requestId + " with no pending invoke, the call may have timed out"));
                return;
            }

            if (status == 0)
                tcs.TrySetResult(payload);
            else if (status == 254)
                tcs.TrySetException(new Exception("The client has no responder registered for the invoked function"));
            else
                tcs.TrySetException(new Exception("The client's responder threw an exception"));
        }

        /// <summary>fails all in-flight invokes, called when the connection goes away</summary>
        internal void FailPendingInvokes()
        {
            if (_invokeRequests == null)
                return;

            for (int i = 0; i < 256; i++)
            {
                var tcs = Interlocked.Exchange(ref _invokeRequests[i], null);
                tcs?.TrySetException(new Exception("The connection was closed while the invoke was in flight"));
            }
        }

        private static void WriteTypeIndex(ref LiteWriter writer, uint typeIdx)
        {
            var typeBytes = RpcServer.Instance.GetTypeSize();
            if (typeBytes == 1)
            {
                writer.WriteByte((byte)typeIdx);
            }
            else if (typeBytes == 2)
            {
                writer.WriteUInt16((ushort)typeIdx);
            }
            else
            {
                writer.WriteUInt32(typeIdx);
            }
        }

        private static uint GetRegisteredTypeIndex(Type type)
        {
            try
            {
                return RpcServer.Instance.GetTypeIndex(type);
            }
            catch (KeyNotFoundException)
            {
                throw new Exception($"Type {type} is not part of the generated type map. Types used with Send/Invoke must appear in the RPC surface or in a Send/Invoke call the generator can see.");
            }
        }
    }
}
