using Domain.Accounts;
using Domain.Common;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class AccountRepository(BudgetoidDbContext dbContext) : IAccountRepository
{
    /// <summary>
    /// Inserts an account the caller minted, sealed and indexed, answering a duplicate name with a 400
    /// keyed on the name and a duplicate identifier with a 409.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two statuses for one SQLSTATE on one table, and the split is between the two CONSTRAINTS rather
    /// than between create and rename.</b> A duplicate name is a person's typed value colliding with
    /// another row's, which is a correction to a field of the request and is exactly what a validation
    /// problem document carries — <c>PayeeRepository.AddAsync</c> answers 409 to the same collision, and
    /// that asymmetry between the two tables is argued in the decision log and is not to be flattened.
    /// </para>
    /// <para>
    /// <b>A duplicate identifier is not a field anybody can correct.</b> The id arrives minted by the
    /// caller, so a POST retried after a network timeout carries a byte-identical body and collides on
    /// <see cref="AccountConfiguration.PrimaryKeyName"/> — which used to be uncaught and therefore
    /// answered 500 for the most ordinary thing an HTTP client does. There is no member for a problem
    /// document to be keyed on: the remedy is either to read the account back or to mint a new
    /// identifier, and neither is an edit to <c>Name</c>. Measured on postgres:17.10, a row violating both
    /// the key and the name index is reported under the key; the probe is written out on that constant.
    /// </para>
    /// <para>
    /// <b>The route stays non-idempotent, deliberately.</b> Answering 200 with the row that already
    /// exists would mean reading it back and deciding whether it is the same account — a comparison over
    /// AEAD envelopes this server cannot open, and it would still have to choose an answer for the case
    /// where the id matches and the name does not. That is a separate decision with its own failure
    /// modes. A 409 that says what happened costs the client one <c>GET /api/accounts/{id}</c>.
    /// </para>
    /// </remarks>
    public async Task AddAsync(Account account, CancellationToken cancellationToken = default)
    {
        dbContext.Accounts.Add(account);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // FIRST because it is the one PostgreSQL reports first, not because the runtime cares: the two
        // filters are mutually exclusive — a PostgresException carries exactly one ConstraintName — so
        // this order is documentation of the measurement, and reversing it changes no behaviour.
        //
        // Named for the reason the arm below is named, plus one that is sharper here: both arms catch the
        // SAME SQLSTATE from the same statement, so SQLSTATE alone cannot tell an id collision from a
        // name collision and whichever answer was written first would be given to both.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: AccountConfiguration.PrimaryKeyName,
        })
        {
            dbContext.Entry(account).State = EntityState.Detached;
            throw DuplicateAccountIdConflictException();
        }
        // Named, because SaveChanges flushes every tracked row and not just this account: only the
        // account name index says the name the caller just typed is the one already taken.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: AccountConfiguration.NameIndexName,
        })
        {
            dbContext.Entry(account).State = EntityState.Detached;
            throw DuplicateNameValidationException();
        }
    }

    public Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Use the filtered DbSet, not Find/FindAsync: Find can return a tracked entity while
        // bypassing global query filters, which would let one user reference another user's account.
        return dbContext.Accounts.FirstOrDefaultAsync(account => account.Id == id, cancellationToken);
    }

    public async Task UpdateAsync(Account account, CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Same index as AddAsync: a rename collides with exactly the rule an insert would.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: AccountConfiguration.NameIndexName,
        })
        {
            // Detach the rejected entity so the failed (Modified) state can't leak into a later
            // SaveChanges if the context were reused, mirroring AddAsync's detach-on-conflict.
            dbContext.Entry(account).State = EntityState.Detached;
            throw DuplicateNameValidationException();
        }
    }

    public async Task DeleteAsync(Account account, CancellationToken cancellationToken = default)
    {
        dbContext.Accounts.Remove(account);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // The transactions reference is the only foreign key pointing at accounts, so this name is the
        // whole of "it has transactions". Naming it also means a second referencing table, or a
        // stranger's 23503 riding along on the same SaveChanges, cannot block a legitimate delete
        // behind a message about transactions the account does not have.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ForeignKeyViolation,
            ConstraintName: TransactionConfiguration.AccountForeignKeyName,
        })
        {
            // Detach the rejected entity so the failed (Deleted) state can't leak into a later
            // SaveChanges if the context were reused, mirroring AddAsync/UpdateAsync's detach-on-conflict.
            dbContext.Entry(account).State = EntityState.Detached;
            throw ReferencedAccountValidationException();
        }
    }

    public Task<bool> HasTransactionsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        return dbContext.Transactions.AnyAsync(transaction => transaction.AccountId == accountId, cancellationToken);
    }

    private static ValidationException DuplicateNameValidationException() => new(new Dictionary<string, string[]>
    {
        [nameof(Account.Name)] = ["Account name must be unique."],
    });

    // ConflictExceptionHandler renders this as the whole of ProblemDetails.Detail, beside a Title fixed
    // for every conflict in the product, so this string is the whole of what a PERSON is told and has to
    // say what to do next by itself. What a CLIENT branches on is the kind beside it, which this site
    // shares with the four other client-minted tables because the next step does not vary with the table.
    //
    // It names the two readings the server genuinely cannot tell apart — a retry that already succeeded,
    // and an identifier reused by mistake — and gives each its own next step, because the client CAN tell
    // them apart: it knows whether it sent this body before.
    //
    // "Read it back" is deliberately not "it is saved": the id is scoped to the whole table while
    // GET /api/accounts/{id} is scoped to the ambient budget, so a collision with an account in some
    // other budget answers 404 on the read-back. Phrasing it as an instruction rather than a promise
    // keeps the sentence true in that case, where the honest conclusion is the second reading.
    //
    // WORDED ALONGSIDE PayeeRepository's twin ON PURPOSE, because the caller's situation is identical:
    // nothing was written and the identifier its client chose is spoken for. Change one and read the
    // other. It carries no SQLSTATE, constraint name or database text — the caller learns what to do and
    // nothing about the schema that refused it.
    private static ConflictException DuplicateAccountIdConflictException() => new(
        "An account already exists with this identifier. If this request is a retry, read that account "
        + "back by its identifier instead of posting it again; otherwise mint a fresh identifier and post "
        + "again.",
        ConflictKind.DuplicateIdentifier);

    private static ValidationException ReferencedAccountValidationException() => new(new Dictionary<string, string[]>
    {
        [nameof(Account.Id)] = ["Account cannot be deleted because it has transactions."],
    });
}
