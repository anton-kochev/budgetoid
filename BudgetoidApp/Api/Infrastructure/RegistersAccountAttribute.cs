namespace Api.Infrastructure;

/// <summary>
/// Declares that this route runs the ceremony that <em>creates</em> an account, so a request reaching it
/// legitimately resolves to nobody. Every other authenticated route answers such a caller 401.
/// </summary>
/// <remarks>
/// <para>
/// <b>It publishes no identity, and that absence is the marker.</b>
/// <see cref="UserProvisioningMiddleware"/>'s arm for this calls the next middleware and does nothing
/// else — it does not resolve, does not mint, and does not name anyone. The account identifier is
/// derived by the handler from the challenge the ceremony was bound to, and published there, <em>after
/// the signature verifies</em>. Publishing anything here would mean naming an account off a provider
/// token before a passkey had proved anything, which is the ordering the whole registration path exists
/// to keep.
/// </para>
/// <para>
/// <b>Opt-in, like <see cref="ProvisionsUserAttribute"/> and <see cref="AcceptsEndedSessionAttribute"/>,
/// and for their reason.</b> A group that forgets this marker refuses a brand-new caller with
/// <see cref="UserProvisioningMiddleware.NoAccountTitle"/> — loud, safe, and caught by the first
/// integration test anybody writes against it. The opt-out polarity would put every route added later on
/// the "a caller with no account may proceed" path by default, which is the silent direction.
/// </para>
/// <para>
/// <b>This marker and <see cref="ProvisionsUserAttribute"/> on one route is a contradiction the
/// middleware cannot honour.</b> They answer questions that read as compatible — "this route serves
/// callers who have no account" and "this route may bring one into existence" — and the middleware reads
/// this one first and returns, so the find-or-create arm is never reached. The route would then create
/// nothing while declaring that it may, and the failure is invisible to everyone who already has an
/// account. The two must stay disjoint.
/// </para>
/// <para>
/// <b>The position of its arm is load-bearing in both directions.</b> It sits <em>below</em> the
/// <c>sub</c>/<c>email</c> and <c>email_verified</c> claim gates, so a registration route is still
/// subject to them — placed above, an account would be created for a caller whose address the provider
/// explicitly declines to assert. And it sits <em>above</em> the resolve, because a caller who is about
/// to register has, by definition, no account to resolve.
/// </para>
/// <para>
/// Read purely as endpoint metadata, like both markers beside it, and applied with a
/// <c>WithMetadata</c> call on a route group. It is deleted in the commit that deletes the
/// middleware — the same commit that collapses the six provisioning markers, since registration having
/// become a consented act is what makes the rest of them unnecessary.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class RegistersAccountAttribute : Attribute;
