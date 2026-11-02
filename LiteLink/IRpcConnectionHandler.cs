using LiteLink.Shared;

namespace LiteLink
{
    /// <summary>
    /// Dispatch target for the socket transports (TCP/QUIC). By default they route into the
    /// process-wide <see cref="RpcServer.Instance"/> via <see cref="RpcServerConnectionHandler"/>;
    /// setting a custom handler on the transport options replaces that entirely — this is how
    /// RpcGateway intercepts traffic without needing a generated RpcServer in its process.
    /// </summary>
    public interface IRpcConnectionHandler
    {
        /// <summary>when true, new connections are refused</summary>
        bool IsShuttingDown { get; }

        void OnConnected(IConnection connection);

        void OnDisconnected(IConnection connection);

        /// <summary>handles one raw client message and returns the raw response</summary>
        Task<byte[]> Receive(IConnection connection, byte[] data);
    }

    /// <summary>default transport handler: routes into RpcServer.Instance, including the ProxyFunction hook</summary>
    public sealed class RpcServerConnectionHandler : IRpcConnectionHandler
    {
        public bool IsShuttingDown => RpcServer.Instance.IsShuttingDown;

        public void OnConnected(IConnection connection) => RpcServer.Instance.AddConnection(connection);

        public void OnDisconnected(IConnection connection) => RpcServer.Instance.RemoveConnection(connection);

        public Task<byte[]> Receive(IConnection connection, byte[] data)
        {
            if (RpcServer.Instance.ProxyFunction != null)
                return RpcServer.Instance.ProxyFunction(data);
            return RpcServer.Instance.Receive(connection, data);
        }
    }
}
