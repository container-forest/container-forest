using ContainerForest.Agent;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "CF_");
builder.Services.AddAgent(builder.Configuration);

var host = builder.Build();
host.Run();
