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
/// so it is not a value either request carries.
/// </para>
/// </remarks>
/// <param name="Email">The address the provider asserted for this caller.</param>
public sealed record BeginAccountRegistrationCommand(string Email);
