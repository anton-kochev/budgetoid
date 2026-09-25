using Application.Abstractions;
using Application.Passkeys;
using Application.Passkeys.BeginRegistration;
using Domain.Common;
using Domain.Users;

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
/// on this surface takes its account off the request; this leg's whole premise is that the caller has
/// no account yet. Nothing publishes an identity for it either: the route's policy names the provider
/// scheme, which authenticates a token and resolves nothing in this installation.
/// </para>
/// <para>
/// <b>It does read one row, and that is not a contradiction of the paragraph above.</b> The paragraph is
/// about the request's <em>identity</em>, which this leg still never publishes and never reads. The read
/// is a question about a provider identity the caller has already proved they hold — does it already have
/// an account — and it runs on a connection naming nobody, against a table exempt from row-level security
/// for exactly that case. The refusal it produces is argued at the call site.
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
    IUserRepository userRepository,
    TimeProvider timeProvider) : ICommandHandler<BeginAccountRegistrationCommand, PasskeyCreationOptions>
{
    public async Task<PasskeyCreationOptions> HandleAsync(
        BeginAccountRegistrationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // BEFORE THE NONCE IS ISSUED, AND THE POSITION IS THE WHOLE POINT OF THE CHECK. A refusal raised
        // one line lower answers the identical 409 with the identical sentence and leaves a row in
        // webauthn_challenges that nothing will ever spend; worse, it would say this fix is about the
        // status code, which it is not. What the finish leg's 409 cannot undo is a passkey: by the time
        // it answers, the browser has run navigator.credentials.create() and the authenticator has saved
        // a credential permanently — WebAuthn gives a relying party no way to delete one it caused to be
        // enrolled. A second registration from the same provider identity therefore used to leave a
        // stray passkey on somebody's phone for an account that was never created and never will be.
        // This is the only place the refusal costs nothing.
        //
        // THE READ IS LEGAL HERE FOR THE REASON THE EXEMPTION EXISTS. `credentials` is exempt from
        // row-level security precisely because it is read BEFORE a request has an identity a policy could
        // be keyed on, and this is that shape exactly: the discovery lookup on (provider, subject),
        // joining nothing to `users`, on a connection naming nobody. This leg publishes no identity — see
        // the class remarks — so there is no owner filter it could have carried instead.
        //
        // AND IT IS NOT AN ACCOUNT-ENUMERATION ORACLE. The route's policy names the provider scheme and
        // nothing else, so the caller holds a provider-verified token for this exact subject; the only
        // question they can ask is whether they themselves are registered, which is a fact about their own
        // identity and one they are entitled to. Substituting somebody else's subject means minting
        // somebody else's token.
        //
        // ONLY THE SUBJECT ARM MOVES, AND THE EMAIL ARM CANNOT FOLLOW IT. Answering
        // EmailAlreadyLinkedMessage needs a read of `users.email`, and `users` is policed by
        // `user_isolation` on the identity this leg has not published — so that read comes back empty at
        // best and dies with 22P02 the moment anything opens a connection expecting a setting that is
        // still ''. An address another Google account already holds therefore stays discoverable only on
        // the finish leg, which means that caller still pays a passkey for the discovery. That is a known
        // limit of this fast path rather than an oversight, and closing it needs somewhere for the email
        // question to be asked without an identity.
        //
        // THE FINISH LEG'S CHECK STAYS. These are two requests with a gap between them, and an account
        // can be created in that gap — by another tab, another device, or a retry of a ceremony already
        // in flight. RegisterAccountHandler's read of the same fact against the unique index is what
        // actually decides; this one stops the common case from costing a passkey and replaces nothing.
        Guid? existingAccountId = await userRepository.FindUserIdByFederatedCredentialAsync(
            Credential.GoogleProvider,
            command.GoogleSubject,
            cancellationToken);

        if (existingAccountId is not null)
        {
            // The finish leg's sentence, byte for byte, because it is one fact reached from two routes —
            // see RegistrationConflicts for why sharing it is a requirement and not tidiness. The id just
            // read is deliberately not mentioned in it and goes no further than this scope.
            //
            // The kind is shared for the same reason and is subject to the same requirement: a client
            // meeting this leg on one visit and the finish leg on the next must branch identically, so
            // the pair travels together and neither may be changed alone.
            throw new ConflictException(
                RegistrationConflicts.SubjectAlreadyRegisteredMessage,
                ConflictKind.SubjectAlreadyRegistered);
        }

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
