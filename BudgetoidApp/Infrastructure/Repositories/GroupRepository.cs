using Application.Groups;
using Domain.Common;
using Domain.Groups;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class GroupRepository(BudgetoidDbContext dbContext) : IGroupRepository
{
    public async Task AddAsync(Group group, CancellationToken cancellationToken = default)
    {
        dbContext.Groups.Add(group);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.Entry(group).State = EntityState.Detached;
            throw DuplicateNameValidationException();
        }
    }

    public Task<Group?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Use the filtered DbSet, not Find/FindAsync: Find can return a tracked entity while
        // bypassing global query filters, which would let one user reference another user's group.
        return dbContext.Groups.FirstOrDefaultAsync(group => group.Id == id, cancellationToken);
    }

    public async Task UpdateAsync(Group group, CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Detach the rejected entity so the failed (Modified) state can't leak into a later
            // SaveChanges if the context were reused, mirroring AddAsync's detach-on-conflict.
            dbContext.Entry(group).State = EntityState.Detached;
            throw DuplicateNameValidationException();
        }
    }

    public async Task DeleteAsync(Group group, CancellationToken cancellationToken = default)
    {
        dbContext.Groups.Remove(group);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            // Detach the rejected entity so the failed (Deleted) state can't leak into a later
            // SaveChanges if the context were reused, mirroring AddAsync/UpdateAsync's detach-on-conflict.
            dbContext.Entry(group).State = EntityState.Detached;
            throw ReferencedGroupValidationException();
        }
    }

    public Task<bool> HasTransactionsAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        return dbContext.Transactions.AnyAsync(transaction => transaction.GroupId == groupId, cancellationToken);
    }

    private static ValidationException DuplicateNameValidationException() => new(new Dictionary<string, string[]>
    {
        [nameof(Group.Name)] = ["Group name must be unique."],
    });

    private static ValidationException ReferencedGroupValidationException() => new(new Dictionary<string, string[]>
    {
        [nameof(Group.Id)] = ["Group cannot be deleted because it has transactions."],
    });
}
