using ContainerForest.Contracts.Bot;
using ContainerForest.DiscordBot.Configuration;
using ContainerForest.DiscordBot.Forest;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;

namespace ContainerForest.DiscordBot;

public static class DependencyInjection
{
    public static IServiceCollection AddDiscordBot(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BotOptions>()
            .Bind(configuration.GetSection(BotOptions.SectionName))
            .Validate(
                o => o.IsValid(),
                "CF_BOT__CONTROLLERURL must be an absolute http(s) URL, CF_BOT__SERVICETOKEN must be set and the timeouts positive.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => CreateChannel(sp.GetRequiredService<IOptions<BotOptions>>().Value.ControllerUrl!));
        services.AddSingleton(sp => new BotGateway.BotGatewayClient(sp.GetRequiredService<GrpcChannel>()));
        services.AddSingleton<IForestClient, GrpcForestClient>();
        return services;
    }

    private static GrpcChannel CreateChannel(Uri controllerUrl) => GrpcChannel.ForAddress(controllerUrl, new GrpcChannelOptions
    {
        HttpHandler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = true,
        },
    });
}
