using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Infrastructure.Repositories;

public sealed class UserRepository(BudgetoidDbContext dbContext) : IUserRepository
{
    public Task<Guid?> FindUserIdByFederatedCredentialAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken = default)
    {
        string trimmedProvider = provider.Trim();
        string trimmedSubject = subject.Trim();

        // The type predicate is not redundant with the provider match: it is what lets PostgreSQL use
        // the partial unique index, whose predicate the planner will only assume from an explicit
        // `type = 'federated'`.
        //
        // credentials alone, projected to the key. This statement runs before the session names
        // anyone, so it may touch no policed table — and joining users would touch the one policed on
        // the very id being resolved.
        //
        // SingleOrDefault, still: a second row would mean the unique (provider, subject) rule has
        // been lost, and one Google identity resolving to two accounts is a broken database rather
        // than a sign-in this method can answer honestly. The projection is nullable so the "no
        // credential" default stays distinguishable from a real id.
        return dbContext.Credentials
            .Where(credential => credential.Type == CredentialType.Federated
                                 && credential.Provider == trimmedProvider
                                 && credential.Subject == trimmedSubject)
            .Select(credential => (Guid?)credential.UserId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Loaded through the set and removed through the change tracker, because ExecuteDelete is a
        // compile error here — see BannedSymbols.txt. RemoveRange over the matched rows rather than a
        // load-then-null-check: an absent row leaves an empty list and the save is a no-op, which is
        // the tolerated outcome rather than a branch.
        List<User> users = await dbContext.Users
            .Where(user => user.Id == userId)
            .ToListAsync(cancellationToken);
        dbContext.Users.RemoveRange(users);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // The empty list above only covers a row that was already gone when this call read. Two
        // erasures of the same account in flight — a double-click, or a client retrying a slow
        // response — both read the row, and the loser blocks on the winner's lock and then, under
        // READ COMMITTED, deletes nothing. EF counts the affected rows, sees zero where it expected
        // one, and raises this. The row is gone, which is the whole post-condition this method
        // states, so letting it escape would answer a completed erasure with a 500 — the same lie
        // as the 404 this method deliberately does not return, arriving by a different route.
        //
        // Narrowed to exactly that, and the entries are what narrows it: a concurrency conflict
        // carries no SQLSTATE, so "every conflicting row is a users row this call itself marked
        // Deleted" is this method's equivalent of the constraint-name filter
        // RepositoryConstraintAttributionTests requires. A conflict over any other entity riding
        // along on the same SaveChanges, or over a row this call did not delete, is a failure this
        // method does not model and must propagate.
        catch (DbUpdateConcurrencyException exception) when (IsAlreadyDeleted(exception))
        {
            // Detached for the reason the catches in this folder detach: the entries are still
            // Deleted, and a later save on this request-scoped context — or a replay of the
            // enclosing unit of work — would repeat a delete that has already been answered.
            DetachDeletedUsers();
        }
    }

    /// <summary>
    /// True when the conflict is only about <see cref="User"/> rows this call removed. The count
    /// test is not redundant: an exception EF could not attribute to any entry would otherwise
    /// satisfy the predicate vacuously.
    /// </summary>
    private static bool IsAlreadyDeleted(DbUpdateConcurrencyException exception) =>
        exception.Entries.Count > 0
        && exception.Entries.All(entry =>
            entry.Entity is User && entry.State == EntityState.Deleted);

    /// <summary>
    /// Detaches every user this context still has marked Deleted, so that state cannot be re-flushed
    /// by a later save on this request-scoped context, or by a replay of the enclosing unit of work.
    /// </summary>
    /// <remarks>
    /// Swept off the change tracker and not off <c>DbUpdateConcurrencyException.Entries</c>, for the
    /// reason spelled out on <c>TransactionRepository.DetachDeletedTransactions</c>: EF reports only
    /// the first mismatching command's entries, so a detach driven by them undoes an arbitrary prefix
    /// of what the catch just claimed to have answered. Here the two sets happen to coincide — the
    /// query above matches on the primary key, so this call marks at most one users row Deleted, and
    /// the request can see no other. That is a property of a <c>Where</c> clause two screens up rather
    /// than of this catch, and writing the correct sweep costs nothing to keep it from turning into
    /// the same defect the day the load widens.
    /// </remarks>
    private void DetachDeletedUsers()
    {
        // Materialised first: assigning Detached removes the entry from the collection being walked.
        List<EntityEntry<User>> deleted = dbContext.ChangeTracker
            .Entries<User>()
            .Where(entry => entry.State == EntityState.Deleted)
            .ToList();

        foreach (EntityEntry<User> entry in deleted)
        {
            entry.State = EntityState.Detached;
        }
    }
}
