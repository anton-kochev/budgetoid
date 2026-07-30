using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class UserRepository(BudgetoidDbContext dbContext) : IUserRepository
{
    public Task<User?> FindByGoogleSubjectAsync(string googleSubject, CancellationToken cancellationToken = default)
    {
        string trimmedGoogleSubject = googleSubject.Trim();
        return dbContext.Users.SingleOrDefaultAsync(user => user.GoogleSubject == trimmedGoogleSubject, cancellationToken);
    }

    public async Task<bool> TryAddAsync(User user, CancellationToken cancellationToken = default)
    {
        dbContext.Users.Add(user);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        // A losing insert can violate both unique rules at once — same subject, same email — and
        // PostgreSQL names only one of them, picked by index creation order rather than by what
        // happened. So this cannot tell the two apart and does not try: either name means "an
        // existing row already holds this identity", and the caller decides which by re-reading the
        // subject. The filter still lists both names, so a 23505 from any other unique rule
        // propagates on purpose: it is a constraint this method does not model, and a 500 naming it
        // is more useful than a false "someone else won the race".
        catch (DbUpdateException exception) when (
            IsUniqueViolationOf(exception, UserConfiguration.GoogleSubjectIndexName)
            || IsUniqueViolationOf(exception, UserConfiguration.EmailIndexName))
        {
            dbContext.Entry(user).State = EntityState.Detached;
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
            // A failed profile refresh must never block a sign-in: identity is the google_subject,
            // and email is only a cached copy of an IdP attribute. Reload discards the rejected
            // change — in the caller's instance too — so the session continues on the stored email.
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
