using LiteLink.Shared;
using LiteLink.Transport;
using System.Collections.Concurrent;
using System.IO.Pipes;

namespace LiteLink.Pipes
{
    public class LiteLinkPipeServerOptions
    {
        /// <summary>
        /// pipe name shared by server and clients. On Windows this becomes \\.\pipe\&lt;name&gt;,
        /// on Linux/macOS .NET maps it to a Unix domain socket under the temp directory
        /// </summary>
        public string PipeName { get; set; }

        /// <summary>
        /// restrict the pipe to the current user (default true) — the cross-platform access
        /// control for local IPC. Both server and client must use the same setting; turn it
        /// off when the two processes run under different accounts
        /// </summary>
        public bool CurrentUserOnly { get; set; } = true;

        /// <summary>upper bound for a single message, protects against malformed length prefixes</summary>
        public int MaxFrameSize { get; set; } = 64 * 1024 * 1024;

        /// <summary>
        /// how many ordinary requests one connection may execute in parallel; the default of 1
        /// processes them sequentially in arrival order (like SignalR's
        /// MaximumParallelInvocationsPerClient), so a single client can't occupy unbounded server
        /// concurrency. Cheap system traffic (handshakes, invoke responses) bypasses the limit, and
        /// multichannel requests are governed per client by RpcLink.AllowMultiChannel instead.
        /// 0 or negative means unlimited.
        /// </summary>
        public int MaxParallelRequestsPerConnection { get; set; } = 1;

        /// <summary>
        /// custom dispatch target (e.g. an RpcGateway); when null, connections are routed into
        /// the process-wide RpcServer.Instance as usual
        /// </summary>
        public IRpcConnectionHandler Handler { get; set; }

        /// <summary>
        /// optional peer authorization, invoked after accept and before the client can exchange
        /// any message; return false to drop the connection. The identity's user is asserted by
        /// the operating system and is safe to use as an approved-user list. Process-based checks
        /// (executable signature via ProcessId) are hardening only — see PipeClientIdentity.
        /// A callback exception counts as a rejection.
        /// </summary>
        public Func<PipeClientIdentity, Task<bool>> AuthorizeClient { get; set; }
    }

    /// <summary>
    /// Hosts the process-wide <see cref="RpcServer.Instance"/> over named pipes for same-machine
    /// inter-process communication. Works on Windows and Linux (where .NET implements named pipes
    /// over Unix domain sockets); no ports, no firewall, no certificates — access control comes
    /// from the operating system. Uses the same frame protocol as the TCP and QUIC transports,
    /// so requests, multichannel calls and server pushes behave identically.
    /// </summary>
    public class LiteLinkPipeServer : IAsyncDisposable
    {
        private readonly LiteLinkPipeServerOptions _options;
        private IRpcConnectionHandler _handler;
        private CancellationTokenSource _cts;
        private Task _acceptLoop;
        private volatile bool _started;
        private readonly ConcurrentDictionary<FramedDuplexChannel, NamedPipeServerStream> _sessions
            = new ConcurrentDictionary<FramedDuplexChannel, NamedPipeServerStream>();

        /// <summary>errors that can't be surfaced to a caller; falls back to RpcServer.Instance.OnError when unset</summary>
        public Action<Exception> OnError { get; set; }

        public string PipeName => _options.PipeName;

        public LiteLinkPipeServer(LiteLinkPipeServerOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public void Start()
        {
            if (_started)
                throw new InvalidOperationException("The server is already started");
            if (string.IsNullOrEmpty(_options.PipeName))
                throw new InvalidOperationException("LiteLinkPipeServerOptions.PipeName is required");
            if (_options.Handler == null && RpcServer.Instance == null)
                throw new InvalidOperationException("RpcServer.Instance is not initialized, reference the generated [RpcServer] assembly first (or provide a custom Handler)");

            _handler = _options.Handler ?? new RpcServerConnectionHandler();
            _cts = new CancellationTokenSource();
            _started = true;
            _acceptLoop = AcceptLoopAsync(_cts.Token);
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    var pipeOptions = PipeOptions.Asynchronous;
                    if (_options.CurrentUserOnly)
                        pipeOptions |= PipeOptions.CurrentUserOnly;

                    pipe = new NamedPipeServerStream(
                        _options.PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        pipeOptions);

                    await pipe.WaitForConnectionAsync(cancellationToken);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    pipe?.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    pipe?.Dispose();
                    ReportError(ex);
                    continue;
                }

                _ = HandleClientAsync(pipe, cancellationToken);
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            FramedDuplexChannel channel = null;
            PipeServerConnection connection = null;
            var registered = false;
            try
            {
                if (_handler.IsShuttingDown)
                {
                    pipe.Dispose();
                    return;
                }

                var identity = PipeClientIdentity.Resolve(pipe, ReportError);

                if (_options.AuthorizeClient != null)
                {
                    bool approved;
                    try
                    {
                        approved = await _options.AuthorizeClient(identity);
                    }
                    catch (Exception ex)
                    {
                        ReportError(ex);
                        approved = false;
                    }

                    if (!approved)
                    {
                        pipe.Dispose(); //the client sees a disconnect before any message was exchanged
                        return;
                    }
                }

                channel = new FramedDuplexChannel(pipe, _options.MaxFrameSize)
                {
                    OnError = ReportError,
                    MaxParallelRequests = _options.MaxParallelRequestsPerConnection
                };
                connection = new PipeServerConnection(channel, identity);
                channel.RequestHandler = data => _handler.Receive(connection, data);

                _sessions.TryAdd(channel, pipe);
                _handler.OnConnected(connection);
                registered = true;

                await channel.RunAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                    ReportError(ex);
            }
            finally
            {
                if (registered)
                    _handler.OnDisconnected(connection);
                connection?.MarkClosed();
                if (channel != null)
                {
                    _sessions.TryRemove(channel, out _);
                    channel.Dispose();
                }
                pipe.Dispose();
            }
        }

        public async Task StopAsync()
        {
            if (!_started)
                return;

            _cts.Cancel();

            foreach (var session in _sessions)
            {
                session.Key.Dispose(); //unblocks the session's read loop
                session.Value.Dispose();
            }

            try
            {
                await _acceptLoop;
            }
            catch (Exception) { }

            _started = false;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }

        private void ReportError(Exception ex)
        {
            (OnError ?? RpcServer.Instance?.OnError)?.Invoke(ex);
        }
    }

    /// <summary>
    /// server-side handle for one connected pipe client; Invoke pushes a one-way message to it.
    /// Procedures can reach the caller's OS identity via
    /// <c>(link.Connection as PipeServerConnection)?.ClientIdentity</c>
    /// </summary>
    public class PipeServerConnection : IConnection
    {
        private readonly FramedDuplexChannel _channel;
        private volatile bool _closed;

        /// <summary>kernel-asserted identity of the connected client, resolved at accept time</summary>
        public PipeClientIdentity ClientIdentity { get; }

        internal PipeServerConnection(FramedDuplexChannel channel, PipeClientIdentity identity)
        {
            _channel = channel;
            ClientIdentity = identity;
        }

        public bool IsConnected => !_closed;
        public bool IsConnecting => false;

        public Task Connect()
        {
            throw new NotSupportedException("This function is only supported on the client side");
        }

        public async Task<byte[]> Invoke(byte[] data)
        {
            await _channel.SendPushAsync(data);
            return null; //server -> client responses are not supported, same as the other transports
        }

        public Task Terminate()
        {
            _channel.Dispose(); //ends the session's read loop, which disconnects the client
            return Task.CompletedTask;
        }

        internal void MarkClosed() => _closed = true;
    }
}
