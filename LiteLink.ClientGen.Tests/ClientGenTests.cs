extern alias server;

using LiteLink.Client;
using LiteLink.ClientGen.Tests.Models;
using Xunit;

//this assembly is a pure LiteLink client: the generator collects models from the typed calls in
//the tests below, plus the listed type (only reachable via the dynamic proxy at runtime), and
//emits typed serializers with structural fingerprints
[assembly: LiteLink.RpcClientModels(typeof(CgListedOnly))]

namespace LiteLink.ClientGen.Tests
{
    //in-memory transport pair; the server half lives behind the "server" alias
    public class InMemoryServerConnection : server::LiteLink.Shared.IConnection
    {
        private readonly InMemoryClientConnection _clientSide;
        public InMemoryServerConnection(InMemoryClientConnection clientSide) => _clientSide = clientSide;

        public bool IsConnected => true;
        public bool IsConnecting => false;
        public Task Connect() => throw new NotSupportedException();
        public Task<byte[]> Invoke(byte[] data)
        {
            _clientSide.DeliverPush(data);
            return Task.FromResult<byte[]>(null);
        }
        public Task Terminate() => Task.CompletedTask;
    }

    public class InMemoryClientConnection : LiteLink.Shared.IConnection
    {
        private readonly server::LiteLink.RpcServer _server;
        public RpcClient Client { get; set; }
        public InMemoryServerConnection ServerSide { get; }

        /// <summary>observes every raw request, to compare generated and dynamic bytes</summary>
        public Action<byte[]> RequestSpy { get; set; }

        public InMemoryClientConnection(server::LiteLink.RpcServer rpcServer)
        {
            _server = rpcServer;
            ServerSide = new InMemoryServerConnection(this);
        }

        public bool IsConnected => true;
        public bool IsConnecting => false;
        public Task Connect()
        {
            _server.AddConnection(ServerSide);
            return Task.CompletedTask;
        }
        public Task<byte[]> Invoke(byte[] data)
        {
            RequestSpy?.Invoke(data);
            return _server.Receive(ServerSide, data);
        }
        public Task Terminate()
        {
            _server.RemoveConnection(ServerSide);
            return Task.CompletedTask;
        }
        public void DeliverPush(byte[] data) => Client?.Receive(data);
    }

    /// <summary>serialized: all tests share the process-wide server singleton and the process-wide
    /// generated-serializer registry</summary>
    [CollectionDefinition("CgServer")]
    public class CgServerCollection : ICollectionFixture<CgServerFixture>
    {
    }

    public class CgServerFixture
    {
        public server::LiteLink.RpcServer Server { get; }

        public CgServerFixture()
        {
            server::LiteLink.ClientGen.Tests.Server.ServerHost.EnsureLoaded();
            Server = server::LiteLink.RpcServer.Instance;
        }

        public async Task<(RpcClient Client, InMemoryClientConnection Connection)> CreateClientAsync()
        {
            var connection = new InMemoryClientConnection(Server);
            var rpc = new RpcClient(connection);
            connection.Client = rpc;
            await rpc.Start();
            return (rpc, connection);
        }
    }

    [Collection("CgServer")]
    public class ClientGenTests
    {
        private readonly CgServerFixture _fixture;

        public ClientGenTests(CgServerFixture fixture)
        {
            _fixture = fixture;
        }

        private static CgModel MakeModel() => new CgModel
        {
            Id = 123,
            Name = "hello ✓",
            Value = -2.75,
            Kind = CgEnum.Beta,
            Numbers = new List<int> { 1, 2, 3 },
            Children = new List<CgChild>
            {
                new CgChild { Id = 1, Name = "a", Next = new CgChild { Id = 11, Name = "aa" } },
                new CgChild { Id = 2, Name = null },
            },
            ChildMap = new Dictionary<int, CgChild> { { 5, new CgChild { Id = 5, Name = "five" } } },
            Pair = (9, "nine"),
            Uid = Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10"),
            When = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Price = 19.95m,
            Blob = new byte[] { 1, 2, 3, 4 },
            MaybeKind = CgEnum.Alpha,
        };

        private static void AssertModelEqual(CgModel sent, CgModel echoed)
        {
            Assert.Equal(sent.Id, echoed.Id);
            Assert.Equal(sent.Name, echoed.Name);
            Assert.Equal(sent.Value, echoed.Value);
            Assert.Equal(sent.Kind, echoed.Kind);
            Assert.Equal(sent.Numbers, echoed.Numbers);
            Assert.Equal(sent.Children.Count, echoed.Children.Count);
            Assert.Equal("aa", echoed.Children[0].Next.Name);
            Assert.Null(echoed.Children[1].Name);
            Assert.Equal("five", echoed.ChildMap[5].Name);
            Assert.Equal(sent.Pair, echoed.Pair);
            Assert.Equal(sent.Uid, echoed.Uid);
            Assert.Equal(sent.When, echoed.When);
            Assert.Equal(sent.Price, echoed.Price);
            Assert.Equal(sent.Blob, echoed.Blob);
            Assert.Equal(sent.MaybeKind, echoed.MaybeKind);
        }

        [Fact]
        public async Task GeneratedSerializers_BindAndRoundTrip()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();

            var echoed = await rpc.Send<CgModel>("Cg.EchoModel", MakeModel());

            AssertModelEqual(MakeModel(), echoed);
            //the fingerprints matched, so the model (and everything it nests) ran on generated code
            Assert.True(rpc.GeneratedSerializerBindings > 0, "no generated serializer was bound");
        }

        [Fact]
        public async Task ListedOnlyType_IsRegisteredViaAttribute()
        {
            //CgListedOnly appears in no typed call in this assembly (this call is the dynamic-shaped
            //path), so its serializers exist only because [RpcClientModels] listed it
            Assert.True(GeneratedClientSerializers.TryGetFingerprint(typeof(CgListedOnly), out _),
                "the attribute-listed type was not registered");

            var (rpc, _) = await _fixture.CreateClientAsync();
            var echoed = await rpc.SendCore<CgListedOnly>(
                CalculateHash(rpc, "Cg.EchoListed"), new CgListedOnly { Tag = "via-attribute" });
            Assert.Equal("via-attribute", echoed.Tag);
        }

        [Fact]
        public async Task Generated_Dynamic_AndWalk_ProduceIdenticalRequestBytes()
        {
            var (generated, generatedConnection) = await _fixture.CreateClientAsync();
            var (dynamicPlan, dynamicConnection) = await _fixture.CreateClientAsync();
            var (walk, walkConnection) = await _fixture.CreateClientAsync();
            dynamicPlan.EnableGeneratedSerializers = false;
            walk.EnableSerializationCache = false;

            byte[] generatedBytes = null, planBytes = null, walkBytes = null;
            generatedConnection.RequestSpy = b => generatedBytes = b;
            dynamicConnection.RequestSpy = b => planBytes = b;
            walkConnection.RequestSpy = b => walkBytes = b;

            AssertModelEqual(MakeModel(), await generated.Send<CgModel>("Cg.EchoModel", MakeModel()));
            AssertModelEqual(MakeModel(), await dynamicPlan.Send<CgModel>("Cg.EchoModel", MakeModel()));
            AssertModelEqual(MakeModel(), await walk.Send<CgModel>("Cg.EchoModel", MakeModel()));

            Assert.True(generated.GeneratedSerializerBindings > 0);
            Assert.Equal(0, dynamicPlan.GeneratedSerializerBindings);

            Assert.True(generatedBytes.AsSpan().SequenceEqual(planBytes), "generated and plan bytes differ");
            Assert.True(generatedBytes.AsSpan().SequenceEqual(walkBytes), "generated and walk bytes differ");
        }

        [Fact]
        public async Task FingerprintMismatch_FallsBackToDynamic()
        {
            //fallback is per type: drifting only CgModel would still let the nested CgChild bind its
            //generated serializer, so both are drifted to prove the fully dynamic path
            Assert.True(GeneratedClientSerializers.TryGetFingerprint(typeof(CgModel), out var originalModel));
            Assert.True(GeneratedClientSerializers.TryGetFingerprint(typeof(CgChild), out var originalChild));
            try
            {
                //simulate a structure drift: the registered fingerprints no longer match what the
                //handshake declares, so the client must silently use the dynamic path
                GeneratedClientSerializers.Register(typeof(CgModel), "drifted");
                GeneratedClientSerializers.Register(typeof(CgChild), "drifted");

                var (rpc, _) = await _fixture.CreateClientAsync();
                var echoed = await rpc.Send<CgModel>("Cg.EchoModel", MakeModel());

                AssertModelEqual(MakeModel(), echoed);
                Assert.Equal(0, rpc.GeneratedSerializerBindings);
            }
            finally
            {
                GeneratedClientSerializers.Register(typeof(CgModel), originalModel);
                GeneratedClientSerializers.Register(typeof(CgChild), originalChild);
            }
        }

        private static ulong CalculateHash(RpcClient rpc, string name)
        {
            //SendCore is used instead of the typed Send<T> so the generator's call scan can't see
            //CgListedOnly; the handshake-negotiated hasher is private, reach it via reflection
            var hasher = (Func<string, ulong>)typeof(RpcClient)
                .GetField("_functionHasher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .GetValue(rpc);
            return hasher(name);
        }
    }
}
