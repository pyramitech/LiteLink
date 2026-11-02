using LiteLink.Shared;
using LiteLink.Shared.Serializer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection.Metadata.Ecma335;
using System.Threading.Tasks;

namespace LiteLink.SignalR
{
    public class LiteHub : Hub
    {
        private static ConcurrentDictionary<string, SignalRHubConnection> _connections = new ConcurrentDictionary<string, SignalRHubConnection>();
        public static IHubContext<LiteHub> Instance { get; set; }

        public async Task<byte[]> Data(byte[] data)
        {
            if (_connections.TryGetValue(this.Context.ConnectionId, out var connection))
            {
                if (RpcServer.Instance.ProxyFunction != null) {
                    return await RpcServer.Instance.ProxyFunction(data);
                }

                return await RpcServer.Instance.Receive(connection, data);
            }

            this.Context.Abort();
            throw new Exception("Missing connection!");
        }

        public override Task OnConnectedAsync()
        {
            var connection = new SignalRHubConnection(this.Context.ConnectionId);
            if(RpcServer.Instance.IsShuttingDown || !_connections.TryAdd(this.Context.ConnectionId, connection))
            {
                this.Context.Abort();
                return base.OnConnectedAsync();
            }
            RpcServer.Instance.AddConnection(connection);
            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            if (_connections.TryRemove(this.Context.ConnectionId, out var connection))
                RpcServer.Instance.RemoveConnection(connection);

            return base.OnDisconnectedAsync(exception);
        }
    }

    public class SignalRHubConnection : IConnection
    {
        public readonly string ConnectionId;
        public SignalRHubConnection(string connectionId) 
        {
            ConnectionId = connectionId;
        }

        public bool IsConnected => true; //TODO: maybe not always?
        public bool IsConnecting => false;

        public Task Connect()
        {
            throw new NotSupportedException("This function is only supported on the client side");
        }

        public async Task<byte[]> Invoke(byte[] data)
        {
            var client = LiteHub.Instance.Clients.Client(ConnectionId);

            if (client != null)
            {
                await client.SendAsync("Data", data);
                return null; //Currently server -> client responses are not supported,
                             //best to make an rpc request to the server if you need a response
            }
            //TODO: warn or exception if null?
            return null;
        }

        public Task Terminate()
        {
            return Task.CompletedTask;
            //throw new NotImplementedException();
        }
    }
    
    public static class ApplicationBuilderExtensions
    {
        public static HubEndpointConventionBuilder UseLiteLink(this IHost app)
        {
            if (app == null)
            {
                throw new ArgumentNullException(nameof(app));
            }            

            if (!(app is IEndpointRouteBuilder))
            {
                throw new Exception("Expected an IEndpointRouteBuilder");
            }

            var res = (app as IEndpointRouteBuilder).MapHub<LiteHub>("/hub");

            LiteHub.Instance = app.Services.GetService<IHubContext<LiteHub>>();

            return res;
        }
    }
}
