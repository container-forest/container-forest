using ContainerForest.DiscordBot;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "CF_");
builder.Services.AddDiscordBot(builder.Configuration, builder.Environment);

var host = builder.Build();
host.Run();
