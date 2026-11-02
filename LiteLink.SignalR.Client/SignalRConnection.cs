using LiteLink.Shared;
using Microsoft.AspNetCore.SignalR.Client;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices.JavaScript;
using LiteLink.Client;

namespace LiteLink.SignalR.Client
{
    public class SignalRConnection : IConnection
    {
        private HubConnection _connection;
        private Func<Task> _reconnectAction = null;

        public bool IsConnected => _connection?.State == HubConnectionState.Connected;

        public bool IsConnecting => _connection?.State == HubConnectionState.Reconnecting || _connection?.State == HubConnectionState.Connecting;

        private SignalRConnection(string host, Func<Task> reconnectAction)
        {
            _reconnectAction = reconnectAction;
            _connection = new HubConnectionBuilder()
                .WithUrl(host + "/hub", options =>
                {
                    options.HttpMessageHandlerFactory = handler =>
                    {
                        var newHandler = new HttpClientHandler();
                        newHandler.ServerCertificateCustomValidationCallback =
                            (message, certificate, chain, sslPolicyErrors) => true;
                        return newHandler;
                    };
                })
                .AddLiteLinkProtocol()
            .Build();

            //fail pending multichannel calls and queued batch items the moment the connection
            //drops, before any reconnect action runs, instead of letting them time out
            _connection.Closed += (e) =>
            {
                ConnectionClosed?.Invoke();
                return Task.CompletedTask;
            };
            if (reconnectAction != null)
                _connection.Closed += (e) => reconnectAction();
        }

        internal Action ConnectionClosed;

        public static RpcClient Create(string host, Func<Task> reconnectAction = null)
        {
            var connection = new SignalRConnection(host, reconnectAction);
            var client = new RpcClient(connection);
            connection._connection.On<byte[]>("Data", client.Receive);
            connection.ConnectionClosed = client.OnConnectionLost;
            return client;
        }

        public Task<byte[]> Invoke(byte[] data)
        {
            return _connection.InvokeAsync<byte[]>("Data", data);
        }

        public async Task Terminate()
        {
            await _connection.StopAsync();
        }

        public async Task Connect()
        {
            await _connection.StartAsync();           
        }
    }
}