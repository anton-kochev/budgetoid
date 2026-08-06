namespace Application.Abstractions;

/// <summary>
/// Which WebAuthn ceremony a challenge was issued for.
/// </summary>
/// <remarks>
/// <para>
/// A challenge is bound to its ceremony because the pools are not interchangeable: a nonce issued to
/// register a new authenticator must not be spendable as proof of an existing one, and replaying it
/// across the boundary is exactly what an attacker who obtained a registration challenge would try.
/// </para>
/// <para>
/// Three pools, and the third is the one whose separation is worth the most. A
/// <see cref="Reauthentication"/> nonce is the only thing that authorizes destroying an account, and
/// neither of the other two may stand in for it. An <see cref="Authentication"/> nonce is minted from
/// an <b>anonymous</b> endpoint, so anyone who can walk a person through a single WebAuthn prompt for
/// this relying party can obtain a correctly signed response over one — accepting it for a sensitive
/// action would make that prompt an erasure. A <see cref="Registration"/> nonce is issued to somebody
/// already signed in, which is precisely the stolen-session adversary re-authentication exists to
/// stop, so it buys nothing either.
/// </para>
/// <para>
/// Splitting a sensitive action out of the re-authentication pool later means a new member here — a
/// fourth ceremony value — never a column on <c>webauthn_challenges</c>, whose exempt column set is
/// pinned for the reason recorded on <c>RowLevelSecurityCoverage</c>.
/// </para>
/// </remarks>
public enum WebAuthnCeremony
{
    Registration,
    Authentication,

    /// <summary>
    /// Proof, moments old, that the person holding the session also holds the account's authenticator.
    /// Issued only from an authenticated endpoint, and never named by a request.
    /// </summary>
    Reauthentication,
}
