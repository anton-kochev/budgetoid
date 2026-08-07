using Application.Abstractions;
using Application.Passkeys.Verification;
using Domain.Common;
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

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        // One credential, its key and its counter, all derived from the credential so that none of
        // them can be filed against a different one. The repository writes the three in one save.
        Credential credential = Credential.CreatePasskey(userId, now);
        PasskeyPublicKey publicKey = PasskeyPublicKey.Register(
            credential,
            verified.WebAuthnCredentialId,
            verified.CoseKey,
            verified.Algorithm);
        PasskeySignatureCounter counter = PasskeySignatureCounter.Start(credential, verified.SignCount);

        if (!await passkeyRepository.TryAddAsync(credential, publicKey, counter, cancellationToken))
        {
            throw new ConflictException("This authenticator is already registered.");
        }
    }

    // Domain.Common.ValidationException by name, because both layers declare one and only that one is
    // what ValidationExceptionHandler turns into a 400 with the field errors on it.
    private static Domain.Common.ValidationException Refused(string message) =>
        new(new Dictionary<string, string[]> { [ResponseField] = [message] });
}
