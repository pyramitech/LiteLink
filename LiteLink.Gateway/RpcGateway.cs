using LiteLink.Shared;
using LiteLink.Shared.Serializer;
using System.Collections.Concurrent;

namespace LiteLink.Gateway
{
    /// <summary>analyze hook: inspect one client message and decide what happens to it</summary>
    public delegate Task<GatewayVerdict> GatewayInterceptor(GatewaySession session, GatewayMessage message);

    public class RpcGatewayOptions
    {
        public string BackendHost { get; set; } = "localhost";
        public int BackendPort { get; set; }

        /// <summary>TLS on the gateway-to-backend hop; usually off — the point of the gateway is offloading TLS at the edge</summary>
        public bool BackendUseTls { get; set; }
        public bool BackendAllowInvalidCertificates { get; set; } = true;
        public string BackendTlsTargetHost { get; set; }

        public int MaxFrameSize { get; set; } = 64 * 1024 * 1024;

        /// <summary>optional analyze hook; when null the gateway is a fully transparent forwarder</summary>
        public GatewayInterceptor Interceptor { get; set; }

        /// <summary>
        /// before a <see cref="GatewayVerdict.Redirect"/> target takes over a session, replay the
        /// client's handshake against it and require a byte-identical response (default true).
        /// The client built its serializers from the original handshake, so a target serving a
        /// different RPC surface would silently corrupt every later call; refusing the redirect is
        /// the only safe outcome. Disable only when all targets are provably the same build.
        /// </summary>
        public bool VerifyHandshakeOnRedirect { get; set; } = true;
    }

    /// <summary>what a <see cref="GatewayInterceptor"/> decided for one message</summary>
    public sealed class GatewayVerdict
    {
        internal enum VerdictKind { Forward, Respond, Reject, Redirect }

        internal VerdictKind Kind { get; private init; }
        internal byte[] Bytes { get; private init; }
        internal ErrorCodes Code { get; private init; }
        internal string Host { get; private init; }
        internal int Port { get; private init; }

        private GatewayVerdict() { }
        private static readonly GatewayVerdict _forward = new GatewayVerdict { Kind = VerdictKind.Forward };

        /// <summary>pass the message to the backend unchanged</summary>
        public static GatewayVerdict Forward() => _forward;

        /// <summary>pass a rewritten message to the backend, e.g. a plaintext password replaced by its hash (see GatewayMessage.WithPayload)</summary>
        public static GatewayVerdict Forward(byte[] replacementMessage) => new GatewayVerdict { Kind = VerdictKind.Forward, Bytes = replacementMessage };

        /// <summary>answer locally with a complete response ([result byte][compressed byte][payload]); the backend never sees the message</summary>
        public static GatewayVerdict Respond(byte[] response) => new GatewayVerdict { Kind = VerdictKind.Respond, Bytes = response };

        /// <summary>refuse the call with an error code, e.g. ErrorCodes.Unauthorized</summary>
        public static GatewayVerdict Reject(ErrorCodes code) => new GatewayVerdict { Kind = VerdictKind.Reject, Code = code };

        /// <summary>
        /// move this session to another backend and forward the message (or its replacement)
        /// there — e.g. a login whose payload names a company that lives on a dedicated compute
        /// server. The message executes on the new backend, so the state it creates (the
        /// authenticated link) is born where the client will keep talking; whatever was pending on
        /// the previous backend is dropped. See <see cref="GatewaySession.RedirectBackendAsync"/>.
        /// </summary>
        public static GatewayVerdict Redirect(string host, int port, byte[] replacementMessage = null)
            => new GatewayVerdict { Kind = VerdictKind.Redirect, Host = host, Port = port, Bytes = replacementMessage };
    }

    /// <summary>
    /// Intercept -> analyze -> transfer gateway for the LiteLink protocol. Plug it into any socket
    /// transport as the connection handler and every client message flows through it:
    /// <code>
    /// var gateway = new RpcGateway(new RpcGatewayOptions { BackendHost = "compute1", BackendPort = 9090 });
    /// new LiteLinkTcpServer(new LiteLinkTcpServerOptions { Port = 443, UseTls = true, Handler = gateway }).Start();
    /// </code>
    /// TLS (and any work done in the interceptor, e.g. password hashing) is paid on the gateway
    /// box; the backend hop is plaintext TCP on the internal network. Each client gets its own
    /// backend connection, so per-connection server state (links, permissions) works unchanged,
    /// and backend pushes — including multichannel replies — route back to the right client.
    /// The gateway needs no generated RpcServer: it is fully protocol-transparent, the backend's
    /// handshake and type maps pass straight through.
    /// </summary>
    public class RpcGateway : IRpcConnectionHandler
    {
        private readonly RpcGatewayOptions _options;
        private readonly ConcurrentDictionary<IConnection, GatewaySession> _sessions
            = new ConcurrentDictionary<IConnection, GatewaySession>();
        private volatile bool _stopped;

        public Action<Exception> OnError { get; set; }
        public Action<GatewaySession> ClientConnected { get; set; }
        public Action<GatewaySession> ClientDisconnected { get; set; }

        public RpcGateway(RpcGatewayOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public bool IsShuttingDown => _stopped;

        public void OnConnected(IConnection connection)
        {
            var session = new GatewaySession(connection, _options, ReportError);
            _sessions[connection] = session;
            session.StartBackendConnect();
            ClientConnected?.Invoke(session);
        }

        public void OnDisconnected(IConnection connection)
        {
            if (_sessions.TryRemove(connection, out var session))
            {
                session.Close();
                ClientDisconnected?.Invoke(session);
            }
        }

        public async Task<byte[]> Receive(IConnection connection, byte[] data)
        {
            if (!_sessions.TryGetValue(connection, out var session))
                return ErrorResponse(ErrorCodes.Uninitialized);

            try
            {
                if (_options.Interceptor != null)
                {
                    var message = new GatewayMessage(data);
                    var verdict = await _options.Interceptor(session, message) ?? GatewayVerdict.Forward();

                    switch (verdict.Kind)
                    {
                        case GatewayVerdict.VerdictKind.Reject:
                            return ErrorResponse(verdict.Code);

                        case GatewayVerdict.VerdictKind.Respond:
                            if (message.IsMultiChannel)
                            {
                                //multichannel callers receive their real response as a push,
                                //the direct reply is only the scheduling ack
                                await SendMultiChannelResponseAsync(connection, message.Channel, verdict.Bytes);
                                return new byte[] { (byte)ErrorCodes.Success, 0 };
                            }
                            return verdict.Bytes;

                        case GatewayVerdict.VerdictKind.Redirect:
                            //a refused redirect (unreachable target, different handshake) throws
                            //here: the session stays on its current backend and the caller gets
                            //an error for this message only
                            await session.RedirectBackendAsync(verdict.Host, verdict.Port);
                            if (verdict.Bytes != null)
                                data = verdict.Bytes;
                            break;

                        default:
                            if (verdict.Bytes != null)
                                data = verdict.Bytes;
                            break;
                    }
                }

                return await session.InvokeBackendAsync(data);
            }
            catch (Exception ex)
            {
                ReportError(ex);
                return ErrorResponse(ErrorCodes.Unhandled);
            }
        }

        public Task StopAsync()
        {
            _stopped = true;
            foreach (var kvp in _sessions)
                OnDisconnected(kvp.Key);
            return Task.CompletedTask;
        }

        //mirrors RpcServer.ReplyMultiChannelData's envelope so clients can't tell the difference
        private static Task SendMultiChannelResponseAsync(IConnection connection, byte channel, byte[] response)
        {
            var writer = new LiteWriter();
            writer.WriteBoolean(false); //no compression
            writer.WriteUInt32(0); //system opcode
            writer.WriteByte(1); //type: multichannel
            writer.WriteByte(channel);
            writer.WriteFixedByteArray(response);
            return connection.Invoke(writer.Close());
        }

        private static byte[] ErrorResponse(ErrorCodes code) => new byte[] { (byte)code, 0 };

        private void ReportError(Exception ex) => OnError?.Invoke(ex);
    }
}
