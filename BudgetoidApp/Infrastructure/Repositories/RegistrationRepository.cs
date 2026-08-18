using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class RegistrationRepository(BudgetoidDbContext dbContext) : IRegistrationRepository
{
    /// <inheritdoc />
    public async Task<RegistrationOutcome> RegisterAsync(
        Registration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        dbContext.Users.Add(registration.User);
        dbContext.Budgets.Add(registration.DefaultBudget);
        dbContext.Credentials.Add(registration.FederatedCredential);
        dbContext.Credentials.Add(registration.PasskeyCredential);
        dbContext.Credentials.Add(registration.RecoveryCodesCredential);
        dbContext.PasskeyPublicKeys.Add(registration.PublicKey);
        dbContext.PasskeySignatureCounters.Add(registration.SignatureCounter);
        dbContext.RecoveryCodeHashes.AddRange(registration.RecoveryCodeHashes);

        // AddRange, and the range is ELEVEN rows: the passkey factor's pair plus one pair per code. A
        // factor is not a credential — see WrappedAccountKeys — so a call that added two here would seal
        // the account under one recovery code and leave the other nine unlocking nothing, with a session
        // handed over either way and nothing going red until a browser months later.
        dbContext.WrappedAccountKeys.AddRange(registration.WrappedAccountKeys);

        // The session and its handle ride on THIS save rather than on a second call to
        // ISessionRepository.AddAsync, which saves on its own. There is no transaction on this path, so
        // two saves would be two transactions — see IRegistrationRepository.RegisterAsync, which is where
        // that departure is argued and where the reader who wants to "fix" it back has to go first.
        dbContext.Sessions.Add(registration.Session);
        dbContext.SessionTokens.Add(registration.SessionToken);

        try
        {
            // ONE SAVE FOR ROUGHLY THIRTY ROWS ACROSS NINE RELATIONS. EF orders the statements from the
            // foreign keys between the entity types, so users precedes credentials, credentials precede
            // everything filed against one, and sessions precedes its token, whatever order they were
            // added in above. What the single save buys is the other direction: there is no window in
            // which some of them are committed and the rest are not.
            await dbContext.SaveChangesAsync(cancellationToken);

            return RegistrationOutcome.Registered;
        }
        // FOUR NAMED CONSTRAINTS AND NOT THE SQLSTATE ALONE. This save writes rows carrying a dozen
        // unique rules between them, so a 23505 says only that some rule was broken; naming the index is
        // what makes each catch mean the one thing a caller can act on. Any other unique violation
        // propagates on purpose — it is a constraint this method does not model, and a 500 naming it is
        // more useful than a confident, specific, false answer. This is the filter
        // RepositoryConstraintAttributionTests requires of every repository in this folder that
        // translates anything, and PasskeyRepository.TryAddAsync's catches are its shape.
        //
        // THREE INDEXES ARE DELIBERATELY NOT AMONG THEM, AND EACH IS UNREACHABLE HERE BESIDES — which is
        // what stops a later reader adding a fifth filter that can never fire. IX_budgets_user_id_name,
        // IX_credentials_user_id_federated and IX_credentials_user_id_recovery_codes are all keyed on
        // user_id, and the user_id every row in this call carries is derived for THIS registration from a
        // challenge this server minted and has just spent. No other row can share it, so none of the
        // three can be breached by anything this save writes. The one collision the derived id could
        // itself produce — two registrations reaching the same account identifier — is the users primary
        // key, PK_users, which is not narrowed either: at 122 bits off a single-use nonce it is not
        // chance, and reporting it as any of the four below would be a sentence about the wrong thing.
        catch (DbUpdateException exception) when (
            IsUniqueViolationOf(exception, CredentialConfiguration.ProviderSubjectIndexName))
        {
            Detach(registration);

            return RegistrationOutcome.SubjectTaken;
        }
        // AMBIGUOUS, AND THE CALLER RESOLVES IT. A losing insert can breach the credential's
        // (provider, subject) AND the email at once, and PostgreSQL names only one of them, picked by the
        // order the rows are written rather than by what happened — the same thing UserRepository.TryAddAsync
        // records about its own two-name filter. EF writes users before credentials, so the credential
        // index being named means the email did NOT collide, while the email index being named says
        // nothing about the subject. The two outcomes are therefore not symmetric, and only the caller
        // can settle the second by re-reading the credential.
        catch (DbUpdateException exception) when (
            IsUniqueViolationOf(exception, UserConfiguration.EmailIndexName))
        {
            Detach(registration);

            return RegistrationOutcome.EmailTaken;
        }
        catch (DbUpdateException exception) when (
            IsUniqueViolationOf(exception, PasskeyPublicKeyConfiguration.WebAuthnCredentialIdIndexName))
        {
            Detach(registration);

            return RegistrationOutcome.AuthenticatorTaken;
        }
        // The primary key over factor_id, which is where that column's uniqueness lives, and it is unique
        // across the WHOLE table rather than per account — the same fact PasskeyRepository.TryAddAsync and
        // RecoveryCodeRepository.AddSetAsync each narrow on. Eleven client-minted identifiers arrive on
        // this one save, and the caller has already refused a set that repeats one among itself, so
        // reaching this catch means an identifier already stands in the table.
        catch (DbUpdateException exception) when (
            IsUniqueViolationOf(exception, WrappedAccountKeysConfiguration.PrimaryKeyName))
        {
            Detach(registration);

            return RegistrationOutcome.FactorTaken;
        }
    }

    /// <summary>
    /// Detaches every entity this call queued, so the rejected rows cannot ride along on a later save
    /// through the same scoped context.
    /// </summary>
    /// <remarks>
    /// All of them, because all of them were queued by this call — the shape
    /// <c>PasskeyRepository.TryAddAsync</c> and <c>UserRepository.TryAddAsync</c> both use. A caller that
    /// went on to read anything after a refusal would otherwise re-flush a save that has already been
    /// answered, from somewhere unrelated.
    /// </remarks>
    private void Detach(Registration registration)
    {
        foreach (object entity in Entities(registration))
        {
            dbContext.Entry(entity).State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Every entity <see cref="Registration"/> carries, in one place so that adding a member to that
    /// record and forgetting to detach it is one edit rather than two.
    /// </summary>
    private static IEnumerable<object> Entities(Registration registration)
    {
        yield return registration.User;
        yield return registration.DefaultBudget;
        yield return registration.FederatedCredential;
        yield return registration.PasskeyCredential;
        yield return registration.RecoveryCodesCredential;
        yield return registration.PublicKey;
        yield return registration.SignatureCounter;

        foreach (RecoveryCodeHash hash in registration.RecoveryCodeHashes)
        {
            yield return hash;
        }

        foreach (WrappedAccountKeys wrappedAccountKeys in registration.WrappedAccountKeys)
        {
            yield return wrappedAccountKeys;
        }

        yield return registration.Session;
        yield return registration.SessionToken;
    }

    private static bool IsUniqueViolationOf(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        } postgresException && postgresException.ConstraintName == indexName;
}
