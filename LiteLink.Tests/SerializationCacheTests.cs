extern alias client;

using Xunit;

namespace LiteLink.Tests
{
    /// <summary>
    /// The C# client's compiled plan cache must be byte-identical to the original walk-per-call
    /// logic: the walk stays available behind EnableSerializationCache = false as the reference
    /// semantics and the escape hatch, and these tests hold the two paths against each other.
    /// </summary>
    [Collection("RpcServer")]
    public class SerializationCacheTests
    {
        private readonly RpcServerFixture _fixture;

        public SerializationCacheTests(RpcServerFixture fixture)
        {
            _fixture = fixture;
        }

        private static ComplexModel MakeModel() => new ComplexModel
        {
            Id = 123,
            Name = "hello ✓",
            Value = -2.75,
            Kind = TestEnum.Beta,
            Numbers = new List<int> { 1, 2, 3 },
            Children = new List<ChildModel> { new ChildModel { Id = 1, Name = "a" }, new ChildModel { Id = 2, Name = null } },
            ChildMap = new Dictionary<int, ChildModel> { { 5, new ChildModel { Id = 5, Name = "five" } } },
            TupleList = new List<(int, ChildModel)> { (9, new ChildModel { Id = 9, Name = "nine" }) },
            Uid = Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10"),
            When = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Price = 19.95m,
        };

        private static SizedEnumModel MakeEnumModel() => new SizedEnumModel
        {
            B = ByteEnum.Max,
            SB = SByteEnum.Neg,
            S = ShortEnum.Neg,
            US = UShortEnum.Big,
            I = TestEnum.Beta,
            UI = UIntEnum.Big,
            L = LongEnum.Huge,
            UL = ULongEnum.Huge,
            MaybeSet = ByteEnum.Zero,
            MaybeNull = null,
            ByteList = new List<ByteEnum> { ByteEnum.Zero, ByteEnum.Max },
            ShortMap = new Dictionary<ShortEnum, string> { { ShortEnum.Neg, "neg" } },
            Pair = (ByteEnum.Max, LongEnum.Huge),
        };

        [Fact]
        public async Task WalkFallback_StillRoundTripsEverything()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();
            rpc.EnableSerializationCache = false;

            var sent = MakeModel();
            var echoed = await rpc.Send<ComplexModel>("Test.EchoModel", sent);
            Assert.Equal(sent.Name, echoed.Name);
            Assert.Equal(sent.Kind, echoed.Kind);
            Assert.Equal("five", echoed.ChildMap[5].Name);
            Assert.Equal(sent.Price, echoed.Price);

            Assert.Equal(ByteEnum.Max, await rpc.Send<ByteEnum>("Test.EchoByteEnum", ByteEnum.Max));
            Assert.Equal(new List<int?> { 5, null }, await rpc.Send<List<int?>>("Test.EchoNullableList", new List<int?> { 5, null }));
            Assert.Null(await rpc.Send<int?>("Test.EchoNullable", null));
        }

        [Fact]
        public async Task PlanAndWalk_ProduceIdenticalRequestBytes()
        {
            var (cached, cachedConnection) = await _fixture.CreateClientAsync();
            var (walked, walkedConnection) = await _fixture.CreateClientAsync();
            walked.EnableSerializationCache = false;

            byte[] cachedRequest = null, walkedRequest = null;
            cachedConnection.RequestSpy = b => cachedRequest = b;
            walkedConnection.RequestSpy = b => walkedRequest = b;

            //runs the same call through both clients, then compares the raw request bytes;
            //awaiting also proves both paths deserialize the same server responses
            async Task Both(Func<client::LiteLink.Client.RpcClient, Task> call, string label)
            {
                await call(cached);
                await call(walked);
                Assert.True(cachedRequest.AsSpan().SequenceEqual(walkedRequest),
                    $"plan and walk produced different request bytes for {label}");
            }

            //every serialization shape in one sweep: models with nested collections/dictionaries/
            //tuples, sized enums in every storage position, bare nullables (set and null)
            await Both(c => c.Send<ComplexModel>("Test.EchoModel", MakeModel()), "EchoModel");
            await Both(c => c.Send<SizedEnumModel>("Test.EchoSizedEnums", MakeEnumModel()), "EchoSizedEnums");
            await Both(c => c.Send<(int, string)>("Test.EchoTuple", (77, "seven")), "EchoTuple");
            await Both(c => c.Send<Dictionary<int, string>>("Test.EchoDict",
                new Dictionary<int, string> { { 1, "one" }, { 2, null } }), "EchoDict");
            await Both(c => c.Send<int?>("Test.EchoNullable", (int?)5), "EchoNullable(5)");
            await Both(c => c.Send<int?>("Test.EchoNullable", null), "EchoNullable(null)");
            await Both(c => c.Send<List<int?>>("Test.EchoNullableList", new List<int?> { 5, null, int.MinValue }), "EchoNullableList");
            await Both(c => c.Send<List<int>>("Test.EchoInts", new List<int> { -1, 0, int.MaxValue }), "EchoInts");
            await Both(c => c.Send<List<ShortEnum>>("Test.EchoEnumList", new List<ShortEnum> { ShortEnum.Neg }), "EchoEnumList");
        }
    }
}
