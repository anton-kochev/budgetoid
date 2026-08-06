namespace Application.Passkeys.CompleteAssertion;

/// <summary>
/// The response an authenticator produced for a sign-in ceremony.
/// </summary>
/// <param name="CredentialId">
/// The credential handle the authenticator answered with, base64url. Every member of this command is
/// supplied by an unauthenticated caller, so nothing here is trusted until the signature over it has
/// verified against the key stored for this handle.
/// </param>
/// <param name="ClientDataJson">
/// <c>response.clientDataJSON</c>, base64url. Part of what the signature covers.
/// </param>
/// <param name="AuthenticatorData">
/// <c>response.authenticatorData</c>, base64url. The other part of what the signature covers.
/// </param>
/// <param name="Signature"><c>response.signature</c>, base64url.</param>
/// <param name="UserHandle">
/// <c>response.userHandle</c>, base64url, when the authenticator returned one. Optional because a
/// conforming authenticator may omit it; present, it has to name the account the credential belongs
/// to.
/// </param>
public sealed record CompleteAssertionCommand(
    string CredentialId,
    string ClientDataJson,
    string AuthenticatorData,
    string Signature,
    string? UserHandle);
