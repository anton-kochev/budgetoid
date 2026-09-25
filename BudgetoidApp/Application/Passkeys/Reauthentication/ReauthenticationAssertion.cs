namespace Application.Passkeys.Reauthentication;

/// <summary>
/// The response an authenticator produced for a <c>reauthentication</c> challenge, as it crosses the
/// wire: every binary member base64url text.
/// </summary>
/// <remarks>
/// <para>
/// The same five members a sign-in assertion carries, and deliberately no more. There is no ceremony
/// member — the pool a nonce was drawn from is decided by the endpoint that issued it and read back
/// out of the store, never named by a caller — and no instant of any kind. How fresh the proof is is
/// the challenge's own server-issued lifetime, so there is nothing here for a client to assert about
/// time and therefore nothing to trust.
/// </para>
/// <para>
/// <see cref="UserHandle"/> is the one optional member, because a conforming authenticator may omit
/// it. It proves nothing the signature does not already prove; present-and-wrong is still a refusal,
/// since it means the response was assembled out of parts of two ceremonies.
/// </para>
/// </remarks>
public sealed record ReauthenticationAssertion(
    string CredentialId,
    string ClientDataJson,
    string AuthenticatorData,
    string Signature,
    string? UserHandle);
