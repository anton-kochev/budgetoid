namespace Application.Abstractions;

/// <summary>
/// Which WebAuthn ceremony a challenge was issued for.
/// </summary>
/// <remarks>
/// A challenge is bound to its ceremony because the two are not interchangeable: a nonce issued to
/// register a new authenticator must not be spendable as proof of an existing one, and replaying it
/// across the boundary is exactly what an attacker who obtained a registration challenge would try.
/// </remarks>
public enum WebAuthnCeremony
{
    Registration,
    Authentication,
}
