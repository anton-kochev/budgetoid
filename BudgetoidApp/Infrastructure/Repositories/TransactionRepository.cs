using Domain.Common;
using Domain.Transactions;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class TransactionRepository(BudgetoidDbContext dbContext) : ITransactionRepository
{
    /// <summary>
    /// Inserts a transaction the caller minted and sealed, answering a duplicate identifier with a 409.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This method's first catch block, and it arrived with the identifier.</b> The id used to be
    /// minted here and could not collide; it is the caller's now, because it is the associated data the
    /// note was sealed against. A POST retried after a network timeout therefore carries a byte-identical
    /// body and violates <see cref="TransactionConfiguration.PrimaryKeyName"/> — which without this arm
    /// answers 500 for the most ordinary thing an HTTP client does.
    /// </para>
    /// <para>
    /// <b>One arm and not two, unlike the four sibling repositories.</b> Those match a name index beside
    /// the key because their tables carry one; this table has no name and no unique index but the key, so
    /// there is no second 23505 for the constraint name to tell apart — the name is matched anyway, for
    /// the reason every catch in this folder matches one: <c>SaveChanges</c> flushes every tracked row,
    /// and a violation belonging to some other row must propagate rather than be reported as this
    /// caller's identifier.
    /// </para>
    /// <para>
    /// <b>The route stays non-idempotent, deliberately</b>, for the reason
    /// <c>CategoryRepository.AddAsync</c> gives: deciding whether the existing row is the same one means
    /// comparing an AEAD envelope this server has no key for.
    /// </para>
    /// </remarks>
    public async Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        dbContext.Transactions.Add(transaction);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: TransactionConfiguration.PrimaryKeyName,
        })
        {
            // Detached so the failed Added state cannot leak into a later SaveChanges on this
            // request-scoped context, mirroring the detach-on-conflict every sibling repository does.
            dbContext.Entry(transaction).State = EntityState.Detached;
            throw DuplicateTransactionIdConflictException();
        }
    }

    public Task<Transaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Query the filtered DbSet rather than Find/FindAsync: Find can answer from the change
        // tracker without ever reaching the budget query filter, which would hand one budget a
        // transaction belonging to another.
        return dbContext.Transactions.FirstOrDefaultAsync(
            transaction => transaction.Id == id,
            cancellationToken);
    }

    public async Task UpdateAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        // The entity came from GetByIdAsync and is already tracked, so saving is the whole update.
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // The handler resolves the account and the category through their filtered repositories
        // before it mutates anything, so a violation here means the row disappeared between that
        // read and this save — a concurrent delete. These catches are that race backstop, not the
        // primary guard. Filtering by constraint name and not by 23503 alone keeps a violation from
        // some other referencing row, riding along on the same SaveChanges, from being reported as
        // an account or category the caller never named; an unmatched one propagates, because a 500
        // naming the real constraint beats a 400 that lies.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ForeignKeyViolation,
            ConstraintName: TransactionConfiguration.AccountForeignKeyName,
        })
        {
            // Detach the rejected entity so its failed (Modified) state can't leak into a later
            // SaveChanges if the context were reused, mirroring AccountRepository's detach-on-conflict.
            dbContext.Entry(transaction).State = EntityState.Detached;
            throw MissingAccountValidationException();
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ForeignKeyViolation,
            ConstraintName: TransactionConfiguration.CategoryForeignKeyName,
        })
        {
            dbContext.Entry(transaction).State = EntityState.Detached;
            throw MissingCategoryValidationException();
        }
    }

    public async Task DeleteAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        // No foreign key points at transactions, so a delete has nothing to violate and needs no
        // 23503 translation the way accounts and categories do.
        dbContext.Transactions.Remove(transaction);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAllForAmbientBudgetAsync(CancellationToken cancellationToken = default)
    {
        // Loaded and removed through the change tracker, because ExecuteDelete is a compile error
        // here — see BannedSymbols.txt. The DbSet carries the BudgetIsolation query filter, which is
        // the entirety of the scoping: no Where on budget_id appears here because there is no budget
        // id to write one from, and that is the point.
        //
        // No 23503 catch, deliberately. Nothing references a transaction, so this delete has no
        // 23503 of its own to translate, and a violation raised by some other row riding along on
        // the same SaveChanges belongs to its caller, named — the rule
        // RepositoryConstraintAttributionTests pins for every repository in this folder.
        List<Transaction> transactions = await dbContext.Transactions.ToListAsync(cancellationToken);
        dbContext.Transactions.RemoveRange(transactions);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // The same race UserRepository.DeleteAsync answers, and this is where it lands first when the
        // budget holds any movement: two erasures of one account in flight both read these rows, the
        // loser blocks on the winner's locks until it commits, and then deletes nothing. EF sees zero
        // affected rows where it expected one per row and raises this. The budget holds no
        // transactions, which is the entire post-condition — reporting a failure would answer a
        // completed erasure with a 500.
        //
        // Narrowed by the entries rather than by an SQLSTATE it does not have: every conflicting row
        // must be a transaction this call itself marked Deleted. A conflict over anything else on
        // the same SaveChanges is a failure this method does not model and must propagate, for the
        // reason the paragraph above gives about 23503.
        catch (DbUpdateConcurrencyException exception) when (IsAlreadyDeleted(exception))
        {
            DetachDeletedTransactions();
        }
    }

    /// <summary>
    /// True when the conflict is only about <see cref="Transaction"/> rows this call removed. The
    /// count test is not redundant: an exception EF could not attribute to any entry would otherwise
    /// satisfy the predicate vacuously.
    /// </summary>
    private static bool IsAlreadyDeleted(DbUpdateConcurrencyException exception) =>
        exception.Entries.Count > 0
        && exception.Entries.All(entry =>
            entry.Entity is Transaction && entry.State == EntityState.Deleted);

    /// <summary>
    /// Detaches every transaction this context still has marked Deleted, so that state cannot be
    /// re-flushed by a later save on this request-scoped context, or by a replay of the enclosing
    /// unit of work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Swept off the change tracker and not off <c>DbUpdateConcurrencyException.Entries</c>, because
    /// EF reports only the first mismatching command's entries. Detaching what the exception hands
    /// back leaves every surplus row Deleted, so a budget holding two or more transactions re-flushes
    /// them on the next save — and erasure's next line is exactly such a save,
    /// <c>UserRepository.DeleteAsync</c> on this same context. It then raises a conflict naming a
    /// <see cref="Transaction" />, which that method's own <c>when</c> clause correctly refuses to
    /// swallow: the original 500, one call later.
    /// </para>
    /// <para>
    /// Scoped to Deleted transactions, which is precisely the set the <c>when</c> clause above accepts
    /// responsibility for — it swallows only when every conflicting row is one of them, and it cannot
    /// tell a reported one from an unreported one, so neither can the undo. Detaching the whole
    /// tracker would be a wider claim than the catch makes, reaching accounts, category groups and
    /// users this method never marked.
    /// </para>
    /// </remarks>
    private void DetachDeletedTransactions()
    {
        // Materialised first: assigning Detached removes the entry from the collection being walked.
        List<EntityEntry<Transaction>> deleted = dbContext.ChangeTracker
            .Entries<Transaction>()
            .Where(entry => entry.State == EntityState.Deleted)
            .ToList();

        foreach (EntityEntry<Transaction> entry in deleted)
        {
            entry.State = EntityState.Detached;
        }
    }

    // The 409 this table can raise. ConflictExceptionHandler renders this message as
    // ProblemDetails.Detail beside a Title fixed for every conflict in the product, and adds no extension
    // member, so this sentence is the whole of what the caller is told.
    //
    // It deliberately does not say "re-read your transactions": the row already wearing this id may carry
    // a different amount, date and note - or sit in a budget the caller cannot read, in which case
    // GET /api/transactions/{id} answers 404 - so a client sent to its list would look for something that
    // is not on it. It names the two readings the server cannot tell apart, a retry that already
    // succeeded and an identifier reused by mistake, and gives each its own next step, because the CLIENT
    // can tell them apart: it knows whether it sent this body before.
    private static ConflictException DuplicateTransactionIdConflictException() => new(
        "A transaction already exists with this identifier. If this request is a retry, read that "
        + "transaction back by its identifier instead of posting it again; otherwise mint a fresh "
        + "identifier and post again.");

    // Worded and keyed the same as the up-front checks in the create and update handlers, so a row
    // that vanished under a race is reported to the caller exactly as one that was never there.
    private static ValidationException MissingAccountValidationException() => new(new Dictionary<string, string[]>
    {
        [nameof(Transaction.AccountId)] = ["Account was not found."],
    });

    private static ValidationException MissingCategoryValidationException() => new(new Dictionary<string, string[]>
    {
        [nameof(Transaction.CategoryId)] = ["Category was not found."],
    });
}
