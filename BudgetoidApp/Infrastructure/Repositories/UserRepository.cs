using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class UserRepository(BudgetoidDbContext dbContext) : IUserRepository
{
    public Task<User?> FindByFederatedCredentialAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken = default)
    {
        string trimmedProvider = provider.Trim();
        string trimmedSubject = subject.Trim();

        // The type predicate is not redundant with the provider match: it is what lets PostgreSQL use
        // the partial unique index, whose predicate the planner will only assume from an explicit
        // `type = 'federated'`. No navigation property links the two entities, so the user is reached
        // by an explicit join rather than by an Include.
        return dbContext.Credentials
            .Where(credential => credential.Type == CredentialType.Federated
                                 && credential.Provider == trimmedProvider
                                 && credential.Subject == trimmedSubject)
            .Join(dbContext.Users, credential => credential.UserId, user => user.Id, (_, user) => user)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> TryAddAsync(
        User user,
        Credential credential,
        CancellationToken cancellationToken = default)
    {
        dbContext.Users.Add(user);
        dbContext.Credentials.Add(credential);

        try
        {
            // One save, so the two rows land together or not at all. A user row without its
            // credential would hold the unique email while nothing resolved to it, refusing that
            // address forever.
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        // A losing insert can violate both unique rules at once — same (provider, subject), same
        // email — and PostgreSQL names only one of them, picked by the order the rows are written
        // rather than by what happened. So this cannot tell the two apart and does not try: either
        // name means "an existing row already holds this identity", and the caller decides which by
        // re-reading the credential. The filter still lists both names, so a 23505 from any other
        // unique rule propagates on purpose: it is a constraint this method does not model, and a 500
        // naming it is more useful than a false "someone else won the race".
        catch (DbUpdateException exception) when (
            IsUniqueViolationOf(exception, CredentialConfiguration.ProviderSubjectIndexName)
            || IsUniqueViolationOf(exception, UserConfiguration.EmailIndexName))
        {
            dbContext.Entry(user).State = EntityState.Detached;
            dbContext.Entry(credential).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<bool> UpdateProfileAsync(User user, CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsUniqueViolationOf(exception, UserConfiguration.EmailIndexName))
        {
            // A failed profile refresh must never block a sign-in: identity is the credential row the
            // caller signed in with, and email is only a cached copy of an IdP attribute. Reload
            // discards the rejected change — in the caller's instance too — so the session continues
            // on the stored email.
            await dbContext.Entry(user).ReloadAsync(cancellationToken);
            return false;
        }
    }

    private static bool IsUniqueViolationOf(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        } postgresException && postgresException.ConstraintName == indexName;
}
