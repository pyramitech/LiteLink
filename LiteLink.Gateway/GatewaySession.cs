using LiteLink.Shared;
using LiteLink.Transport;
using System.Net.Security;
using System.Net.Sockets;

namespace LiteLink.Gateway
{
    /// <summary>
    /// One client connection paired with its own backend connection. Backend pushes (multichannel
    /// replies, server-to-client invokes) are routed back to this session's client automatically.
    /// The backend can be swapped mid-session via <see cref="RedirectBackendAsync"/>.
    /// </summary>
    public class GatewaySession
    {
        private readonly RpcGatewayOptions _options;
        private readonly Action<Exception> _reportError;
        private readonly object _backendLock = new object();
        private Task<FramedDuplexChannel> _backend; //the current backend, swapped by redirects
        private volatile bool _closed;

        //the handshake this client performed through the session; replayed against redirect
        //targets to prove they serve the RPC surface the client built its serializers from
        private byte[] _handshakeRequest;
        private byte[] _handshakeResponse;

        /// <summary>the client-facing connection (push to it via Invoke, kick it via Terminate)</summary>
        public IConnection ClientConnection { get; }

        /// <summary>free slot for interceptor state, e.g. authentication progress</summary>
        public object Tag { get; set; }

        /// <summary>the backend this session currently talks to; starts at the configured default</summary>
        public string BackendHost { get; private set; }
        public int BackendPort { get; private set; }

        internal GatewaySession(IConnection clientConnection, RpcGatewayOptions options, Action<Exception> reportError)
        {
            ClientConnection = clientConnection;
            _options = options;
            _reportError = reportError;
            BackendHost = options.BackendHost;
            BackendPort = options.BackendPort;
        }

        //kick the backend connect off as soon as the client connects, requests await the same task
        internal void StartBackendConnect() => _ = CurrentBackend();

        private Task<FramedDuplexChannel> CurrentBackend()
        {
            lock (_backendLock)
                return _backend ??= ConnectBackendAsync(_options.BackendHost, _options.BackendPort);
        }

        /// <summary>
        /// sends a raw message to the backend and returns its raw response; also usable from
        /// interceptors for gateway-originated calls (e.g. fetching data needed for analysis)
        /// </summary>
        public async Task<byte[]> InvokeBackendAsync(byte[] data)
        {
            var channel = await CurrentBackend();
            var response = await channel.InvokeAsync(data);

            if (IsHandshakeRequest(data))
            {
                _handshakeRequest = data;
                _handshakeResponse = response;
            }
            return response;
        }

        /// <summary>
        /// Moves this session to another backend: connects to it, verifies it answers the client's
        /// handshake identically (see <see cref="RpcGatewayOptions.VerifyHandshakeOnRedirect"/>),
        /// then swaps it in and closes the previous backend connection. From then on every message
        /// and every backend push flows through the new server. Anything still pending on the
        /// previous backend (in-flight multichannel calls, server-initiated invokes, its link state)
        /// is dropped, so redirect on a quiet connection — typically the login. Throws, leaving the
        /// session on its current backend, when the target is unreachable or serves a different
        /// RPC surface.
        /// </summary>
        public async Task RedirectBackendAsync(string host, int port)
        {
            if (_closed)
                throw new InvalidOperationException("The session is closed");

            var replacement = await ConnectBackendAsync(host, port);
            try
            {
                if (_options.VerifyHandshakeOnRedirect && _handshakeRequest != null)
                {
                    var handshake = await replacement.InvokeAsync(_handshakeRequest);
                    if (!handshake.AsSpan().SequenceEqual(_handshakeResponse))
                        throw new InvalidOperationException(
                            $"Redirect to {host}:{port} refused: its handshake differs from the one this client connected with, the backends don't serve the same RPC surface");
                }
            }
            catch
            {
                replacement.Dispose(); //never became current, so its read loop exits quietly
                throw;
            }

            Task<FramedDuplexChannel> previous;
            lock (_backendLock)
            {
                previous = _backend;
                _backend = Task.FromResult(replacement);
                BackendHost = host;
                BackendPort = port;
            }
            DisposeBackend(previous);
        }

        //[u16 0][compressed 0][type 0], or the legacy 3-byte [u16 0][0]
        private static bool IsHandshakeRequest(byte[] data)
        {
            return (data.Length == 3 || data.Length == 4)
                && data[0] == 0 && data[1] == 0 && data[2] == 0
                && (data.Length == 3 || data[3] == 0);
        }

        private async Task<FramedDuplexChannel> ConnectBackendAsync(string host, int port)
        {
            var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(host, port);

            Stream stream = tcp.GetStream();
            if (_options.BackendUseTls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false,
                    _options.BackendAllowInvalidCertificates ? (sender, cert, chain, errors) => true : null);
                await ssl.AuthenticateAsClientAsync(_options.BackendTlsTargetHost ?? host);
                stream = ssl;
            }

            if (_closed)
            {
                tcp.Dispose();
                throw new Exception("The session was closed while its backend connection was being established");
            }

            var channel = new FramedDuplexChannel(stream, _options.MaxFrameSize)
            {
                PushHandler = data => _ = ForwardPushAsync(data),
                OnError = _reportError
            };

            _ = RunBackendAsync(channel, tcp);
            return channel;
        }

        private async Task ForwardPushAsync(byte[] data)
        {
            try
            {
                await ClientConnection.Invoke(data);
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
        }

        private async Task RunBackendAsync(FramedDuplexChannel channel, TcpClient tcp)
        {
            await channel.RunAsync(CancellationToken.None);
            channel.Dispose();
            tcp.Dispose();

            //a backend that was replaced by a redirect (or never became current) ends quietly;
            //only the loss of the current backend takes the client down
            if (_closed || !IsCurrent(channel))
                return;

            _closed = true;
            try
            {
                await ClientConnection.Terminate(); //so the client notices and can reconnect
            }
            catch (Exception ex)
            {
                _reportError(ex);
            }
        }

        private bool IsCurrent(FramedDuplexChannel channel)
        {
            lock (_backendLock)
                return _backend != null && _backend.IsCompletedSuccessfully && ReferenceEquals(_backend.Result, channel);
        }

        private static void DisposeBackend(Task<FramedDuplexChannel> backend)
        {
            if (backend == null)
                return;
            if (backend.IsCompleted)
            {
                if (backend.IsCompletedSuccessfully)
                    backend.Result.Dispose();
                return;
            }
            //still connecting: dispose it as soon as it lands
            _ = backend.ContinueWith(
                t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); },
                TaskContinuationOptions.ExecuteSynchronously);
        }

        internal void Close()
        {
            _closed = true;
            Task<FramedDuplexChannel> backend;
            lock (_backendLock)
                backend = _backend;
            DisposeBackend(backend);
        }
    }
}
