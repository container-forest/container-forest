using ContainerForest.DiscordBot.Commands;
using ContainerForest.DiscordBot.Configuration;
using ContainerForest.DiscordBot.Dashboard;
using ContainerForest.DiscordBot.Forest;
using ContainerForest.DiscordBot.Interactions;
using ContainerForest.DiscordBot.Notifications;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;
using BotGateway = ContainerForest.Contracts.Bot.BotGateway;

namespace ContainerForest.DiscordBot;

public static class DependencyInjection
{
    public static IServiceCollection AddDiscordBot(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddForestClient(configuration);
        services.AddCommands();

        var isDevelopment = environment.IsDevelopment();
        services.AddOptions<DiscordOptions>()
            .Bind(configuration.GetSection(DiscordOptions.SectionName))
            .Validate(
                o => o.IsValid(isDevelopment),
                isDevelopment
                    ? "CF_DISCORD__TOKEN (ASCII, no spaces) and CF_DISCORD__DEVGUILDID (the test server's id) must be set."
                    : "CF_DISCORD__TOKEN must be set: ASCII characters, no spaces.")
            .ValidateOnStart();

        services.AddSingleton(_ => new DiscordSocketClient(new DiscordSocketConfig
        {
            // Slash commands need no privileged intents: the bot reads no messages and no member lists.
            GatewayIntents = GatewayIntents.Guilds,
            LogLevel = LogSeverity.Info,
        }));
        services.AddSingleton(sp => new InteractionService(sp.GetRequiredService<DiscordSocketClient>(), new InteractionServiceConfig
        {
            // A command waits for the aggregator; running it inline would stall the gateway meanwhile.
            DefaultRunMode = RunMode.Async,
            LogLevel = LogSeverity.Info,
        }));
        services.AddHostedService<DiscordBotService>();

        services.AddSingleton<INotificationPoster, DiscordNotificationPoster>();
        services.AddSingleton(_ => new ReconnectBackoff());
        services.AddHostedService<NotificationWatcher>();
        return services;
    }

    /// <summary>The aggregator's BotGateway behind <see cref="IForestClient"/>.</summary>
    public static IServiceCollection AddForestClient(this IServiceCollection services, IConfiguration configuration)
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
        services.AddSingleton<INotificationSource, GrpcNotificationSource>();
        return services;
    }

    /// <summary>The slash commands' logic; stateless, so one instance each.</summary>
    public static IServiceCollection AddCommands(this IServiceCollection services)
    {
        services.AddSingleton<EnvironmentResolver>();
        services.AddSingleton<AutocompleteSource>();
        services.AddSingleton<EnvironmentsCommand>();
        services.AddSingleton<ContainersCommand>();
        services.AddSingleton<StartContainerCommand>();
        services.AddSingleton<StopContainerCommand>();
        services.AddSingleton<RestartContainerCommand>();
        services.AddSingleton<ContainerLogsCommand>();
        services.AddSingleton<StacksCommand>();
        services.AddSingleton<RestartStackCommand>();
        services.AddSingleton<UpStackCommand>();
        services.AddSingleton<PullStackCommand>();
        services.AddSingleton<RemoveContainerCommand>();
        services.AddSingleton<DownStackCommand>();
        services.AddSingleton<LinkCommand>();
        services.AddSingleton<DashboardService>();
        return services;
    }

    private static GrpcChannel CreateChannel(Uri controllerUrl) => GrpcChannel.ForAddress(controllerUrl, new GrpcChannelOptions
    {
        HttpHandler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = true,

            // The notification stream can be silent for hours: pings keep proxies from closing it and notice a dead connection.
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
        },
    });
}
