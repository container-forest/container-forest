using System.Reflection;
using System.Text.Json.Serialization;
using ContainerForest.Aggregator.Api.Endpoints;
using ContainerForest.Aggregator.Api.ErrorHandling;
using ContainerForest.Aggregator.Api.Grpc;
using ContainerForest.Aggregator.Application;
using ContainerForest.Aggregator.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "CF_");

builder.Services.AddApplication();

// The build-time OpenAPI generator starts the host, hosted services included; it must not create a database.
var isOpenApiGeneration = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
builder.Services.AddInfrastructure(builder.Configuration, migrateDatabase: !isOpenApiGeneration);

builder.Services.AddGrpc();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.UseExceptionHandler();

app.MapOpenApi();
app.MapGrpcService<AgentGatewayService>();

if (app.Environment.IsDevelopment())
{
    // No authentication yet (security invariant 4): never expose these outside local development.
    app.MapDevEnvironmentEndpoints();
}

app.Run();

// Lets integration tests reference the entry point.
public partial class Program;
