using Domain.Users;

namespace Application.Passkeys.CompleteRegistration;

/// <summary>
/// The response an authenticator produced for a registration ceremony, and the share of the account
/// keys the factor it stands for is to hold.
/// </summary>
/// <remarks>
/// <para>
/// <b>The three key-custody members are declared non-nullable and are deliberately not
/// <c>required</c></b>, matching every other member here and the argument
/// <c>RecoveryCodeGenerationRequest</c> already makes about its own: an absent member binds to
/// <see langword="null"/> despite the declaration and reaches
/// <see cref="CompleteRegistrationHandler"/>'s own refusal, which is a sentence this ceremony worded
/// for the person holding the device. Marking them required would buy a framework 400 instead, raised
/// before the handler has judged anything the authenticator signed — so a device that genuinely cannot
/// do PRF would be told its <em>payload</em> was malformed, which is the wrong thing to send somebody
/// off to debug.
/// </para>
/// <para>
/// <see cref="FactorId"/> is <see cref="string"/> rather than <see cref="Guid"/> for the same reason,
/// and for one more: the framework parses more spellings of a uuid than this contract accepts, so a
/// <see cref="Guid"/> member would silently widen the wire format past what the handler's own rule
/// allows.
/// </para>
/// </remarks>
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
/// <param name="FactorId">
/// The client-minted identifier of the factor this passkey stands for, and the associated data both
/// envelopes below were sealed with — see <see cref="WrappedAccountKeys.FactorId"/> for why it is
/// deliberately not <c>credentials.id</c>. One uuid in one spelling: the 36-character hyphenated form,
/// because this contract is cross-client and a value the browser cannot recognise as the bytes it bound
/// is a factor whose envelopes never open.
/// </param>
/// <param name="WrappedContentKey">
/// The account's content key as this factor holds it: one base64url envelope, judged by
/// <see cref="WrappedKeyEnvelope.TryDecode"/>.
/// </param>
/// <param name="WrappedIndexKey">The account's index key — the same shape, judged by the same rule.</param>
public sealed record CompleteRegistrationCommand(
    string ClientDataJson,
    string AttestationObject,
    PasskeyClientExtensionResults? ClientExtensionResults,
    string FactorId,
    string WrappedContentKey,
    string WrappedIndexKey);

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
