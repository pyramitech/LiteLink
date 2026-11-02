extern alias client;

using LiteLink.Shared;

namespace LiteLink.Tests
{
    /// <summary>
    /// The server-facing half of an in-memory transport: what RpcServer stores per link.
    /// Invoke here means "push to the client", mirroring SignalRHubConnection (fire-and-forget, null result).
    /// </summary>
    public class InMemoryServerConnection : IConnection
    {
        private readonly InMemoryClientConnection _clientSide;

        public InMemoryServerConnection(InMemoryClientConnection clientSide)
        {
            _clientSide = clientSide;
        }

        public bool IsConnected => true;
        public bool IsConnecting => false;

        public Task Connect() => throw new NotSupportedException("This function is only supported on the client side");

        public Task<byte[]> Invoke(byte[] data)
        {
            _clientSide.DeliverPush(data);
            return Task.FromResult<byte[]>(null);
        }

        public Task Terminate() => Task.CompletedTask;
    }

    /// <summary>
    /// The client-facing half: what RpcClient uses. Invoke here means "send a request and
    /// await the response", forwarded straight into RpcServer.Receive with no transport.
    /// </summary>
    public class InMemoryClientConnection : client::LiteLink.Shared.IConnection
    {
        private readonly RpcServer _server;

        public client::LiteLink.Client.RpcClient Client { get; set; }

        public InMemoryServerConnection ServerSide { get; }

        /// <summary>observes every raw response, e.g. to assert on the wire-level compression flag</summary>
        public Action<byte[]> ResponseSpy { get; set; }

        /// <summary>observes every raw request, e.g. to assert whether it went out as a batch</summary>
        public Action<byte[]> RequestSpy { get; set; }

        /// <summary>how many transport-level sends occurred, the metric request batching reduces</summary>
        public int InvokeCount;

        public InMemoryClientConnection(RpcServer server)
        {
            _server = server;
            ServerSide = new InMemoryServerConnection(this);
        }

        public bool IsConnected => true;
        public bool IsConnecting => false;

        public Task Connect()
        {
            _server.AddConnection(ServerSide);
            return Task.CompletedTask;
        }

        public async Task<byte[]> Invoke(byte[] data)
        {
            Interlocked.Increment(ref InvokeCount);
            RequestSpy?.Invoke(data);
            var response = await _server.Receive(ServerSide, data);
            ResponseSpy?.Invoke(response);
            return response;
        }

        public Task Terminate()
        {
            _server.RemoveConnection(ServerSide);
            return Task.CompletedTask;
        }

        public void DeliverPush(byte[] data) => Client?.Receive(data);
    }
}
