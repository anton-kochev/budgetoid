using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Api.Infrastructure;

/// <summary>
/// The scheme a request carrying an identity provider's token authenticates on, named once so that
/// everything asking "did the provider vouch for this caller?" asks it of one value.
/// </summary>
/// <remarks>
/// <para>
/// <b>A name rather than the literal at the route.</b> Registration is the one surface whose policy
/// <em>names</em> a scheme instead of inheriting the fallback, and it names it because an account cannot
/// exist without a completed provider exchange. Writing
/// <see cref="JwtBearerDefaults.AuthenticationScheme"/> at the route would spell the answer to that
/// question as the answer to a different one — which handler validates the bearer — and the two are only
/// the same value until the day sign-in leaves the identity provider.
/// </para>
/// <para>
/// <b>This constant survives the day the registration under it does not.</b> Sign-in has already left
/// the identity provider: the session cookie is the default scheme and the fallback policy names it, so
/// the <c>JwtBearer</c> registration is now reached by exactly one policy — the registration group's,
/// through this name. What registration still needs is a way to say "the provider vouched for this
/// caller", and if that ever becomes a claim gate rather than a scheme, this is the name it will be
/// reached through, so the route table does not have to be edited in the same commit that deletes a
/// handler.
/// </para>
/// </remarks>
public static class ProviderAuthentication
{
    /// <summary>The scheme an identity provider's bearer token is answered by today.</summary>
    public const string SchemeName = JwtBearerDefaults.AuthenticationScheme;
}
