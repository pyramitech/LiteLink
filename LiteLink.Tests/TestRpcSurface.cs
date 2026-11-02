using LiteLink;

namespace LiteLink.Tests
{
    /// <summary>
    /// The RPC surface the generator compiles for this test assembly. The generated
    /// [ModuleInitializer] constructs the RpcServer singleton when the assembly loads,
    /// so integration tests run against the real generated serializers and procedure table.
    /// </summary>
    public class TestLink : RpcLink
    {
        public int Counter;
    }

    public enum TestPermissions
    {
        Admin = 0,
        Viewer = 1
    }

    [RpcServer<TestLink>(UseCompression = true, PermissionGroups = typeof(TestPermissions), ShowApi = true, ApiPassword = "vault")]
    [RpcGroup("Test")]
    public static class TestProcedures
    {
        [RpcFunc("EchoModel")]
        public static ComplexModel EchoModel(TestLink link, ComplexModel model) => model;

        [RpcFunc("EchoInts")]
        public static List<int> EchoInts(TestLink link, List<int> numbers) => numbers;

        [RpcFunc("EchoDict")]
        public static Dictionary<int, string> EchoDict(TestLink link, Dictionary<int, string> map) => map;

        [RpcFunc("EchoTuple")]
        public static (int, string) EchoTuple(TestLink link, (int, string) tuple) => tuple;

        [RpcFunc("EchoNullable")]
        public static int? EchoNullable(TestLink link, int? value) => value;

        [RpcFunc("BigEcho")]
        public static string BigEcho(TestLink link, string payload) => payload;

        //per-function override: never compressed even though the server sets UseCompression = true,
        //the pattern for responses that carry already-compressed data
        [RpcFunc("BigEchoRaw", Compression = RpcCompression.Disabled)]
        public static string BigEchoRaw(TestLink link, string payload) => payload;

        [RpcFunc("Slow")]
        public static async Task<int> Slow(TestLink link)
        {
            await Task.Delay(100);
            return 42;
        }

        [RpcFunc("VoidNoArg")]
        public static void VoidNoArg(TestLink link)
        {
            link.Counter++;
        }

        [RpcFunc("Throws")]
        public static int Throws(TestLink link) => throw new InvalidOperationException("boom");

        [RpcFunc("Guarded", PermissionGroups = new[] { "Admin" })]
        public static int Guarded(TestLink link) => 7;

        [RpcFunc("Push")]
        public static void Push(TestLink link, string message)
        {
            link.Send("TestPush", new PushModel { Message = message });
        }

        [RpcFunc("WhoAmI")]
        public static string WhoAmI(TestLink link) => RpcCallContext.Current?.FunctionName;

        //a procedure that awaits the client's reply mid-call: at a framed-transport parallelism
        //limit of 1 this only completes because invoke responses (type 3) bypass the request gate
        [RpcFunc("AskClient")]
        public static async Task<string> AskClient(TestLink link)
        {
            return await link.Invoke<string>("Client.Echo", "from-server");
        }

        [RpcFunc("EchoSizedEnums")]
        public static SizedEnumModel EchoSizedEnums(TestLink link, SizedEnumModel model) => model;

        [RpcFunc("EchoByteEnum")]
        public static ByteEnum EchoByteEnum(TestLink link, ByteEnum value) => value;

        [RpcFunc("EchoEnumList")]
        public static List<ShortEnum> EchoEnumList(TestLink link, List<ShortEnum> values) => values;

        //nullables as collection elements exercise the runtime WriteNullable/ReadNullable fallback,
        //the generated inline nullable path only covers model properties and bare arguments
        [RpcFunc("EchoNullableList")]
        public static async Task<List<int?>> EchoNullableList(TestLink link, List<int?> values)
        {
            await Task.Delay(10); //async so the response is written from a continuation, like WebTest's ReturnTaskWithValueNoArg
            return values;
        }
    }

    public class ComplexModel
    {
        public int? Id { get; set; }
        public string Name { get; set; }
        public double Value { get; set; }
        public TestEnum Kind { get; set; }
        public List<int> Numbers { get; set; } = new List<int>();
        public List<ChildModel> Children { get; set; } = new List<ChildModel>();
        public Dictionary<int, ChildModel> ChildMap { get; set; } = new Dictionary<int, ChildModel>();
        public List<(int, ChildModel)> TupleList { get; set; } = new List<(int, ChildModel)>();
        public Guid Uid { get; set; }
        public DateTime When { get; set; }
        public decimal Price { get; set; }
    }

    public class ChildModel
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class PushModel
    {
        public string Message { get; set; }
    }

    public enum TestEnum
    {
        Alpha = 0,
        Beta = 1
    }

    //enums of every underlying size: serialized at their native width, not int32
    public enum ByteEnum : byte { Zero = 0, Max = 255 }
    public enum SByteEnum : sbyte { Neg = -100 }
    public enum ShortEnum : short { Neg = -12345 }
    public enum UShortEnum : ushort { Big = 65000 }
    public enum UIntEnum : uint { Big = 4000000000 } //doesn't fit in int32
    public enum LongEnum : long { Huge = 123456789012345 } //doesn't fit in int32
    public enum ULongEnum : ulong { Huge = 18000000000000000000 }

    /// <summary>exercises every enum size in every storage position</summary>
    public class SizedEnumModel
    {
        public ByteEnum B { get; set; }
        public SByteEnum SB { get; set; }
        public ShortEnum S { get; set; }
        public UShortEnum US { get; set; }
        public TestEnum I { get; set; }
        public UIntEnum UI { get; set; }
        public LongEnum L { get; set; }
        public ULongEnum UL { get; set; }
        public ByteEnum? MaybeSet { get; set; }
        public LongEnum? MaybeNull { get; set; }
        public List<ByteEnum> ByteList { get; set; }
        public Dictionary<ShortEnum, string> ShortMap { get; set; }
        public (ByteEnum, LongEnum) Pair { get; set; }
    }
}
