using Application.Abstractions;
using Application.Passkeys;
using Application.Passkeys.BeginRegistration;

namespace Application.Registration;

/// <summary>
/// Issues the nonce an account is created against, and the credential creation options bound to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The <c>AccountRegistration</c> pool, never <c>Registration</c>.</b> That one is minted for somebody
/// already signed in who is adding a device to an account that exists; this one is minted for a caller
/// who has an identity from a provider and nothing else. The account's identifier is derived from these
/// bytes, so a shared pool would let an add-a-device nonce name a brand-new account — the exact replay
/// across a boundary the pools exist to refuse. See <see cref="WebAuthnCeremony.AccountRegistration"/>.
/// </para>
/// <para>
/// <b>It reads no <see cref="IUserContext"/>, because there is nobody to read.</b> Every other ceremony
/// on this surface takes its account off the request; this leg's whole premise is that the request
/// resolves to no account, which is what <c>RegistersAccountAttribute</c> declares to the middleware.
/// </para>
/// <para>
/// <see cref="PasskeyCreationOptions"/> is reused whole rather than copied into a record of this
/// feature's own: these members are WebAuthn's, and a second declaration of them would be two contracts
/// able to disagree about a payload the browser parses to one specification.
/// </para>
/// </remarks>
public sealed class BeginAccountRegistrationHandler(
    IWebAuthnChallengeStore challengeStore,
    IPasskeyCeremonyPolicy policy,
    TimeProvider timeProvider) : ICommandHandler<BeginAccountRegistrationCommand, PasskeyCreationOptions>
{
    public async Task<PasskeyCreationOptions> HandleAsync(
        BeginAccountRegistrationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        IssuedChallenge issued = await challengeStore.IssueAsync(
            WebAuthnCeremony.AccountRegistration,
            cancellationToken);

        return new PasskeyCreationOptions
        {
            Challenge = PasskeyEncoding.Encode(issued.Challenge.Span),
            Rp = new PasskeyRelyingParty(policy.RelyingPartyId, policy.RelyingPartyName),

            // THE HANDLE THE AUTHENTICATOR WILL STORE, DERIVED FROM THE NONCE JUST ISSUED. It is the id
            // the account row will be written under when the ceremony finishes, and the two are made
            // equal by construction rather than by carrying a value between two requests — see
            // RegistrationAccountId. An authenticator keeps the handle it was given, so an account
            // created under any other value answers no assertion that device will ever produce, silently
            // and permanently.
            //
            // Deriving here is safe in a way deriving on the finish leg would not be: these bytes came
            // out of the store a line above rather than off a payload a caller sent.
            User = new PasskeyUser(
                PasskeyEncoding.Encode(
                    PasskeyEncoding.ToUserHandle(RegistrationAccountId.For(issued.Challenge.Span))),
                command.Email,
                command.Email),
            PubKeyCredParams = [.. PasskeyCeremonyConstants.OfferedAlgorithms.Select(
                algorithm => new PasskeyCredentialParameter(
                    PasskeyCeremonyConstants.PublicKeyCredentialType,
                    (int)algorithm))],
            Timeout = PasskeyCeremonyTimeout.Milliseconds(
                policy.CeremonyTimeout,
                issued.ExpiresAtUtc,
                timeProvider),
            Attestation = PasskeyCeremonyConstants.NoAttestation,
            AuthenticatorSelection = new PasskeyAuthenticatorSelection(
                PasskeyCeremonyConstants.RequiredResidentKey,
                RequireResidentKey: true,
                PasskeyCeremonyConstants.RequiredUserVerification),

            // EMPTY, AND IT STAYS EMPTY. Three reasons, and no one of them alone would settle it.
            //
            // There is nothing to exclude: the account does not exist, so it holds no credential an
            // authenticator could be asked to decline enrolling a second time.
            //
            // The read that would produce a list has no acceptable shape. Scoped to the derived account
            // id it is always empty — that account has no rows — and unscoped it is an enumeration of
            // every handle in the table, which is precisely what the passkey_public_keys row-level
            // security exemption was argued as NOT permitting (ADR 0012, and the owner filter
            // PasskeyRepository.ListWebAuthnCredentialIdsForUserAsync carries for that reason).
            //
            // And a non-empty list here would refuse a legitimate act. A WebAuthn credential is keyed on
            // (rpId, user handle), and every registration mints a fresh handle, so somebody opening a
            // second account from the same laptop is doing something this product allows — with an
            // exclusion list they would be turned away at the authenticator, by an error the server never
            // sees and cannot explain.
            ExcludeCredentials = [],
            Extensions = new PasskeyRegistrationExtensions(),
        };
    }
}
