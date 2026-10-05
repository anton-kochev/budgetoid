using Application.Abstractions;
using Application.Passkeys.Reauthentication;
using Application.Sessions.RevokeSessionsForCredential;
using Domain.Common;
using Domain.Users;

namespace Application.Users.ChangeEmail;

/// <summary>
/// Moves the signed-in account to the Google identity and address the provider just asserted.
/// </summary>
/// <remarks>
/// <para>
/// <b>A new subject is a credential retired and one filed, never a subject rewritten in place</b> —
/// <c>credentials</c> holds no <c>UPDATE</c> grant, and <see cref="FederatedIdentityChange"/> carries the
/// argument. Retiring a credential follows the rule every removal path follows: <b>revoke its sessions,
/// then delete it</b>. The cascade from <c>credentials</c> removes the same session rows either way, so
/// the count the sweep returns is the only evidence it ran, and it is what the response reports.
/// </para>
/// <para>
/// <b>Every refusal leaves the transactional delegate by throwing.</b> The sweep commits on a save of
/// its own before the change is applied; a delegate that <em>returned</em> a refusal would hand the
/// executor a commit of that sweep, signing the person's other browsers out of a Google credential the
/// response says is still theirs.
/// </para>
/// <para>
/// <b>No refusal names an address or a subject.</b> Each sentence travels as a problem document's
/// <c>detail</c>, and a value in a response is a value in a log.
/// </para>
/// </remarks>
public sealed class ChangeEmailHandler(
    IEmailChangeRepository emailChanges,
    IUserRepository users,
    IUserContext userContext,
    IPersistenceState persistenceState,
    ITransactionalExecutor transactionalExecutor,
    PasskeyReauthentication reauthentication,
    RevokeSessionsForCredentialHandler revokeSessions,
    TimeProvider timeProvider)
    : ICommandHandler<ChangeEmailCommand, EmailChange>
{
    /// <summary>
    /// What a caller is told when the Google identity they chose is already attached to another
    /// account.
    /// </summary>
    /// <remarks>
    /// One sentence for the pre-check, for a save refused on the credential index whose re-read does not
    /// find this account on the subject, and for an ambiguous address collision the re-read settles the
    /// same way, because all three are one fact reached at three moments. The wording is the design book's <c>google-account-taken</c> line, and it says
    /// which remedy helps without saying whose account it is.
    /// </remarks>
    private const string ProviderIdentityInUseMessage =
        "That Google account is already attached to another Budgetoid account, so nothing changed. "
        + "Choose a different Google account.";

    /// <summary>
    /// <c>RegisterAccountHandler</c>'s sentence for an address another account holds, verbatim, and
    /// this is the second copy of it.
    /// </summary>
    /// <remarks>
    /// Reached only once the re-read has shown the chosen Google identity is attached to no other
    /// account, so the account holding the address answers to a different Google identity — which is
    /// what the sentence says. Change one and change both.
    /// </remarks>
    private const string EmailAlreadyLinkedMessage =
        "This email address is already linked to a different Google account.";

    /// <summary>
    /// What a caller is told when another change of this account's Google identity landed first.
    /// </summary>
    /// <remarks>
    /// Reached two ways: the credential this change would retire is no longer the account's, or the
    /// save was refused on the credential index and the re-read finds this account itself holding the
    /// subject — a racing change to the same Google identity committed first.
    /// </remarks>
    private const string AccountIdentityMovedMessage =
        "The Google account attached to this account was changed by another request while this one was "
        + "running, so this change was not made. Read the address back, and change it again if it is not "
        + "the one you chose.";

    public async Task<EmailChange> HandleAsync(
        ChangeEmailCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The gate runs to completion OUTSIDE the transactional delegate, for the two reasons
        // RevokePasskeyHandler and EraseAccountHandler write out: the nonce has to stay spent when the
        // change rolls back, and the delegate is replayed by the execution strategy, where a second
        // consume would refuse a valid request with the 401 an attacker gets. First of all, too: the
        // subject lookup below is the exempt discovery lookup, and an unproven caller asking it is an
        // oracle for which Google identities hold an account.
        await reauthentication.VerifyAsync(command.Assertion, cancellationToken);

        // Trimmed once, here, because FederatedIdentityChange.Decide trims before it compares and
        // Credential.CreateFederated stores the subject trimmed, while the lookup matches ordinally. An
        // untrimmed lookup finds nobody for a subject another account holds.
        string subject = command.Subject.Trim();
        Guid userId = userContext.UserId;

        // The pre-check, before any transaction opens: the common case of a Google identity already in
        // use is answered without a sweep to roll back. It cannot close the race — another account can
        // file the subject between this read and the save — which is what the save's own refusal and the
        // re-read below are for. The account's own id is not a refusal: that is the subject unchanged.
        if (await IsHeldByAnotherAccountAsync(subject, userId, cancellationToken))
        {
            throw new ConflictException(ProviderIdentityInUseMessage, ConflictKind.ProviderIdentityInUse);
        }

        return await transactionalExecutor.ExecuteAsync(
            async token =>
            {
                // REPLAY HYGIENE: the execution strategy replays this body against a database that
                // rolled the abandoned attempt back and a change tracker that did not. The gate above
                // also left the proving passkey's material tracked on this scoped context.
                persistenceState.DiscardTrackedEntities();

                // Owner- and type-scoped by the port: credentials is exempt from row-level security, so
                // the owner is the only thing narrowing it, and the type keeps a passkey from ever being
                // the credential retired. Every account is registered with one, so its absence is an
                // integrity failure and raised — a 500 on purpose, as RevokePasskeyHandler raises a
                // missing manifest: nothing the caller sent is wrong and nothing they could send helps.
                Credential current =
                    await emailChanges.FindFederatedCredentialAsync(userId, token)
                    ?? throw new InvalidOperationException(
                        "The account holds no federated credential, so there is no Google identity to move.");

                // The session authenticated this account, so a missing row is the same kind of failure.
                User user =
                    await emailChanges.FindUserAsync(userId, token)
                    ?? throw new InvalidOperationException(
                        "The signed-in account has no user row, so there is no address to move.");

                // A decision, never an act: Decide touches neither entity. The address moves only when
                // the repository's save lands, so a refusal leaves the loaded user as it was stored.
                FederatedIdentityChange change = FederatedIdentityChange.Decide(
                    user,
                    current,
                    subject,
                    command.Email,
                    timeProvider.GetUtcNow().UtcDateTime);

                if (change.IsNoChange)
                {
                    return new EmailChange(0);
                }

                int sessionsEnded = 0;

                if (change.Retired is not null)
                {
                    // Revoke, then delete — keyed on the RETIRED credential, never the filed one, which
                    // has no sessions and would report zero, and never the account, which would sign the
                    // person out of the browser in their hand. Through the command handler so one
                    // decision to end access is stamped as one instant.
                    sessionsEnded = await revokeSessions.HandleAsync(
                        new RevokeSessionsForCredentialCommand(change.Retired.Id),
                        token);

                    // A second discard, and no test pins it. The revocation loaded every unrevoked
                    // Session of the retired credential into the tracker. Remove the Credential with
                    // those dependents still tracked and EF cascades into the copies it can see, emitting
                    // its own DELETE FROM sessions — which succeeds now that the role holds DELETE there
                    // for the ended-session sweep, and takes the rows the database's cascade from
                    // credentials would have. Measured: without this line both email-change integration
                    // classes stay green, and the unit fakes have no change tracker to notice. It stays
                    // because a tracked dependent the role cannot delete still dies with 42501 —
                    // session_tokens among them. RevokePasskeyHandler states the same mechanism.
                    persistenceState.DiscardTrackedEntities();
                }

                EmailChangeOutcome outcome = await emailChanges.ApplyAsync(change, userId, token);

                if (outcome is not EmailChangeOutcome.Applied)
                {
                    // THROWN, never returned — see the class remarks: a returned refusal commits the
                    // sweep above.
                    throw await RefusalFor(outcome, subject, userId, token);
                }

                return new EmailChange(sessionsEnded);
            },
            cancellationToken);
    }

    /// <summary>
    /// Turns a refused save into the conflict the caller can act on, resolving the two outcomes the
    /// repository cannot.
    /// </summary>
    /// <remarks>
    /// <b><see cref="EmailChangeOutcome.EmailTaken"/> and <see cref="EmailChangeOutcome.SubjectTaken"/>
    /// are both ambiguous and this is where they are settled</b>, by re-reading who holds the subject.
    /// The first the way <c>RegisterAccountHandler</c> settles <see cref="RegistrationOutcome.EmailTaken"/>:
    /// one save can breach the subject and the address at once and PostgreSQL names only one. The second
    /// because the credential holding the subject may be this account's own, filed by a racing change.
    /// A reported unique violation means the conflicting transaction committed, so a winning credential
    /// on this subject is visible to a re-read by now. The re-read runs inside the delegate: EF takes a savepoint before a
    /// save inside an open transaction, so the refused save leaves the transaction usable, and
    /// <c>credentials</c> is exempt from row-level security.
    /// </remarks>
    private async Task<ConflictException> RefusalFor(
        EmailChangeOutcome outcome,
        string subject,
        Guid userId,
        CancellationToken cancellationToken)
    {
        switch (outcome)
        {
            case EmailChangeOutcome.SubjectTaken:
                // This account's OWN id means a change of this account to the same Google identity
                // committed first: its insert met the winner's credential on the subject index, which
                // PostgreSQL checks ahead of the one-per-account index. Another account's id is the
                // pre-check's fact reached late. Nobody means the holder left again — nothing observed
                // says this account moved, so the answer stays the one the refusal always gave.
                return await FindHolderAsync(subject, cancellationToken) == userId
                    ? new ConflictException(AccountIdentityMovedMessage, ConflictKind.AccountIdentityMoved)
                    : new ConflictException(ProviderIdentityInUseMessage, ConflictKind.ProviderIdentityInUse);

            case EmailChangeOutcome.EmailTaken:
                // The account's OWN id is an answer too — the subject did not change, or this account
                // still holds it — and it means the address alone collided. Only another account's id
                // makes this a Google identity in use.
                return await IsHeldByAnotherAccountAsync(subject, userId, cancellationToken)
                    ? new ConflictException(ProviderIdentityInUseMessage, ConflictKind.ProviderIdentityInUse)
                    : new ConflictException(EmailAlreadyLinkedMessage, ConflictKind.EmailAlreadyLinked);

            case EmailChangeOutcome.FederatedCredentialMoved:
                return new ConflictException(AccountIdentityMovedMessage, ConflictKind.AccountIdentityMoved);

            // Applied never reaches here — the caller returns on it — and every other member is a
            // refusal somebody added without deciding what it tells the caller.
            case EmailChangeOutcome.Applied:
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(outcome),
                    outcome,
                    $"A {nameof(EmailChangeOutcome)} member was added and nobody chose what it tells the "
                    + "caller.");
        }
    }

    /// <summary>
    /// Whether the Google identity <paramref name="subject"/> names is attached to an account other than
    /// <paramref name="userId"/>.
    /// </summary>
    private async Task<bool> IsHeldByAnotherAccountAsync(
        string subject,
        Guid userId,
        CancellationToken cancellationToken) =>
        await FindHolderAsync(subject, cancellationToken) is { } id && id != userId;

    /// <summary>
    /// The account the Google identity <paramref name="subject"/> names is attached to now, or
    /// <see langword="null"/> when no account holds it.
    /// </summary>
    private Task<Guid?> FindHolderAsync(string subject, CancellationToken cancellationToken) =>
        users.FindUserIdByFederatedCredentialAsync(Credential.GoogleProvider, subject, cancellationToken);
}
