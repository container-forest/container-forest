using ContainerForest.Aggregator.Application.Registries;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>
/// Temporary endpoints for an environment's registry credentials until the panel API exists. Like
/// <see cref="DevEnvironmentEndpoints"/>, they act as the X-Dev-User user and are mapped only in Development.
/// Passwords are accepted but never returned.
/// </summary>
internal static class DevRegistryEndpoints
{
    public static IEndpointRouteBuilder MapDevRegistryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dev/environments/{id:guid}/registry-credentials").WithTags("Dev: registry credentials");

        group.MapGet("/", async (Guid id, RegistryCredentialService service, CancellationToken ct) =>
            (await service.ListAsync(EnvironmentId.From(id), ct)).Select(ToResponse));

        // PUT by server: setting the credentials of a registry again replaces them, e.g. after a token rotation.
        group.MapPut("/", async Task<IResult> (Guid id, SetRegistryCredentialRequest request, RegistryCredentialService service, CancellationToken ct) =>
        {
            var (credential, created) = await service.SetAsync(
                EnvironmentId.From(id), request.Server, request.Username, request.Password, ct);
            var response = ToResponse(credential);
            return created
                ? TypedResults.Created($"/api/dev/environments/{id}/registry-credentials/{credential.Id}", response)
                : TypedResults.Ok(response);
        });

        group.MapDelete("/{credentialId:guid}", async (Guid id, Guid credentialId, RegistryCredentialService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(EnvironmentId.From(id), RegistryCredentialId.From(credentialId), ct);
            return TypedResults.NoContent();
        });

        return app;
    }

    private static RegistryCredentialResponse ToResponse(RegistryCredentialSummary c) =>
        new(c.Id.Value, c.Server, c.Username, c.CreatedAt, c.UpdatedAt);

    /// <param name="Server">Registry host, e.g. "ghcr.io", "registry.example.com:5000" or "docker.io".</param>
    /// <param name="Password">Password or access token with read access to the registry.</param>
    public sealed record SetRegistryCredentialRequest(string Server, string Username, string Password);

    public sealed record RegistryCredentialResponse(Guid Id, string Server, string Username, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
}
