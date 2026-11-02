extern alias client;

using Xunit;

namespace LiteLink.Tests
{
    /// <summary>
    /// Shared fixture for all integration tests. RpcServer is a process-wide singleton
    /// (created by the generated module initializer), so every test that touches server
    /// state must live in this collection — that also serializes them, which keeps
    /// mutations of CallMiddleware and friends safe.
    /// </summary>
    public class RpcServerFixture
    {
        public RpcServer<TestLink> Server { get; }

        public RpcServerFixture()
        {
            Server = (RpcServer<TestLink>)RpcServer.Instance
                ?? throw new InvalidOperationException("generated module initializer did not run");
        }

        /// <summary>creates a connected client with its own in-memory connection pair</summary>
        public async Task<(client::LiteLink.Client.RpcClient Client, InMemoryClientConnection Connection)> CreateClientAsync()
        {
            var connection = new InMemoryClientConnection(Server);
            var rpcClient = new client::LiteLink.Client.RpcClient(connection);
            connection.Client = rpcClient;
            await rpcClient.Start();
            return (rpcClient, connection);
        }

        /// <summary>the server-side link for a client's connection, e.g. to grant permissions</summary>
        public TestLink GetLink(InMemoryClientConnection connection)
        {
            return Server.GetConnections(x => ReferenceEquals(x.Connection, connection.ServerSide)).Single();
        }
    }

    [CollectionDefinition("RpcServer")]
    public class RpcServerCollection : ICollectionFixture<RpcServerFixture>
    {
    }
}
