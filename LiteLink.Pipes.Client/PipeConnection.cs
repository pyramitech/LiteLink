using LiteLink.Client;
using LiteLink.Shared;
using LiteLink.Transport;
using System.IO.Pipes;
using System.Security.Principal;

namespace LiteLink.Pipes.Client
{
    public class PipeConnectionOptions
    {
        /// <summary>
        /// machine hosting the pipe; "." (the default) is the local machine. Remote server names
        /// are a Windows-only feature — on Linux only "." is valid
        /// </summary>
        public string ServerName { get; set; } = ".";

        /// <summary>
        /// restrict the pipe to the current user (default true) — must match the server's setting;
        /// turn it off when the two processes run under different accounts
        /// </summary>
        public bool CurrentUserOnly { get; set; } = true;

        /// <summary>how long Connect waits for the pipe to become available, in milliseconds</summary>
        public int ConnectTimeout { get; set; } = 10000;

        /// <summary>
        /// Windows only (ignored elsewhere): how much of this client's identity the server may
        /// use. The default, Identification, lets the server read who we are (for allow lists)
        /// without being able to act on our behalf; raise to Impersonation only if the server
        /// legitimately uses RunAsClient
        /// </summary>
        public TokenImpersonationLevel ImpersonationLevel { get; set; } = TokenImpersonationLevel.Identification;

        /// <summary>upper bound for a single message, protects against malformed length prefixes</summary>
        public int MaxFrameSize { get; set; } = 64 * 1024 * 1024;
    }

    /// <summary>
    /// Named pipe client transport for same-machine inter-process communication (Windows and
    /// Linux). Create an <see cref="RpcClient"/> with
    /// <see cref="Create(string, PipeConnectionOptions, Func{Task})"/>, then call RpcClient.Start()
    /// to connect and handshake, exactly like the other transports.
    /// </summary>
    public class PipeConnection : IConnection
    {
        private readonly string _pipeName;
        private readonly PipeConnectionOptions _options;
        private readonly Func<Task> _reconnectAction;

        private NamedPipeClientStream _pipe;
        private FramedDuplexChannel _channel;
        private volatile bool _isConnected;
        private volatile bool _isConnecting;

        internal Action<byte[]> PushReceived;

        /// <summary>fires when the connection drops (or Terminate is called), so RpcClient can fail
        /// pending multichannel calls and queued batch items immediately instead of timing them out</summary>
        internal Action ConnectionClosed;

        /// <summary>errors that can't be surfaced to a caller (protocol violations, push handler crashes)</summary>
        public Action<Exception> OnError { get; set; }

        public bool IsConnected => _isConnected;
        public bool IsConnecting => _isConnecting;

        private PipeConnection(string pipeName, PipeConnectionOptions options, Func<Task> reconnectAction)
        {
            _pipeName = pipeName;
            _options = options ?? new PipeConnectionOptions();
            _reconnectAction = reconnectAction;
        }

        public static RpcClient Create(string pipeName, PipeConnectionOptions options = null, Func<Task> reconnectAction = null)
        {
            var connection = new PipeConnection(pipeName, options, reconnectAction);
            var client = new RpcClient(connection);
            connection.PushReceived = client.Receive;
            connection.ConnectionClosed = client.OnConnectionLost;
            return client;
        }

        public async Task Connect()
        {
            _isConnecting = true;
            try
            {
                CloseCurrent();

                var pipeOptions = PipeOptions.Asynchronous;
                if (_options.CurrentUserOnly)
                    pipeOptions |= PipeOptions.CurrentUserOnly;

                //the impersonation-level constructor is Windows-only
                var pipe = OperatingSystem.IsWindows()
                    ? new NamedPipeClientStream(_options.ServerName, _pipeName, PipeDirection.InOut, pipeOptions, _options.ImpersonationLevel)
                    : new NamedPipeClientStream(_options.ServerName, _pipeName, PipeDirection.InOut, pipeOptions);
                await pipe.ConnectAsync(_options.ConnectTimeout);

                var channel = new FramedDuplexChannel(pipe, _options.MaxFrameSize)
                {
                    PushHandler = data => PushReceived?.Invoke(data),
                    OnError = ex => OnError?.Invoke(ex)
                };

                _pipe = pipe;
                _channel = channel;
                _isConnected = true;

                _ = RunAsync(channel, pipe);
            }
            finally
            {
                _isConnecting = false;
            }
        }

        private async Task RunAsync(FramedDuplexChannel channel, NamedPipeClientStream pipe)
        {
            await channel.RunAsync(CancellationToken.None);
            channel.Dispose();
            pipe.Dispose();

            //only tear down state if a newer Connect() hasn't already replaced this channel
            if (ReferenceEquals(_channel, channel))
            {
                _isConnected = false;
                ConnectionClosed?.Invoke(); //before any reconnect, so stale requests fail instantly
                if (_reconnectAction != null)
                {
                    try
                    {
                        await _reconnectAction();
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke(ex);
                    }
                }
            }
        }

        public Task<byte[]> Invoke(byte[] data)
        {
            var channel = _channel;
            if (channel == null || !_isConnected)
                throw new InvalidOperationException("The connection is not established, call Connect() first");
            return channel.InvokeAsync(data);
        }

        public Task Terminate()
        {
            _isConnected = false;
            CloseCurrent(); //nulls _channel, so RunAsync's teardown block won't also fire the callback
            ConnectionClosed?.Invoke();
            return Task.CompletedTask;
        }

        private void CloseCurrent()
        {
            var channel = _channel;
            _channel = null;
            channel?.Dispose();
            _pipe?.Dispose();
            _pipe = null;
        }
    }
}
