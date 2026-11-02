using LiteLink.Shared;
using LiteLink.Shared.Func;
using LiteLink.Shared.Serializer;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Dynamic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace LiteLink.Client
{
    /// <summary>how the client packs concurrent requests into transport messages</summary>
    public enum RpcBatchMode
    {
        /// <summary>every call goes out immediately as its own transport message (the default)</summary>
        Disabled = 0,

        /// <summary>
        /// the first call goes out immediately, calls made while a send is in flight are packed
        /// into one follow-up batch; adds no latency on an idle connection, packs under load
        /// </summary>
        Coalesce = 1,

        /// <summary>
        /// calls are held for <see cref="RpcClient.BatchWindow"/> and flushed as one batch;
        /// packs most aggressively but delays every call by up to the window
        /// </summary>
        Window = 2
    }

    public class RpcClient
    {
        public delegate void RPCClientFunctionDelegate(ref LiteReader reader);
        private delegate uint TypeReaderDelegate(ref LiteReader reader);

        public dynamic Remote { get; set; }

        public IConnection Connection { get; private set; }

        private List<string> _propertyMap = null;
        private List<TypeMap> _typeMaps = null;
        private Func<string, ulong> _functionHasher = null;
        private Dictionary<ulong, FunctionMap> _functionMaps = null;
        private Func<string, ulong> _permissionHasher = null;
        private Dictionary<ulong, PermissionBitmask> _permissionMaps = null;
        private TypeReaderDelegate _typeIdxReader = null;

        private Dictionary<uint, RPCClientFunctionDelegate> _registeredFunctions = new Dictionary<uint, RPCClientFunctionDelegate>();

        //handlers for server-initiated invokes that expect a response (push type 2)
        private class ResponderEntry
        {
            public Type ArgType;
            public Func<object, Task<object>> Handler;
        }
        private Dictionary<uint, ResponderEntry> _registeredResponders = new Dictionary<uint, ResponderEntry>();

        private TaskCompletionSource<byte[]>[] _channelRequests = new TaskCompletionSource<byte[]>[256];
        private byte _lastChannelIndex = 0;
        private readonly object _channelLock = new object();

        //every public option below is a backed property so the client can tell configuration from
        //session state: a set while disconnected (including before the first Start) updates the
        //baseline too, a set while connected is session-only. Start() restores the baseline, so
        //whatever a session earned (e.g. multichannel unlocked by a login) dies with the connection
        //instead of silently carrying into an unauthenticated reconnect
        private struct OptionBaseline
        {
            public TimeSpan ResponseTimeout;
            public bool EnableSerializationCache;
            public bool EnableGeneratedSerializers;
            public RpcBatchMode BatchMode;
            public TimeSpan BatchWindow;
            public int MaxBatchSize;
            public bool MultiChannelByDefault;
        }
        private OptionBaseline _baseline;
        private bool _sessionActive; //a Start() handshake completed on the current connection

        private bool IsConfiguring => !_sessionActive || Connection?.IsConnected != true;

        private TimeSpan _responseTimeout = TimeSpan.FromSeconds(60);
        /// <summary>
        /// how long any request (plain, batched or multichannel, including the wait for a free
        /// multichannel slot and the Start handshake) may take before failing with a
        /// <see cref="TimeoutException"/>; Timeout.InfiniteTimeSpan disables the limit
        /// </summary>
        public TimeSpan ResponseTimeout
        {
            get => _responseTimeout;
            set { _responseTimeout = value; if (IsConfiguring) _baseline.ResponseTimeout = value; }
        }

        private bool _enableSerializationCache = true;
        /// <summary>
        /// When true (the default) the client compiles per-type read/write plans from the handshake
        /// once and reuses them for every call, instead of re-walking the type maps with reflection.
        /// Set to false to force the original walk-per-call logic, e.g. to rule the plan cache out
        /// while debugging a serialization issue. Both paths produce identical bytes.
        /// </summary>
        public bool EnableSerializationCache
        {
            get => _enableSerializationCache;
            set { _enableSerializationCache = value; if (IsConfiguring) _baseline.EnableSerializationCache = value; }
        }

        private bool _enableGeneratedSerializers = true;
        /// <summary>
        /// When true (the default) models whose generated serializers (see [RpcClientModels])
        /// structurally match the server's handshake are read/written by generated code instead of
        /// compiled plans. Set to false to force the plan path, e.g. for A/B comparison.
        /// </summary>
        public bool EnableGeneratedSerializers
        {
            get => _enableGeneratedSerializers;
            set { _enableGeneratedSerializers = value; if (IsConfiguring) _baseline.EnableGeneratedSerializers = value; }
        }

        private bool _multiChannelByDefault;
        /// <summary>
        /// When true every Send/Remote call is sent as a multichannel request, so this connection's
        /// calls run in parallel on the server (up to 256 in flight) instead of sequentially. The
        /// server must allow it (RpcLink.AllowMultiChannel, denied calls fail with error code 2) —
        /// the intended flow is to log in, have the server grant the link, then enable this.
        /// Like every option, a mid-connection change reverts when Start() runs again after a
        /// disconnect, matching the server-side grant dying with the connection.
        /// </summary>
        public bool MultiChannelByDefault
        {
            get => _multiChannelByDefault;
            set { _multiChannelByDefault = value; if (IsConfiguring) _baseline.MultiChannelByDefault = value; }
        }

        /// <summary>how many types this connection bound to generated serializers at plan-build time,
        /// exposed so tests and diagnostics can verify the fast path actually engaged</summary>
        public int GeneratedSerializerBindings { get; private set; }

        //plans are the handshake type maps compiled into delegate trees: property accessors, factories
        //and conversions are all resolved once per (wire type, CLR type) pair instead of per call
        private delegate object ReadPlanDelegate(ref LiteReader reader);
        private delegate void WritePlanDelegate(ref LiteWriter writer, object value);

        //a plan under construction is published as a forwarder to its box; a concurrent caller that
        //executes the forwarder mid-build waits on the box lock (held by the builder) until it's filled
        private sealed class ReadPlanBox
        {
            public ReadPlanDelegate Plan;
            public ReadPlanDelegate Wait()
            {
                var plan = Volatile.Read(ref Plan);
                if (plan != null)
                    return plan;
                lock (this)
                    return Plan;
            }
        }

        private sealed class WritePlanBox
        {
            public WritePlanDelegate Plan;
            public WritePlanDelegate Wait()
            {
                var plan = Volatile.Read(ref Plan);
                if (plan != null)
                    return plan;
                lock (this)
                    return Plan;
            }
        }

        private readonly ConcurrentDictionary<(uint typeIdx, Type modelType), ReadPlanDelegate> _readPlans
            = new ConcurrentDictionary<(uint, Type), ReadPlanDelegate>();
        private readonly ConcurrentDictionary<(uint typeIdx, Type runtimeType), WritePlanDelegate> _writePlans
            = new ConcurrentDictionary<(uint, Type), WritePlanDelegate>();

        /// <summary>
        /// invoked for errors that can't be surfaced to a caller, e.g. a multichannel response arriving for a request that already timed out
        /// </summary>
        public Action<Exception> OnError { get; set; }

        private RpcBatchMode _batchMode = RpcBatchMode.Disabled;
        /// <summary>how concurrent requests are packed into transport messages, see <see cref="RpcBatchMode"/></summary>
        public RpcBatchMode BatchMode
        {
            get => _batchMode;
            set { _batchMode = value; if (IsConfiguring) _baseline.BatchMode = value; }
        }

        private TimeSpan _batchWindow = TimeSpan.FromMilliseconds(50);
        /// <summary>how long <see cref="RpcBatchMode.Window"/> holds requests before flushing them as one batch</summary>
        public TimeSpan BatchWindow
        {
            get => _batchWindow;
            set { _batchWindow = value; if (IsConfiguring) _baseline.BatchWindow = value; }
        }

        private int _maxBatchSize = 64;
        /// <summary>
        /// most requests packed into a single batch; a full batch flushes immediately.
        /// Must not exceed the server's RpcServer.MaxBatchSize (64 by default) or batches will be rejected.
        /// </summary>
        public int MaxBatchSize
        {
            get => _maxBatchSize;
            set { _maxBatchSize = value; if (IsConfiguring) _baseline.MaxBatchSize = value; }
        }

        private class PendingBatchItem
        {
            public byte[] Data;

            /// <summary>where this request's compressible payload starts (3 plain, 8 multichannel),
            /// used when a lone flush compresses the request individually</summary>
            public int CompressOffset;

            public TaskCompletionSource<byte[]> Tcs;
        }

        private readonly object _batchLock = new object();
        private List<PendingBatchItem> _batchQueue = new List<PendingBatchItem>();
        private bool _batchFlushScheduled; //Window mode: a delayed flush is already pending
        private bool _batchSendInFlight;   //Coalesce mode: the send pump is running, new calls queue behind it

        public RpcClient(IConnection connection) {
            Remote = new RpcDynamicObject(this);
            Connection = connection;
            _baseline = CaptureBaseline();

            //register multichannel extension
            _registeredFunctions.Add(0, ProcessSystemMessage); //0 = system message
        }

        private OptionBaseline CaptureBaseline() => new OptionBaseline
        {
            ResponseTimeout = _responseTimeout,
            EnableSerializationCache = _enableSerializationCache,
            EnableGeneratedSerializers = _enableGeneratedSerializers,
            BatchMode = _batchMode,
            BatchWindow = _batchWindow,
            MaxBatchSize = _maxBatchSize,
            MultiChannelByDefault = _multiChannelByDefault,
        };

        private void RestoreBaseline()
        {
            _responseTimeout = _baseline.ResponseTimeout;
            _enableSerializationCache = _baseline.EnableSerializationCache;
            _enableGeneratedSerializers = _baseline.EnableGeneratedSerializers;
            _batchMode = _baseline.BatchMode;
            _batchWindow = _baseline.BatchWindow;
            _maxBatchSize = _baseline.MaxBatchSize;
            _multiChannelByDefault = _baseline.MultiChannelByDefault;
        }

        public async Task Start()
        {
            //a (re)connect reverts every option changed during the previous session: whatever
            //authorized those changes (e.g. a login that unlocked MultiChannelByDefault) did not
            //survive the connection, so the session's settings must not either
            RestoreBaseline();
            _sessionActive = false;

            //handshake
            //16 bit - opcode (0)
            //8 bit - bool isCompressed (0)
            //8 bit - function type (0 = handshake) / new in version 1.1 for extensions

            await Connection.Connect();
            var ret = await Connection.Invoke(new byte[] { 0, 0, 0, 0 }).WaitAsync(ResponseTimeout);
            OnConnected(ret);
            _sessionActive = true;
        }

        /// <summary>
        /// function for receiving data invoked from the server
        /// </summary>
        /// <param name="data"></param>
        public void Receive(byte[] data)
        {
            var reader = new LiteReader(data);

            var isCompressed = reader.ReadBoolean();
            if (isCompressed)
                reader.Decompress();

            //32 bit function hash
            var hash = reader.ReadUInt32();

            if (hash == 0) //system message
            {
                var type = reader.ReadByte();
                if (type == 1) //multi channel response
                {
                    var channel = reader.ReadByte();
                    //Exchange so a reply and a timeout racing on the same channel can't both claim the request
                    var tcs = Interlocked.Exchange(ref _channelRequests[channel], null);
                    if (tcs != null)
                    {
                        //pass only the payload so reading it doesn't depend on the envelope layout or its compression
                        tcs.TrySetResult(reader.ReadRemaining());
                    }
                    else
                    {
                        OnError?.Invoke(new Exception("Received a multichannel response on channel " + channel + " with no pending request, the request may have timed out"));
                    }
                    return;
                }

                if (type == 2) //server-initiated invoke expecting a response
                {
                    var requestId = reader.ReadByte();
                    var resultTypeIdx = _typeIdxReader(ref reader);
                    var funcHash = reader.ReadUInt32();
                    var hasArg = reader.ReadByte();

                    if (!_registeredResponders.TryGetValue(funcHash, out var responder))
                    {
                        _ = SendInvokeResponseAsync(requestId, 254, 0, null); //no responder, fail the server's call fast
                        return;
                    }

                    object arg = null;
                    if (hasArg == 1)
                    {
                        var argTypeIdx = _typeIdxReader(ref reader);
                        arg = Deserialize(ref reader, responder.ArgType, argTypeIdx);
                    }

                    //run on the pool so a slow responder doesn't block receiving further messages
                    _ = RunResponderAsync(requestId, resultTypeIdx, responder, arg);
                    return;
                }
            }

            //find function
            if (!_registeredFunctions.ContainsKey(hash))
            {
                //TODO: error?
                throw new Exception("unknown function call with hash: " + hash);
            }

            _registeredFunctions[hash](ref reader);
        }

        private void ProcessSystemMessage(ref LiteReader reader)
        {
            
        }

        /// <summary>
        /// Registers a responder for server-initiated <c>link.Invoke&lt;TResult&gt;(name, model)</c> calls.
        /// The returned value is serialized back to the server as the call's result.
        /// When the server invokes without an argument, <typeparamref name="TArg"/> receives its default value.
        /// </summary>
        public void RegisterFunction<TArg, TResult>(string name, Func<TArg, TResult> handler)
        {
            RegisterResponder(name, typeof(TArg), arg => Task.FromResult<object>(handler(arg is TArg typed ? typed : default)));
        }

        /// <summary>async variant of <see cref="RegisterFunction{TArg, TResult}(string, Func{TArg, TResult})"/></summary>
        public void RegisterFunction<TArg, TResult>(string name, Func<TArg, Task<TResult>> handler)
        {
            RegisterResponder(name, typeof(TArg), async arg => await handler(arg is TArg typed ? typed : default));
        }

        private void RegisterResponder(string name, Type argType, Func<object, Task<object>> handler)
        {
            var hash = CRCFunctions.CalculateCRC32(name);
            if (_registeredResponders.ContainsKey(hash) || _registeredFunctions.ContainsKey(hash))
            {
                throw new Exception("Function with this hash is already registered, make sure you are not registering the function twice, otherwise try changing the invocation name");
            }

            _registeredResponders.Add(hash, new ResponderEntry { ArgType = argType, Handler = handler });
        }

        private async Task RunResponderAsync(byte requestId, uint resultTypeIdx, ResponderEntry responder, object arg)
        {
            byte status = 0;
            object result = null;
            try
            {
                result = await responder.Handler(arg);
            }
            catch (Exception ex)
            {
                status = 255;
                OnError?.Invoke(ex);
            }
            await SendInvokeResponseAsync(requestId, status, resultTypeIdx, result);
        }

        private async Task SendInvokeResponseAsync(byte requestId, byte status, uint resultTypeIdx, object result)
        {
            try
            {
                var response = BuildInvokeResponse(requestId, status, resultTypeIdx, result);
                await Connection.Invoke(response).WaitAsync(ResponseTimeout);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex); //connection likely died, the server's invoke will fail on disconnect
            }
        }

        //sync helper because LiteWriter is a ref struct and can't appear in async methods
        private byte[] BuildInvokeResponse(byte requestId, byte status, uint resultTypeIdx, object result)
        {
            var writer = new LiteWriter();
            writer.WriteUInt16(0); //system opcode
            writer.WriteByte(0); //not compressed
            writer.WriteByte(3); //type 3: invoke response
            writer.WriteByte(requestId);
            writer.WriteByte(status);
            if (status == 0)
            {
                Serialize(ref writer, resultTypeIdx, result);
            }
            return writer.Close();
        }

        public void RegisterFunction<T>(string name, Action<T> function)
        {
            var hash = CRCFunctions.CalculateCRC32(name);
            if (_registeredFunctions.ContainsKey(hash) || _registeredResponders.ContainsKey(hash))
            {
                throw new Exception("Function with this hash is already registered, make sure you are not registering the function twice, otherwise try changing the invocation name");
            }

            _registeredFunctions.Add(hash, (ref LiteReader reader) => {
                try
                {
                    var typeIdx = _typeIdxReader(ref reader);

                    var res = Deserialize(ref reader, typeof(T), typeIdx);
                    function((T)res);
                }
                catch (Exception ex) //TODO: would be good to log this somehow
                {

                }
            });
        }

        /// <summary>
        /// Called by transports the moment the connection drops: fails every pending multichannel
        /// call and every request still queued for batching immediately, instead of letting their
        /// callers burn the full <see cref="ResponseTimeout"/> waiting for responses that can never
        /// arrive. Also marks the session inactive, so option sets before the next Start() count as
        /// configuration. Safe to call more than once; the built-in transports call it for you.
        /// </summary>
        public void OnConnectionLost()
        {
            _sessionActive = false;
            FailPendingSessionWork("Connection was lost");
        }

        private void FailPendingSessionWork(string reason)
        {
            for (int i = 0; i < 256; i++)
            {
                //fail any request that was in flight, otherwise its caller would await its full timeout
                var pending = Interlocked.Exchange(ref _channelRequests[i], null);
                pending?.TrySetException(new Exception(reason + " while the request was in flight"));
            }
            _lastChannelIndex = 0;

            //requests still queued for batching can't be sent on this connection anymore
            List<PendingBatchItem> staleBatch = null;
            lock (_batchLock)
            {
                if (_batchQueue.Count > 0)
                {
                    staleBatch = _batchQueue;
                    _batchQueue = new List<PendingBatchItem>();
                }
                _batchFlushScheduled = false;
                _batchSendInFlight = false;
            }
            if (staleBatch != null)
            {
                foreach (var item in staleBatch)
                    item.Tcs.TrySetException(new Exception(reason + " while the request was queued for batching"));
            }
        }

        public void OnConnected(byte[] handshake)
        {
            //also runs here for transports that can't observe the drop itself: anything still
            //pending was built against the previous handshake's opcodes and type indices
            FailPendingSessionWork("Connection was reset");

            //type indices are reassigned by every handshake, plans and fingerprints from the
            //previous connection are meaningless
            _readPlans.Clear();
            _writePlans.Clear();
            _typeFingerprints.Clear();
            GeneratedSerializerBindings = 0;

            var reader = new LiteReader(handshake);
            var res = reader.ReadByte();
            var isCompressed = reader.ReadByte();

            if (isCompressed == 1)
                reader.Decompress();

            var propertyBytes = reader.ReadByte();

            var numProperties = reader.ReadUInt32();

            _propertyMap = new List<string>();

            for (int i = 0; i < numProperties; i++)
            {
                _propertyMap.Add(reader.ReadString());
            }

            var typeBytes = reader.ReadByte();

            if (typeBytes == 1)      _typeIdxReader = (ref LiteReader reader) => reader.ReadByte();
            else if (typeBytes == 2) _typeIdxReader = (ref LiteReader reader) => reader.ReadUInt16();
            else if (typeBytes == 4) _typeIdxReader = (ref LiteReader reader) => reader.ReadUInt32();
            else throw new Exception("Incorrectly defined type index size in handshake.");

            _typeMaps = new List<TypeMap>();

            var numTypes = reader.ReadUInt32();

            for (int i = 0; i < numTypes; i++)
            {
                var propCount = reader.ReadUInt16();

                if (propCount == 0xffff)
                {
                    var typeSpecial = reader.ReadByte();
                    if (typeSpecial == 1) //dictionary
                    {
                        var keyType = ReadBytes(ref reader, typeBytes);
                        var valueType = ReadBytes(ref reader, typeBytes);

                        var type = new TypeMap()
                        {
                            specialType = SpecialType.Dictionary,
                            basicType = BasicTypes.Object,
                            types = new[] { keyType, valueType }
                        };

                        _typeMaps.Add(type);
                    }
                    else if (typeSpecial == 2) //collection
                    {
                        var type = new TypeMap()
                        {
                            specialType = SpecialType.Collection,
                            basicType = BasicTypes.Object,
                            types = new[] { ReadBytes(ref reader, typeBytes) }
                        };

                        _typeMaps.Add(type);
                    }
                    else if (typeSpecial == 3) //tuple
                    {
                        var numTupleTypes = reader.ReadByte();

                        var type = new TypeMap()
                        {
                            specialType = SpecialType.Tuple,
                            basicType = BasicTypes.Object,
                            types = new uint[numTupleTypes]
                        };

                        for (int j = 0; j < numTupleTypes; j++)
                        {
                            type.types[j] = ReadBytes(ref reader, typeBytes);
                        }

                        _typeMaps.Add(type);
                    }
                    else if (typeSpecial == 4) //nullable
                    {
                        var type = new TypeMap()
                        {
                            specialType = SpecialType.Nullable,
                            basicType = BasicTypes.Object,
                            types = new[] { ReadBytes(ref reader, typeBytes) }
                        };

                        _typeMaps.Add(type);
                    }
                    else
                    {
                        var type = new TypeMap()
                        {
                            specialType = SpecialType.Normal,
                            basicType = GetBasicType(reader.ReadString())
                        };

                        _typeMaps.Add(type);
                    }
                }
                else
                {
                    var type = new TypeMap()
                    {
                        specialType = SpecialType.Normal,
                        basicType = BasicTypes.Object,
                        properties = new PropertyMap[propCount]
                    };

                    for (int j = 0; j < propCount; j++)
                    {
                        var prop = new PropertyMap();
                        prop.propertyIdx = ReadBytes(ref reader, propertyBytes);
                        prop.typeIdx = ReadBytes(ref reader, typeBytes);
                        prop.storageType = (StorageType)reader.ReadByte();
                        type.properties[j] = prop;
                    }

                    _typeMaps.Add(type);
                }
            }

            var funcBytes = reader.ReadByte();

            if (funcBytes == 1) _functionHasher = (x) => CRCFunctions.CalculateCRC8(x);
            else if (funcBytes == 2) _functionHasher = (x) => CRCFunctions.CalculateCRC16(x);
            else if (funcBytes == 4) _functionHasher = (x) => CRCFunctions.CalculateCRC32(x);
            else if (funcBytes == 8) _functionHasher = (x) => CRCFunctions.CalculateCRC64(x);
            else throw new Exception("Incorrectly defined func hash size in handshake.");

            var numFuncs = reader.ReadUInt16();

            _functionMaps = new Dictionary<ulong, FunctionMap>();

            for (int i = 0; i < numFuncs; i++)
            {
                var funcMap = new FunctionMap();
                funcMap.opcode = reader.ReadUInt16();
                var funcHash = ReadBytes(ref reader, funcBytes);
                if (reader.ReadByte() == 1)
                {
                    funcMap.argType = ReadBytes(ref reader, typeBytes);
                }
                if (reader.ReadByte() == 1)
                {
                    funcMap.retType = ReadBytes(ref reader, typeBytes);
                }
                _functionMaps.Add(funcHash, funcMap);
            }

            var permissionBytes = reader.ReadByte();

            if (permissionBytes == 1) _permissionHasher = (x) => CRCFunctions.CalculateCRC8(x);
            else if (permissionBytes == 2) _permissionHasher = (x) => CRCFunctions.CalculateCRC16(x);
            else if (permissionBytes == 4) _permissionHasher = (x) => CRCFunctions.CalculateCRC32(x);
            else if (permissionBytes == 8) _permissionHasher = (x) => CRCFunctions.CalculateCRC64(x);
            else throw new Exception("Incorrectly defined func hash size in handshake.");

            var numPermissions = reader.ReadUInt16();

            _permissionMaps = new Dictionary<ulong, PermissionBitmask>();
            for (int i = 0; i < numPermissions; i++) 
            { 
                var permMap = new PermissionBitmask();
                var permHash = ReadBytes(ref reader, permissionBytes);

                if (i < 64)
                {
                    permMap.Bitmask1 = _bitmasks[i];
                    permMap.Bitmask2 = 0;
                }
                else
                {
                    permMap.Bitmask1 = 0;
                    permMap.Bitmask2 = _bitmasks[i - 64];
                }
                _permissionMaps.Add(permHash, permMap);
            }
        }

        public Task<T> Send<T>(string functionName, object model)
        {
            var hash = _functionHasher(functionName);
            return SendCore<T>(hash, model);
        }

        /// <summary>
        /// Requests the server's API description (function names and signatures). Only available when the
        /// server was built with ShowApi = true on its [RpcServer] attribute; pass the password if the
        /// server requires one. Throws on error responses (e.g. wrong password).
        /// </summary>
        public async Task<List<RpcApiFunctionInfo>> GetApiInfo(string password = null)
        {
            var response = await Connection.Invoke(BuildApiInfoRequest(password)).WaitAsync(ResponseTimeout);
            return ReadApiInfo(response);
        }

        private static byte[] BuildApiInfoRequest(string password)
        {
            var writer = new LiteWriter();
            writer.WriteUInt16(0); //system opcode
            writer.WriteByte(0); //not compressed
            writer.WriteByte(2); //type 2 = api info
            writer.WriteString(password);
            return writer.Close();
        }

        private static List<RpcApiFunctionInfo> ReadApiInfo(byte[] response)
        {
            var reader = new LiteReader(response);

            var res = reader.ReadByte();
            if (res != 0)
            {
                throw new Exception("Error retrieving API info! Error code: " + res);
            }

            if (reader.ReadBoolean())
            {
                reader.Decompress();
            }

            var count = reader.ReadUInt16();
            var functions = new List<RpcApiFunctionInfo>(count);

            for (int i = 0; i < count; i++)
            {
                functions.Add(new RpcApiFunctionInfo
                {
                    Name = reader.ReadString(),
                    ArgType = reader.ReadString(),
                    ReturnType = reader.ReadString(),
                });
            }

            return functions;
        }

        public async Task<T> SendCore<T>(ulong funcHash, object model)
        {
            if (!_functionMaps.TryGetValue(funcHash, out var funcMap))
                throw new Exception("Unknown function hash");

            //multichannel-by-default changes only the envelope: callers keep using Send/Remote,
            //system traffic (handshake, api info, invoke responses) never routes through here
            if (MultiChannelByDefault)
                return await SendMultiChannelCore<T>(funcMap, model);

            var data = WriteMessage(funcMap.opcode, funcMap.argType, model, compress: BatchMode == RpcBatchMode.Disabled);
            var ret = await SendTransport(data, compressOffset: 3).WaitAsync(ResponseTimeout);
            return ReadMessage<T>(ret, funcMap.retType);
        }

        private byte[] GenerateMultichannelPayload(byte channel, ushort opcode, uint? argType, object model)
        {
            var writer = new LiteWriter();
            writer.WriteUInt16(0); //opcode
            writer.WriteBoolean(false);
            writer.WriteByte(1);
            writer.WriteByte(channel);
            WriteMessage(ref writer, opcode, argType, model);
            //multichannel header is 5 bytes, then 2 bytes for req opcode and 1 for isCompressed;
            //uncompressed when batching, see WriteMessage
            return writer.Close(BatchMode == RpcBatchMode.Disabled ? 8 : -1);
        }

        public Task<T> SendMultiChannelMessage<T>(string functionName, object model)
        {
            var hash = _functionHasher(functionName);
            if (!_functionMaps.TryGetValue(hash, out var funcMap))
                throw new Exception("Unknown function hash");

            return SendMultiChannelCore<T>(funcMap, model);
        }

        private async Task<T> SendMultiChannelCore<T>(FunctionMap funcMap, object model)
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            var channel = await ScheduleChannel(tcs);

            try
            {
                var payload = GenerateMultichannelPayload(channel, funcMap.opcode, funcMap.argType, model);

                var res = await SendRaw(payload).WaitAsync(ResponseTimeout);

                if (res[0] != 0)
                {
                    throw new Exception("Error calling function! Error code: " + res[0]
                        + (res[0] == (byte)ErrorCodes.Unauthorized
                            ? " (Unauthorized - the server has not granted this connection AllowMultiChannel)"
                            : ""));
                }

                var data = await tcs.Task.WaitAsync(ResponseTimeout);

                return ReadMessage<T>(data, funcMap.retType);
            }
            catch
            {
                //release the channel so a failed request doesn't occupy it forever,
                //CompareExchange so we don't clear it if a response claimed it (and possibly a new request took it) in the meantime
                Interlocked.CompareExchange(ref _channelRequests[channel], null, tcs);
                throw;
            }
        }

        private async ValueTask<byte> ScheduleChannel(TaskCompletionSource<byte[]> tcs)
        {
            var timeout = ResponseTimeout;
            var deadline = timeout == System.Threading.Timeout.InfiniteTimeSpan
                ? DateTime.MaxValue
                : DateTime.UtcNow + timeout;
            while (true)
            {
                lock (_channelLock)
                {
                    unchecked
                    {
                        //start at the last used channel and wrap around, the channels right after it are the most likely to be free
                        byte useIdx = _lastChannelIndex;
                        for (int i = 0; i < 256; i++, useIdx++)
                        {
                            if (_channelRequests[useIdx] == null)
                            {
                                _channelRequests[useIdx] = tcs;
                                _lastChannelIndex = useIdx;
                                return useIdx;
                            }
                        }
                    }
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException("No multichannel slot became free within ResponseTimeout (" + timeout + "), all 256 are occupied");
                await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1));
            }
        }

        private Task<byte[]> SendRaw(byte[] data)
        {
            return SendTransport(data, compressOffset: 8);
        }

        /// <summary>
        /// every procedure request (plain and multichannel) funnels through here; system traffic with
        /// latency requirements (handshake, invoke responses) calls Connection.Invoke directly instead.
        /// When batching is off the data is already compressed by its builder; when batching is on it
        /// arrives raw and compression is applied at flush time (per request for a lone flush, once
        /// over the whole envelope for a pack).
        /// </summary>
        private Task<byte[]> SendTransport(byte[] data, int compressOffset)
        {
            switch (BatchMode)
            {
                case RpcBatchMode.Coalesce:
                    return EnqueueCoalesce(data, compressOffset);
                case RpcBatchMode.Window:
                    return EnqueueWindow(data, compressOffset);
                default:
                    return Connection.Invoke(data);
            }
        }

        private Task<byte[]> EnqueueWindow(byte[] data, int compressOffset)
        {
            var item = new PendingBatchItem
            {
                Data = data,
                CompressOffset = compressOffset,
                Tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously)
            };

            List<PendingBatchItem> fullBatch = null;
            lock (_batchLock)
            {
                _batchQueue.Add(item);
                if (_batchQueue.Count >= MaxBatchSize)
                {
                    //full batch flushes immediately, a pending timer flush just finds an empty queue later
                    fullBatch = _batchQueue;
                    _batchQueue = new List<PendingBatchItem>();
                }
                else if (!_batchFlushScheduled)
                {
                    _batchFlushScheduled = true;
                    _ = FlushAfterWindowAsync();
                }
            }

            if (fullBatch != null)
                _ = SendBatchAsync(fullBatch);

            return item.Tcs.Task;
        }

        private async Task FlushAfterWindowAsync()
        {
            await Task.Delay(BatchWindow);

            List<PendingBatchItem> batch;
            lock (_batchLock)
            {
                _batchFlushScheduled = false;
                batch = _batchQueue;
                _batchQueue = new List<PendingBatchItem>();
            }

            if (batch.Count > 0)
                await SendBatchAsync(batch);
        }

        private Task<byte[]> EnqueueCoalesce(byte[] data, int compressOffset)
        {
            var item = new PendingBatchItem
            {
                Data = data,
                CompressOffset = compressOffset,
                Tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously)
            };

            lock (_batchLock)
            {
                if (_batchSendInFlight)
                {
                    _batchQueue.Add(item);
                    return item.Tcs.Task;
                }
                _batchSendInFlight = true;
            }

            //idle connection: this call starts the pump and goes out immediately with no added latency
            _ = RunCoalescePumpAsync(new List<PendingBatchItem> { item });
            return item.Tcs.Task;
        }

        private async Task RunCoalescePumpAsync(List<PendingBatchItem> batch)
        {
            while (true)
            {
                await SendBatchAsync(batch);

                lock (_batchLock)
                {
                    if (_batchQueue.Count == 0)
                    {
                        _batchSendInFlight = false;
                        return;
                    }

                    var take = Math.Min(_batchQueue.Count, MaxBatchSize);
                    batch = _batchQueue.GetRange(0, take);
                    _batchQueue.RemoveRange(0, take);
                }
            }
        }

        private async Task SendBatchAsync(List<PendingBatchItem> batch)
        {
            try
            {
                if (batch.Count == 1)
                {
                    //a lone request goes out unwrapped, batching must not add overhead when there's
                    //nothing to pack; it was built raw, so compress it individually here
                    var single = LiteCompression.Compress(batch[0].Data, batch[0].CompressOffset);
                    batch[0].Tcs.TrySetResult(await Connection.Invoke(single).WaitAsync(ResponseTimeout));
                    return;
                }

                //raw requests packed together, then one compression pass over the whole envelope;
                //a timeout here fails every item, so the coalesce pump can't hang on a dead transport
                var response = await Connection.Invoke(LiteCompression.Compress(BuildBatchRequest(batch), 3)).WaitAsync(ResponseTimeout);
                CompleteBatch(batch, response);
            }
            catch (Exception ex)
            {
                foreach (var item in batch)
                    item.Tcs.TrySetException(ex);
            }
        }

        //sync helpers because LiteWriter/LiteReader are ref structs and can't appear in async methods
        private static byte[] BuildBatchRequest(List<PendingBatchItem> batch)
        {
            var writer = new LiteWriter();
            writer.WriteUInt16(0); //system opcode
            writer.WriteByte(0); //compressed, set by LiteCompression.Compress when the envelope compresses
            writer.WriteByte(4); //type 4: batch
            writer.WriteUInt16((ushort)batch.Count);
            foreach (var item in batch)
                writer.WriteByteArray(item.Data);
            return writer.Close();
        }

        private static void CompleteBatch(List<PendingBatchItem> batch, byte[] response)
        {
            var reader = new LiteReader(response);
            var res = reader.ReadByte();
            var isCompressed = reader.ReadBoolean();

            if (res != 0)
            {
                var ex = new Exception("Batch request failed! Error code: " + res
                    + (res == 1 ? " (BadRequest - is the batch larger than the server's MaxBatchSize, or the server too old to support batching?)" : ""));
                foreach (var item in batch)
                    item.Tcs.TrySetException(ex);
                return;
            }

            if (isCompressed)
                reader.Decompress();

            var count = reader.ReadUInt16();
            if (count != batch.Count)
            {
                var ex = new Exception($"Batch response item count mismatch: sent {batch.Count}, received {count}");
                foreach (var item in batch)
                    item.Tcs.TrySetException(ex);
                return;
            }

            for (int i = 0; i < count; i++)
                batch[i].Tcs.TrySetResult(reader.ReadByteArray());
        }

        private T ReadMessage<T>(byte[] data, uint? retType)
        {
            var reader = new LiteReader(data);
            return ReadMessage<T>(ref reader, retType);
        }

        private T ReadMessage<T>(ref LiteReader reader, uint? retType)
        {
            var res = reader.ReadByte();
            var isCompressed = reader.ReadBoolean();

            if (res != 0)
            {
                throw new Exception("Error calling function! Error code: " + res);
            }

            if (retType.HasValue)
            {
                if (isCompressed)
                {
                    reader.Decompress();
                }

                return (T)Deserialize(ref reader, typeof(T), retType.Value);
            }

            return default;
        }

        private byte[] WriteMessage(ushort opcode, uint? argType, object model, bool compress = true)
        {
            var writer = new LiteWriter();
            WriteMessage(ref writer, opcode, argType, model);
            //when batching, requests stay uncompressed here so the batcher can compress the
            //whole envelope in one pass at flush time (lone flushes compress individually)
            return writer.Close(compress ? 3 : -1);
        }

        private void WriteMessage(ref LiteWriter writer, ushort opcode, uint? argType, object model)
        {
            writer.WriteUInt16(opcode);
            writer.WriteByte(0); //compression placeholder

            if (argType.HasValue)
            {
                if (model == null && !IsTypeNullable(argType.Value))
                    throw new Exception("Missing argument");

                Serialize(ref writer, argType.Value, model);
            }
        }

        /// <summary>true when the wire format can express null for this type (objects, nullables, collections, strings)</summary>
        private bool IsTypeNullable(uint type)
        {
            var typeMap = _typeMaps[(int)type];
            return typeMap.basicType == BasicTypes.Object || typeMap.basicType == BasicTypes.String;
        }

        private uint ReadBytes(ref LiteReader reader, int numBytes)
        {
            if (numBytes == 1)
            {
                return reader.ReadByte();
            }
            else if (numBytes == 2)
            {
                return reader.ReadUInt16();
            }
            else if (numBytes == 4)
            {
                return reader.ReadUInt32();
            }
            else throw new Exception("Incorrect size parameter: " + numBytes);
        }

        private BasicTypes GetBasicType(string type)
        {
            switch (type)
            {
                case "bool":
                    return BasicTypes.Bool;
                case "sbyte":
                    return BasicTypes.SByte;
                case "byte":
                    return BasicTypes.Byte;
                case "short":
                    return BasicTypes.Int16;
                case "ushort":
                    return BasicTypes.UInt16;
                case "int":
                    return BasicTypes.Int32;
                case "uint":
                    return BasicTypes.UInt32;
                case "long":
                    return BasicTypes.Int64;
                case "ulong":
                    return BasicTypes.UInt64;
                case "float":
                    return BasicTypes.Single;
                case "double":
                    return BasicTypes.Double;
                case "decimal":
                    return BasicTypes.Decimal;
                case "string":
                    return BasicTypes.String;
                case "date":
                    return BasicTypes.DateTime;
                case "guid":
                    return BasicTypes.Guid;
                default:
                    return BasicTypes.Object;
            }
        }

        //dynamic serialization: the entry points route through compiled per-type plans (built once
        //per handshake, see the serialization plan cache region); the *Walk methods below are the
        //original walk-per-call logic, kept as the switchable fallback and the reference semantics

        public object Deserialize(ref LiteReader reader, Type modelType, uint type)
        {
            if (EnableSerializationCache)
                return GetReadPlan(type, modelType)(ref reader);
            return DeserializeWalk(ref reader, modelType, type);
        }

        public object DeserializeWalk(ref LiteReader reader, Type modelType, uint type)
        {
            var typeMap = _typeMaps[(int)type];
            if (typeMap.basicType == BasicTypes.Object)
            {
                if(typeMap.specialType == SpecialType.Dictionary)
                {
                    return ReadDictionary(ref reader, modelType, typeMap.types[0], typeMap.types[1]);
                }
                else if (typeMap.specialType == SpecialType.Collection)
                {
                    return ReadCollection(ref reader, modelType, typeMap.types[0]);
                }
                else if (typeMap.specialType == SpecialType.Tuple)
                {
                    return ReadTuple(ref reader, modelType, typeMap.types);
                }
                else if (typeMap.specialType == SpecialType.Nullable)
                {
                    //var nullableType = Nullable.GetUnderlyingType();
                    return ReadNullable(ref reader, modelType, typeMap.types[0]);
                }
                else
                {
                    if (reader.ReadByte() == 1)
                    {
                        var res = Activator.CreateInstance(modelType);
                        foreach (var map in typeMap.properties)
                        {
                            var property = modelType.GetProperty(_propertyMap[(int)map.propertyIdx]);
                            if (property != null)
                            {
                                var propType = property.PropertyType;
                                if (map.storageType == StorageType.Array || map.storageType == StorageType.List)
                                {
                                    SetPropertyValue(property, res, ReadCollection(ref reader, propType, map.typeIdx));
                                }
                                else if (map.storageType == StorageType.Nullable)
                                {
                                    SetPropertyValue(property, res, ReadNullable(ref reader, propType, map.typeIdx));
                                }
                                else
                                {
                                    SetPropertyValue(property, res, DeserializeWalk(ref reader, propType, map.typeIdx));
                                }
                            }
                            else
                            {
                                if (map.storageType == StorageType.Array || map.storageType == StorageType.List)
                                {
                                    SkipCollection(ref reader, map.typeIdx);
                                }
                                else if (map.storageType == StorageType.Nullable)
                                {
                                    SkipNullable(ref reader, map.typeIdx);
                                }
                                else
                                {
                                    SkipDeserialize(ref reader, map.typeIdx);
                                }
                            }
                        }
                        return res;
                    }
                    else return null;
                }
            }
            else
            {
                var value = ReadBasicValue(ref reader, typeMap.basicType);

                //enums travel as their underlying primitive (any size); box the value back as the
                //exact enum type so unboxing casts, IList.Add and Activator calls with enum
                //targets all succeed regardless of the enum's underlying type
                if (modelType != null && modelType.IsEnum && value != null)
                    return Enum.ToObject(modelType, value);

                return value;
            }
        }

        private static object ReadBasicValue(ref LiteReader reader, BasicTypes basicType)
        {
            switch (basicType)
            {
                case BasicTypes.Bool:
                    return reader.ReadBoolean();
                case BasicTypes.SByte:
                    return reader.ReadSByte();
                case BasicTypes.Byte:
                    return reader.ReadByte();
                case BasicTypes.Int16:
                    return reader.ReadInt16();
                case BasicTypes.UInt16:
                    return reader.ReadUInt16();
                case BasicTypes.Int32:
                    return reader.ReadInt32();
                case BasicTypes.UInt32:
                    return reader.ReadUInt32();
                case BasicTypes.Int64:
                    return reader.ReadInt64();
                case BasicTypes.UInt64:
                    return reader.ReadUInt64();
                case BasicTypes.Single:
                    return reader.ReadSingle();
                case BasicTypes.Double:
                    return reader.ReadDouble();
                case BasicTypes.Decimal:
                    return reader.ReadDecimal();
                case BasicTypes.String:
                    return reader.ReadString();
                case BasicTypes.DateTime:
                    return reader.ReadDate();
                case BasicTypes.Guid:
                    return reader.ReadGuid();
                default:
                    throw new Exception("Unknown basic type: " + basicType);
            }
        }

        public void SkipDeserialize(ref LiteReader reader, uint type)
        {
            var typeMap = _typeMaps[(int)type];
            if (typeMap.basicType == BasicTypes.Object)
            {
                if (typeMap.specialType == SpecialType.Dictionary)
                {
                    SkipDictionary(ref reader, typeMap.types[0], typeMap.types[1]);
                    return;
                }
                else if (typeMap.specialType == SpecialType.Collection)
                {
                    SkipCollection(ref reader, typeMap.types[0]);
                    return;
                }
                else if (typeMap.specialType == SpecialType.Tuple)
                {
                    SkipTuple(ref reader, typeMap.types);
                    return;
                }
                else if (typeMap.specialType == SpecialType.Nullable)
                {
                    SkipNullable(ref reader, typeMap.types[0]);
                    return;
                }
                else
                {
                    if (reader.ReadByte() == 1)
                    {
                        foreach (var map in typeMap.properties)
                        {
                            if (map.storageType == StorageType.Array || map.storageType == StorageType.List)
                            {
                                SkipCollection(ref reader, map.typeIdx);
                            }
                            else if (map.storageType == StorageType.Nullable)
                            {
                                SkipNullable(ref reader, map.typeIdx);
                            }
                            else
                            {
                                SkipDeserialize(ref reader, map.typeIdx);
                            }
                        }
                    }
                    else return;
                }
            }
            else
            {
                switch (typeMap.basicType)
                {
                    case BasicTypes.Bool:
                        reader.ReadBoolean();
                        return;
                    case BasicTypes.SByte:
                        reader.ReadSByte();
                        return;
                    case BasicTypes.Byte:
                        reader.ReadByte();
                        return;
                    case BasicTypes.Int16:
                        reader.ReadInt16();
                        return;
                    case BasicTypes.UInt16:
                        reader.ReadUInt16();
                        return;
                    case BasicTypes.Int32:
                        reader.ReadInt32();
                        return;
                    case BasicTypes.UInt32:
                        reader.ReadUInt32();
                        return;
                    case BasicTypes.Int64:
                        reader.ReadInt64();
                        return;
                    case BasicTypes.UInt64:
                        reader.ReadUInt64();
                        return;
                    case BasicTypes.Single:
                        reader.ReadSingle();
                        return;
                    case BasicTypes.Double:
                        reader.ReadDouble();
                        return;
                    case BasicTypes.Decimal:
                        reader.ReadDecimal();
                        return;
                    case BasicTypes.String:
                        reader.ReadString();
                        return;
                    case BasicTypes.DateTime:
                        reader.ReadDate();
                        return;
                    case BasicTypes.Guid:
                        reader.ReadGuid();
                        return;
                    default:
                        throw new Exception("Unknown basic type: " + typeMap.basicType);
                }
            }
        }

        public IDictionary ReadDictionary(ref LiteReader reader, Type modelType, uint typeKey, uint typeVal)
        {
            var len = reader.ReadLength();
            if (len == ulong.MaxValue)
            {
                return null;
            }            

            var res = Activator.CreateInstance(modelType) as IDictionary;

            var dictType = res.GetType();

            var keyType = dictType.GenericTypeArguments[0];
            var objType = dictType.GenericTypeArguments[1];

            for (var i = 0UL; i < len; i++)
            {
                var key = DeserializeWalk(ref reader, keyType, typeKey);
                var value = DeserializeWalk(ref reader, objType, typeVal);
                res.Add(key, value);
            }

            return res;
        }

        public void SkipDictionary(ref LiteReader reader, uint typeKey, uint typeVal)
        {
            var len = reader.ReadLength();
            if (len == ulong.MaxValue)
            {
                return;
            }

            for (var i = 0UL; i < len; i++)
            {
                SkipDeserialize(ref reader, typeKey);
                SkipDeserialize(ref reader, typeVal);
            }
        }

        public ICollection ReadCollection(ref LiteReader reader, Type modelType, uint type)
        {
            var len = reader.ReadLength();
            if (len == ulong.MaxValue)
            {
                return null;
            }

            IList res;
            Type elementType = modelType.GetElementType();

            if (modelType.IsArray)
            {
                elementType = modelType.GetElementType();
                res = Array.CreateInstance(elementType, (int)len); 
                
                for (int i = 0; i < (int)len; i++)
                {
                    res[i] = DeserializeWalk(ref reader, elementType, type);
                }
            }
            else
            {
                elementType = modelType.GetGenericArguments()[0];
                res = Activator.CreateInstance(modelType) as IList;

                for (int i = 0; i < (int)len; i++)
                {
                    res.Add(DeserializeWalk(ref reader, elementType, type));
                }
            }

            return res;
        }

        public void SkipCollection(ref LiteReader reader, uint type)
        {
            var len = reader.ReadLength();
            if (len == ulong.MaxValue)
            {
                return;
            }

            for (var i = 0UL; i < len; i++)
            {
                SkipDeserialize(ref reader, type);
            }
        }

        public object ReadNullable(ref LiteReader reader, Type modelType, uint type)
        {
            if (reader.ReadByte() == 1)
            {
                var underlyingType = Nullable.GetUnderlyingType(modelType) ?? modelType; //if default value is set it will get unboxed, so model type will actually be the underlying type

                var args = new object[1];

                //if (genArgs[0].IsValueType)
                //    args[0] = Activator.CreateInstance(genArgs[0]);
                //else args[0] = null;               

                args[0] = DeserializeWalk(ref reader, underlyingType, type);

                if (underlyingType.IsEnum) args[0] = Enum.ToObject(underlyingType, args[0]);

                return Activator.CreateInstance(modelType, args[0]);
            }
            else
            {
                return null;
            }
        }

        public void SkipNullable(ref LiteReader reader, uint type)
        {
            if (reader.ReadByte() == 1)
            {
                SkipDeserialize(ref reader, type);
            }
        }

        public ITuple ReadTuple(ref LiteReader reader, Type modelType, uint[] type)
        {
            var objs = new object[type.Length];

            for (int i = 0; i < type.Length; i++)
            {
                objs[i] = DeserializeWalk(ref reader, modelType.GenericTypeArguments[i], type[i]);
            }

            return Activator.CreateInstance(modelType, objs) as ITuple;
        }

        public void SkipTuple(ref LiteReader reader, uint[] type)
        {
            for (int i = 0; i < type.Length; i++)
            {
                SkipDeserialize(ref reader, type[i]);
            }
        }

        public void Serialize(ref LiteWriter writer, uint type, object value)
        {
            if (EnableSerializationCache)
            {
                GetWritePlan(type, value?.GetType())(ref writer, value);
                return;
            }
            SerializeWalk(ref writer, type, value);
        }

        public void SerializeWalk(ref LiteWriter writer, uint type, object value)
        {
            var typeMap = _typeMaps[(int)type];
            if (typeMap.basicType == BasicTypes.Object)
            {
                if (typeMap.specialType == SpecialType.Dictionary)
                {
                    WriteDictionary(ref writer, value as IDictionary, typeMap.types[0], typeMap.types[1]);
                }
                else if (typeMap.specialType == SpecialType.Collection)
                {
                    WriteCollection(ref writer, value as ICollection, typeMap.types[0]);
                }
                else if (typeMap.specialType == SpecialType.Tuple)
                {
                    WriteTuple(ref writer, value as ITuple, typeMap.types);
                }
                else if (typeMap.specialType == SpecialType.Nullable)
                {
                    if (value == null)
                    {
                        writer.WriteByte(0);
                    }
                    else
                    {
                        writer.WriteByte(1);
                        //a boxed Nullable<T> is already a boxed T, so the value can be serialized directly
                        //(GetProperty("Value") would return null here and NRE)
                        SerializeWalk(ref writer, typeMap.types[0], value);
                    }
                }
                else
                {
                    if (value != null)
                    {
                        writer.WriteByte(1);
                        var mapType = value.GetType();
                        foreach (var map in typeMap.properties)
                        {
                            var property = value.GetType().GetProperty(_propertyMap[(int)map.propertyIdx]);
                            var propertyValue = property == null ? null : property.GetValue(value);
                            if (map.storageType == StorageType.Array || map.storageType == StorageType.List)
                            {
                                WriteCollection(ref writer, propertyValue as ICollection, map.typeIdx);
                            }
                            else if (map.storageType == StorageType.Nullable)
                            {
                                WriteNullable(ref writer, propertyValue, map.typeIdx);
                            }
                            else
                            {
                                SerializeWalk(ref writer, map.typeIdx, propertyValue);
                            }
                        }
                    }
                    else
                    {
                        writer.WriteByte(0);
                    }
                }
            }
            else
            {
                if (value == null)
                {
                    if (typeMap.basicType == BasicTypes.String)
                    {
                        //strings are nullable on the wire, substituting "" would change the value
                        writer.WriteString(null);
                        return;
                    }
                    value = GetDefaultValue(typeMap.basicType);
                }

                switch (typeMap.basicType)
                {
                    case BasicTypes.Bool:
                        writer.WriteBoolean(UnboxConvertTo<bool>(value));
                        break;
                    case BasicTypes.SByte:
                        writer.WriteSByte(UnboxConvertTo<sbyte>(value));
                        break;
                    case BasicTypes.Byte:
                        writer.WriteByte(UnboxConvertTo<byte>(value));
                        break;
                    case BasicTypes.Int16:
                        writer.WriteInt16(UnboxConvertTo<short>(value));
                        break;
                    case BasicTypes.UInt16:
                        writer.WriteUInt16(UnboxConvertTo<ushort>(value));
                        break;
                    case BasicTypes.Int32:
                        writer.WriteInt32(UnboxConvertTo<int>(value));
                        break;
                    case BasicTypes.UInt32:
                        writer.WriteUInt32(UnboxConvertTo<uint>(value));
                        break;
                    case BasicTypes.Int64:
                        writer.WriteInt64(UnboxConvertTo<long>(value));
                        break;
                    case BasicTypes.UInt64:
                        writer.WriteUInt64(UnboxConvertTo<ulong>(value));
                        break;
                    case BasicTypes.Single:
                        writer.WriteSingle(UnboxConvertTo<float>(value));
                        break;
                    case BasicTypes.Double:
                        writer.WriteDouble(UnboxConvertTo<double>(value));
                        break;
                    case BasicTypes.Decimal:
                        writer.WriteDecimal(UnboxConvertTo<decimal>(value));
                        break;
                    case BasicTypes.String:
                        writer.WriteString(value.ToString());
                        break;
                    case BasicTypes.DateTime:
                        writer.WriteDate((DateTime)value);
                        break;
                    case BasicTypes.Guid:
                        writer.WriteGuid((Guid)value);
                        break;
                    default:
                        throw new Exception("Unknown basic type: " + typeMap.basicType);
                }
            }
        }

        public T UnboxConvertTo<T>(object value)
        {
            if (value == null)
            {
                if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) == null)
                {
                    throw new InvalidCastException("Cannot convert null to a value type.");
                }
                return default(T);
            }

            if (value is T)
            {
                return (T)value;
            }
                      
            return (T)Convert.ChangeType(value, typeof(T));     
        }

        private object GetDefaultValue(BasicTypes basicType)
        {
            switch (basicType)
            {
                case BasicTypes.Bool:
                    return false;
                case BasicTypes.SByte:
                    return (sbyte)0;
                case BasicTypes.Byte:
                    return (byte)0;
                case BasicTypes.Int16:
                    return (short)0;
                case BasicTypes.UInt16:
                    return (ushort)0;
                case BasicTypes.Int32:
                    return (int)0;
                case BasicTypes.UInt32:
                    return (uint)0;
                case BasicTypes.Int64:
                    return (long)0;
                case BasicTypes.UInt64:
                    return (ulong)0;
                case BasicTypes.Single:
                    return (float)0;
                case BasicTypes.Double:
                    return (double)0;
                case BasicTypes.Decimal:
                    return (decimal)0;
                case BasicTypes.String:
                    return string.Empty;
                case BasicTypes.DateTime:
                    return DateTime.Now;
                case BasicTypes.Guid:
                    return Guid.Empty;
            }
            return null;
        }

        public void WriteCollection(ref LiteWriter writer, ICollection value, uint type)
        {
            if (value == null)
            {
                writer.WriteLength(ulong.MaxValue);
            }
            else
            {
                writer.WriteLength((ulong)value.Count);

                foreach (var obj in value)
                {
                    SerializeWalk(ref writer, type, obj);
                }
            }
        }

        public void WriteNullable(ref LiteWriter writer, object value, uint type)
        {
            if (value == null)
            {
                writer.WriteByte(0);
            }
            else
            {
                writer.WriteByte(1);

                SerializeWalk(ref writer, type, value);
            }
        }

        public void WriteDictionary(ref LiteWriter writer, IDictionary value, uint typeKey, uint typeVal)
        {
            if (value == null)
            {
                writer.WriteLength(ulong.MaxValue);
            }
            else
            {
                writer.WriteLength((ulong)value.Count);

                foreach (DictionaryEntry entry in value)
                {
                    SerializeWalk(ref writer, typeKey, entry.Key);
                    SerializeWalk(ref writer, typeVal, entry.Value);
                }
            }
        }

        public void WriteTuple(ref LiteWriter writer, ITuple value, uint[] type)
        {
            for (int i = 0; i < value.Length; i++)
            {
                SerializeWalk(ref writer, type[i], value[i]);
            }
        }

        public void SetPropertyValue(PropertyInfo propertyInfo, object targetObject, object value)
        {            
            Type propertyType = propertyInfo.PropertyType;
            Type underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

            if (underlyingType.IsEnum && value != null)
            {
                Type enumUnderlyingType = Enum.GetUnderlyingType(underlyingType);

                object convertedValue;
           
                convertedValue = Convert.ChangeType(value, enumUnderlyingType); 

                propertyInfo.SetValue(targetObject, Enum.ToObject(underlyingType, convertedValue));
            }
            else
            {
                object convertedValue = (value == null) ? null : Convert.ChangeType(value, underlyingType);
                propertyInfo.SetValue(targetObject, convertedValue, null);
            }
        }

        #region serialization plan cache

        //Plans compile the handshake type maps into delegate trees once per (wire type, CLR type)
        //pair: reflection lookups, Convert.ChangeType decisions, factories and property accessors
        //are all resolved at build time, so executing a plan only moves bytes. Expression trees are
        //used for accessors and factories (never for anything taking LiteReader/LiteWriter: ByRefLike
        //types can't appear in expression lambdas); the reader/writer flow is composed closures.

        private ReadPlanDelegate GetReadPlan(uint typeIdx, Type modelType)
        {
            var key = (typeIdx, modelType);
            if (_readPlans.TryGetValue(key, out var existing))
                return existing;

            //publish a forwarder before building so self-referencing models resolve during their own
            //build; the box lock makes concurrent executors wait until the plan is filled
            var box = new ReadPlanBox();
            ReadPlanDelegate forwarder = (ref LiteReader r) => box.Wait()(ref r);

            lock (box)
            {
                var winner = _readPlans.GetOrAdd(key, forwarder);
                if (!ReferenceEquals(winner, forwarder))
                    return winner;

                try
                {
                    box.Plan = BuildReadPlan(typeIdx, modelType);
                }
                catch (Exception ex)
                {
                    //poison the box so a waiter rethrows instead of hitting a null plan
                    var error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                    box.Plan = (ref LiteReader r) => throw error.SourceException;
                    _readPlans.TryRemove(key, out _);
                    throw;
                }
            }

            _readPlans[key] = box.Plan; //future lookups skip the forwarder indirection
            return box.Plan;
        }

        private ReadPlanDelegate BuildReadPlan(uint typeIdx, Type modelType)
        {
            var typeMap = _typeMaps[(int)typeIdx];
            if (typeMap.basicType != BasicTypes.Object)
                return BuildBasicReadPlan(typeMap.basicType, modelType);

            switch (typeMap.specialType)
            {
                case SpecialType.Dictionary:
                    return BuildDictionaryReadPlan(typeMap.types[0], typeMap.types[1], modelType);
                case SpecialType.Collection:
                    return BuildCollectionReadPlan(typeMap.types[0], modelType);
                case SpecialType.Tuple:
                    return BuildTupleReadPlan(typeMap.types, modelType);
                case SpecialType.Nullable:
                    return BuildNullableReadPlan(typeMap.types[0], modelType);
                default:
                    //a structurally matching generated serializer beats any composed plan
                    return TryGetGeneratedReadPlan(typeIdx, modelType)
                        ?? BuildObjectReadPlan(typeMap, modelType);
            }
        }

        private static ReadPlanDelegate BuildBasicReadPlan(BasicTypes basicType, Type modelType)
        {
            ReadPlanDelegate plan;
            switch (basicType)
            {
                case BasicTypes.Bool: plan = (ref LiteReader r) => r.ReadBoolean(); break;
                case BasicTypes.SByte: plan = (ref LiteReader r) => r.ReadSByte(); break;
                case BasicTypes.Byte: plan = (ref LiteReader r) => r.ReadByte(); break;
                case BasicTypes.Int16: plan = (ref LiteReader r) => r.ReadInt16(); break;
                case BasicTypes.UInt16: plan = (ref LiteReader r) => r.ReadUInt16(); break;
                case BasicTypes.Int32: plan = (ref LiteReader r) => r.ReadInt32(); break;
                case BasicTypes.UInt32: plan = (ref LiteReader r) => r.ReadUInt32(); break;
                case BasicTypes.Int64: plan = (ref LiteReader r) => r.ReadInt64(); break;
                case BasicTypes.UInt64: plan = (ref LiteReader r) => r.ReadUInt64(); break;
                case BasicTypes.Single: plan = (ref LiteReader r) => r.ReadSingle(); break;
                case BasicTypes.Double: plan = (ref LiteReader r) => r.ReadDouble(); break;
                case BasicTypes.Decimal: plan = (ref LiteReader r) => r.ReadDecimal(); break;
                case BasicTypes.String: plan = (ref LiteReader r) => r.ReadString(); break;
                case BasicTypes.DateTime: plan = (ref LiteReader r) => r.ReadDate(); break;
                case BasicTypes.Guid: plan = (ref LiteReader r) => r.ReadGuid(); break;
                default: throw new Exception("Unknown basic type: " + basicType);
            }

            if (modelType != null && modelType.IsEnum)
            {
                //enums travel as their underlying primitive of any width, box back as the exact enum type
                var enumType = modelType;
                var inner = plan;
                return (ref LiteReader r) => Enum.ToObject(enumType, inner(ref r));
            }

            return plan;
        }

        private ReadPlanDelegate BuildObjectReadPlan(TypeMap typeMap, Type modelType)
        {
            var factory = CompileFactory(modelType);
            var readers = new ReadPlanDelegate[typeMap.properties.Length];
            var setters = new Action<object, object>[typeMap.properties.Length];

            for (int i = 0; i < typeMap.properties.Length; i++)
            {
                var map = typeMap.properties[i];
                var property = modelType.GetProperty(_propertyMap[(int)map.propertyIdx]);
                if (property == null)
                {
                    //the server sends this property but the client model doesn't know it: skip its bytes
                    readers[i] = BuildSkipReadPlan(map);
                    continue;
                }

                Type resultType;
                if (map.storageType == StorageType.Array || map.storageType == StorageType.List)
                {
                    readers[i] = BuildCollectionReadPlan(map.typeIdx, property.PropertyType);
                    resultType = property.PropertyType;
                }
                else if (map.storageType == StorageType.Nullable)
                {
                    readers[i] = BuildNullableReadPlan(map.typeIdx, property.PropertyType);
                    resultType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                }
                else
                {
                    readers[i] = GetReadPlan(map.typeIdx, property.PropertyType);
                    resultType = GetReadPlanResultType(map.typeIdx, property.PropertyType);
                }
                setters[i] = CompileSetter(modelType, property, resultType);
            }

            return (ref LiteReader r) =>
            {
                if (r.ReadByte() != 1)
                    return null;

                var res = factory();
                for (int i = 0; i < readers.Length; i++)
                {
                    var value = readers[i](ref r);
                    setters[i]?.Invoke(res, value);
                }
                return res;
            };
        }

        private ReadPlanDelegate BuildSkipReadPlan(PropertyMap map)
        {
            var idx = map.typeIdx;
            if (map.storageType == StorageType.Array || map.storageType == StorageType.List)
                return (ref LiteReader r) => { SkipCollection(ref r, idx); return null; };
            if (map.storageType == StorageType.Nullable)
                return (ref LiteReader r) => { SkipNullable(ref r, idx); return null; };
            return (ref LiteReader r) => { SkipDeserialize(ref r, idx); return null; };
        }

        private ReadPlanDelegate BuildNullableReadPlan(uint underlyingIdx, Type declaredType)
        {
            var underlyingType = declaredType == null ? null : Nullable.GetUnderlyingType(declaredType) ?? declaredType;
            //a boxed T is a valid boxed T?, so the plan returns the boxed underlying value directly
            var inner = GetReadPlan(underlyingIdx, underlyingType);
            return (ref LiteReader r) => r.ReadByte() == 1 ? inner(ref r) : null;
        }

        private ReadPlanDelegate BuildCollectionReadPlan(uint elementIdx, Type collectionType)
        {
            if (collectionType.IsArray)
            {
                var elementType = collectionType.GetElementType();
                var elementPlan = GetReadPlan(elementIdx, elementType);
                return (ref LiteReader r) =>
                {
                    var len = r.ReadLength();
                    if (len == ulong.MaxValue)
                        return null;
                    var arr = Array.CreateInstance(elementType, (int)len);
                    for (int i = 0; i < (int)len; i++)
                        arr.SetValue(elementPlan(ref r), i);
                    return arr;
                };
            }
            else
            {
                var elementType = collectionType.GetGenericArguments()[0];
                var elementPlan = GetReadPlan(elementIdx, elementType);
                var factory = CompileFactory(collectionType);
                return (ref LiteReader r) =>
                {
                    var len = r.ReadLength();
                    if (len == ulong.MaxValue)
                        return null;
                    var list = (IList)factory();
                    for (ulong i = 0; i < len; i++)
                        list.Add(elementPlan(ref r));
                    return list;
                };
            }
        }

        private ReadPlanDelegate BuildDictionaryReadPlan(uint keyIdx, uint valueIdx, Type dictionaryType)
        {
            var args = dictionaryType.GenericTypeArguments;
            var keyPlan = GetReadPlan(keyIdx, args[0]);
            var valuePlan = GetReadPlan(valueIdx, args[1]);
            var factory = CompileFactory(dictionaryType);
            return (ref LiteReader r) =>
            {
                var len = r.ReadLength();
                if (len == ulong.MaxValue)
                    return null;
                var dict = (IDictionary)factory();
                for (ulong i = 0; i < len; i++)
                {
                    var key = keyPlan(ref r);
                    var value = valuePlan(ref r);
                    dict.Add(key, value);
                }
                return dict;
            };
        }

        private ReadPlanDelegate BuildTupleReadPlan(uint[] elementIdxs, Type tupleType)
        {
            var argTypes = tupleType.GenericTypeArguments;
            var plans = new ReadPlanDelegate[elementIdxs.Length];
            for (int i = 0; i < elementIdxs.Length; i++)
                plans[i] = GetReadPlan(elementIdxs[i], i < argTypes.Length ? argTypes[i] : null);

            var create = CompileTupleFactory(tupleType, argTypes, elementIdxs.Length);
            return (ref LiteReader r) =>
            {
                var objs = new object[plans.Length];
                for (int i = 0; i < plans.Length; i++)
                    objs[i] = plans[i](ref r);
                return create(objs);
            };
        }

        private Type GetReadPlanResultType(uint typeIdx, Type declaredType)
        {
            var typeMap = _typeMaps[(int)typeIdx];
            if (typeMap.basicType != BasicTypes.Object)
                return declaredType != null && declaredType.IsEnum ? declaredType : GetBasicClrType(typeMap.basicType);
            if (typeMap.specialType == SpecialType.Nullable)
                return declaredType == null ? null : Nullable.GetUnderlyingType(declaredType) ?? declaredType;
            return declaredType;
        }

        private static Type GetBasicClrType(BasicTypes basicType)
        {
            switch (basicType)
            {
                case BasicTypes.Bool: return typeof(bool);
                case BasicTypes.SByte: return typeof(sbyte);
                case BasicTypes.Byte: return typeof(byte);
                case BasicTypes.Int16: return typeof(short);
                case BasicTypes.UInt16: return typeof(ushort);
                case BasicTypes.Int32: return typeof(int);
                case BasicTypes.UInt32: return typeof(uint);
                case BasicTypes.Int64: return typeof(long);
                case BasicTypes.UInt64: return typeof(ulong);
                case BasicTypes.Single: return typeof(float);
                case BasicTypes.Double: return typeof(double);
                case BasicTypes.Decimal: return typeof(decimal);
                case BasicTypes.String: return typeof(string);
                case BasicTypes.DateTime: return typeof(DateTime);
                case BasicTypes.Guid: return typeof(Guid);
                default: return null;
            }
        }

        private WritePlanDelegate GetWritePlan(uint typeIdx, Type runtimeType)
        {
            var key = (typeIdx, runtimeType);
            if (_writePlans.TryGetValue(key, out var existing))
                return existing;

            var box = new WritePlanBox();
            WritePlanDelegate forwarder = (ref LiteWriter w, object v) => box.Wait()(ref w, v);

            lock (box)
            {
                var winner = _writePlans.GetOrAdd(key, forwarder);
                if (!ReferenceEquals(winner, forwarder))
                    return winner;

                try
                {
                    box.Plan = BuildWritePlan(typeIdx, runtimeType);
                }
                catch (Exception ex)
                {
                    var error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                    box.Plan = (ref LiteWriter w, object v) => throw error.SourceException;
                    _writePlans.TryRemove(key, out _);
                    throw;
                }
            }

            _writePlans[key] = box.Plan;
            return box.Plan;
        }

        private WritePlanDelegate BuildWritePlan(uint typeIdx, Type runtimeType)
        {
            if (runtimeType == null)
            {
                //null carries no runtime type; the walk already encodes null exactly per type-map
                //kind (null markers, MaxValue lengths, basic defaults), reuse it as the null plan
                var nullIdx = typeIdx;
                return (ref LiteWriter w, object v) => SerializeWalk(ref w, nullIdx, v);
            }

            var typeMap = _typeMaps[(int)typeIdx];
            if (typeMap.basicType != BasicTypes.Object)
                return BuildBasicWritePlan(typeMap.basicType, runtimeType);

            switch (typeMap.specialType)
            {
                case SpecialType.Dictionary:
                    return BuildDictionaryWritePlan(typeMap.types[0], typeMap.types[1], runtimeType);
                case SpecialType.Collection:
                    return BuildCollectionWritePlan(typeMap.types[0], runtimeType);
                case SpecialType.Tuple:
                    return BuildTupleWritePlan(typeMap.types, runtimeType);
                case SpecialType.Nullable:
                {
                    //GetType() of a boxed T? already IS the underlying T
                    var inner = BuildSlotWritePlan(typeMap.types[0], Nullable.GetUnderlyingType(runtimeType) ?? runtimeType);
                    return (ref LiteWriter w, object v) =>
                    {
                        if (v == null) { w.WriteByte(0); return; }
                        w.WriteByte(1);
                        inner(ref w, v);
                    };
                }
                default:
                    return TryGetGeneratedWritePlan(typeIdx, runtimeType)
                        ?? BuildObjectWritePlan(typeMap, runtimeType);
            }
        }

        /// <summary>write plan for a value slot inside a composite, keyed by the slot's declared CLR type (may be null)</summary>
        private WritePlanDelegate BuildSlotWritePlan(uint typeIdx, Type declaredType)
        {
            var typeMap = _typeMaps[(int)typeIdx];
            if (typeMap.basicType != BasicTypes.Object)
                return BuildBasicWritePlan(typeMap.basicType, declaredType);

            if (typeMap.specialType == SpecialType.Nullable)
            {
                var underlying = declaredType == null ? null : Nullable.GetUnderlyingType(declaredType) ?? declaredType;
                var inner = BuildSlotWritePlan(typeMap.types[0], underlying);
                return (ref LiteWriter w, object v) =>
                {
                    if (v == null) { w.WriteByte(0); return; }
                    w.WriteByte(1);
                    inner(ref w, v);
                };
            }

            if (declaredType != null && (declaredType.IsValueType || declaredType.IsSealed))
                return GetWritePlan(typeIdx, declaredType); //the runtime type can't differ, resolve once

            //reference types can be subclassed: resolve the plan per instance (one dictionary hit)
            var idx = typeIdx;
            return (ref LiteWriter w, object v) => GetWritePlan(idx, v?.GetType())(ref w, v);
        }

        private WritePlanDelegate BuildBasicWritePlan(BasicTypes basicType, Type declaredType)
        {
            //nulls take the walk's defaults (see SerializeWalk); the slow branches keep the walk's
            //UnboxConvertTo semantics for enums and cross-version width mismatches, decided once here
            switch (basicType)
            {
                case BasicTypes.Bool:
                    if (declaredType == typeof(bool)) return (ref LiteWriter w, object v) => w.WriteBoolean(v != null && (bool)v);
                    return (ref LiteWriter w, object v) => w.WriteBoolean(v != null && UnboxConvertTo<bool>(v));
                case BasicTypes.SByte:
                    if (declaredType == typeof(sbyte)) return (ref LiteWriter w, object v) => w.WriteSByte(v == null ? (sbyte)0 : (sbyte)v);
                    return (ref LiteWriter w, object v) => w.WriteSByte(v == null ? (sbyte)0 : UnboxConvertTo<sbyte>(v));
                case BasicTypes.Byte:
                    if (declaredType == typeof(byte)) return (ref LiteWriter w, object v) => w.WriteByte(v == null ? (byte)0 : (byte)v);
                    return (ref LiteWriter w, object v) => w.WriteByte(v == null ? (byte)0 : UnboxConvertTo<byte>(v));
                case BasicTypes.Int16:
                    if (declaredType == typeof(short)) return (ref LiteWriter w, object v) => w.WriteInt16(v == null ? (short)0 : (short)v);
                    return (ref LiteWriter w, object v) => w.WriteInt16(v == null ? (short)0 : UnboxConvertTo<short>(v));
                case BasicTypes.UInt16:
                    if (declaredType == typeof(ushort)) return (ref LiteWriter w, object v) => w.WriteUInt16(v == null ? (ushort)0 : (ushort)v);
                    return (ref LiteWriter w, object v) => w.WriteUInt16(v == null ? (ushort)0 : UnboxConvertTo<ushort>(v));
                case BasicTypes.Int32:
                    if (declaredType == typeof(int)) return (ref LiteWriter w, object v) => w.WriteInt32(v == null ? 0 : (int)v);
                    return (ref LiteWriter w, object v) => w.WriteInt32(v == null ? 0 : UnboxConvertTo<int>(v));
                case BasicTypes.UInt32:
                    if (declaredType == typeof(uint)) return (ref LiteWriter w, object v) => w.WriteUInt32(v == null ? 0u : (uint)v);
                    return (ref LiteWriter w, object v) => w.WriteUInt32(v == null ? 0u : UnboxConvertTo<uint>(v));
                case BasicTypes.Int64:
                    if (declaredType == typeof(long)) return (ref LiteWriter w, object v) => w.WriteInt64(v == null ? 0L : (long)v);
                    return (ref LiteWriter w, object v) => w.WriteInt64(v == null ? 0L : UnboxConvertTo<long>(v));
                case BasicTypes.UInt64:
                    if (declaredType == typeof(ulong)) return (ref LiteWriter w, object v) => w.WriteUInt64(v == null ? 0UL : (ulong)v);
                    return (ref LiteWriter w, object v) => w.WriteUInt64(v == null ? 0UL : UnboxConvertTo<ulong>(v));
                case BasicTypes.Single:
                    if (declaredType == typeof(float)) return (ref LiteWriter w, object v) => w.WriteSingle(v == null ? 0f : (float)v);
                    return (ref LiteWriter w, object v) => w.WriteSingle(v == null ? 0f : UnboxConvertTo<float>(v));
                case BasicTypes.Double:
                    if (declaredType == typeof(double)) return (ref LiteWriter w, object v) => w.WriteDouble(v == null ? 0d : (double)v);
                    return (ref LiteWriter w, object v) => w.WriteDouble(v == null ? 0d : UnboxConvertTo<double>(v));
                case BasicTypes.Decimal:
                    if (declaredType == typeof(decimal)) return (ref LiteWriter w, object v) => w.WriteDecimal(v == null ? 0m : (decimal)v);
                    return (ref LiteWriter w, object v) => w.WriteDecimal(v == null ? 0m : UnboxConvertTo<decimal>(v));
                case BasicTypes.String:
                    //strings are nullable on the wire; non-strings stringify like the walk did
                    return (ref LiteWriter w, object v) => w.WriteString(v?.ToString());
                case BasicTypes.DateTime:
                    return (ref LiteWriter w, object v) => w.WriteDate(v == null ? DateTime.Now : (DateTime)v);
                case BasicTypes.Guid:
                    return (ref LiteWriter w, object v) => w.WriteGuid(v == null ? Guid.Empty : (Guid)v);
                default:
                    throw new Exception("Unknown basic type: " + basicType);
            }
        }

        private WritePlanDelegate BuildObjectWritePlan(TypeMap typeMap, Type runtimeType)
        {
            var getters = new Func<object, object>[typeMap.properties.Length];
            var writers = new WritePlanDelegate[typeMap.properties.Length];

            for (int i = 0; i < typeMap.properties.Length; i++)
            {
                var map = typeMap.properties[i];
                var property = runtimeType.GetProperty(_propertyMap[(int)map.propertyIdx]);
                var declared = property?.PropertyType;

                if (map.storageType == StorageType.Array || map.storageType == StorageType.List)
                {
                    writers[i] = BuildCollectionWritePlan(map.typeIdx, declared);
                }
                else if (map.storageType == StorageType.Nullable)
                {
                    var underlying = declared == null ? null : Nullable.GetUnderlyingType(declared) ?? declared;
                    var inner = BuildSlotWritePlan(map.typeIdx, underlying);
                    writers[i] = (ref LiteWriter w, object v) =>
                    {
                        if (v == null) { w.WriteByte(0); return; }
                        w.WriteByte(1);
                        inner(ref w, v);
                    };
                }
                else
                {
                    writers[i] = BuildSlotWritePlan(map.typeIdx, declared);
                }

                //a missing property writes its slot as null/default, same as the walk
                getters[i] = property == null ? null : CompileGetter(runtimeType, property);
            }

            return (ref LiteWriter w, object v) =>
            {
                //null still reaches this plan through sealed-type slots resolved at build time
                if (v == null)
                {
                    w.WriteByte(0);
                    return;
                }

                w.WriteByte(1);
                for (int i = 0; i < writers.Length; i++)
                {
                    var propertyValue = getters[i]?.Invoke(v);
                    writers[i](ref w, propertyValue);
                }
            };
        }

        private WritePlanDelegate BuildCollectionWritePlan(uint elementIdx, Type declaredCollectionType)
        {
            Type elementType = null;
            if (declaredCollectionType != null)
            {
                if (declaredCollectionType.IsArray)
                    elementType = declaredCollectionType.GetElementType();
                else if (declaredCollectionType.GenericTypeArguments.Length == 1)
                    elementType = declaredCollectionType.GenericTypeArguments[0];
            }

            var elementPlan = BuildSlotWritePlan(elementIdx, elementType);
            return (ref LiteWriter w, object v) =>
            {
                if (v == null) { w.WriteLength(ulong.MaxValue); return; }
                var collection = (ICollection)v;
                w.WriteLength((ulong)collection.Count);
                foreach (var item in collection)
                    elementPlan(ref w, item);
            };
        }

        private WritePlanDelegate BuildDictionaryWritePlan(uint keyIdx, uint valueIdx, Type declaredDictionaryType)
        {
            Type keyType = null, valueType = null;
            var args = declaredDictionaryType?.GenericTypeArguments;
            if (args != null && args.Length == 2)
            {
                keyType = args[0];
                valueType = args[1];
            }

            var keyPlan = BuildSlotWritePlan(keyIdx, keyType);
            var valuePlan = BuildSlotWritePlan(valueIdx, valueType);
            return (ref LiteWriter w, object v) =>
            {
                if (v == null) { w.WriteLength(ulong.MaxValue); return; }
                var dict = (IDictionary)v;
                w.WriteLength((ulong)dict.Count);
                foreach (DictionaryEntry entry in dict)
                {
                    keyPlan(ref w, entry.Key);
                    valuePlan(ref w, entry.Value);
                }
            };
        }

        private WritePlanDelegate BuildTupleWritePlan(uint[] elementIdxs, Type tupleType)
        {
            var argTypes = tupleType.GenericTypeArguments;
            var plans = new WritePlanDelegate[elementIdxs.Length];
            for (int i = 0; i < elementIdxs.Length; i++)
                plans[i] = BuildSlotWritePlan(elementIdxs[i], i < argTypes.Length ? argTypes[i] : null);

            return (ref LiteWriter w, object v) =>
            {
                var tuple = (ITuple)v;
                for (int i = 0; i < plans.Length; i++)
                    plans[i](ref w, tuple[i]);
            };
        }

        private static Func<object> CompileFactory(Type type)
        {
            var ctor = type.GetConstructor(Type.EmptyTypes);
            if (ctor == null)
                return () => Activator.CreateInstance(type); //structs and exotic types, parity with the walk
            return Expression.Lambda<Func<object>>(Expression.Convert(Expression.New(ctor), typeof(object))).Compile();
        }

        private static Func<object[], object> CompileTupleFactory(Type tupleType, Type[] argTypes, int elementCount)
        {
            var ctor = argTypes.Length == elementCount ? tupleType.GetConstructor(argTypes) : null;
            if (ctor == null)
                return objs => Activator.CreateInstance(tupleType, objs); //e.g. tuples with a TRest tail

            var args = Expression.Parameter(typeof(object[]), "args");
            var call = Expression.New(ctor, argTypes.Select((t, i) =>
                (Expression)Expression.Convert(Expression.ArrayIndex(args, Expression.Constant(i)), t)));
            var body = Expression.Convert(call, typeof(object));
            return Expression.Lambda<Func<object[], object>>(body, args).Compile();
        }

        private Action<object, object> CompileSetter(Type ownerType, PropertyInfo property, Type valueType)
        {
            var propType = property.PropertyType;
            var direct = valueType != null &&
                (propType == valueType
                 || Nullable.GetUnderlyingType(propType) == valueType
                 || propType.IsAssignableFrom(valueType));

            //cross-version width mismatches (e.g. the server sends short where the property is int)
            //keep the walk's Convert.ChangeType behavior; struct owners must mutate the box, which
            //only reflection does. Both decisions are made once per plan, not per call.
            if (!direct || property.SetMethod == null || ownerType.IsValueType)
                return (target, value) => SetPropertyValue(property, target, value);

            var target = Expression.Parameter(typeof(object), "target");
            var value = Expression.Parameter(typeof(object), "value");
            var assign = Expression.Assign(
                Expression.Property(Expression.Convert(target, ownerType), property),
                Expression.Convert(value, propType));
            return Expression.Lambda<Action<object, object>>(assign, target, value).Compile();
        }

        private static Func<object, object> CompileGetter(Type ownerType, PropertyInfo property)
        {
            if (property.GetMethod == null)
                return _ => null;
            if (ownerType.IsValueType)
                return target => property.GetValue(target); //unbox-copy semantics match the walk

            var target = Expression.Parameter(typeof(object), "target");
            var body = Expression.Convert(
                Expression.Property(Expression.Convert(target, ownerType), property),
                typeof(object));
            return Expression.Lambda<Func<object, object>>(body, target).Compile();
        }

        //--- generated serializer binding -------------------------------------------------------
        //Client assemblies marked [assembly: RpcClientModels] register typed generated serializers
        //plus a structural fingerprint per model. When the same fingerprint computed from this
        //connection's handshake matches, the generated code is provably byte-compatible with the
        //server's layout and replaces the composed plan for that model (and everything it nests).

        private readonly Dictionary<uint, string> _typeFingerprints = new Dictionary<uint, string>();

        //wrappers are per model type and process-wide: the generated functions are static
        private static readonly ConcurrentDictionary<Type, ReadPlanDelegate> _generatedReadWrappers
            = new ConcurrentDictionary<Type, ReadPlanDelegate>();
        private static readonly ConcurrentDictionary<Type, WritePlanDelegate> _generatedWriteWrappers
            = new ConcurrentDictionary<Type, WritePlanDelegate>();

        private ReadPlanDelegate TryGetGeneratedReadPlan(uint typeIdx, Type modelType)
        {
            if (!EnableGeneratedSerializers || modelType == null)
                return null;
            if (!GeneratedClientSerializers.TryGetFingerprint(modelType, out var expected)
                || GetTypeFingerprint(typeIdx) != expected)
                return null;

            GeneratedSerializerBindings++;
            return _generatedReadWrappers.GetOrAdd(modelType, static type =>
                (ReadPlanDelegate)typeof(RpcClient)
                    .GetMethod(nameof(WrapGeneratedRead), BindingFlags.NonPublic | BindingFlags.Static)
                    .MakeGenericMethod(type)
                    .Invoke(null, null));
        }

        private WritePlanDelegate TryGetGeneratedWritePlan(uint typeIdx, Type modelType)
        {
            if (!EnableGeneratedSerializers || modelType == null)
                return null;
            if (!GeneratedClientSerializers.TryGetFingerprint(modelType, out var expected)
                || GetTypeFingerprint(typeIdx) != expected)
                return null;

            GeneratedSerializerBindings++;
            return _generatedWriteWrappers.GetOrAdd(modelType, static type =>
                (WritePlanDelegate)typeof(RpcClient)
                    .GetMethod(nameof(WrapGeneratedWrite), BindingFlags.NonPublic | BindingFlags.Static)
                    .MakeGenericMethod(type)
                    .Invoke(null, null));
        }

        //a ref-struct reader/writer can't flow through MethodInfo.Invoke, so a typed delegate is
        //created once per model type and cached (same pattern as LiteSerializer.SerializeObject)
        private static ReadPlanDelegate WrapGeneratedRead<T>()
        {
            var read = LiteSerializer.GetReaderDelegate<T>();
            return (ref LiteReader r) => read(ref r);
        }

        private static WritePlanDelegate WrapGeneratedWrite<T>()
        {
            var write = LiteSerializer.GetWriterDelegate<T>();
            //the generated writer handles null itself (it writes the 0 marker), default(T) covers it
            return (ref LiteWriter w, object v) => write(ref w, v is T typed ? typed : default);
        }

        /// <summary>
        /// Canonical structural fingerprint of a handshake type entry. The generator computes the
        /// same encoding from its compile-time type graph (see RPCGenerator.AppendFingerprint);
        /// both sides must stay in step. Cycles encode as a back-reference to the position of the
        /// revisited entry in the current path.
        /// </summary>
        private string GetTypeFingerprint(uint typeIdx)
        {
            if (_typeFingerprints.TryGetValue(typeIdx, out var cached))
                return cached;

            var sb = new System.Text.StringBuilder();
            AppendFingerprint(sb, typeIdx, new List<uint>());
            var fingerprint = sb.ToString();
            _typeFingerprints[typeIdx] = fingerprint;
            return fingerprint;
        }

        private void AppendFingerprint(System.Text.StringBuilder sb, uint typeIdx, List<uint> path)
        {
            var typeMap = _typeMaps[(int)typeIdx];
            if (typeMap.basicType != BasicTypes.Object)
            {
                sb.Append('b').Append((int)typeMap.basicType);
                return;
            }

            var backRef = path.IndexOf(typeIdx);
            if (backRef >= 0)
            {
                sb.Append('#').Append(backRef);
                return;
            }

            path.Add(typeIdx);
            switch (typeMap.specialType)
            {
                case SpecialType.Dictionary:
                    sb.Append("d[");
                    AppendFingerprint(sb, typeMap.types[0], path);
                    sb.Append(',');
                    AppendFingerprint(sb, typeMap.types[1], path);
                    sb.Append(']');
                    break;
                case SpecialType.Collection:
                    sb.Append("c[");
                    AppendFingerprint(sb, typeMap.types[0], path);
                    sb.Append(']');
                    break;
                case SpecialType.Tuple:
                    sb.Append("t[");
                    for (int i = 0; i < typeMap.types.Length; i++)
                    {
                        if (i > 0)
                            sb.Append(',');
                        AppendFingerprint(sb, typeMap.types[i], path);
                    }
                    sb.Append(']');
                    break;
                case SpecialType.Nullable:
                    sb.Append("n[");
                    AppendFingerprint(sb, typeMap.types[0], path);
                    sb.Append(']');
                    break;
                default:
                    sb.Append("o{");
                    for (int i = 0; i < typeMap.properties.Length; i++)
                    {
                        var property = typeMap.properties[i];
                        if (i > 0)
                            sb.Append(';');
                        sb.Append(_propertyMap[(int)property.propertyIdx]).Append('=');
                        if (property.storageType == StorageType.Array || property.storageType == StorageType.List)
                        {
                            sb.Append("c[");
                            AppendFingerprint(sb, property.typeIdx, path);
                            sb.Append(']');
                        }
                        else if (property.storageType == StorageType.Nullable)
                        {
                            sb.Append("n[");
                            AppendFingerprint(sb, property.typeIdx, path);
                            sb.Append(']');
                        }
                        else
                        {
                            AppendFingerprint(sb, property.typeIdx, path);
                        }
                    }
                    sb.Append('}');
                    break;
            }
            path.RemoveAt(path.Count - 1);
        }

        #endregion

        private readonly ulong[] _bitmasks = new ulong[]
       {
            0x0000000000000001,
            0x0000000000000002,
            0x0000000000000004,
            0x0000000000000008,
            0x0000000000000010,
            0x0000000000000020,
            0x0000000000000040,
            0x0000000000000080,
            0x0000000000000100,
            0x0000000000000200,
            0x0000000000000400,
            0x0000000000000800,
            0x0000000000001000,
            0x0000000000002000,
            0x0000000000004000,
            0x0000000000008000,
            0x0000000000010000,
            0x0000000000020000,
            0x0000000000040000,
            0x0000000000080000,
            0x0000000000100000,
            0x0000000000200000,
            0x0000000000400000,
            0x0000000000800000,
            0x0000000001000000,
            0x0000000002000000,
            0x0000000004000000,
            0x0000000008000000,
            0x0000000010000000,
            0x0000000020000000,
            0x0000000040000000,
            0x0000000080000000,
            0x0000000100000000,
            0x0000000200000000,
            0x0000000400000000,
            0x0000000800000000,
            0x0000001000000000,
            0x0000002000000000,
            0x0000004000000000,
            0x0000008000000000,
            0x0000010000000000,
            0x0000020000000000,
            0x0000040000000000,
            0x0000080000000000,
            0x0000100000000000,
            0x0000200000000000,
            0x0000400000000000,
            0x0000800000000000,
            0x0001000000000000,
            0x0002000000000000,
            0x0004000000000000,
            0x0008000000000000,
            0x0010000000000000,
            0x0020000000000000,
            0x0040000000000000,
            0x0080000000000000,
            0x0100000000000000,
            0x0200000000000000,
            0x0400000000000000,
            0x0800000000000000,
            0x1000000000000000,
            0x2000000000000000,
            0x4000000000000000,
            0x8000000000000000
       };
    }

    enum StorageType
    {
        Normal = 0,
        Array = 1,
        List = 2,
        Dictionary = 3,
        Nullable = 4,
        Tuple = 5
    }

    enum SpecialType
    {
        Normal = 0,
        Dictionary = 1,
        Collection = 2,
        Tuple = 3,
        Nullable = 4
    }

    class TypeMap
    {
        public SpecialType specialType = SpecialType.Normal;
        public uint[] types;
        public BasicTypes basicType;
        public PropertyMap[] properties;
    }

    class PropertyMap
    {
        public StorageType storageType;
        public uint propertyIdx;
        public uint typeIdx;
    }

    class FunctionMap
    {
        public ushort opcode;
        public uint? argType;
        public uint? retType;
}

    /// <summary>one entry of the server's API description, retrieved via RpcClient.GetApiInfo</summary>
    public class RpcApiFunctionInfo
    {
        /// <summary>the RPC name as invoked, e.g. "FooProcedures.ReturnTask"</summary>
        public string Name { get; set; }
        public string ArgType { get; set; }
        public string ReturnType { get; set; }
    }

    enum BasicTypes
    {
        Bool = 0,
        SByte,
        Byte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        Decimal,
        String,
        DateTime,
        Guid,
        Object
    }

    internal class RpcDynamicObject : DynamicObject
    {
        private RpcClient _client;

        private string _prefix;

        public RpcDynamicObject(RpcClient client, string prefix = null)
        {
            _client = client;   
            _prefix = prefix;
        }

        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            result = new RpcDynamicObject(_client, _prefix + binder.Name + ".");
            return true;
        }

        public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result)
        {
            var methodName = _prefix + binder.Name; 
            var csharpBinder = binder.GetType().GetInterface("Microsoft.CSharp.RuntimeBinder.ICSharpInvokeOrInvokeMemberBinder");
            var typeArgs = (csharpBinder.GetProperty("TypeArguments").GetValue(binder, null) as IList<Type>);
            if (typeArgs != null && typeArgs.Count > 0) //(args.Length > 0 && args[0] is Type genericType)
            {
                var genericType = typeArgs[0];
                if(args.Length > 0)
                {
                    var argument = args[0];
                    MethodInfo method = typeof(RpcClient).GetMethod("Send").MakeGenericMethod(genericType);
                    result = method.Invoke(_client, new object[] { methodName, argument });
                }
                else
                {
                    MethodInfo method = typeof(RpcClient).GetMethod("Send").MakeGenericMethod(genericType);
                    result = method.Invoke(_client, new object[] { methodName, null });
                }
            }
            else
            {
                if (args.Length > 0)
                {
                    var argument = args[0];
                    MethodInfo method = typeof(RpcClient).GetMethod("Send").MakeGenericMethod(typeof(object));
                    result = method.Invoke(_client, new object[] { methodName, argument });
                }
                else
                {
                    MethodInfo method = typeof(RpcClient).GetMethod("Send").MakeGenericMethod(typeof(object));
                    result = method.Invoke(_client, new object[] { methodName, null });
                }
            }
            return true;
        }
    }
}