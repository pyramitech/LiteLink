extern alias client;

using LiteLink.Gateway;
using LiteLink.Shared;
using LiteLink.Shared.Serializer;
using LiteLink.Tcp;
using LiteLink.Tcp.Client;
using System.Diagnostics;
using System.Net;
using Xunit;

namespace LiteLink.Tests
{
    /// <summary>
    /// Full chain over real sockets: client -> (TLS) -> gateway -> (plaintext) -> backend.
    /// The backend is the singleton test server; the gateway is a plain RpcGateway with no
    /// generated code, plugged into a TCP listener via the Handler seam — the TLS-offload topology.
    /// </summary>
    [Collection("RpcServer")]
    public class GatewayTests
    {
        private sealed class Chain : IAsyncDisposable
        {
            public LiteLinkTcpServer Backend;
            public LiteLinkTcpServer GatewayServer;
            public RpcGateway Gateway;
            public client::LiteLink.Client.RpcClient Client;

            public async ValueTask DisposeAsync()
            {
                await Gateway.StopAsync();
                await GatewayServer.StopAsync();
                await Backend.StopAsync();
            }
        }

        private static async Task<Chain> StartAsync(GatewayInterceptor interceptor = null)
        {
            //backend: the generated singleton server, plaintext TCP on the "internal network"
            var backend = new LiteLinkTcpServer(new LiteLinkTcpServerOptions { Address = IPAddress.Loopback });
            backend.Start();

            var gateway = new RpcGateway(new RpcGatewayOptions
            {
                BackendHost = "127.0.0.1",
                BackendPort = backend.Port,
                Interceptor = interceptor
            });

            //edge: TLS terminates at the gateway, the backend never pays for it
            var gatewayServer = new LiteLinkTcpServer(new LiteLinkTcpServerOptions
            {
                Address = IPAddress.Loopback,
                UseTls = true,
                Handler = gateway
            });
            gatewayServer.Start();

            var rpc = TcpConnection.Create("127.0.0.1", gatewayServer.Port, new TcpConnectionOptions { UseTls = true });
            await rpc.Start();

            return new Chain { Backend = backend, GatewayServer = gatewayServer, Gateway = gateway, Client = rpc };
        }

        /// <summary>string argument of a call message, or null for system/unparseable messages</summary>
        private static string TryReadStringArg(GatewayMessage message)
        {
            if (message.EffectiveOpcode == 0)
                return null;
            try
            {
                var reader = new LiteReader(message.GetPayload());
                return reader.ReadString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static byte[] BuildStringPayload(string value)
        {
            var writer = new LiteWriter();
            writer.WriteString(value);
            return writer.Close();
        }

        private static byte[] BuildStringResponse(string value)
        {
            var writer = new LiteWriter();
            writer.WriteByte((byte)ErrorCodes.Success);
            writer.WriteByte(0); //not compressed
            writer.WriteString(value);
            return writer.Close();
        }

        [Fact]
        public async Task Gateway_TransparentForwarding_RoundTrips()
        {
            await using var chain = await StartAsync();

            //the handshake, type maps, calls and compression all pass through untouched
            Assert.Equal("through the gateway", await chain.Client.Send<string>("Test.BigEcho", "through the gateway"));

            var big = new string('g', 20000) + "END";
            Assert.Equal(big, await chain.Client.Send<string>("Test.BigEcho", big));

            var ints = await chain.Client.Send<List<int>>("Test.EchoInts", new List<int> { 1, 2, 3 });
            Assert.Equal(new List<int> { 1, 2, 3 }, ints);
        }

        [Fact]
        public async Task Gateway_Multichannel_RunsConcurrently()
        {
            await using var chain = await StartAsync();

            var timer = Stopwatch.StartNew();
            var results = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(_ => chain.Client.SendMultiChannelMessage<int>("Test.Slow", null)));
            timer.Stop();

            //multichannel replies are backend pushes, routed back to the right client by the session
            Assert.All(results, r => Assert.Equal(42, r));
            Assert.True(timer.ElapsedMilliseconds < 1500, $"calls were serialized: {timer.ElapsedMilliseconds}ms");
        }

        [Fact]
        public async Task Gateway_ServerPush_IsForwardedToClient()
        {
            await using var chain = await StartAsync();

            var received = new TaskCompletionSource<PushModel>(TaskCreationOptions.RunContinuationsAsynchronously);
            chain.Client.RegisterFunction<PushModel>("TestPush", m => received.TrySetResult(m));

            await chain.Client.Send<object>("Test.Push", "hi through gateway");

            var pushed = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("hi through gateway", pushed.Message);
        }

        [Fact]
        public async Task Gateway_Interceptor_CanRespondWithoutBackend()
        {
            //the offload pattern: answer selected calls entirely at the gateway
            await using var chain = await StartAsync((session, message) =>
            {
                if (TryReadStringArg(message) == "INTERCEPT")
                    return Task.FromResult(GatewayVerdict.Respond(BuildStringResponse("from-gateway")));
                return Task.FromResult(GatewayVerdict.Forward());
            });

            Assert.Equal("from-gateway", await chain.Client.Send<string>("Test.BigEcho", "INTERCEPT"));
            //multichannel local responses are delivered through the push envelope
            Assert.Equal("from-gateway", await chain.Client.SendMultiChannelMessage<string>("Test.BigEcho", "INTERCEPT"));
            //everything else still reaches the backend
            Assert.Equal("normal", await chain.Client.Send<string>("Test.BigEcho", "normal"));
        }

        [Fact]
        public async Task Gateway_Interceptor_CanRewriteMessages()
        {
            //the password-hashing pattern: replace payload content before it reaches the backend
            await using var chain = await StartAsync((session, message) =>
            {
                var arg = TryReadStringArg(message);
                if (arg != null && arg.StartsWith("hash-me"))
                {
                    var rewritten = message.WithPayload(BuildStringPayload("HASHED:" + arg));
                    return Task.FromResult(GatewayVerdict.Forward(rewritten));
                }
                return Task.FromResult(GatewayVerdict.Forward());
            });

            //the backend echoes what IT received, proving the rewrite happened at the gateway
            Assert.Equal("HASHED:hash-me", await chain.Client.Send<string>("Test.BigEcho", "hash-me"));
            Assert.Equal("HASHED:hash-me", await chain.Client.SendMultiChannelMessage<string>("Test.BigEcho", "hash-me"));
        }

        [Fact]
        public async Task Gateway_Interceptor_CanReject()
        {
            await using var chain = await StartAsync((session, message) =>
            {
                //let system traffic (handshake) through, refuse every actual call
                if (message.EffectiveOpcode != 0)
                    return Task.FromResult(GatewayVerdict.Reject(ErrorCodes.Unauthorized));
                return Task.FromResult(GatewayVerdict.Forward());
            });

            var ex = await Assert.ThrowsAsync<Exception>(() => chain.Client.Send<int>("Test.Guarded", null));
            Assert.Contains("2", ex.Message); //ErrorCodes.Unauthorized
        }

        [Fact]
        public async Task Gateway_SessionEvents_FireAndCarryTag()
        {
            var backend = new LiteLinkTcpServer(new LiteLinkTcpServerOptions { Address = IPAddress.Loopback });
            backend.Start();

            var gateway = new RpcGateway(new RpcGatewayOptions { BackendHost = "127.0.0.1", BackendPort = backend.Port });
            GatewaySession connected = null, disconnected = null;
            gateway.ClientConnected = s => { s.Tag = "session-state"; connected = s; };
            gateway.ClientDisconnected = s => disconnected = s;

            var gatewayServer = new LiteLinkTcpServer(new LiteLinkTcpServerOptions
            {
                Address = IPAddress.Loopback,
                Handler = gateway
            });
            gatewayServer.Start();

            var rpc = TcpConnection.Create("127.0.0.1", gatewayServer.Port);
            await rpc.Start();
            Assert.NotNull(connected);
            Assert.Equal("session-state", connected.Tag);

            await rpc.Connection.Terminate();
            //disconnect propagates through the transport's session teardown
            for (int i = 0; i < 100 && disconnected == null; i++)
                await Task.Delay(20);
            Assert.Same(connected, disconnected);

            await gateway.StopAsync();
            await gatewayServer.StopAsync();
            await backend.StopAsync();
        }
    }
}
