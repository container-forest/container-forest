namespace ContainerForest.Aggregator.Api.Access;

/// <summary>
/// A read-only API token may only read: every change in the API is a POST, PUT, PATCH or DELETE, so those are
/// refused before any endpoint runs. <see cref="Domain.Access.AccessScope.Allows"/> refuses the same in the services.
/// </summary>
internal static class ReadOnlyTokens
{
    /// <summary>Marks an endpoint a read-only token may call with any method, e.g. revoking the token itself.</summary>
    public sealed class AllowedMetadata;

    public static TBuilder AllowReadOnlyTokens<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AllowedMetadata());

    public static IApplicationBuilder UseReadOnlyTokens(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var method = context.Request.Method;
            if (ApiTokenClaims.IsReadOnly(context.User)
                && !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
                && context.GetEndpoint()?.Metadata.GetMetadata<AllowedMetadata>() is null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status403Forbidden,
                        Title = "Forbidden",
                        Detail = "This API token is read-only.",
                    },
                });
                return;
            }

            await next(context);
        });
}
