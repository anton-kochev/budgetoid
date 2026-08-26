using Application.Passkeys.CompleteRegistration;
using Application.RecoveryCodes.GenerateRecoveryCodes;
using Domain.Sessions;
using Domain.Users;

namespace Application.Registration;

/// <summary>
/// Everything one consented registration presents: who the provider says the caller is, what the
/// authenticator produced, and every recovery factor's share of the account keys.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two claim members and the rest from the wire, and the split is the security property.</b>
/// <see cref="GoogleSubject"/> and <see cref="Email"/> are read off the request's own authenticated
/// principal by the endpoint and are never bound from the body — a subject a caller could type is an
/// account they could file under somebody else's provider identity, and an address a caller could type
/// is the gate on <c>email_verified</c> made worthless. Keeping <c>System.Security.Claims</c> out of this
/// layer is what puts that read at the endpoint, the way <c>SessionEndpoints</c> reads its session id.
/// </para>
/// <para>
/// <b>Every wire member is <see cref="string"/> and none is <c>required</c></b>, for the argument
/// <see cref="CompleteRegistrationCommand"/> already makes about its own: an absent member binds to
/// <see langword="null"/> despite the declaration and reaches the handler's own refusal — a sentence
/// worded for the person holding the device, raised past the prf gate — instead of a framework 400 that
/// tells somebody whose authenticator genuinely cannot do PRF that their <em>payload</em> was malformed.
/// <see cref="FactorId"/> is a <see cref="string"/> for one more reason: the framework parses more
/// spellings of a uuid than this contract accepts, so a <see cref="Guid"/> member would silently widen
/// the wire format past what <see cref="Application.Security.CanonicalIdentifier"/> allows — and that
/// value is the associated data both envelopes were sealed with.
/// </para>
/// <para>
/// <b><see cref="Codes"/> is ten whole submissions, never ten verifiers beside one factor and one
/// envelope pair.</b> A set is ten separate secrets under a single credential and the client derives a
/// key-encryption key from each <em>code</em>, so one pair for the whole set would seal the account under
/// whichever code that pair belonged to and leave nine of the ten unlocking nothing. It reuses
/// <see cref="RecoveryCodeSubmission"/> rather than declaring a shape of its own, exactly as the
/// generation route does, because a copy would be four declarations able to disagree with that one about
/// which spellings a caller may send. It is named <see cref="Codes"/> for the same reason it is that type:
/// <c>GenerateRecoveryCodesCommand.Codes</c> is the other command that accepts a set, and one name across
/// both is one wire contract — <c>codes</c> — for the two write paths a client has.
/// </para>
/// <para>
/// <b>A set of codes is issued here and not on a later request, and that is not a convenience.</b> An
/// account whose only factor is one passkey is an account whose keys leave with that device; the card is
/// the way back, so it is minted in the same consented act and written in the same save.
/// </para>
/// </remarks>
/// <param name="GoogleSubject">The provider's stable identifier, off the principal's <c>sub</c> claim.</param>
/// <param name="Email">The verified address, off the principal's <c>email</c> claim.</param>
/// <param name="ClientDataJson">
/// <c>response.clientDataJSON</c>, base64url. Its challenge is the only source of the account identifier,
/// which is why it is derived only after the challenge store has answered.
/// </param>
/// <param name="AttestationObject">
/// <c>response.attestationObject</c>, base64url. Under <c>attestation: "none"</c> this carries the
/// authenticator data and an empty statement.
/// </param>
/// <param name="ClientExtensionResults">
/// <c>getClientExtensionResults()</c>, or <see langword="null"/> when the client reported none.
/// </param>
/// <param name="FactorId">
/// The client-minted identifier of the passkey factor, in the lower-case 36-character hyphenated form
/// and no other spelling — see <see cref="WrappedAccountKeys.FactorId"/>.
/// </param>
/// <param name="WrappedContentKey">The account's content key as the passkey factor holds it.</param>
/// <param name="WrappedIndexKey">The account's index key — the same shape, judged by the same rule.</param>
/// <param name="Codes">The card, one whole submission per code.</param>
public sealed record RegisterAccountCommand(
    string GoogleSubject,
    string Email,
    string ClientDataJson,
    string AttestationObject,
    PasskeyClientExtensionResults? ClientExtensionResults,
    string FactorId,
    string WrappedContentKey,
    string WrappedIndexKey,
    IReadOnlyList<RecoveryCodeSubmission> Codes);

/// <summary>
/// What a completed registration has to say for itself: how much of the account the session it opened
/// reaches, and until when.
/// </summary>
/// <remarks>
/// <b>No account id, no credential id, no session id, no factor id and no echo of the address.</b> Each of
/// the identifiers this request brought into existence is either the associated data an envelope was
/// sealed with or a stable handle to something, and a value in a response body is a value in a client log,
/// a proxy cache and a browser's network panel. The address discloses nothing to a caller who sent it, and
/// is left out for the same reason: one more copy of it, for a member no client needs. The session's kind
/// is here because it is the one fact the behaviour can be observed through — see
/// <c>EstablishedSession</c>, which makes the argument for its own two members.
/// </remarks>
public sealed record RegisteredAccount(SessionKind Kind, DateTime ExpiresAtUtc);
