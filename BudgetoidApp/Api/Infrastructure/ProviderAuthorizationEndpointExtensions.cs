namespace Api.Infrastructure;

/// <summary>
/// Declares, on the route table, that a route also requires the identity provider's token.
/// </summary>
public static class ProviderAuthorizationEndpointExtensions
{
    /// <summary>
    /// Adds <see cref="ProviderAuthorizationGate" /> and the <see cref="RequiresProviderAuthorizationMetadata" />
    /// marker that lets a census see it, as one act.
    /// </summary>
    /// <remarks>
    /// It declares no policy and names no scheme, so the route keeps the fallback policy — the session,
    /// and <see cref="FullSessionRequirement" /> — and the provider's token is judged in addition, never
    /// instead.
    /// </remarks>
    public static TBuilder RequireProviderAuthorization<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .WithMetadata(new RequiresProviderAuthorizationMetadata())
            .AddEndpointFilter<TBuilder, ProviderAuthorizationGate>();
    }
}
