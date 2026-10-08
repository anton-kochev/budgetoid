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
/// Four pools, and the separation of <see cref="Reauthentication"/> is worth the most. A
/// <see cref="Reauthentication"/> nonce is the only thing that authorizes destroying an account, and
/// none of the others may stand in for it. An <see cref="Authentication"/> nonce is minted from
/// an <b>anonymous</b> endpoint, so anyone who can walk a person through a single WebAuthn prompt for
/// this relying party can obtain a correctly signed response over one — accepting it for a sensitive
/// action would make that prompt an erasure. A <see cref="Registration"/> nonce is issued to somebody
/// already signed in, which is precisely the stolen-session adversary re-authentication exists to
/// stop, so it buys nothing either.
/// </para>
/// <para>
/// Splitting a sensitive action out of the re-authentication pool later means a new member here — a
/// fifth ceremony value — never a column on <c>webauthn_challenges</c>, whose exempt column set is
/// pinned for the reason recorded on <c>RowLevelSecurityCoverage</c>. That instruction has been
/// followed once already: <see cref="AccountRegistration"/> is the fourth member, added rather than
/// distinguished by a column or by a flag on an existing pool.
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

    /// <summary>
    /// The ceremony that creates an account: a caller holding a provider token and no account at all
    /// registers the passkey the account will be reached by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately not <see cref="Registration"/>, and the two are not interchangeable in either
    /// direction.</b> That pool is minted for somebody who is already signed in and is adding a device
    /// to an account that exists; this one is minted for a caller who has an identity from a provider
    /// and nothing else. A later commit derives the new account's id from this nonce, so sharing the
    /// pool would let an add-a-device nonce name a brand-new account — the exact replay across a
    /// boundary the pools exist to refuse, and the one an attacker holding a registration challenge
    /// would try.
    /// </para>
    /// <para>
    /// <b>It ships one commit ahead of the route that spends it</b>, so the baseline migration is
    /// regenerated exactly once for the whole of this story rather than once per member of it. Until
    /// that route exists, nothing issues a nonce in this pool and nothing accepts one: the value is
    /// spellable, storable and unreachable, which is a strictly narrower state than the pools that
    /// have callers.
    /// </para>
    /// </remarks>
    AccountRegistration,
}
