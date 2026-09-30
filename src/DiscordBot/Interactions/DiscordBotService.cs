using System.Net;
using ContainerForest.DiscordBot.Configuration;
using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Options;

namespace ContainerForest.DiscordBot.Interactions;

/// <summary>Keeps the bot connected to Discord, registers the slash commands and hands interactions to them.</summary>
public sealed class DiscordBotService(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
    IOptions<DiscordOptions> options,
    IHostEnvironment environment,
    IHostApplicationLifetime lifetime,
    ILogger<DiscordBotService> logger) : BackgroundService
{
    private const int AuthenticationFailedCloseCode = 4004;

    private int commandsRegistered;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.Log += LogAsync;
        interactions.Log += LogAsync;
        client.Ready += RegisterCommandsAsync;
        client.InteractionCreated += HandleInteractionAsync;
        interactions.InteractionExecuted += ReportFailureAsync;
        await interactions.AddModulesAsync(typeof(DiscordBotService).Assembly, services);

        try
        {
            await client.LoginAsync(TokenType.Bot, options.Value.Token);
            // LoginAsync only checks the token's format. Without a real request a rejected token would surface
            // as endless gateway reconnects, each failing with 401.
            await client.GetApplicationInfoAsync();
        }
        catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.Unauthorized)
        {
            StopOnRejectedToken();
            return;
        }

        // The token can also be reset in the Developer Portal while the bot runs.
        client.Disconnected += StopIfTokenRejectedAsync;

        // Reconnects on its own after network failures; Ready fires again after each full reconnect.
        await client.StartAsync();
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            await client.StopAsync();
            await client.LogoutAsync();
        }
    }

    /// <summary>Discord closes the gateway with 4004 for a bad token; fetching the gateway URL fails with 401.</summary>
    private Task StopIfTokenRejectedAsync(Exception exception)
    {
        if (exception is HttpException { HttpCode: HttpStatusCode.Unauthorized }
            || exception is WebSocketClosedException { CloseCode: AuthenticationFailedCloseCode })
        {
            StopOnRejectedToken();
        }

        return Task.CompletedTask;
    }

    private void StopOnRejectedToken()
    {
        logger.LogCritical("Discord rejected the bot token. Check CF_DISCORD__TOKEN (Discord:Token in user secrets).");
        Environment.ExitCode = 1;
        lifetime.StopApplication();
    }

    private async Task RegisterCommandsAsync()
    {
        // Registration replaces the whole command set, so once per process is enough.
        if (Interlocked.Exchange(ref commandsRegistered, 1) == 1)
        {
            return;
        }

        try
        {
            if (environment.IsDevelopment())
            {
                var guildId = options.Value.DevGuildId!.Value;
                await interactions.RegisterCommandsToGuildAsync(guildId, deleteMissing: true);
                logger.LogInformation("Registered {Count} slash commands on the test server {GuildId}", interactions.SlashCommands.Count, guildId);
            }
            else
            {
                await interactions.RegisterCommandsGloballyAsync(deleteMissing: true);
                logger.LogInformation("Registered {Count} slash commands globally", interactions.SlashCommands.Count);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref commandsRegistered, 0);
            logger.LogError(ex, "Cannot register the slash commands; retrying on the next reconnect");
        }
    }

    private async Task HandleInteractionAsync(SocketInteraction interaction)
    {
        // Commands run asynchronously (RunMode.Async), so this returns before they finish and does not block the gateway.
        await interactions.ExecuteCommandAsync(new SocketInteractionContext(client, interaction), services);
    }

    /// <summary>
    /// Commands turn their own failures into replies; this catches what never reached them, e.g. an unknown
    /// command left over from an older registration or options Discord.Net could not convert.
    /// </summary>
    private async Task ReportFailureAsync(ICommandInfo? command, IInteractionContext context, IResult result)
    {
        if (result.IsSuccess)
        {
            return;
        }

        logger.LogWarning("Interaction {Command} failed: {Error}: {Reason}", command?.Name ?? "(unknown)", result.Error, result.ErrorReason);
        if (context.Interaction is IAutocompleteInteraction || context.Interaction.HasResponded)
        {
            return;
        }

        try
        {
            await context.Interaction.RespondAsync("❌ This command is not available right now.", ephemeral: true);
        }
        catch (HttpException ex)
        {
            logger.LogDebug("Cannot answer the failed interaction: {Reason}", ex.Message);
        }
    }

    private Task LogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            _ => LogLevel.Trace,
        };
        logger.Log(level, message.Exception, "Discord.Net {Source}: {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }
}
