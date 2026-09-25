namespace Application.Registration;

/// <summary>
/// Opens the ceremony that creates an account, for a caller the identity provider has vouched for and
/// who has no account at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>The address is asserted rather than supplied.</b> It reaches this command off the <c>email</c>
/// claim the endpoint read from the request's own principal, past the middleware gate that refuses an
/// address the provider will not report as verified. It is not a member a client fills in: what the
/// authenticator lists the credential under is what every later sign-in shows as the account being
/// reached, and a name the caller chose would be a name they could put another person's address in.
/// </para>
/// <para>
/// <b>No account may be named here and none could be.</b> There is nothing to name — the account this
/// ceremony will create does not exist, and its identifier is derived from the challenge this leg mints,
/// so it is not a value either request carries. <see cref="GoogleSubject"/> is not a counter-example: it
/// names a <em>provider identity</em>, not an account, and it is here so the handler can ask whether that
/// identity already has one. A subject may be named for the same reason the address may — it is read off
/// the request's own principal by the endpoint, exactly as the finish leg reads it, and never bound from
/// a body. A subject a caller could type is an account filed under somebody else's provider identity.
/// </para>
/// <para>
/// The two members are ordered <c>subject, email</c> to match
/// <see cref="RegisterAccountCommand"/>, so the pair a reader sees at one call site is the pair they see
/// at the other.
/// </para>
/// </remarks>
/// <param name="GoogleSubject">The provider's stable identifier, off the principal's <c>sub</c> claim.</param>
/// <param name="Email">The address the provider asserted for this caller.</param>
public sealed record BeginAccountRegistrationCommand(string GoogleSubject, string Email);
