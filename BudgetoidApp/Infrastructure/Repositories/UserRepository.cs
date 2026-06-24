using Application.Users;
using Domain.Users;
using Infrastructure.Persistence;
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
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.Entry(user).State = EntityState.Detached;
            return false;
        }
    }

    public Task UpdateProfileAsync(User user, CancellationToken cancellationToken = default) => dbContext.SaveChangesAsync(cancellationToken);
}
