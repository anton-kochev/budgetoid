using Application.Abstractions;
using Application.Passkeys.Verification;
using Application.Security;
using Domain.Common;
using Domain.Security;
using Domain.Users;

namespace Application.Passkeys.CompleteRegistration;

/// <summary>
/// Verifies a registration response and files the credential, its public key and its counter.
/// </summary>
/// <remarks>
/// <para>
/// Authenticated throughout, which is what lets every refusal here say what was wrong. The sign-in
/// leg cannot afford that — see <see cref="PasskeyVerificationException"/> — but a person adding a
/// passkey to an account they are already signed in to learns nothing from a real sentence that they
/// could not learn by trying again.
/// </para>
/// <para>
/// <b>Nothing about <c>prf</c> is stored.</b> The claim arrives in the client extension results: it
/// is asserted by the client, it is covered by no signature, the server cannot reproduce it, and the
/// PRF output itself never leaves the client. A column holding it would be a column recording an
/// unverifiable assertion, which is data nothing can act on.
/// </para>
/// <para>
/// Requiring it is therefore a <b>product gate on a claim the server cannot verify</b> — an upper
/// layer restating a rule for error quality, never for enforcement (see the Rule Enforcement section
/// of <c>CLAUDE.md</c> and ADR 0002). There is no attacker for it: the claim is the account holder's
/// own browser describing the account holder's own authenticator, and whoever forges it registers a
/// passkey whose keys they will not be able to derive, harming only themselves. The gate exists so
/// that a device which cannot hold the account's keys is turned away while the person is still
/// standing in front of it, not months later when their data cannot be decrypted.
/// </para>
/// <para>
/// A later change deciding what PRF may gate has to start from that: this flag proves nothing about
/// the authenticator, and any rule keyed on it is a rule a client can satisfy by saying so. Anything
/// that needs to <i>rely</i> on PRF must key on a value derived through PRF that the server can
/// check — never on this flag, and never on the fact that this endpoint refuses without it.
/// </para>
/// <para>
/// <b>The factor's key material is that value, and it is worth saying exactly how far it goes.</b> It
/// is <em>not</em> the check the paragraph above asks for: the server cannot verify that the
/// key-encryption key the private key was wrapped under came out of an authenticator's PRF evaluation
/// rather than out of a constant a client chose, and no member it could be handed would let it. What it
/// does buy is the half that is enforceable here — a factor cannot exist without a wrapped private key
/// and a copy of the account's keys encapsulated to it.
/// <see cref="Domain.Users.WrappedAccountKeys"/>'s two <c>NOT NULL</c> columns and the single save
/// below make "registered, but holding no share of the account" unstorable rather than merely
/// uncustomary, so the failure the prf gate is a product guess about is at least no longer reachable by
/// a client that simply omitted the members.
/// </para>
/// </remarks>
public sealed class CompleteRegistrationHandler(
    IWebAuthnChallengeStore challengeStore,
    IPasskeyRepository passkeyRepository,
    IUserContext userContext,
    IPasskeyCeremonyPolicy policy,
    TimeProvider timeProvider) : ICommandHandler<CompleteRegistrationCommand>
{
    private const string ResponseField = "Response";

    public async Task HandleAsync(
        CompleteRegistrationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Guid userId = userContext.UserId;

        // Bounded like the sign-in leg, and for the same reason rather than a weaker one: this caller
        // is authenticated, but a bearer token says who is asking and nothing about how much work they
        // may ask for, and the decode happens before anything here has looked at what was sent. The
        // ceilings differ from the assertion's because the payload does — the attestation object
        // carries attested credential data and a public key — and both live in PasskeyPayloadLimits.
        //
        // Unlike the assertion, the refusal says which member and why. That difference is the one
        // CompleteRegistrationHandler's own remarks argue for: an authenticated caller learns nothing
        // from a real sentence that trying again would not tell them.
        if (!PasskeyEncoding.TryDecode(
                command.ClientDataJson,
                PasskeyPayloadLimits.ClientDataJsonBytes,
                out byte[]? clientDataJson))
        {
            throw Refused(
                "clientDataJSON was not base64url text within "
                + $"{PasskeyPayloadLimits.ClientDataJsonBytes} bytes.");
        }

        if (!PasskeyEncoding.TryDecode(
                command.AttestationObject,
                PasskeyPayloadLimits.AttestationObjectBytes,
                out byte[]? attestationObject))
        {
            throw Refused(
                "attestationObject was not base64url text within "
                + $"{PasskeyPayloadLimits.AttestationObjectBytes} bytes.");
        }

        // Parsed here only to recover the challenge the response claims to answer; the verifier below
        // parses the same bytes again and is what actually judges them.
        if (!CollectedClientData.Parse(clientDataJson).TryGetValue(out CollectedClientData? clientData, out _))
        {
            throw Refused("clientDataJSON was not the JSON object a ceremony produces.");
        }

        // Spent before the response is verified, so a failed attempt burns the nonce. A challenge that
        // survived a refusal would let a caller keep trying different responses against one issue.
        WebAuthnCeremony? ceremony = await challengeStore.ConsumeAsync(clientData.Challenge, cancellationToken);
        if (ceremony is not WebAuthnCeremony.Registration)
        {
            // Covers four cases on purpose and does not separate them: never issued, already spent,
            // and issued for either of the other two ceremonies. No pool is interchangeable with
            // another — an authentication nonce is minted anonymously, a re-authentication one
            // authorizes destroying an account — and the ceremony a nonce was issued for is what
            // says so.
            throw Refused("The challenge is not a live registration challenge.");
        }

        PasskeyRegistrationExpectations expectations = new()
        {
            // The bytes the store just confirmed it had issued and had not yet spent. The verifier's
            // own challenge comparison is therefore already satisfied — what makes the nonce mean
            // anything is ConsumeAsync above, not this equality.
            Challenge = clientData.Challenge,
            AllowedOrigins = policy.AllowedOrigins,
            RelyingPartyId = policy.RelyingPartyId,
            OfferedAlgorithms = PasskeyCeremonyConstants.OfferedAlgorithms,
        };

        if (!PasskeyRegistrationVerifier.Verify(clientDataJson, attestationObject, expectations)
                .TryGetValue(out VerifiedRegistration? verified, out PasskeyVerificationFailure failure))
        {
            throw Refused($"The registration response was refused: {failure}.");
        }

        // Last, and last on purpose. Everything above judges signed material; this judges a sentence
        // the client wrote about its own device. Checked earlier, a malformed, replayed or
        // wrong-origin response would be told "your authenticator cannot hold the keys" — a lie about
        // the device, and one the person would act on by going to buy another. The claim is weighed
        // only once the response is genuine in every verifiable respect, so that when this sentence is
        // said, the device is the only thing left that it can be about.
        if (command.ClientExtensionResults?.Prf is not { Enabled: true })
        {
            throw Refused(
                "This authenticator cannot hold the account's keys: it did not report an enabled prf "
                + "extension result. Register a passkey from a device whose authenticator supports "
                + "the prf extension — most current phones, laptops and hardware security keys do.");
        }

        // AFTER the prf gate, and the ordering is the same rule that gate's own comment makes about
        // itself rather than a second one. A client that cannot do PRF cannot have produced a wrapped
        // key either, so these three members are very often absent on exactly the requests the gate is
        // for. Judged first, such a request would be told its PAYLOAD was malformed — sending somebody
        // holding a device that genuinely lacks the extension off to debug their client, when what they
        // need to hear is that the device cannot hold the account's keys. The gate says the true thing;
        // these refusals are only reached once it has passed and the payload is the only thing left they
        // can be about.
        //
        // One spelling of the identifier and no more, judged by CanonicalIdentifier because this path and
        // recovery-code generation write the same column and must not drift on what that spelling is.
        // The rule is shared; this sentence is not — it is worded for the person registering a device.
        if (!CanonicalIdentifier.TryParse(command.FactorId, out Guid factorId))
        {
            throw Refused(
                "factorId must be a uuid in the lower-case 36-character hyphenated form with no "
                + "surrounding whitespace, and not the all-zero uuid.");
        }

        // ONE SENTENCE PER MEMBER, STATING THE WHOLE REQUIREMENT, the shape
        // GenerateRecoveryCodesHandler.DecodeAndValidate argues for its own: splitting "not base64url"
        // from "wrong width" from "unknown version" would tell a caller which half of an opaque value it
        // got wrong. The two members are judged separately because they are supplied separately — a
        // handler that decoded one and passed the other through would file whatever a client felt like
        // sending into half of the account's key custody.
        //
        // The width and the version are read off the entity that refuses a row against them, never
        // written out here: a message carrying its own copy of either goes on being confident after the
        // real bound has moved.
        //
        // TWO DECODERS, NOT ONE CALLED TWICE. Two cryptographic suites, two widths, two floors, and the
        // same leading byte — so one type used for both compiles, clears every version case, and
        // measures the encapsulated value against a floor 65 bytes below its own.
        if (!WrappedPrivateKeyEnvelope.TryDecode(command.WrappedPrivateKey, out byte[]? wrappedPrivateKey))
        {
            throw Refused(MalformedWrappedPrivateKey());
        }

        if (!EncapsulatedAccountKeysEnvelope.TryDecode(
                command.EncapsulatedAccountKeys,
                out byte[]? encapsulatedAccountKeys))
        {
            throw Refused(MalformedEncapsulatedAccountKeys());
        }

        // LAST OF THE PAYLOAD REFUSALS, the position RegisterAccountHandler's rung 12 argues for and for
        // its reasons rather than new ones. It is after the prf gate because a client that cannot do PRF
        // has no account keys, so it has no factor key pairs and nothing to seal a manifest with — an
        // absent manifest is very often exactly what a request that gate is for carries, and judged
        // first such a caller would be told their payload was malformed. It is after the three members
        // above because this one value is a statement about the whole factor set they establish.
        //
        // A THIRD FRAMING AND A THIRD DECODER, NOT ONE OF THE TWO ABOVE CALLED AGAIN. A manifest is
        // sealed under the account's content key with no key agreement in front of it, so it carries
        // CiphertextEnvelope where the encapsulated value carries a 65-byte ephemeral point — and both
        // lead with 0x01, so routing it through the neighbour would clear the version case and measure
        // it against a floor 65 bytes above its own.
        //
        // WHAT IS JUDGED IS THE FRAMING AND NOTHING ELSE. The blob is sealed under a key this server has
        // never held, so a manifest naming this passkey, one naming none and 4096 bytes of noise are the
        // same value here. Presence, framing and the epoch are the enforceable half; the set itself is
        // held by the authentication tag and by the client that can verify it.
        if (!FactorManifestEnvelope.TryDecode(command.Manifest, out byte[]? manifestBytes))
        {
            throw Refused(MalformedManifest());
        }

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        // One credential, its key, its counter and its share of the account keys, all derived from the
        // credential so that none of them can be filed against a different one. The repository writes
        // the four in one save.
        Credential credential = Credential.CreatePasskey(userId, now);
        PasskeyPublicKey publicKey = PasskeyPublicKey.Register(
            credential,
            verified.WebAuthnCredentialId,
            verified.CoseKey,
            verified.Algorithm);
        PasskeySignatureCounter counter = PasskeySignatureCounter.Start(credential, verified.SignCount);
        WrappedAccountKeys wrappedAccountKeys = WrappedAccountKeys.For(
            credential,
            factorId,
            wrappedPrivateKey,
            encapsulatedAccountKeys,
            now);

        // LOADED, MUTATED, SAVED — and the shape is the rule rather than a habit.
        // FactorManifest.For(user, bytes, epoch + 1) followed by an Update is the shape a reader reaches
        // for first, and it is the one FactorManifest's own remarks spend a paragraph refusing: a
        // detached instance hands EF original values that are its current ones, so the UPDATE carries
        // WHERE rotation_epoch = <the new value> — which matches nothing against the row it was computed
        // from, and DOES match against a row a racing promotion has already moved to N + 1. That is
        // precisely the statement the concurrency token exists to refuse.
        //
        // A MISS IS AN INTEGRITY VIOLATION AND IS RAISED, NEVER BRANCHED ON AND NEVER REPAIRED HERE.
        // Registration has written a manifest for every account since the table existed and no
        // production environment has ever held a row, so there is no account this can legitimately find
        // nothing for. The two repairs a reader might reach for are both worse than the throw: filing a
        // first manifest would let this route write the account's factor set under an epoch and a blob
        // nothing upstream established, and skipping the promotion would register a factor the manifest
        // does not name — the silent half of the very state this whole path exists to make unreachable.
        // It is not a ValidationException either: nothing the caller sent is wrong, so there is no
        // member to key a 400 on and nothing they could correct.
        FactorManifest factorManifest =
            await passkeyRepository.FindFactorManifestAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException(
                "The account holds no factor manifest, so there is no generation to promote.");

        // Promoted BEFORE the save and NOT inside the repository: the step is a domain rule, and the
        // entity is the only thing that can check it — the successor is measured against the generation
        // this instance was loaded at. The epoch stored is the client's own number, because it is bound
        // into the manifest's associated data; what the server owes is the refusal of anything that is
        // not stored + 1, and that refusal is Promote's.
        factorManifest.Promote(manifestBytes, command.RotationEpoch);

        // The five rows in ONE save. A concurrent promotion that moved the generation between the load
        // above and this line loses on the concurrency token, and the repository answers it as a
        // FactorSetMoved conflict — a 409 telling this caller to re-read the generation and run the
        // ceremony again. That is a DIFFERENT answer from Promote's refusal two lines above, and the
        // difference is the caller: Promote refuses an epoch that was never one greater than stored, so
        // it is a 400 keyed on the member; the token fires on an epoch that WAS right when it was read.
        // The translation lives in the repository because DbUpdateConcurrencyException is EF's and this
        // layer has never heard of it.
        if (!await passkeyRepository.TryAddAsync(
                credential,
                publicKey,
                counter,
                wrappedAccountKeys,
                factorManifest,
                cancellationToken))
        {
            // RegisterAccountHandler's sentence and kind for the same refusal, reached from the other
            // route: the account exists here and is being created there, but the caller does the same
            // thing about it either way.
            throw new ConflictException(
                "This authenticator is already registered.",
                ConflictKind.AuthenticatorAlreadyRegistered);
        }
    }

    /// <summary>
    /// What is required of <c>wrappedPrivateKey</c>, said whole rather than split into which part of it
    /// was wrong.
    /// </summary>
    /// <remarks>
    /// <b>Two sentences rather than one taking the member's name.</b> The helper this replaced was
    /// parameterised because both members were one framing at one width; they are now two suites at two
    /// widths, so the numbers differ too and a shared sentence could only be right about one of them.
    /// A borrowed width reddens nothing — a well-formed sentence stating the wrong number is still a
    /// 400, and the person it misdirects is holding the device.
    /// </remarks>
    private static string MalformedWrappedPrivateKey() =>
        "wrappedPrivateKey must be base64url text decoding to exactly "
        + $"{WrappedAccountKeys.WrappedPrivateKeyLength} bytes carrying AEAD framing version "
        + $"{WrappedAccountKeys.WrappedPrivateKeyVersion}.";

    /// <summary>
    /// What is required of <c>encapsulatedAccountKeys</c>, said whole rather than split into which part
    /// of it was wrong.
    /// </summary>
    /// <remarks>
    /// The twin of <see cref="MalformedWrappedPrivateKey"/>, naming its own suite for the reason stated
    /// there. Every number it reads belongs to the encapsulation framing.
    /// </remarks>
    private static string MalformedEncapsulatedAccountKeys() =>
        "encapsulatedAccountKeys must be base64url text decoding to exactly "
        + $"{WrappedAccountKeys.EncapsulatedAccountKeysLength} bytes carrying encapsulation framing "
        + $"version {WrappedAccountKeys.EncapsulatedAccountKeysVersion}.";

    /// <summary>
    /// What is required of <c>manifest</c>, said whole rather than split into which part of it was
    /// wrong.
    /// </summary>
    /// <remarks>
    /// <b>A range where its two neighbours state a width, and the sentence has to say so.</b> Those two
    /// name one legal size each because their plaintexts are fixed-width keys; a manifest's plaintext
    /// grows with the number of factors it names, so the only bounds that exist are the framing's floor
    /// and the column's cap. <c>RegisterAccountHandler.MalformedManifest</c> is this sentence on the
    /// other path that accepts a manifest, and every number in both is read off the type that refuses a
    /// row against it rather than written out.
    /// </remarks>
    private static string MalformedManifest() =>
        "manifest must be base64url text decoding to between "
        + $"{CiphertextEnvelope.MinimumLength} and {FactorManifest.MaximumBytes} bytes carrying AEAD "
        + $"framing version {CiphertextEnvelope.Version}.";

    // Domain.Common.ValidationException by name, because both layers declare one and only that one is
    // what ValidationExceptionHandler turns into a 400 with the field errors on it.
    private static Domain.Common.ValidationException Refused(string message) =>
        new(new Dictionary<string, string[]> { [ResponseField] = [message] });
}
