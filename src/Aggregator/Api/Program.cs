using System.Reflection;
using System.Text.Json.Serialization;
using ContainerForest.Aggregator.Api.Access;
using ContainerForest.Aggregator.Api.Endpoints;
using ContainerForest.Aggregator.Api.ErrorHandling;
using ContainerForest.Aggregator.Api.Grpc;
using ContainerForest.Aggregator.Api.Live;
using ContainerForest.Aggregator.Application;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Live;
using ContainerForest.Aggregator.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "CF_");

builder.Services.AddApplication();

// Before AddInfrastructure, which falls back to sending nothing.
builder.Services.AddSignalR(options => options.EnableDetailedErrors = builder.Environment.IsDevelopment());
builder.Services.AddSingleton<ILiveUpdates, SignalRLiveUpdates>();

// The build-time OpenAPI generator starts the host, hosted services included; it must not create a database.
var isOpenApiGeneration = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
builder.Services.AddInfrastructure(builder.Configuration, runHostedServices: !isOpenApiGeneration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ClaimsCurrentUser>();
builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<ClaimsCurrentUser>());
builder.Services.AddScoped<IRequestOrigin, HttpRequestOrigin>();
builder.Services.AddScoped<IdentityConfirmer>();
builder.Services.AddSessionAuthentication(builder.Environment);
builder.Services.AddCliAuthorizationRateLimits();
builder.Services.Configure<RateLimiterOptions>(options => options.AddSopsRateLimit().AddSignInRateLimit());
DiscordSignIn.AddTo(builder.Services, builder.Configuration, builder.Environment);

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
app.UseReadOnlyTokens();
app.UseAuthorization();
app.UseRateLimiter();

app.MapOpenApi();
app.MapGrpcService<AgentGatewayService>();
app.MapGrpcService<BotGatewayService>();
app.MapAuthEndpoints();
app.MapPasskeyEndpoints();
app.MapDiscordAuthEndpoints();
app.MapMeEndpoints();
app.MapApiTokenEndpoints();
app.MapSigningKeyEndpoints();
app.MapCliAuthorizationEndpoints();
app.MapUserEndpoints();
app.MapAuditEndpoints();
app.MapIdentityConfirmationEndpoints();
app.MapBackupEndpoints();
app.MapEnvironmentEndpoints();
app.MapAgeKeyEndpoints();
app.MapSopsEndpoints();
app.MapGitConnectionEndpoints();
app.MapStackEndpoints();
app.MapPolicyEndpoints();
app.MapWebhookEndpoints();
app.MapImageUpdateEndpoints();
app.MapNotificationEndpoints();
app.MapDiscordLinkEndpoints();
// A signed-out session must not keep hearing updates on an open WebSocket. The panel's hub is for sessions, not API tokens.
app.MapHub<LiveHub>("/hubs/live", options => options.CloseOnAuthenticationExpiration = true)
    .RequireAuthorization(AuthenticationSetup.SessionPolicy);

app.Run();

// Lets integration tests reference the entry point.
public partial class Program;
