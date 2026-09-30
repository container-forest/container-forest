extern alias bot;

using bot::ContainerForest.DiscordBot;
using bot::ContainerForest.DiscordBot.Configuration;
using bot::ContainerForest.DiscordBot.Forest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ContainerForest.DiscordBot.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void ValidConfiguration_ProvidesTheGrpcClient()
    {
        using var services = Build(("Bot:ControllerUrl", "http://localhost:5081"), ("Bot:ServiceToken", "token"));

        Assert.IsType<GrpcForestClient>(services.GetRequiredService<IForestClient>());
    }

    [Theory]
    [InlineData("http://localhost:5081", null)]
    [InlineData("http://localhost:5081", " ")]
    [InlineData(null, "token")]
    [InlineData("localhost:5081", "token")]
    [InlineData("ftp://localhost:5081", "token")]
    public void InvalidConfiguration_FailsValidation(string? controllerUrl, string? serviceToken)
    {
        using var services = Build(("Bot:ControllerUrl", controllerUrl), ("Bot:ServiceToken", serviceToken));

        var ex = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<BotOptions>>().Value);
        Assert.Contains("CF_BOT__SERVICETOKEN", ex.Message);
    }

    [Fact]
    public void ValidationMessage_DoesNotContainTheToken()
    {
        using var services = Build(("Bot:ControllerUrl", "not a url"), ("Bot:ServiceToken", "secret-token-value"));

        var ex = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<BotOptions>>().Value);
        Assert.DoesNotContain("secret-token-value", ex.Message);
    }

    private static ServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return new ServiceCollection()
            .AddLogging()
            .AddDiscordBot(configuration)
            .BuildServiceProvider();
    }
}
