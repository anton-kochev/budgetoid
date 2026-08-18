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
/// <b>This constant survives that day; the registration under it does not.</b>
/// <c>Program.cs</c> already promises a reader that the <c>JwtBearer</c> registration and the
/// <c>Budgetoid.Bridge</c> policy scheme are deleted when the passkey and recovery-code ceremonies carry
/// sign-in on their own. What registration still needs afterwards is a way to say "the provider vouched
/// for this caller", which by then is a claim gate rather than a scheme — and this is the name that gate
/// will be reached through, so the route table does not have to be edited in the same commit that
/// deletes a handler.
/// </para>
/// <para>
/// Not read by <see cref="UserProvisioningMiddleware"/>, deliberately: that middleware branches on the
/// endpoint's markers and on the published identity, never on which scheme authenticated, for the reason
/// its own remarks give about a list of schemes falling behind the schemes registered.
/// </para>
/// </remarks>
public static class ProviderAuthentication
{
    /// <summary>The scheme an identity provider's bearer token is answered by today.</summary>
    public const string SchemeName = JwtBearerDefaults.AuthenticationScheme;
}
