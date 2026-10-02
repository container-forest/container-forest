extern alias bot;

using bot::ContainerForest.DiscordBot;
using bot::ContainerForest.DiscordBot.Commands;
using bot::ContainerForest.DiscordBot.Configuration;
using bot::ContainerForest.DiscordBot.Forest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace ContainerForest.DiscordBot.Tests;

public sealed class DependencyInjectionTests
{
    private static readonly (string, string?)[] ValidBot =
        [("Bot:ControllerUrl", "http://localhost:5081"), ("Bot:ServiceToken", "token")];

    [Fact]
    public void ValidConfiguration_ProvidesTheGrpcClientAndTheCommands()
    {
        using var services = Build(Environments.Production, [.. ValidBot, ("Discord:Token", "discord-token")]);

        Assert.IsType<GrpcForestClient>(services.GetRequiredService<IForestClient>());
        Assert.IsType<GrpcNotificationSource>(services.GetRequiredService<INotificationSource>());
        Assert.NotNull(services.GetRequiredService<EnvironmentsCommand>());
        Assert.NotNull(services.GetRequiredService<ContainersCommand>());
        Assert.NotNull(services.GetRequiredService<StartContainerCommand>());
        Assert.NotNull(services.GetRequiredService<StopContainerCommand>());
        Assert.NotNull(services.GetRequiredService<RestartContainerCommand>());
        Assert.NotNull(services.GetRequiredService<LinkCommand>());
        Assert.NotNull(services.GetRequiredService<AutocompleteSource>());
    }

    [Theory]
    [InlineData("http://localhost:5081", null)]
    [InlineData("http://localhost:5081", " ")]
    [InlineData(null, "token")]
    [InlineData("localhost:5081", "token")]
    [InlineData("ftp://localhost:5081", "token")]
    public void InvalidBotConfiguration_FailsValidation(string? controllerUrl, string? serviceToken)
    {
        using var services = Build(Environments.Production, ("Bot:ControllerUrl", controllerUrl), ("Bot:ServiceToken", serviceToken));

        var ex = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<BotOptions>>().Value);
        Assert.Contains("CF_BOT__SERVICETOKEN", ex.Message);
    }

    [Fact]
    public void ValidationMessages_DoNotContainTheTokens()
    {
        using var services = Build(
            Environments.Development,
            ("Bot:ControllerUrl", "not a url"), ("Bot:ServiceToken", "secret-token-value"), ("Discord:Token", "discord-secret"));

        var bot = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<BotOptions>>().Value);
        var discord = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<DiscordOptions>>().Value);
        Assert.DoesNotContain("secret-token-value", bot.Message);
        Assert.DoesNotContain("discord-secret", discord.Message);
    }

    [Fact]
    public void DiscordToken_IsRequired()
    {
        using var services = Build(Environments.Production, ValidBot);

        var ex = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<DiscordOptions>>().Value);
        Assert.Contains("CF_DISCORD__TOKEN", ex.Message);
    }

    [Theory]
    [InlineData("MTIzNDU2Nzg5.abc.defч")]  // a key pressed in another keyboard layout
    [InlineData("MTIzNDU2Nzg5.abc.def​")]
    [InlineData("MTIzNDU2Nzg5.abc.def ")]
    public void DiscordToken_WithCharactersAnHttpHeaderCannotCarry_IsRejectedAtStart(string token)
    {
        using var services = Build(Environments.Production, [.. ValidBot, ("Discord:Token", token)]);

        var ex = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<DiscordOptions>>().Value);
        Assert.Contains("ASCII", ex.Message);
        Assert.DoesNotContain(token.Trim(), ex.Message);
    }

    [Fact]
    public void Development_RequiresTheTestServer()
    {
        using var services = Build(Environments.Development, [.. ValidBot, ("Discord:Token", "discord-token")]);

        var ex = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<DiscordOptions>>().Value);
        Assert.Contains("CF_DISCORD__DEVGUILDID", ex.Message);
    }

    [Fact]
    public void Development_WithTheTestServer_IsValid()
    {
        using var services = Build(
            Environments.Development, [.. ValidBot, ("Discord:Token", "discord-token"), ("Discord:DevGuildId", "1234567890123456789")]);

        Assert.Equal(1234567890123456789UL, services.GetRequiredService<IOptions<DiscordOptions>>().Value.DevGuildId);
    }

    private static ServiceProvider Build(string environmentName, params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return new ServiceCollection()
            .AddLogging()
            .AddDiscordBot(configuration, new HostingEnvironment { EnvironmentName = environmentName })
            .BuildServiceProvider();
    }
}
