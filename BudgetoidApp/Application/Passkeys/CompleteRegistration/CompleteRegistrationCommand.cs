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
/// see. It is stored nowhere and believed of nothing — yet registration only completes when it is
/// present and says <c>true</c>, because a device that cannot derive the account's keys is worth
/// turning away while the person is still holding it. Absent, explicitly null, and <c>false</c> are
/// alike refused, and the type is nullable so that all three reach that refusal: a non-nullable
/// <c>bool</c> would make an omitted member mean <c>false</c> by a language default nobody chose,
/// and an explicit <c>null</c> fail deserialization, answering with the framework's generic problem
/// document instead of the sentence this ceremony words for the person holding the device. See
/// <see cref="CompleteRegistrationHandler"/> for why that gate is a product rule rather than an
/// enforced one, and for what a later rule relying on PRF must key on instead.
/// </remarks>
public sealed record PasskeyPrfResults(bool? Enabled);
