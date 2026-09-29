var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "CF_");

builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();

app.Run();
