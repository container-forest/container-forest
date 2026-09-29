var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "CF_");

var host = builder.Build();
host.Run();
