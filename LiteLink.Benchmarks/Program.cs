extern alias server;

using System.Diagnostics;
using LiteLink.Client;

//client-mode generation: typed serializers for the models used by the Send<T> calls below,
//so the "generated" benchmark rows exercise the same fast path a real client app gets
[assembly: LiteLink.RpcClientModels]

namespace LiteLink.Benchmarks
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
        public Task<byte[]> Invoke(byte[] data) => _server.Receive(ServerSide, data);
        public Task Terminate()
        {
            _server.RemoveConnection(ServerSide);
            return Task.CompletedTask;
        }
        public void DeliverPush(byte[] data) => Client?.Receive(data);
    }

    public static class Program
    {
        public static async Task Main(string[] args)
        {
            //touching a model type loads the host assembly, which runs the generated server initializer
            _ = typeof(BenchModel);

            var model = MakeModel();
            var ints = Enumerable.Range(0, 1000).ToList();
            var dict = Enumerable.Range(0, 100).ToDictionary(i => i, i => "value-" + i);

            //two full passes: the first warms tiered JIT across every mode so the second pass
            //compares them on equal footing; only the second pass is printed
            for (int pass = 0; pass < 2; pass++)
            {
                var print = pass == 1;
                if (print)
                    Console.WriteLine($"{"benchmark",-36}{"us/op",12}{"B/op",14}");

                foreach (var (label, cache, generated) in new[]
                {
                    ("walk", false, false),
                    ("plan", true, false),
                    ("generated", true, true),
                })
                {
                    //fresh client per mode: the plan cache is per connection, reusing one client
                    //would let a previous mode's plans shadow the mode under test
                    var connection = new InMemoryClientConnection(server::LiteLink.RpcServer.Instance);
                    var rpc = new RpcClient(connection);
                    connection.Client = rpc;
                    rpc.EnableSerializationCache = cache;
                    rpc.EnableGeneratedSerializers = generated;
                    await rpc.Start();

                    await Bench(print, $"EchoInt [{label}]", 50_000, () => rpc.Send<int>("Bench.EchoInt", 42));
                    await Bench(print, $"EchoModel [{label}]", 10_000, () => rpc.Send<BenchModel>("Bench.EchoModel", model));
                    await Bench(print, $"EchoInts (1000) [{label}]", 2_000, () => rpc.Send<List<int>>("Bench.EchoInts", ints));
                    await Bench(print, $"EchoDict (100) [{label}]", 5_000, () => rpc.Send<Dictionary<int, string>>("Bench.EchoDict", dict));

                    if (print && generated)
                        Console.WriteLine($"generated serializer bindings: {rpc.GeneratedSerializerBindings}");

                    await connection.Terminate();
                }
            }
        }

        private static async Task Bench<T>(bool print, string name, int iterations, Func<Task<T>> op)
        {
            for (int i = 0; i < Math.Max(iterations / 10, 100); i++)
                await op();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                await op();
            sw.Stop();
            var alloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

            if (print)
                Console.WriteLine($"{name,-36}{sw.Elapsed.TotalMicroseconds / iterations,12:F2}{(double)alloc / iterations,14:F0}");
        }

        private static BenchModel MakeModel() => new BenchModel
        {
            Id = 123,
            Name = "hello benchmark",
            Value = -2.75,
            Kind = BenchEnum.Beta,
            Numbers = new List<int> { 1, 2, 3, 4, 5, 6, 7, 8 },
            Children = new List<BenchChild>
            {
                new BenchChild { Id = 1, Name = "a" },
                new BenchChild { Id = 2, Name = "b" },
                new BenchChild { Id = 3, Name = null },
            },
            ChildMap = new Dictionary<int, BenchChild>
            {
                { 5, new BenchChild { Id = 5, Name = "five" } },
                { 6, new BenchChild { Id = 6, Name = "six" } },
            },
            Pair = (9, "nine"),
            Uid = Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10"),
            When = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Price = 19.95m,
        };
    }
}
