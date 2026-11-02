extern alias client;

using LiteLink.Pipes;
using LiteLink.Pipes.Client;
using System.Diagnostics;
using Xunit;

namespace LiteLink.Tests
{
    /// <summary>
    /// End-to-end tests over real named pipes (inter-process transport): the same singleton
    /// server, hosted by LiteLinkPipeServer and reached through the framed wire protocol.
    /// Works on Windows and Linux (.NET maps named pipes to Unix domain sockets there).
    /// </summary>
    [Collection("RpcServer")]
    public class PipeTransportTests
    {
        private static async Task<(LiteLinkPipeServer Server, client::LiteLink.Client.RpcClient Client)> StartAsync()
        {
            //unique name per test so parallel/repeated runs never collide
            var pipeName = "litelink-test-" + Guid.NewGuid().ToString("N");

            var server = new LiteLinkPipeServer(new LiteLinkPipeServerOptions { PipeName = pipeName });
            server.Start();

            var rpc = PipeConnection.Create(pipeName);
            await rpc.Start();
            return (server, rpc);
        }

        [Fact]
        public async Task Pipe_ComplexModel_RoundTrips()
        {
            var (server, rpc) = await StartAsync();
            await using (server)
            {
                var sent = new ComplexModel
                {
                    Id = 77,
                    Name = "over pipes ✓",
                    Numbers = new List<int> { 4, 5, 6 },
                    ChildMap = new Dictionary<int, ChildModel> { { 3, new ChildModel { Id = 3, Name = "three" } } },
                    Price = 3.5m,
                };

                var echoed = await rpc.Send<ComplexModel>("Test.EchoModel", sent);

                Assert.Equal(sent.Id, echoed.Id);
                Assert.Equal(sent.Name, echoed.Name);
                Assert.Equal(sent.Numbers, echoed.Numbers);
                Assert.Equal("three", echoed.ChildMap[3].Name);
                Assert.Equal(sent.Price, echoed.Price);
            }
        }

        [Fact]
        public async Task Pipe_BigPayload_CompressedInBothDirections()
        {
            var (server, rpc) = await StartAsync();
            await using (server)
            {
                var payload = new string('p', 20000) + "END";
                Assert.Equal(payload, await rpc.Send<string>("Test.BigEcho", payload));
            }
        }

        [Fact]
        public async Task Pipe_Multichannel_CallsRunConcurrently()
        {
            var (server, rpc) = await StartAsync();
            await using (server)
            {
                var timer = Stopwatch.StartNew();
                var results = await Task.WhenAll(Enumerable.Range(0, 20)
                    .Select(_ => rpc.SendMultiChannelMessage<int>("Test.Slow", null)));
                timer.Stop();

                Assert.All(results, r => Assert.Equal(42, r));
                Assert.True(timer.ElapsedMilliseconds < 1500, $"calls were serialized: {timer.ElapsedMilliseconds}ms");
            }
        }

        [Fact]
        public async Task Pipe_ServerPush_ReachesRegisteredClientFunction()
        {
            var (server, rpc) = await StartAsync();
            await using (server)
            {
                var received = new TaskCompletionSource<PushModel>(TaskCreationOptions.RunContinuationsAsynchronously);
                rpc.RegisterFunction<PushModel>("TestPush", m => received.TrySetResult(m));

                await rpc.Send<object>("Test.Push", "hi over pipes");

                var pushed = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("hi over pipes", pushed.Message);
            }
        }

        [Fact]
        public async Task Pipe_ClientIdentity_IsKernelAssertedAndExposedOnTheLink()
        {
            var (server, rpc) = await StartAsync();
            await using (server)
            {
                var serverInstance = (RpcServer<TestLink>)RpcServer.Instance;
                var link = serverInstance.GetConnections(x => x.Connection is PipeServerConnection).Single();
                var identity = ((PipeServerConnection)link.Connection).ClientIdentity;

                //client and server are this same test process, so the identity must be ours
                Assert.Equal(Environment.UserName, identity.UserName, ignoreCase: true);
                Assert.Equal(Environment.ProcessId, identity.ProcessId);
            }
        }

        [Fact]
        public async Task Pipe_AuthorizeClient_ApprovedClientConnects()
        {
            var pipeName = "litelink-test-" + Guid.NewGuid().ToString("N");
            var server = new LiteLinkPipeServer(new LiteLinkPipeServerOptions
            {
                PipeName = pipeName,
                //an approved-user list would compare identity.UserName / identity.Uid
                AuthorizeClient = identity => Task.FromResult(identity.ProcessId == Environment.ProcessId)
            });
            server.Start();
            await using (server)
            {
                var rpc = PipeConnection.Create(pipeName);
                await rpc.Start();
                Assert.Equal("approved", await rpc.Send<string>("Test.BigEcho", "approved"));
            }
        }

        [Fact]
        public async Task Pipe_AuthorizeClient_RejectedClientIsDroppedBeforeHandshake()
        {
            var pipeName = "litelink-test-" + Guid.NewGuid().ToString("N");
            var server = new LiteLinkPipeServer(new LiteLinkPipeServerOptions
            {
                PipeName = pipeName,
                AuthorizeClient = _ => Task.FromResult(false)
            });
            server.Start();
            await using (server)
            {
                var rpc = PipeConnection.Create(pipeName);
                //the connection is dropped before any message is answered, so Start (handshake) fails
                await Assert.ThrowsAnyAsync<Exception>(() => rpc.Start());
            }
        }

        [Fact]
        public async Task Pipe_ServerInvoke_RoundTripsOverTheSocket()
        {
            var (server, rpc) = await StartAsync();
            await using (server)
            {
                rpc.RegisterFunction<string, string>("Client.PipeEcho", s => "pipe:" + s);

                var serverInstance = (RpcServer<TestLink>)RpcServer.Instance;
                var link = serverInstance.GetConnections(x => x.Connection is PipeServerConnection).Single();

                //request travels as a push frame, the response as a regular request frame
                Assert.Equal("pipe:hello", await link.Invoke<string>("Client.PipeEcho", "hello"));
            }
        }

        [Fact]
        public async Task Pipe_MultipleClients_GetIndependentLinks()
        {
            var (server, rpc1) = await StartAsync();
            await using (server)
            {
                //second client on the same pipe name: the server serves one pipe instance per client
                var rpc2 = PipeConnection.Create(server.PipeName);
                await rpc2.Start();

                var both = await Task.WhenAll(
                    rpc1.Send<string>("Test.BigEcho", "client-one"),
                    rpc2.Send<string>("Test.BigEcho", "client-two"));

                Assert.Equal("client-one", both[0]);
                Assert.Equal("client-two", both[1]);
            }
        }
    }
}
