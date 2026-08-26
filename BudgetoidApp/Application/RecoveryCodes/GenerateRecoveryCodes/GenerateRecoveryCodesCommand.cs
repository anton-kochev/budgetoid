using Application.Passkeys;
using Application.Passkeys.Reauthentication;
using Domain.Users;

namespace Application.RecoveryCodes.GenerateRecoveryCodes;

/// <summary>
/// Asks for the signed-in account's set of recovery codes to be issued, presenting one whole
/// submission per code and the fresh WebAuthn assertion that authorizes the issue.
/// </summary>
/// <remarks>
/// <para>
/// <b>No account may be named here</b>, the same rule <c>EraseAccountCommand</c> and
/// <c>RevokePasskeyCommand</c> state: the only identity the handler may act on is
/// <see cref="Application.Abstractions.IUserContext.UserId"/>, because a user id declared on a command
/// is an account a caller can choose. Issuing <em>replaces</em>, so a chooseable account here would be
/// a way to destroy a stranger's recovery codes.
/// </para>
/// <para>
/// <b>Ten whole submissions, never ten bare verifiers beside one factor and one pair of envelopes.</b>
/// A set is ten separate secrets filed under a single <c>credentials</c> row, and the client derives a
/// key-encryption key from each <em>code</em> — so ten codes are ten key-encryption keys and ten pairs
/// of envelopes, no one of which can stand for the others. One factor and one pair for the whole set
/// would seal the account under whichever code that pair belonged to: the person redeems any one of
/// the ten, is handed a session, and nine times out of ten unlocks nothing. See
/// <see cref="WrappedAccountKeys"/> and ADR 0018.
/// </para>
/// <para>
/// <b>Verifiers, never codes.</b> The browser mints each code, derives
/// <c>V = HKDF(canonical(code), …)</c> and sends only <c>V</c>; the key-encryption key that sealed that
/// same code's envelopes comes off the same code on an independent HKDF branch, so a code arriving here
/// would hand the operator that key. There is deliberately no member a code could travel in.
/// </para>
/// <para>
/// <c>canonical</c> sits inside the derivation rather than in the ellipsis, because a derivation is
/// not specified until it says what text goes in: the client upper-cases the code, strips the
/// whitespace and hyphens it was grouped with when it was written down, and folds <c>I</c> and
/// <c>L</c> onto <c>1</c> and <c>O</c> onto <c>0</c>. Nothing on this side can check that it did — a
/// verifier derived from the raw text is a perfectly well-formed 32 bytes, and the mismatch surfaces
/// only when the person types the code back and no row answers to it.
/// </para>
/// <para>
/// The assertion is a member of the command rather than a separate call from the endpoint so that
/// issuing without proof is unreachable rather than merely uncustomary: one command, one handler, the
/// gate inside it.
/// </para>
/// </remarks>
/// <param name="Codes">The set, one whole submission per code.</param>
/// <param name="Assertion">The fresh re-authentication the issue is authorized by.</param>
public sealed record GenerateRecoveryCodesCommand(
    IReadOnlyList<RecoveryCodeSubmission> Codes,
    ReauthenticationAssertion Assertion);

/// <summary>
/// One code of a set: the verifier derived from it, and the share of the account keys sealed under the
/// key-encryption key derived from that same code.
/// </summary>
/// <remarks>
/// <para>
/// <b>The three key-custody members belong to the code and not to the set</b>, which is the whole of
/// why this type exists — see <see cref="GenerateRecoveryCodesCommand"/>.
/// </para>
/// <para>
/// <b>Every member is <see cref="string"/>, none is <c>required</c>, and neither is a style choice.</b>
/// Every one of them is text on the wire, the factor identifier included: a <see cref="Guid"/> member
/// would earn a framework 400 on a malformed value, raised before the handler is entered and therefore
/// before the re-authentication gate has run — telling an unproven caller that the server has an
/// opinion about this account's key custody, which is the exact disclosure the handler's
/// gate-before-validation ordering exists to prevent. It would also silently widen the wire format,
/// since the framework parses more spellings of a uuid than this contract accepts. Non-nullable and
/// not <c>required</c> for the same reason: an absent member binds to <see langword="null"/> despite
/// the declaration and is refused past the gate with a sentence, rather than in front of it with a
/// framework 400.
/// </para>
/// <para>
/// <b>Neither envelope is a key.</b> Each is a sealed blob the client wrapped under a key-encryption
/// key derived from the code it minted; the server can open neither and holds no value that could,
/// which is why they may cross this boundary at all when a recovery code may not.
/// </para>
/// </remarks>
/// <param name="Verifier">The verifier derived from this code, as base64url text.</param>
/// <param name="FactorId">
/// The client-minted identifier of the factor this code stands for, and the associated data both
/// envelopes below were sealed with — see <see cref="WrappedAccountKeys.FactorId"/> for why it is
/// deliberately not <c>credentials.id</c>. One uuid in one spelling: the <b>lower-case</b> 36-character
/// hyphenated form with no surrounding whitespace, which is what a <see cref="Guid"/> renders as and
/// therefore what every later read hands back. This contract is cross-client, and a value the browser
/// cannot recognise as the bytes it bound is a code that opens nothing. Enforced by
/// <see cref="Application.Security.CanonicalIdentifier.TryParse"/>, which compares the text against what
/// the parsed value renders as — <see cref="Guid.TryParseExact(string, string, out Guid)"/> under
/// <c>"D"</c> admits upper-case and mixed-case hex and trims whitespace before it reads the format at
/// all, so the format alone does not pin a spelling.
/// </param>
/// <param name="WrappedContentKey">
/// The account's content key as this code holds it: one base64url envelope, judged by
/// <see cref="WrappedKeyEnvelope.TryDecode"/>. It is wrapped under a key-encryption key the client
/// derived from this code, on an HKDF branch independent of the verifier above — so nothing on this
/// command lets the server open it, and nothing may be added that would.
/// </param>
/// <param name="WrappedIndexKey">The account's index key — the same shape, judged by the same rule.</param>
public sealed record RecoveryCodeSubmission(
    string Verifier,
    string FactorId,
    string WrappedContentKey,
    string WrappedIndexKey);
