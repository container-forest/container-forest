using System.Reflection;
using System.Text.Json.Serialization;
using ContainerForest.Aggregator.Api.Access;
using ContainerForest.Aggregator.Api.Endpoints;
using ContainerForest.Aggregator.Api.ErrorHandling;
using ContainerForest.Aggregator.Api.Grpc;
using ContainerForest.Aggregator.Application;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Infrastructure;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "CF_");

builder.Services.AddApplication();

// The build-time OpenAPI generator starts the host, hosted services included; it must not create a database.
var isOpenApiGeneration = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
builder.Services.AddInfrastructure(builder.Configuration, migrateDatabase: !isOpenApiGeneration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, ClaimsCurrentUser>();
if (builder.Environment.IsDevelopment())
{
    // Stand-in for the login until it exists: the X-Dev-User header names the acting user, without a password.
    builder.Services.AddAuthentication(DevUserAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevUserAuthenticationHandler>(DevUserAuthenticationHandler.SchemeName, null);
}
else
{
    builder.Services.AddAuthentication();
}

builder.Services.AddGrpc();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();

app.MapOpenApi();
app.MapGrpcService<AgentGatewayService>();
app.MapMeEndpoints();

if (app.Environment.IsDevelopment())
{
    // Authentication here is the password-less dev header: never expose these outside local development.
    app.MapDevEnvironmentEndpoints();
    app.MapDevAccessEndpoints();
}

app.Run();

// Lets integration tests reference the entry point.
public partial class Program;
