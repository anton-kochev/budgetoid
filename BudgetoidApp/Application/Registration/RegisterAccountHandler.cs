using Application.Abstractions;
using Application.Passkeys;
using Application.Passkeys.Verification;
using Application.RecoveryCodes;
using Application.Security;
using Application.Sessions;
using Application.Users;
using Domain.Budgets;
using Domain.Common;
using Domain.Sessions;
using Domain.Users;

// Domain.Common.ValidationException by name, because both rings declare one and only that one is what
// ValidationExceptionHandler turns into a 400 with the field errors on it. Aliased rather than qualified
// at each throw, the shape RecoveryCodeSetValidation already uses.
using ValidationException = Domain.Common.ValidationException;

namespace Application.Registration;

/// <summary>
/// Creates the whole account: verifies the registration ceremony a caller holding a provider token ran,
/// files the passkey, the card of recovery codes and every factor's share of the account keys, and signs
/// the person in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order of the steps below is the security property, not an implementation detail</b>, and each
/// one carries the reason it sits where it does. <c>CompleteRegistrationHandler</c> is the ladder this
/// mirrors, and where a rung's argument is already written out there this one points at it rather than
/// re-deriving it — two copies of an argument are two things to keep in step.
/// </para>
/// <para>
/// <b>Three rungs of the ladder are not here, they are the first three, and they are not coming.</b>
/// That the request carries a live provider token is judged by the route's own policy, which names the
/// identity provider's scheme and nothing else. That the principal has a <c>sub</c> and an <c>email</c>,
/// and that the provider asserts the address as verified, are judged by <c>RegistrationClaimGate</c>, an
/// endpoint filter on that same group — which is why the two claim members arrive on the command rather
/// than being re-read here. That filter is now the only thing judging them: the provisioning middleware
/// that carried a second copy is gone, and the copies never disagreed because they ran together for one
/// commit and the tests compared against one pair of constants.
/// </para>
/// <para>
/// <b>An earlier version of these remarks promised those three would come down into this ring, and that
/// promise is corrected rather than kept.</b> Judging <c>email_verified</c> here needs one of two
/// things and may have neither: a <c>ClaimsPrincipal</c> in this project, against the rule the
/// registration endpoint states where it reads the two claim members off the principal at the call site
/// — the rule that keeps <c>System.Security.Claims</c> out of this ring altogether — or a member on
/// <see cref="RegisterAccountCommand"/> for the answer to land in, which the users-and-ownership
/// documentation argues against by name: the verified-email claim is read and never stored, and the
/// command carries only the subject and the address precisely so there is nowhere for it to go. The
/// gates therefore stay at the boundary that already holds the principal.
/// <c>RegistrationClaimGate</c> carries the whole argument, including why an authorization requirement
/// and a scheme event were each refused.
/// </para>
/// <para>
/// <b>No <see cref="ITransactionalExecutor"/> wraps the write, and that is a correctness ruling rather
/// than a cost one.</b> Such an executor opens its transaction through <c>CreateExecutionStrategy()</c>,
/// and <c>BeginTransactionAsync</c> is what opens the connection — which is when
/// <c>SessionContextInterceptor</c> writes <c>app.current_user_id</c>. A wrap whose delegate contains the
/// publication at rung 13 therefore configures the connection while the setting is still empty, and the
/// <c>users</c> INSERT meets <c>''::uuid</c> in its <c>WITH CHECK</c>: a <c>22P02</c>. Inside a
/// transaction the interceptor runs exactly once, at the begin, so nothing later in the delegate can
/// repair a connection configured before the identity existed — and the size of the write changes
/// none of it. The provisioning path that used to carry this same ruling made it for three rows;
/// it is unchanged at thirty. Publishing
/// outside the wrap fixes it at the price of an ordering rule nobody may re-break and a retry that must
/// not re-publish; one save through <see cref="IRegistrationRepository.RegisterAsync"/> carries no such
/// rule. This is the single most likely thing a later reader "improves".
/// </para>
/// <para>
/// <b>The session is established over the passkey credential and never over the recovery-codes one.</b>
/// Both open a <see cref="SessionKind.Full"/> session, so the mistake satisfies every check constraint,
/// every foreign key and every test that reads the response — what it changes is which credential a later
/// revocation sweeps. Revoking the passkey would then leave the session standing, and replacing the card
/// would sign the person out of a session their passkey opened.
/// </para>
/// </remarks>
public sealed class RegisterAccountHandler(
    IWebAuthnChallengeStore challengeStore,
    IPasskeyCeremonyPolicy policy,
    IRegistrationRepository registrationRepository,
    IUserRepository userRepository,
    IUserContextWriter userContextWriter,
    TimeProvider timeProvider) : ICommandHandler<RegisterAccountCommand, Issued<RegisteredAccount>>
{
    private const string ResponseField = "Response";

    /// <summary>
    /// What a caller whose address another account already holds is told when <c>IX_users_email</c>
    /// refuses the save.
    /// </summary>
    /// <remarks>
    /// The wording used to be shared verbatim with the provisioning path, which reached the same rule
    /// from the other direction; that path is gone and this is now the only sentence for it. It is kept
    /// as written because it says what a person can act on — a different Google account holds this
    /// address — without saying which account, which would report on somebody else's registration.
    /// </remarks>
    private const string EmailAlreadyLinkedMessage =
        "This email address is already linked to a different Google account.";

    /// <summary>
    /// <c>PasskeyRepository.TryAddAsync</c>'s sentence for a WebAuthn handle already enrolled, verbatim.
    /// </summary>
    private const string AuthenticatorAlreadyRegisteredMessage = "This authenticator is already registered.";

    /// <summary>
    /// The sentence both writes of <c>wrapped_account_keys</c> already answer a claimed factor identifier
    /// with — <c>PasskeyRepository.TryAddAsync</c> and <c>RecoveryCodeRepository.AddSetAsync</c> — and this
    /// is the third copy of it.
    /// </summary>
    /// <remarks>
    /// Written out rather than read off either, because those are private constants in a ring this one may
    /// not reference. One fact about one table reached from three routes: the identifier a client chose is
    /// spoken for, nothing was written, and re-sending the envelopes cannot work because the old identifier
    /// was the associated data they were sealed with. Change one and change all three.
    /// </remarks>
    private const string FactorAlreadyRegisteredMessage =
        "That factor identifier is already registered. Mint a fresh one, wrap the account keys under it, "
        + "and run the ceremony again.";

    public async Task<Issued<RegisteredAccount>> HandleAsync(
        RegisterAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 1 and 2. Bounded before anything is validated or decoded, for the reason
        //    CompleteRegistrationHandler gives about its own two: the decode happens before anything here
        //    has looked at what was sent, and a provider token says who is asking and nothing about how
        //    much work they may ask for. The ceilings live in PasskeyPayloadLimits with the protocol
        //    reasoning for each.
        if (!PasskeyEncoding.TryDecode(
                command.ClientDataJson,
                PasskeyPayloadLimits.ClientDataJsonBytes,
                out byte[]? clientDataJson))
        {
            throw Refused(
                ResponseField,
                "clientDataJSON was not base64url text within "
                + $"{PasskeyPayloadLimits.ClientDataJsonBytes} bytes.");
        }

        if (!PasskeyEncoding.TryDecode(
                command.AttestationObject,
                PasskeyPayloadLimits.AttestationObjectBytes,
                out byte[]? attestationObject))
        {
            throw Refused(
                ResponseField,
                "attestationObject was not base64url text within "
                + $"{PasskeyPayloadLimits.AttestationObjectBytes} bytes.");
        }

        // 3. Parsed here only to recover the challenge the response claims to answer; the verifier below
        //    parses the same bytes again and is what actually judges them.
        if (!CollectedClientData.Parse(clientDataJson).TryGetValue(out CollectedClientData? clientData, out _))
        {
            throw Refused(ResponseField, "clientDataJSON was not the JSON object a ceremony produces.");
        }

        // 4. THE NONCE IS BURNT FROM HERE ON, AND IT IS SPENT BEFORE THE RESPONSE IS VERIFIED. Consuming
        //    afterwards would leave every refusal below replayable, so a caller could grind responses
        //    against one issued challenge — and on this path the challenge is also what the account
        //    identifier is derived from, so a replayable one is a replayable account id.
        WebAuthnCeremony? ceremony = await challengeStore.ConsumeAsync(clientData.Challenge, cancellationToken);
        if (ceremony is not WebAuthnCeremony.AccountRegistration)
        {
            // ONE UNDIFFERENTIATED REFUSAL covering six cases: never issued, already spent, expired, and
            // issued for any of the other three pools. No pool is interchangeable with another — an
            // add-a-device nonce is minted for somebody already signed in, an authentication nonce is
            // minted anonymously, a re-authentication one authorizes destroying an account — and the
            // ceremony a nonce was issued for is what says so.
            throw Refused(ResponseField, "The challenge is not a live account-registration challenge.");
        }

        PasskeyRegistrationExpectations expectations = new()
        {
            // The bytes the store just confirmed it had issued and had not yet spent, so the verifier's
            // own challenge comparison is already satisfied. What makes the nonce mean anything is the
            // ConsumeAsync above, not this equality.
            Challenge = clientData.Challenge,
            AllowedOrigins = policy.AllowedOrigins,
            RelyingPartyId = policy.RelyingPartyId,
            OfferedAlgorithms = PasskeyCeremonyConstants.OfferedAlgorithms,
        };

        // 5. The signature, the origin, the relying party and the algorithm.
        if (!PasskeyRegistrationVerifier.Verify(clientDataJson, attestationObject, expectations)
                .TryGetValue(out VerifiedRegistration? verified, out PasskeyVerificationFailure failure))
        {
            throw Refused(ResponseField, $"The registration response was refused: {failure}.");
        }

        // 6. THE PRF GATE, AND ITS POSITION IS CompleteRegistrationHandler'S ARGUMENT RATHER THAN A SECOND
        //    ONE. Everything above judges signed material; this judges a sentence the client wrote about
        //    its own device, so it is weighed only once the response is genuine in every verifiable
        //    respect — when the device is the only thing left that it can be about. The sentence is that
        //    handler's, verbatim, because it is said to the same person about the same hardware.
        if (command.ClientExtensionResults?.Prf is not { Enabled: true })
        {
            throw Refused(
                ResponseField,
                "This authenticator cannot hold the account's keys: it did not report an enabled prf "
                + "extension result. Register a passkey from a device whose authenticator supports "
                + "the prf extension — most current phones, laptops and hardware security keys do.");
        }

        // 7 to 10. AFTER THE PRF GATE, for the reason CompleteRegistrationHandler.cs states about its own
        //    key-custody members: a client that cannot do PRF cannot have produced a wrapped key either,
        //    so these members are very often absent on exactly the requests that gate is for. Judged
        //    first, such a request would be told its PAYLOAD was malformed, sending somebody holding a
        //    device that genuinely lacks the extension off to debug their client.
        //
        //    Each refusal is keyed under the member the caller can correct, which is what separates rungs
        //    8 and 9 from the ten envelope pairs inside rung 10: the passkey's two are corrected under
        //    their own names, and a code's under that code's own submission.
        if (!CanonicalIdentifier.TryParse(command.FactorId, out Guid factorId))
        {
            throw Refused(
                nameof(RegisterAccountCommand.FactorId),
                "factorId must be a uuid in the lower-case 36-character hyphenated form with no "
                + "surrounding whitespace, and not the all-zero uuid.");
        }

        // ONE SENTENCE PER MEMBER, STATING THE WHOLE REQUIREMENT — splitting "not base64url" from "wrong
        // width" from "unknown version" would tell a caller which half of an opaque value it got wrong.
        // The two are judged separately because they are supplied separately: a handler that decoded one
        // and passed the other through would file whatever a client felt like sending into half of the
        // account's key custody.
        if (!WrappedKeyEnvelope.TryDecode(command.WrappedContentKey, out byte[]? wrappedContentKey))
        {
            throw Refused(
                nameof(RegisterAccountCommand.WrappedContentKey),
                MalformedEnvelope("wrappedContentKey"));
        }

        if (!WrappedKeyEnvelope.TryDecode(command.WrappedIndexKey, out byte[]? wrappedIndexKey))
        {
            throw Refused(
                nameof(RegisterAccountCommand.WrappedIndexKey),
                MalformedEnvelope("wrappedIndexKey"));
        }

        // The card, judged by the one definition every write path that accepts a set shares — how many
        // codes a set holds, what each member of a submission must decode to, that no two verifiers repeat
        // and that no two factor identifiers do. What this call site owns is the ordering above and the
        // member the refusals are keyed under, which is this command's own.
        IReadOnlyList<PresentedCode> presented = RecoveryCodeSetValidation.DecodeAndValidate(
            command.Codes,
            nameof(RegisterAccountCommand.Codes));

        // 11. THE ELEVENTH FACTOR AGAINST THE TEN, AND IT IS NOT RUNG 10'S DISTINCTNESS CHECK. That one
        //     compares the ten among themselves; this compares the passkey's identifier against them. Left
        //     to PK_wrapped_account_keys it arrives mid-save as a conflict whose sentence tells the caller
        //     an identifier is already registered — naming a factor nobody has registered, on a request
        //     that was merely wrong, and sending them looking for a request they never made.
        if (presented.Any(code => code.FactorId == factorId))
        {
            throw Refused(
                nameof(RegisterAccountCommand.FactorId),
                "The passkey's factor identifier must differ from every recovery code's.");
        }

        // 12. AFTER RUNG 4, AND THE POSITION IS THE WHOLE OF WHAT MAKES THIS VALUE UNCHOOSABLE. The only
        //     source of the challenge bytes on this leg is the client's own clientDataJSON, so a
        //     derivation before the store has answered would be derived from a value the caller supplied —
        //     an account identifier of their choosing, wearing the shape of one this server minted.
        //     RegistrationAccountId carries the argument; this call site owes the reader the pointer.
        Guid accountId = RegistrationAccountId.For(clientData.Challenge.Span);

        // 13. BEFORE THE INSERT, NEVER AFTER. app.current_user_id reaches the database on the next
        //     connection open, and the users INSERT is checked against it, so an identity published
        //     afterwards is an identity that statement ran without — every policed row in the save below
        //     would meet ''::uuid and the request would die with 22P02. It is also only published now, and
        //     not off the provider token at the top: naming an account before the signature verified would
        //     be trusting a value the caller sent.
        userContextWriter.ResolveUser(accountId);

        // ONE `now` FOR EVERY ROW. The account, its budget, its three credentials, the passkey's key
        // material and counter, ten hashes, eleven shares of the account keys and the session all come
        // into existence in one save, so they carry one creation instant.
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        User user = User.CreateWithId(accountId, command.Email, now);

        // MINTED HERE, AND THIS IS THE ONE NARRATIVE ROW IDENTIFIER THIS SERVER CHOOSES. Every other
        // one arrives from the browser, because the browser sealed a value against it: a row's id is
        // the associated data its envelopes were bound with, so a server that picked the id would pick
        // an id no client can rebuild and the value would never open again. This budget carries no
        // name — CreateDefault is the nameless path — so there is nothing sealed and nothing to seal
        // against, and the browser has no basis on which to choose. Budget.Create, the naming path, has
        // no minting overload precisely so that this line has to be written out rather than defaulted
        // into.
        Budget defaultBudget = Budget.CreateDefault(Guid.CreateVersion7(), user.Id, now);
        Credential federated = Credential.CreateFederated(
            user.Id,
            Credential.GoogleProvider,
            command.GoogleSubject,
            now);
        Credential passkey = Credential.CreatePasskey(user.Id, now);
        Credential recoveryCodes = Credential.CreateRecoveryCodes(user.Id, now);

        PasskeyPublicKey publicKey = PasskeyPublicKey.Register(
            passkey,
            verified.WebAuthnCredentialId,
            verified.CoseKey,
            verified.Algorithm);
        PasskeySignatureCounter counter = PasskeySignatureCounter.Start(passkey, verified.SignCount);

        // One row per code, each hashed by the entity and filed against the ONE credential standing for
        // the whole set.
        IReadOnlyList<RecoveryCodeHash> hashes =
            [.. presented.Select(code => RecoveryCodeHash.From(recoveryCodes, code.Verifier, now))];

        // ELEVEN ROWS: the passkey's pair, then one pair per code. A factor is not a credential — each
        // code derives its own key-encryption key, and a person redeems whichever code they still hold —
        // so a single pair for the card would seal the account under one code and leave the other nine
        // unlocking nothing, with a session handed over either way and nothing red until a browser months
        // later.
        //
        // The card's rows are projected from the one validated list rather than zipped from three, so a
        // code's factor identifier and a code's envelopes cannot come apart. That is the load-bearing
        // pairing: the identifier is the associated data the client sealed both envelopes with, so a row
        // holding its neighbour's envelopes rebuilds associated data reproducing neither seal, and that
        // factor opens nothing — ever, for anybody — with every constraint the database holds satisfied.
        // Discovered by somebody who redeemed a code, was handed a session, and found the account still
        // locked.
        //
        // The verifier is the member that could be permuted harmlessly, and knowing which half is which
        // is the point of saying so: it lands in `recovery_code_hashes`, which carries no `factor_id` and
        // no link of any kind to `wrapped_account_keys`, so a set whose verifiers were shuffled against
        // its identifier-and-envelope triples redeems and unwraps exactly like a correct one. The
        // projection still covers all four members, because a projection is cheaper than a rule about
        // which of them may be zipped.
        //
        // The passkey's pair is filed against `passkey` and the card's against `recoveryCodes`, never both
        // against whichever credential is nearest to hand: the two factors derive different
        // key-encryption keys, and a misfiled row satisfies every check constraint and every foreign key
        // here.
        IReadOnlyList<WrappedAccountKeys> wrappedAccountKeys =
        [
            WrappedAccountKeys.For(passkey, factorId, wrappedContentKey, wrappedIndexKey, now),
            .. presented.Select(code => WrappedAccountKeys.For(
                recoveryCodes,
                code.FactorId,
                code.WrappedContentKey,
                code.WrappedIndexKey,
                now)),
        ];

        // OVER THE PASSKEY CREDENTIAL, NEVER THE RECOVERY-CODES ONE — see the class remarks for what the
        // mistake costs and why nothing catches it. Built from the Credential and never from a kind named
        // here: Session.Establish derives the kind from the credential's type, which is what makes "a
        // sign-in reaching more of the account than its credential may" unrepresentable.
        Session session = Session.Establish(passkey, now, now + SessionPolicy.Lifetime);

        // Nothing about this handle leaves the process unless the save commits: the two things it offers
        // are "file your digest against this session" and "state yourself for the client", and only the
        // endpoint on the far side of a successful return can reach the second.
        SessionHandle handle = SessionHandle.Mint();

        // Qualified, because this file's own namespace is called Registration and a simple name would
        // bind to it rather than to the record.
        RegistrationOutcome outcome = await registrationRepository.RegisterAsync(
            new Domain.Users.Registration(
                user,
                defaultBudget,
                federated,
                passkey,
                recoveryCodes,
                publicKey,
                counter,
                hashes,
                wrappedAccountKeys,
                session,
                handle.TokenFor(session)),
            cancellationToken);

        if (outcome is not RegistrationOutcome.Registered)
        {
            throw await RefusalFor(outcome, command.GoogleSubject, cancellationToken);
        }

        // The handoff travels BESIDE the result. RegisteredAccount is what the response says, and the
        // handle leaves the server in one place only — the cookie the endpoint writes, which is HttpOnly
        // precisely so nothing else carries it.
        return new Issued<RegisteredAccount>(
            new RegisteredAccount(session.Kind, session.ExpiresAtUtc),
            handle.IssuedFor(session));
    }

    /// <summary>
    /// Turns a losing save into the sentence the caller can act on, resolving the one outcome the
    /// repository cannot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="RegistrationOutcome.EmailTaken"/> is ambiguous and this is where it is settled.</b> A
    /// losing insert can breach the credential's <c>(provider, subject)</c> and the email at once, and
    /// PostgreSQL names only one of them, picked by the order the rows are written rather than by what
    /// happened — which is why <see cref="IRegistrationRepository"/>'s two-name filter reports the
    /// collision without claiming which rule it was. EF writes <c>users</c>
    /// before <c>credentials</c>, so the credential index being named means the email did not collide and
    /// is unambiguous; the email index being named says nothing about the subject. Only a re-read of the
    /// credential separates the two, and it is sound because of what a <em>reported</em> unique violation
    /// implies: under read committed the losing insert waits on the conflicting transaction and would
    /// have succeeded had it aborted, so the violation being raised at all means that transaction
    /// committed and a winning credential on this subject is visible by now. Finding none proves the
    /// subject was never duplicated and the email alone collided.
    /// </para>
    /// <para>
    /// The read runs on <c>credentials</c>, which is exempt from row-level security, so it is unaffected
    /// by the identity published at rung 13 naming a row that was never written.
    /// </para>
    /// </remarks>
    private async Task<Exception> RefusalFor(
        RegistrationOutcome outcome,
        string googleSubject,
        CancellationToken cancellationToken)
    {
        switch (outcome)
        {
            case RegistrationOutcome.SubjectTaken:
                return new ConflictException(
                    RegistrationConflicts.SubjectAlreadyRegisteredMessage,
                    ConflictKind.SubjectAlreadyRegistered);

            case RegistrationOutcome.EmailTaken:
                Guid? winnerId = await userRepository.FindUserIdByFederatedCredentialAsync(
                    Credential.GoogleProvider,
                    googleSubject,
                    cancellationToken);

                // THE KIND BRANCHES WITH THE SENTENCE, and this is the one site in the product where a
                // single construction answered two different facts. The read above is what separates
                // them, so the ternary that chose the message has to choose the kind too — pairing one
                // kind with both sentences would tell somebody who CANNOT get in (the address belongs to
                // another Google identity, so no passkey or code of theirs opens it) to go and sign in.
                return winnerId is null
                    ? new ConflictException(EmailAlreadyLinkedMessage, ConflictKind.EmailAlreadyLinked)
                    : new ConflictException(
                        RegistrationConflicts.SubjectAlreadyRegisteredMessage,
                        ConflictKind.SubjectAlreadyRegistered);

            case RegistrationOutcome.AuthenticatorTaken:
                return new ConflictException(
                    AuthenticatorAlreadyRegisteredMessage,
                    ConflictKind.AuthenticatorAlreadyRegistered);

            // A DIFFERENT KIND FROM ITS NEIGHBOUR, though both are refusals of one ceremony. An
            // authenticator already enrolled means use another one — or stop, it already works. A factor
            // identifier already standing means re-wrap the account keys under a fresh one and run the
            // ceremony again, which is work the caller has to do before it can retry anything.
            case RegistrationOutcome.FactorTaken:
                return new ConflictException(
                    FactorAlreadyRegisteredMessage,
                    ConflictKind.FactorAlreadyRegistered);

            // Registered never reaches here — the caller returns on it — and every other member is a
            // refusal somebody added without deciding what it says. Written out rather than folded into a
            // default so that adding one is a decision made here.
            case RegistrationOutcome.Registered:
            default:
                return new ArgumentOutOfRangeException(
                    nameof(outcome),
                    outcome,
                    $"A {nameof(RegistrationOutcome)} member was added and nobody chose what it tells the "
                    + "caller.");
        }
    }

    /// <summary>
    /// What is required of <paramref name="member"/>, said whole rather than split into which part of it
    /// was wrong.
    /// </summary>
    private static string MalformedEnvelope(string member) =>
        $"{member} must be base64url text decoding to exactly {WrappedAccountKeys.EnvelopeLength} bytes "
        + $"carrying envelope version {WrappedAccountKeys.EnvelopeVersion}.";

    // Domain.Common.ValidationException by name, because both layers declare one and only that one is
    // what ValidationExceptionHandler turns into a 400 with the field errors on it.
    private static ValidationException Refused(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}
