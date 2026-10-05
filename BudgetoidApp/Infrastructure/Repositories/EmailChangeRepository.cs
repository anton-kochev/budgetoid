using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

/// <summary>
/// Saves an account's move to a new Google identity and address as one <c>SaveChanges</c>: the retired
/// credential deleted, its replacement inserted, and the <c>users.email</c> column updated.
/// </summary>
/// <remarks>
/// <para>
/// <b>Delete-plus-insert, never an update, and both in the one save.</b> <c>credentials</c> holds
/// <c>INSERT</c> and <c>DELETE</c> and no <c>UPDATE</c> of any shape, so a subject cannot be rewritten in
/// place — <see cref="FederatedIdentityChange"/> carries that argument. One save rather than two is what
/// makes a refusal write nothing: split, the swap commits and the address is refused after it, and the
/// account answers to a Google identity the caller is told did not attach.
/// </para>
/// <para>
/// <b>EF sends the DELETE ahead of the INSERT here, and this class does not choose that.</b>
/// <c>IX_credentials_user_id_federated</c> allows one federated row per account, so an INSERT sent first
/// would collide with the row the DELETE is about to remove. EF's own command sort puts the DELETE, then
/// the <c>UPDATE users</c>, then the INSERT into one batch whatever order the tracker holds them in —
/// measured against postgres:17 in four variants. The pin is
/// <c>ApplyAsync_WithANewSubject_SendsOneBatch_DeleteThenUpdateThenInsert</c> in
/// <c>EmailChangeRepositoryTests</c>, which reads the order off the wire and goes red if an EF upgrade
/// reorders the batch.
/// </para>
/// <para>
/// <b>A lost race has one arm, and nothing else is translated.</b> A racing change of the same account
/// that filed its replacement surfaces as <c>23505</c> on <c>IX_credentials_user_id_federated</c>. An
/// erasure committing underneath surfaces as <c>23503</c> on <c>FK_credentials_users_user_id</c> under a
/// subject change, or as a concurrency failure on the <c>UPDATE users</c> under an address-only change,
/// and escapes as a 500 either way —
/// <c>ApplyAsync_WhenTheAccountIsErasedUnderneathASubjectChange_LetsTheForeignKeyViolationEscape</c> and
/// <c>ApplyAsync_WhenTheAccountIsErasedUnderneathTheSave_LetsTheConcurrencyFailureEscape</c>. There is no
/// concurrency catch: a bare zero-row DELETE comes from no product path, so it escapes too —
/// <c>ApplyAsync_WhenTheRetiredCredentialVanishedWithNoReplacement_LetsTheConcurrencyFailureEscape</c>.
/// <c>PasskeyRepository</c> and <c>RecoveryCodeRepository</c> draw the same line: each catches the
/// zero-row DELETE a double tap produces, and lets <c>Remove</c> raise on any other row that is not there.
/// </para>
/// </remarks>
public sealed class EmailChangeRepository(BudgetoidDbContext dbContext) : IEmailChangeRepository
{
    /// <inheritdoc />
    public Task<User?> FindUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        // The owner is named although users is policed on the same id: the policy scopes the statement to
        // whoever the session names, and this predicate is what makes the answer the account the caller
        // asked about rather than the only row the policy happened to leave visible.
        dbContext.Users
            .Where(user => user.Id == userId)
            .SingleOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Credential?> FindFederatedCredentialAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        // BOTH predicates are the scope. credentials is exempt from row-level security, so the owner is the
        // only thing narrowing this read to the account asking, and the credential it returns is the one
        // ApplyAsync deletes. The type keeps a passkey or a recovery-code credential from ever being
        // handed to Decide as the one to retire.
        //
        // Tracked, because the entity travels on to ApplyAsync's Remove. SingleOrDefault because
        // IX_credentials_user_id_federated allows one row per account, and a second is a broken database
        // rather than a choice this read should make quietly.
        dbContext.Credentials
            .Where(credential => credential.UserId == userId && credential.Type == CredentialType.Federated)
            .SingleOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<EmailChangeOutcome> ApplyAsync(
        FederatedIdentityChange change,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (change.IsNoChange)
        {
            throw new ArgumentException("A change that moves nothing has nothing to apply.", nameof(change));
        }

        // Remove attaches a detached instance and marks it Deleted, which is the case the handler produces:
        // it discards tracked entities after its session revocation, so the credential it decided on
        // arrives here with no entry. Nothing it depends on should be tracked by then — an EF cascade into
        // tracked sessions would emit its own DELETE FROM sessions, which the ended-session sweep's grant
        // now lets succeed, and into a tracked handle a DELETE FROM session_tokens, which the app role
        // holds no grant for.
        if (change.Retired is not null)
        {
            dbContext.Credentials.Remove(change.Retired);
        }

        if (change.Filed is not null)
        {
            dbContext.Credentials.Add(change.Filed);
        }

        // Loaded and mutated, never Update(user): a detached Update marks every column modified, and the
        // app role holds UPDATE (email) and nothing else on users, so that shape dies with 42501. A tracked
        // entity whose one property moved emits an UPDATE naming that column alone. The load resolves to
        // the caller's instance when it is still tracked, and re-reads the row when it is not.
        User user = await FindUserAsync(userId, cancellationToken)
                    ?? throw new InvalidOperationException(
                        "The account has no user row, so there is no address to move.");
        user.ChangeEmail(change.Email.Value);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);

            return EmailChangeOutcome.Applied;
        }
        // THREE NAMED CONSTRAINTS AND NOT THE SQLSTATE ALONE, the filter
        // RepositoryConstraintAttributionTests requires of every translating repository in this folder:
        // SaveChangesAsync flushes the whole change tracker, so a 23505 says only that some rule broke.
        // Any other unique violation — PK_credentials, reachable only by a UUIDv7 collision — escapes as
        // the 500 naming it rather than a confident, false 409.
        catch (DbUpdateException exception) when (
            IsUniqueViolationOf(exception, CredentialConfiguration.ProviderSubjectIndexName))
        {
            Detach(change, user);

            return EmailChangeOutcome.SubjectTaken;
        }
        // AMBIGUOUS, AND THE CALLER RESOLVES IT: one save can breach the subject and the address at once,
        // and PostgreSQL names only one, picked by the order the statements run. The port says so on
        // EmailChangeOutcome.EmailTaken, and ChangeEmailHandler settles it with a re-read.
        catch (DbUpdateException exception) when (
            IsUniqueViolationOf(exception, UserConfiguration.EmailIndexName))
        {
            Detach(change, user);

            return EmailChangeOutcome.EmailTaken;
        }
        // THE ONLY LOST-RACE ARM. A racing change of the same account retired this request's credential
        // and filed its own, so this request's INSERT meets the winner's row on the one-per-account index.
        // Measured, that 23505 is what surfaces rather than the zero-row DELETE's concurrency failure, so
        // dropping this arm turns a lost race into a 500 —
        // ApplyAsync_WhenARacingChangeAlreadyReplacedTheRetiredCredential_AnswersFederatedCredentialMoved.
        // A winner that retires without filing is not a product path, and an erasure arrives as 23503 on
        // FK_credentials_users_user_id; both escape.
        catch (DbUpdateException exception) when (
            IsUniqueViolationOf(exception, CredentialConfiguration.FederatedPerUserIndexName))
        {
            Detach(change, user);

            return EmailChangeOutcome.FederatedCredentialMoved;
        }
    }

    /// <summary>
    /// Detaches every entity this call queued, so the refused change cannot ride along on a later save
    /// through the same scoped context — the shape <c>RegistrationRepository.RegisterAsync</c> uses.
    /// </summary>
    /// <remarks>
    /// The user is detached rather than reverted: its tracked copy holds the refused address, and a
    /// detached entry is re-read from the row on the next load instead of being trusted. Left tracked, the
    /// next save anybody makes on this context re-sends the refused <c>UPDATE users</c>;
    /// <c>ApplyAsync_AfterARefusedAddress_DoesNotResendItOnTheNextSave</c> holds that, and is the one case
    /// that goes red when the user's detach is dropped.
    /// </remarks>
    private void Detach(FederatedIdentityChange change, User user)
    {
        if (change.Retired is not null)
        {
            dbContext.Entry(change.Retired).State = EntityState.Detached;
        }

        if (change.Filed is not null)
        {
            dbContext.Entry(change.Filed).State = EntityState.Detached;
        }

        dbContext.Entry(user).State = EntityState.Detached;
    }

    private static bool IsUniqueViolationOf(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        } postgresException && postgresException.ConstraintName == indexName;
}
