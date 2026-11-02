extern alias client;

using LiteLink.Gateway;
using LiteLink.Shared.Serializer;
using LiteLink.Tcp;
using LiteLink.Tcp.Client;
using System.Net;
using Xunit;

namespace LiteLink.Tests
{
    /// <summary>
    /// Backend redirects: a session moves from the default backend to another listener when the
    /// interceptor asks for it. Both listeners front the same singleton test server, which is the
    /// one setup an in-process test can have; the handshake check therefore passes by construction.
    /// </summary>
    [Collection("RpcServer")]
    public class GatewayRedirectTests
    {
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

        [Fact]
        public async Task Gateway_Redirect_MovesSessionToOtherBackend()
        {
            var backendA = new LiteLinkTcpServer(new LiteLinkTcpServerOptions { Address = IPAddress.Loopback });
            var backendB = new LiteLinkTcpServer(new LiteLinkTcpServerOptions { Address = IPAddress.Loopback });
            backendA.Start();
            backendB.Start();

            //the marker message plays the role of the login that names the target server
            var gateway = new RpcGateway(new RpcGatewayOptions
            {
                BackendHost = "127.0.0.1",
                BackendPort = backendA.Port,
                Interceptor = (session, message) => Task.FromResult(
                    TryReadStringArg(message) == "go-to-b"
                        ? GatewayVerdict.Redirect("127.0.0.1", backendB.Port)
                        : GatewayVerdict.Forward())
            });
            GatewaySession session = null;
            gateway.ClientConnected = s => session = s;

            var gatewayServer = new LiteLinkTcpServer(new LiteLinkTcpServerOptions { Address = IPAddress.Loopback, Handler = gateway });
            gatewayServer.Start();

            var rpc = TcpConnection.Create("127.0.0.1", gatewayServer.Port);
            await rpc.Start();
            try
            {
                Assert.Equal(backendA.Port, session.BackendPort);
                Assert.Equal("before", await rpc.Send<string>("Test.BigEcho", "before"));

                //the triggering message itself is answered by the new backend
                Assert.Equal("go-to-b", await rpc.Send<string>("Test.BigEcho", "go-to-b"));
                Assert.Equal(backendB.Port, session.BackendPort);

                //plain calls, multichannel replies (pushes from the new backend) and big payloads keep working
                Assert.Equal("after", await rpc.Send<string>("Test.BigEcho", "after"));
                Assert.Equal(42, await rpc.SendMultiChannelMessage<int>("Test.Slow", null));
                var big = new string('r', 20000) + "END";
                Assert.Equal(big, await rpc.Send<string>("Test.BigEcho", big));

                //stopping the backend the session left behind must not take the session down
                await backendA.StopAsync();
                await Task.Delay(100);
                Assert.Equal("still up", await rpc.Send<string>("Test.BigEcho", "still up"));
            }
            finally
            {
                await gateway.StopAsync();
                await gatewayServer.StopAsync();
                await backendA.StopAsync();
                await backendB.StopAsync();
            }
        }

        [Fact]
        public async Task Gateway_Redirect_UnreachableTarget_KeepsCurrentBackend()
        {
            var backend = new LiteLinkTcpServer(new LiteLinkTcpServerOptions { Address = IPAddress.Loopback });
            backend.Start();

            //a listener that is started only to learn a free port, then closed again
            var closed = new LiteLinkTcpServer(new LiteLinkTcpServerOptions { Address = IPAddress.Loopback });
            closed.Start();
            var deadPort = closed.Port;
            await closed.StopAsync();

            var errors = new List<Exception>();
            var gateway = new RpcGateway(new RpcGatewayOptions
            {
                BackendHost = "127.0.0.1",
                BackendPort = backend.Port,
                Interceptor = (session, message) => Task.FromResult(
                    TryReadStringArg(message) == "go-elsewhere"
                        ? GatewayVerdict.Redirect("127.0.0.1", deadPort)
                        : GatewayVerdict.Forward())
            });
            gateway.OnError = errors.Add;
            GatewaySession session = null;
            gateway.ClientConnected = s => session = s;

            var gatewayServer = new LiteLinkTcpServer(new LiteLinkTcpServerOptions { Address = IPAddress.Loopback, Handler = gateway });
            gatewayServer.Start();

            var rpc = TcpConnection.Create("127.0.0.1", gatewayServer.Port);
            await rpc.Start();
            try
            {
                //only the triggering call fails; the session stays where it was and keeps working
                var ex = await Assert.ThrowsAsync<Exception>(() => rpc.Send<string>("Test.BigEcho", "go-elsewhere"));
                Assert.Contains("Error code: 255", ex.Message);
                Assert.NotEmpty(errors);
                Assert.Equal(backend.Port, session.BackendPort);
                Assert.Equal("still here", await rpc.Send<string>("Test.BigEcho", "still here"));
            }
            finally
            {
                await gateway.StopAsync();
                await gatewayServer.StopAsync();
                await backend.StopAsync();
            }
        }
    }
}
