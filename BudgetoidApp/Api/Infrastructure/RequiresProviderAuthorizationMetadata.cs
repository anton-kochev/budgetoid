namespace Api.Infrastructure;

/// <summary>
/// Marks a route on which <see cref="ProviderAuthorizationGate" /> authenticates the identity provider's
/// token beside the session.
/// </summary>
/// <remarks>
/// The filter is invisible to anything reading the route table — an endpoint filter is a delegate, not
/// metadata — so this marker is how a census answers "which routes reach the provider scheme" without
/// reading the policy alone. It is added together with the filter by
/// <see cref="ProviderAuthorizationEndpointExtensions.RequireProviderAuthorization{TBuilder}" /> and has
/// no behaviour of its own: a route carrying the marker without the filter would be a route that lies
/// about its gate, which is why there is no other way to add it.
/// </remarks>
public sealed class RequiresProviderAuthorizationMetadata
{
    internal RequiresProviderAuthorizationMetadata()
    {
    }
}
