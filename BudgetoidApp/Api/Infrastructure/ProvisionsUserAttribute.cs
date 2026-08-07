namespace Api.Infrastructure;

/// <summary>
/// Declares that an authenticated request reaching this route may bring an account into existence.
/// Applied to a route group; a route without it writes nothing at all — it resolves an account that
/// already exists, or the request is refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in, and the polarity is the whole point.</b> Under opt-out the minting set is "everything
/// that did not say otherwise", so a browser still holding a provider token for an account that was
/// erased resurrects it through <c>GET /api/transactions</c> — the very scenario this marker exists to
/// close. Opt-out also fails silently: a new endpoint whose author never heard of the marker mints on
/// sight and nothing anywhere says so. Opt-in fails loudly and safely instead — a group that forgot
/// the marker answers a brand-new user 401, which any integration test catches, and it writes no row
/// on the way past.
/// </para>
/// <para>
/// Named <c>ProvisionsUser</c> rather than <c>ProvisionsAccount</c> because <em>account</em> is
/// already this codebase's word for <c>Domain.Accounts.Account</c>, and this marker sits on
/// <c>MapGroup("/api/accounts")</c>, where that collision would be at its most confusing.
/// "Provisioning" is the word this codebase already uses for bringing a user into existence.
/// </para>
/// <para>
/// Read purely as endpoint metadata: <see cref="UserProvisioningMiddleware" /> asks the endpoint, and
/// every application of it is a <c>WithMetadata</c> call on a route group.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class ProvisionsUserAttribute : Attribute;
