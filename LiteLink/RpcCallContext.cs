namespace LiteLink
{
    /// <summary>continues the middleware chain and returns the serialized RPC response</summary>
    public delegate Task<byte[]> RpcCallHandler(RpcCallContext context);

    /// <summary>
    /// Describes a single incoming RPC procedure call. While a call is being processed (and
    /// <see cref="RpcServer.CallMiddleware"/> is set), the call is available anywhere in its async flow
    /// via <see cref="Current"/>, so log entries written from inside procedures can be correlated
    /// with the RPC call they belong to.
    /// </summary>
    public class RpcCallContext
    {
        private static readonly AsyncLocal<RpcCallContext> _current = new AsyncLocal<RpcCallContext>();

        /// <summary>the call flowing through the current async context, null outside of an RPC call</summary>
        public static RpcCallContext Current
        {
            get => _current.Value;
            internal set => _current.Value = value;
        }

        public ushort Opcode { get; internal set; }

        /// <summary>the RPC name as invoked by clients, e.g. "FooProcedures.ReturnTask"</summary>
        public string FunctionName { get; internal set; }

        /// <summary>the link the call arrived on; cast to your TLink type for per-connection state</summary>
        public RpcLink Link { get; internal set; }

        /// <summary>true when the call arrived wrapped in a multichannel request</summary>
        public bool IsMultiChannel { get; internal set; }

        /// <summary>user state that middleware can attach to the call (e.g. a Stopwatch or logging scope)</summary>
        public object Tag { get; set; }
    }
}
