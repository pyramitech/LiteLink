extern alias client;

using System.Diagnostics;
using Xunit;

namespace LiteLink.Tests
{
    /// <summary>
    /// Full-stack tests: real generated server + real C# client, wired through an in-memory
    /// connection pair instead of SignalR. Everything the transport would carry as byte[] goes
    /// through the actual serializers, handshake, opcodes and compression.
    /// </summary>
    [Collection("RpcServer")]
    public class IntegrationTests
    {
        private readonly RpcServerFixture _fixture;

        public IntegrationTests(RpcServerFixture fixture)
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

        [Fact]
        public async Task ComplexModel_RoundTrips()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();
            var sent = MakeModel();

            var echoed = await rpc.Send<ComplexModel>("Test.EchoModel", sent);

            Assert.Equal(sent.Id, echoed.Id);
            Assert.Equal(sent.Name, echoed.Name);
            Assert.Equal(sent.Value, echoed.Value);
            Assert.Equal(sent.Kind, echoed.Kind);
            Assert.Equal(sent.Numbers, echoed.Numbers);
            Assert.Equal(sent.Children.Count, echoed.Children.Count);
            Assert.Equal(sent.Children[0].Name, echoed.Children[0].Name);
            Assert.Null(echoed.Children[1].Name);
            Assert.Equal("five", echoed.ChildMap[5].Name);
            Assert.Equal(9, echoed.TupleList[0].Item1);
            Assert.Equal("nine", echoed.TupleList[0].Item2.Name);
            Assert.Equal(sent.Uid, echoed.Uid);
            Assert.Equal(sent.When, echoed.When);
            Assert.Equal(sent.Price, echoed.Price);
        }

        [Fact]
        public async Task Collections_Dictionary_Tuple_Nullable_RoundTrip()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();

            var ints = await rpc.Send<List<int>>("Test.EchoInts", new List<int> { -1, 0, int.MaxValue });
            Assert.Equal(new List<int> { -1, 0, int.MaxValue }, ints);

            var dict = await rpc.Send<Dictionary<int, string>>("Test.EchoDict",
                new Dictionary<int, string> { { 1, "one" }, { 2, null } });
            Assert.Equal("one", dict[1]);
            Assert.Null(dict[2]);

            var tuple = await rpc.Send<(int, string)>("Test.EchoTuple", (77, "seven"));
            Assert.Equal((77, "seven"), tuple);

            Assert.Equal(5, await rpc.Send<int?>("Test.EchoNullable", (int?)5));
            Assert.Null(await rpc.Send<int?>("Test.EchoNullable", null));
        }

        [Fact]
        public async Task NullableCollectionElements_RoundTrip()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();

            //nullable elements resolve their writer/reader at runtime instead of through generated code;
            //this used to throw "Type System.Nullable`1[System.Int32] not marked for serialization"
            var values = new List<int?> { 5, null, int.MinValue };
            var echoed = await rpc.Send<List<int?>>("Test.EchoNullableList", values);

            Assert.Equal(values, echoed);
        }

        [Fact]
        public async Task BigPayload_CompressedInBothDirections()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();
            var payload = new string('x', 20000) + "END";

            //request > 80 bytes is lz4-compressed by the client, response by the server (UseCompression = true)
            var echoed = await rpc.Send<string>("Test.BigEcho", payload);

            Assert.Equal(payload, echoed);
        }

        [Fact]
        public async Task PerFunctionCompression_OverrideDisablesServerWideSetting()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            byte[] lastResponse = null;
            connection.ResponseSpy = bytes => lastResponse = bytes;
            var payload = new string('x', 20000) + "END"; //well above the compression threshold

            //baseline: the server-wide UseCompression = true compresses BigEcho's response
            await rpc.Send<string>("Test.BigEcho", payload);
            Assert.Equal(1, lastResponse[1]); //[result][isCompressed] header
            Assert.True(lastResponse.Length < payload.Length, "baseline response was not compressed");

            //override: Compression = RpcCompression.Disabled ships the same payload uncompressed
            var echoed = await rpc.Send<string>("Test.BigEchoRaw", payload);
            Assert.Equal(payload, echoed);
            Assert.Equal(0, lastResponse[1]);
            Assert.True(lastResponse.Length > payload.Length, "override response was unexpectedly compressed");
        }

        [Fact]
        public async Task BigPayload_Multichannel_CompressedInBothDirections()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();
            var payload = new string('y', 20000) + "END";

            var echoed = await rpc.SendMultiChannelMessage<string>("Test.BigEcho", payload);

            Assert.Equal(payload, echoed);
        }

        [Fact]
        public async Task Multichannel_CallsRunConcurrently()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();

            var timer = Stopwatch.StartNew();
            var results = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(_ => rpc.SendMultiChannelMessage<int>("Test.Slow", null)));
            timer.Stop();

            Assert.All(results, r => Assert.Equal(42, r));
            //20 sequential calls would take >= 2000ms, concurrent ones roughly one delay
            Assert.True(timer.ElapsedMilliseconds < 1500, $"calls were serialized: {timer.ElapsedMilliseconds}ms");
        }

        [Fact]
        public async Task VoidFunction_CompletesAndRunsServerSide()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            var link = _fixture.GetLink(connection);
            var before = link.Counter;

            await rpc.Send<object>("Test.VoidNoArg", null);

            Assert.Equal(before + 1, link.Counter);
        }

        [Fact]
        public async Task ServerException_SurfacesAsUnhandledErrorCode()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();

            var ex = await Assert.ThrowsAsync<Exception>(() => rpc.Send<int>("Test.Throws", null));

            Assert.Contains("255", ex.Message); //ErrorCodes.Unhandled
        }

        [Fact]
        public async Task Permissions_DeniedWithoutGrant_AllowedAfterGrant()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();

            var ex = await Assert.ThrowsAsync<Exception>(() => rpc.Send<int>("Test.Guarded", null));
            Assert.Contains("2", ex.Message); //ErrorCodes.Unauthorized

            _fixture.GetLink(connection).GrantPermission("Admin");

            Assert.Equal(7, await rpc.Send<int>("Test.Guarded", null));
        }

        [Fact]
        public async Task ApiInfo_RequiresPassword_ListsFunctions()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();

            var api = await rpc.GetApiInfo("vault");
            Assert.Contains(api, f => f.Name == "Test.EchoModel");
            Assert.Contains(api, f => f.Name == "Test.Guarded");

            var ex = await Assert.ThrowsAsync<Exception>(() => rpc.GetApiInfo("wrong"));
            Assert.Contains("2", ex.Message); //ErrorCodes.Unauthorized
        }

        [Fact]
        public async Task ServerPush_ReachesRegisteredClientFunction()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();
            var received = new TaskCompletionSource<PushModel>(TaskCreationOptions.RunContinuationsAsynchronously);
            rpc.RegisterFunction<PushModel>("TestPush", m => received.TrySetResult(m));

            await rpc.Send<object>("Test.Push", "hi from test");

            var pushed = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("hi from test", pushed.Message);
        }

        [Fact]
        public async Task Middleware_ObservesStartEnd_AndAmbientContextFlows()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();
            var events = new List<string>();

            _fixture.Server.CallMiddleware = async (call, next) =>
            {
                lock (events) events.Add("start " + call.FunctionName);
                try
                {
                    return await next(call);
                }
                finally
                {
                    lock (events) events.Add("end " + call.FunctionName);
                }
            };

            try
            {
                //WhoAmI returns RpcCallContext.Current?.FunctionName from inside the procedure
                Assert.Equal("Test.WhoAmI", await rpc.Send<string>("Test.WhoAmI", null));
                Assert.Equal(new[] { "start Test.WhoAmI", "end Test.WhoAmI" }, events);

                //multichannel calls flow through the same middleware, flagged as such
                RpcCallContext observed = null;
                _fixture.Server.CallMiddleware = (call, next) =>
                {
                    observed = call;
                    return next(call);
                };
                await rpc.SendMultiChannelMessage<string>("Test.WhoAmI", null);
                Assert.True(observed.IsMultiChannel);
            }
            finally
            {
                _fixture.Server.CallMiddleware = null;
            }

            //without middleware there is no ambient context
            Assert.Null(await rpc.Send<string>("Test.WhoAmI", null));
        }

        [Fact]
        public async Task SizedEnums_RoundTripInEveryPosition()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();
            var sent = new SizedEnumModel
            {
                B = ByteEnum.Max,
                SB = SByteEnum.Neg,
                S = ShortEnum.Neg,
                US = UShortEnum.Big,
                I = TestEnum.Beta,
                UI = UIntEnum.Big,   //value above int.MaxValue, impossible with int32 encoding
                L = LongEnum.Huge,   //value above int.MaxValue, impossible with int32 encoding
                UL = ULongEnum.Huge,
                MaybeSet = ByteEnum.Max,
                MaybeNull = null,
                ByteList = new List<ByteEnum> { ByteEnum.Zero, ByteEnum.Max },
                ShortMap = new Dictionary<ShortEnum, string> { { ShortEnum.Neg, "neg" } },
                Pair = (ByteEnum.Max, LongEnum.Huge),
            };

            var echoed = await rpc.Send<SizedEnumModel>("Test.EchoSizedEnums", sent);

            Assert.Equal(sent.B, echoed.B);
            Assert.Equal(sent.SB, echoed.SB);
            Assert.Equal(sent.S, echoed.S);
            Assert.Equal(sent.US, echoed.US);
            Assert.Equal(sent.I, echoed.I);
            Assert.Equal(sent.UI, echoed.UI);
            Assert.Equal(sent.L, echoed.L);
            Assert.Equal(sent.UL, echoed.UL);
            Assert.Equal(sent.MaybeSet, echoed.MaybeSet);
            Assert.Null(echoed.MaybeNull);
            Assert.Equal(sent.ByteList, echoed.ByteList);
            Assert.Equal("neg", echoed.ShortMap[ShortEnum.Neg]);
            Assert.Equal(sent.Pair, echoed.Pair);
        }

        [Fact]
        public async Task TopLevelEnums_RoundTripAtNativeSize()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();

            Assert.Equal(ByteEnum.Max, await rpc.Send<ByteEnum>("Test.EchoByteEnum", ByteEnum.Max));

            var list = new List<ShortEnum> { ShortEnum.Neg, (ShortEnum)7 };
            Assert.Equal(list, await rpc.Send<List<ShortEnum>>("Test.EchoEnumList", list));
        }

        [Fact]
        public async Task ServerInvoke_ReturnsClientResponse()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            rpc.RegisterFunction<string, string>("Client.Echo", s => "client:" + s);

            var link = _fixture.GetLink(connection);
            Assert.Equal("client:ping", await link.Invoke<string>("Client.Echo", "ping"));
        }

        [Fact]
        public async Task ServerInvoke_AsyncResponder_NoArgument()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            rpc.RegisterFunction<object, PushModel>("Client.Make", async _ =>
            {
                await Task.Delay(10);
                return new PushModel { Message = "made by client" };
            });

            var link = _fixture.GetLink(connection);
            var result = await link.Invoke<PushModel>("Client.Make");
            Assert.Equal("made by client", result.Message);
        }

        [Fact]
        public async Task ServerInvoke_CallsRunConcurrently()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            rpc.RegisterFunction<int, int>("Client.SlowDouble", async n =>
            {
                await Task.Delay(100);
                return n * 2;
            });

            var link = _fixture.GetLink(connection);
            var timer = Stopwatch.StartNew();
            var results = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(i => link.Invoke<int>("Client.SlowDouble", i)));
            timer.Stop();

            for (int i = 0; i < 20; i++)
                Assert.Equal(i * 2, results[i]);
            Assert.True(timer.ElapsedMilliseconds < 1500, $"invokes were serialized: {timer.ElapsedMilliseconds}ms");
        }

        [Fact]
        public async Task ServerInvoke_ResponderThrows_FaultsTheCall()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            //explicit delegate type: a throw-only lambda converts to both the sync and async overload
            rpc.RegisterFunction<string, string>("Client.Boom", (Func<string, string>)(_ => throw new InvalidOperationException("boom")));

            var link = _fixture.GetLink(connection);
            var ex = await Assert.ThrowsAsync<Exception>(() => link.Invoke<string>("Client.Boom", "x"));
            Assert.Contains("responder threw", ex.Message);
        }

        [Fact]
        public async Task ServerInvoke_NoResponder_FailsFast()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();

            var link = _fixture.GetLink(connection);
            //the client answers with "unknown" immediately instead of letting the server hit its timeout
            var ex = await Assert.ThrowsAsync<Exception>(() => link.Invoke<string>("Client.Missing", "x"));
            Assert.Contains("no responder", ex.Message);
        }

        [Fact]
        public async Task SendAndLegacyInvoke_BothStillPush()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            var received = new TaskCompletionSource<PushModel>(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            rpc.RegisterFunction<PushModel>("LegacyPush", m =>
            {
                if (Interlocked.Increment(ref count) == 2)
                    received.TrySetResult(m);
            });

            var link = _fixture.GetLink(connection);
            link.Send("LegacyPush", new PushModel { Message = "one" });
#pragma warning disable CS0618 //the deprecated Invoke must keep working until removed
            link.Invoke("LegacyPush", new PushModel { Message = "two" });
#pragma warning restore CS0618

            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, count);
        }

        [Fact]
        public async Task Middleware_ExceptionStillProducesErrorResponse()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();
            Exception seenByMiddleware = null;

            _fixture.Server.CallMiddleware = async (call, next) =>
            {
                try
                {
                    return await next(call);
                }
                catch (Exception e)
                {
                    seenByMiddleware = e;
                    throw;
                }
            };

            try
            {
                var ex = await Assert.ThrowsAsync<Exception>(() => rpc.Send<int>("Test.Throws", null));
                Assert.Contains("255", ex.Message);
                Assert.IsType<InvalidOperationException>(seenByMiddleware);
            }
            finally
            {
                _fixture.Server.CallMiddleware = null;
            }
        }
    }
}
