using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;

namespace LiteLink.SignalR
{
    public static class SignalRExtensions
    {
        public static TBuilder AddLiteLinkProtocol<TBuilder>(this TBuilder builder) where TBuilder : ISignalRBuilder
        {
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHubProtocol, LiteLinkHubProtocol>());
            return builder;
        }
    }
}
