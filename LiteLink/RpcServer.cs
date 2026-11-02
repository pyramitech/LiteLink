using LiteLink.Shared;
using LiteLink.Shared.Serializer;
using System.Collections.Concurrent;
using System.ComponentModel;

namespace LiteLink
{
    public abstract class RpcServer
    {
        public static RpcServer Instance { get; private set; }

        private Dictionary<string, PermissionBitmask> _permissionGroups = new Dictionary<string, PermissionBitmask>();

        protected Dictionary<Type, uint> _typeMaps = new Dictionary<Type, uint>();

        public static byte[] HandshakeResponse { get; set; }

        /// <summary>
        /// pregenerated API description (function names and signatures) served on system opcode 0 type 2;
        /// null (the default) unless the [RpcServer] attribute sets ShowApi = true
        /// </summary>
        public static byte[] ApiInfoResponse { get; set; }

        /// <summary>
        /// password clients must supply to retrieve <see cref="ApiInfoResponse"/>; null means no password required.
        /// Seeded from the [RpcServer] ApiPassword property, can be overridden at runtime (e.g. from configuration).
        /// </summary>
        public static string ApiInfoPassword { get; set; }

        /// <summary>
        /// returns data to return or null if function is empty.
        /// </summary>
        public Func<byte[], Task<byte[]>> ProxyFunction { get; set; } = null;

        public Action<Exception> OnError { get; set; }
        public Action<RpcLink> OnConnected { get; set; }
        public Action<RpcLink> OnDisconnected { get; set; }

        /// <summary>
        /// upper bound on how many requests a single client batch (system opcode 0 type 4) may carry;
        /// larger batches are rejected whole with BadRequest so one transport message can't be
        /// amplified into unbounded procedure invocations
        /// </summary>
        public int MaxBatchSize { get; set; } = 64;

        /// <summary>
        /// seed for <see cref="RpcLink.AllowMultiChannel"/> on new connections. True (the default)
        /// keeps the historical behavior of every client being allowed to use multichannel; set it
        /// to false to make parallel calls an earned privilege, granted per client via
        /// link.AllowMultiChannel (e.g. after login)
        /// </summary>
        public bool AllowMultiChannelByDefault { get; set; } = true;

        /// <summary>
        /// Wraps every procedure invocation, e.g. for logging call start/end:
        /// <code>
        /// server.CallMiddleware = async (call, next) => {
        ///     Log($"start {call.FunctionName}");
        ///     try { return await next(call); }
        ///     finally { Log($"end {call.FunctionName}"); }
        /// };
        /// </code>
        /// While a call runs, <see cref="RpcCallContext.Current"/> is populated so logs written anywhere
        /// in the call's async flow can be correlated with it. When null (the default), calls are
        /// dispatched directly with no overhead.
        /// </summary>
        public Func<RpcCallContext, RpcCallHandler, Task<byte[]>> CallMiddleware { get; set; }

        public bool IsShuttingDown { get; private set; }

        public RpcServer()
        {
            if (RpcServer.Instance != null)
                throw new Exception("Only one instance of RPCServer is currently supported. Make sure [RPCServer] is only used in one place");
            RpcServer.Instance = this;
        }

        /// <summary>called by generated code, see <see cref="RpcServer{TLink}.__addProcedure"/></summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void __addPermissionGroup(string name, PermissionBitmask bitmask)
        {
            if (_permissionGroups.ContainsKey(name))
            {
                throw new Exception("PermissionGroups already contains: " + name);
            }
            _permissionGroups.Add(name, bitmask);
        }

        public void Shutdown()
        {
            IsShuttingDown = true;
        }

        public byte GetTypeSize() {
            var length = _typeMaps.Count;
            if (length <= 0xff)
                return 1;
            else if (length <= 0xffff)
                return 2;
            else return 4;
        }
        public uint GetTypeIndex(Type t) => _typeMaps[t];

        public PermissionBitmask GetPermissionGroupBitmask(string name)
        {
            return _permissionGroups[name];
        }

        public abstract Task<byte[]> Receive(IConnection connection, byte[] data);
        public abstract void AddConnection(IConnection connection);
        public abstract void RemoveConnection(IConnection connection);

    }

    public partial class RpcServer<TLink> : RpcServer where TLink : RpcLink, new()
    {
        private ConcurrentDictionary<IConnection, TLink> _connections { get; set; } = new ConcurrentDictionary<IConnection, TLink>();

        public delegate Task<byte[]> ProcedureProcessorDelegate(TLink link, ref LiteReader reader);

        private ProcedureProcessorDelegate[] _procedures = new ProcedureProcessorDelegate[ushort.MaxValue];
        private PermissionBitmask[] _permissions = new PermissionBitmask[ushort.MaxValue];
        private string[] _procedureNames = new string[ushort.MaxValue];

        //per-opcode response compression policy (server-wide setting merged with the [RpcFunc]
        //override at generation time); applied at the exit boundary so batches can compress
        //whole envelopes and generated code never compresses
        private bool[] _compressResponses = new bool[ushort.MaxValue];

        public override void AddConnection(IConnection connection)
        {
            var newConnection = new TLink() {
                Connection = connection,
                AllowMultiChannel = AllowMultiChannelByDefault
            };
            
            _connections.TryAdd(connection, newConnection);

            if (OnConnected != null)
                OnConnected.Invoke(newConnection);
        }

        public override void RemoveConnection(IConnection connection)
        {
            _connections.TryRemove(connection, out var link);

            //callers awaiting Invoke<TResult> on this link would otherwise hang until their timeout
            link?.FailPendingInvokes();

            if (OnDisconnected != null)
                OnDisconnected.Invoke(link);
        }

        public List<TLink> GetConnections(Predicate<TLink> predicate)
        {
            return _connections.Values.Where(x => predicate(x)).ToList();
        }

        public List<TLink> GetConnections()
        {
            return _connections.Values.ToList();
        }

        /// <summary>
        /// Called by generated code to register a procedure. Not part of the public API: generated
        /// code binds to it at compile time, so a LiteLink runtime that doesn't match the generator
        /// fails the consumer's build instead of crashing at startup. Keep the signature in step
        /// with ServerInitializerTemplate.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void __addProcedure(ushort opcode, PermissionBitmask permissions, string name, bool compresses, ProcedureProcessorDelegate del)
        {
            if (_procedures[opcode] != null)
                throw new Exception($"Attempted to register an occupied opcode {opcode}. Try rebuilding the project, if the problem persists it might be an issue with the generator, please file a bug report.");
            _procedures[opcode] = del;
            _permissions[opcode] = permissions;
            _procedureNames[opcode] = name;
            _compressResponses[opcode] = compresses;
        }

        /// <summary>the RPC name registered for an opcode, e.g. "FooProcedures.ReturnTask", or null when unregistered</summary>
        public string GetProcedureName(ushort opcode) => _procedureNames[opcode];

        /// <summary>called by generated code, see <see cref="__addProcedure"/></summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void __addTypeMap(Type type, uint typeIdx)
        {
            if (_typeMaps.ContainsKey(type))
                throw new Exception("Type map already registered for type: " + type);

            _typeMaps.Add(type, typeIdx);
        }

        /// <summary>
        /// The main method for receiving data
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="data">data received from the client</param>
        /// <returns>byte array that should be passed back to the client</returns>
        public override Task<byte[]> Receive(IConnection connection, byte[] data)
        {
            return Receive(connection, data, 0, allowBatch: true, rawResponse: false);
        }

        /// <param name="rawResponse">when true (batch items) the response is returned uncompressed,
        /// the batch handler compresses the whole envelope instead</param>
        private Task<byte[]> Receive(IConnection connection, byte[] data, int index, bool allowBatch, bool rawResponse)
        {
            if (base.IsShuttingDown)
            {
                var writer = new LiteWriter();
                writer.WriteByte((byte)ErrorCodes.ShuttingDown);
                writer.WriteByte(0);
                return Task.FromResult(writer.Close());
            }

            try
            {
                var reader = new LiteReader(data, index);

                var request = new RequestHeader();
                request.Opcode = reader.ReadUInt16();
                request.Compressed = reader.ReadBoolean();
                if (request.Compressed)
                {
                    reader.Decompress();
                }

                if (ProxyFunction != null)
                {
                    var res = ProxyFunction(reader.Data.ToArray());
                    if (res != null)
                    {
                        return res;
                    }
                }

                var writer = new LiteWriter();

                if (request.Opcode == 0) //system functions
                {
                    if (reader.Data.Length == 3) //for old version, 0 was default for handshake, now it's generic system functions
                        return Task.FromResult(HandshakeResponse);

                    var type = reader.ReadByte();
                    switch (type)
                    {
                        case 0:
                            return Task.FromResult(HandshakeResponse);

                        case 1:
                            {
                                if (index != 0) //a multichannel request can't be nested inside another multichannel request
                                {
                                    writer.WriteByte((byte)ErrorCodes.BadRequest);
                                    writer.WriteByte(0); //compressed
                                    return Task.FromResult(writer.Close());
                                }

                                //multichannel is the per-connection concurrency grant: on framed
                                //transports plain requests are capped by the transport's
                                //MaxParallelRequestsPerConnection, so this flag decides whether the
                                //client gets to run calls in parallel at all
                                if (!_connections.TryGetValue(connection, out var mcLink))
                                {
                                    writer.WriteByte((byte)ErrorCodes.Uninitialized);
                                    writer.WriteByte(0); //compressed
                                    return Task.FromResult(writer.Close());
                                }
                                if (!mcLink.AllowMultiChannel)
                                {
                                    writer.WriteByte((byte)ErrorCodes.Unauthorized);
                                    writer.WriteByte(0); //compressed
                                    return Task.FromResult(writer.Close());
                                }

                                var reqChannel = reader.ReadByte();
                                Task.Run(async () => {
                                    try
                                    {
                                        //batches can't hide inside a multichannel envelope, they are top-level only;
                                        //the inner response is compressed normally before being wrapped in the push
                                        var res = await Receive(connection, data, 5, allowBatch: false, rawResponse: false);
                                        await ReplyMultiChannelData(connection, reqChannel, res);
                                    }
                                    catch (Exception ex)
                                    {
                                        if (OnError != null)
                                            OnError.Invoke(ex);
                                    }
                                });
                                //return schedule as success
                                return Task.FromResult(new byte[] { (byte)ErrorCodes.Success, 0 });
                            }

                        case 2: //pregenerated API info, requires ShowApi = true on [RpcServer]
                            {
                                if (ApiInfoResponse == null)
                                {
                                    writer.WriteByte((byte)ErrorCodes.BadRequest);
                                    writer.WriteByte(0); //compressed
                                    return Task.FromResult(writer.Close());
                                }

                                var password = reader.ReadString();
                                if (ApiInfoPassword != null && password != ApiInfoPassword)
                                {
                                    writer.WriteByte((byte)ErrorCodes.Unauthorized);
                                    writer.WriteByte(0); //compressed
                                    return Task.FromResult(writer.Close());
                                }

                                return Task.FromResult(ApiInfoResponse);
                            }

                        case 3: //the client's response to a server-initiated Invoke<TResult>
                            {
                                if (!_connections.TryGetValue(connection, out var invokeLink))
                                {
                                    writer.WriteByte((byte)ErrorCodes.Uninitialized);
                                    writer.WriteByte(0); //compressed
                                    return Task.FromResult(writer.Close());
                                }

                                var requestId = reader.ReadByte();
                                var status = reader.ReadByte();
                                invokeLink.CompleteInvoke(requestId, status, reader.ReadRemaining());

                                return Task.FromResult(new byte[] { (byte)ErrorCodes.Success, 0 });
                            }

                        case 4: //a batch of independent requests packed into one transport message
                            {
                                //batches don't nest and can't hide inside multichannel envelopes,
                                //otherwise one message could amplify into unbounded work
                                if (!allowBatch)
                                {
                                    writer.WriteByte((byte)ErrorCodes.BadRequest);
                                    writer.WriteByte(0); //compressed
                                    return Task.FromResult(writer.Close());
                                }

                                var count = reader.ReadUInt16();
                                if (count == 0 || count > MaxBatchSize)
                                {
                                    writer.WriteByte((byte)ErrorCodes.BadRequest);
                                    writer.WriteByte(0); //compressed
                                    return Task.FromResult(writer.Close());
                                }

                                var items = new byte[count][];
                                for (int i = 0; i < count; i++)
                                    items[i] = reader.ReadByteArray();

                                return ProcessBatch(connection, items);
                            }
                    }
                }

                var procedure = _procedures[request.Opcode];
                if (procedure == null)
                {
                    writer.WriteByte((byte)ErrorCodes.BadRequest);
                    writer.WriteByte(0); //compressed
                                         //writer.WriteInt32(0); //no data
                    return Task.FromResult(writer.Close());
                }
                else
                {
                    if (_connections.TryGetValue(connection, out var link))
                    {
                        var permission = _permissions[request.Opcode];
                        if ((permission.Bitmask1 | permission.Bitmask2) != 0 && !permission.Compare(link.Permissions))
                        {
                            writer.WriteByte((byte)ErrorCodes.Unauthorized);
                            writer.WriteByte(0); //compressed
                                                 //writer.WriteInt32(0); //no data
                            return Task.FromResult(writer.Close());
                        }

                        Task<byte[]> responseTask;
                        if (CallMiddleware == null)
                        {
                            responseTask = procedure.Invoke(link, ref reader);
                        }
                        else
                        {
                            //middleware path: the reader is a ref struct and can't be captured by the chain,
                            //so copy the remaining payload and rebuild a reader when the chain invokes the procedure
                            var callContext = new RpcCallContext
                            {
                                Opcode = request.Opcode,
                                FunctionName = _procedureNames[request.Opcode],
                                Link = link,
                                IsMultiChannel = index != 0
                            };
                            var payload = reader.ReadRemaining();
                            responseTask = InvokeWithMiddleware(callContext, procedure, link, payload);
                        }

                        //compression happens here at the exit boundary, not in generated code, so batch
                        //envelopes can compress as one block; middleware always sees uncompressed bytes
                        if (rawResponse || !_compressResponses[request.Opcode])
                            return responseTask;
                        return CompressResponseAsync(responseTask);
                    }
                    else
                    {
                        writer.WriteByte((byte)ErrorCodes.Uninitialized);
                        writer.WriteByte(0); //compressed
                                             //writer.WriteInt32(0); //no data
                        return Task.FromResult(writer.Close());
                    }
                }
            }
            catch (Exception ex)
            {
                if (OnError != null)
                    OnError.Invoke(ex);

                var writer = new LiteWriter();
                writer.WriteByte((byte)ErrorCodes.Unhandled);
                writer.WriteByte(0);
                return Task.FromResult(writer.Close());
            }
        }
        private async Task<byte[]> InvokeWithMiddleware(RpcCallContext callContext, ProcedureProcessorDelegate procedure, TLink link, byte[] payload)
        {
            RpcCallContext.Current = callContext;
            try
            {
                return await CallMiddleware(callContext, ctx =>
                {
                    var reader = new LiteReader(payload);
                    return procedure.Invoke(link, ref reader);
                });
            }
            catch (Exception ex)
            {
                //mirror Receive's synchronous error handling so an exception escaping the middleware
                //(or the procedure) still produces an error response instead of faulting the transport
                if (OnError != null)
                    OnError.Invoke(ex);

                return BuildErrorResponse(ErrorCodes.Unhandled);
            }
            finally
            {
                RpcCallContext.Current = null;
            }
        }

        private async Task<byte[]> ProcessBatch(IConnection connection, byte[][] items)
        {
            //items run sequentially in request order: batching is a network optimization, not a
            //concurrency primitive, so an earlier call's effects are visible to every later call
            var responses = new byte[items.Length][];
            var compressible = new bool[items.Length];
            var allCompressible = true;
            for (int i = 0; i < items.Length; i++)
            {
                //system requests (opcode 0: nested multichannel envelopes, handshakes) produce tiny
                //responses and don't object to envelope compression; procedures follow their policy
                var opcode = items[i] != null && items[i].Length >= 2
                    ? (ushort)(items[i][0] | (items[i][1] << 8))
                    : (ushort)0;
                compressible[i] = opcode == 0 || _compressResponses[opcode];
                if (!compressible[i])
                    allCompressible = false;

                responses[i] = await RunBatchItem(connection, items[i]);
            }

            if (allCompressible)
            {
                //one compression pass over the whole envelope: items arrive uncompressed (rawResponse)
                //so small responses share redundancy that per-item compression can't reach
                return LiteCompression.Compress(BuildBatchResponse(responses), 2);
            }

            //mixed batch: a Compression.Disabled response (e.g. pre-compressed data) must not be
            //recompressed, so compress each willing item individually and leave the envelope plain
            for (int i = 0; i < responses.Length; i++)
            {
                if (compressible[i])
                    responses[i] = LiteCompression.Compress(responses[i], 2);
            }
            return BuildBatchResponse(responses);
        }

        private async Task<byte[]> RunBatchItem(IConnection connection, byte[] item)
        {
            try
            {
                return await Receive(connection, item, 0, allowBatch: false, rawResponse: true);
            }
            catch (Exception ex)
            {
                //a faulted item fails only its own slot, the rest of the batch still completes
                if (OnError != null)
                    OnError.Invoke(ex);
                return BuildErrorResponse(ErrorCodes.Unhandled);
            }
        }

        private static async Task<byte[]> CompressResponseAsync(Task<byte[]> responseTask)
        {
            return LiteCompression.Compress(await responseTask, 2);
        }

        private static byte[] BuildBatchResponse(byte[][] responses)
        {
            var writer = new LiteWriter();
            writer.WriteByte((byte)ErrorCodes.Success);
            writer.WriteByte(0); //compressed, set by LiteCompression.Compress when the envelope compresses
            writer.WriteUInt16((ushort)responses.Length);
            foreach (var response in responses)
                writer.WriteByteArray(response);
            return writer.Close();
        }

        //separate sync helper because LiteWriter is a ref struct and can't be used inside async methods
        private static byte[] BuildErrorResponse(ErrorCodes code)
        {
            var writer = new LiteWriter();
            writer.WriteByte((byte)code);
            writer.WriteByte(0); //compressed
            return writer.Close();
        }

        //not async because LiteWriter is a ref struct, the returned task must be awaited so send failures are observed
        private Task ReplyMultiChannelData(IConnection connection, byte channel, byte[] data)
        {
            var writer = new LiteWriter();
            writer.WriteBoolean(false); //no compression
            writer.WriteUInt32(0); //0 = system opcode
            writer.WriteByte(1); //type
            writer.WriteByte(channel); //channel
            writer.WriteFixedByteArray(data);
            var fin = writer.Close();
            return connection.Invoke(fin);
        }
    }

    public class PermissionGroup
    {
        private PermissionBitmask _bitmask = new PermissionBitmask();
        public string Name { get; }
        public PermissionGroup(string name) {
            Name = name;
        }

        public PermissionBitmask GetBitMask() { return _bitmask; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class RpcServerAttribute<TLink> : Attribute where TLink : RpcLink
    {
        public Type ClientType = typeof(TLink);
        public bool UseCompression { get; set; }
        public Type PermissionGroups { get; set; }

        /// <summary>when true, the generator pregenerates an API description (function names and signatures) served on system opcode 0 type 2</summary>
        public bool ShowApi { get; set; }

        /// <summary>optional password clients must supply to retrieve the API description; can be overridden at runtime via RpcServer.ApiInfoPassword</summary>
        public string ApiPassword { get; set; }
        public RpcServerAttribute() { 
            //UseCompression = false;
            //PermissionGroups = new PermissionGroup[0]; 
        }
    }
}