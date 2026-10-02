using System.Reflection;
using System.Text.Json.Serialization;
using ContainerForest.Aggregator.Api.Access;
using ContainerForest.Aggregator.Api.Endpoints;
using ContainerForest.Aggregator.Api.ErrorHandling;
using ContainerForest.Aggregator.Api.Grpc;
using ContainerForest.Aggregator.Application;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "CF_");

builder.Services.AddApplication();

// The build-time OpenAPI generator starts the host, hosted services included; it must not create a database.
var isOpenApiGeneration = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
builder.Services.AddInfrastructure(builder.Configuration, runHostedServices: !isOpenApiGeneration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, ClaimsCurrentUser>();
builder.Services.AddSessionAuthentication(builder.Environment);

builder.Services.AddGrpc()
    .AddServiceOptions<BotGatewayService>(options => options.Interceptors.Add<BotGatewayInterceptor>());
builder.Services.AddOptions<BotGatewayOptions>()
    .Bind(builder.Configuration.GetSection(BotGatewayOptions.SectionName))
    .Validate(o => o.IsValid(), "BotGateway:TokenHashes must be SHA-256 hashes: 64 lowercase hex characters.")
    .ValidateOnStart();
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
app.MapGrpcService<BotGatewayService>();
app.MapAuthEndpoints();
app.MapMeEndpoints();
app.MapUserEndpoints();
app.MapEnvironmentEndpoints();
app.MapStackEndpoints();
app.MapNotificationEndpoints();
app.MapDiscordLinkEndpoints();

if (app.Environment.IsDevelopment())
{
    // Authentication here is the password-less dev header: never expose these outside local development.
    app.MapDevEnvironmentEndpoints();
    app.MapDevRegistryEndpoints();
}

app.Run();

// Lets integration tests reference the entry point.
public partial class Program;
