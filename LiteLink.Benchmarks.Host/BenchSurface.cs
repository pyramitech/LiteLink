using LiteLink;

namespace LiteLink.Benchmarks
{
    public class BenchLink : RpcLink
    {
    }

    /// <summary>
    /// The RPC surface the benchmarks run against. Server-side execution goes through the real
    /// generated serializers; the measurements are dominated by the C# client's paths, which is
    /// what the benchmarks exist to compare.
    /// </summary>
    [RpcServer<BenchLink>]
    [RpcGroup("Bench")]
    public static class BenchProcedures
    {
        [RpcFunc("EchoModel")]
        public static BenchModel EchoModel(BenchLink link, BenchModel model) => model;

        [RpcFunc("EchoInts")]
        public static List<int> EchoInts(BenchLink link, List<int> numbers) => numbers;

        [RpcFunc("EchoInt")]
        public static int EchoInt(BenchLink link, int value) => value;

        [RpcFunc("EchoDict")]
        public static Dictionary<int, string> EchoDict(BenchLink link, Dictionary<int, string> map) => map;
    }

    public class BenchModel
    {
        public int? Id { get; set; }
        public string Name { get; set; }
        public double Value { get; set; }
        public BenchEnum Kind { get; set; }
        public List<int> Numbers { get; set; } = new List<int>();
        public List<BenchChild> Children { get; set; } = new List<BenchChild>();
        public Dictionary<int, BenchChild> ChildMap { get; set; } = new Dictionary<int, BenchChild>();
        public (int, string) Pair { get; set; }
        public Guid Uid { get; set; }
        public DateTime When { get; set; }
        public decimal Price { get; set; }
    }

    public class BenchChild
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public enum BenchEnum : byte
    {
        Alpha = 0,
        Beta = 1
    }
}
