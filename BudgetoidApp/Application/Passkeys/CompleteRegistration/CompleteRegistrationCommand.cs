namespace Application.Passkeys.CompleteRegistration;

/// <summary>
/// The response an authenticator produced for a registration ceremony.
/// </summary>
/// <param name="ClientDataJson">
/// <c>response.clientDataJSON</c>, base64url. It is decoded and parsed where it lands and never
/// re-encoded — the bytes are what the ceremony was bound to.
/// </param>
/// <param name="AttestationObject">
/// <c>response.attestationObject</c>, base64url. Under <c>attestation: "none"</c> this carries the
/// authenticator data and an empty statement.
/// </param>
/// <param name="ClientExtensionResults">
/// <c>getClientExtensionResults()</c>, or null when the client reported none.
/// </param>
public sealed record CompleteRegistrationCommand(
    string ClientDataJson,
    string AttestationObject,
    PasskeyClientExtensionResults? ClientExtensionResults);

/// <summary>The client extension results a registration response may carry.</summary>
public sealed record PasskeyClientExtensionResults(PasskeyPrfResults? Prf);

/// <summary>
/// What the client says the <c>prf</c> extension did.
/// </summary>
/// <remarks>
/// Asserted by the client, covered by no signature, and not derivable from anything the server can
/// see. It is reported, never stored — see <c>RegisteredPasskey.PrfEnabled</c>.
/// </remarks>
public sealed record PasskeyPrfResults(bool Enabled);
