using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Common;
using Domain.Payees;
using Domain.Security;
using Domain.Transactions;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;

namespace Infrastructure.Repositories;

/// <summary>
/// The five budget-owned sets a rotation chunk re-seals, read by row identifier and written back in one
/// save.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every read is scoped by the identifiers the chunk named, and the predicate is the point of the
/// member rather than tidiness.</b> Dropped, each read answers the whole budget: every chunk of a
/// rotation would drag the account's entire <c>transactions</c> table — the largest an account holds —
/// through the change tracker, on a request that is chunked precisely because the account does not fit
/// in one. It also widens the blast radius of every later defect on this path from the rows a client
/// named to the whole budget. <c>ResealChunkTests.ResealChunk_LoadsOnlyTheRowsTheChunkNames</c> asks
/// these members directly rather than reading a chunk's side effects, because a handler that drives
/// from the command writes to none of the surplus and the over-fetch is invisible on disk.
/// </para>
/// <para>
/// <b>The budget predicate is NOT written beside it, and that is the one scoping difference from
/// <see cref="KeyRotationRepository" />.</b> These five sets carry the <c>BudgetIsolation</c> query
/// filter, which is a real predicate EF puts in the SQL — so a row of another budget is absent from the
/// answer whether or not the <c>budget_isolation</c> policy is in force. The written owner predicates
/// that file argues for are about <c>user_isolation</c>, which is a <em>policy</em> and nothing more:
/// there a query that lost its scoping answers empty rather than wrong, and empty is the dangerous
/// answer. Restating the budget here would be a second copy of a filter the model already applies to
/// every query over these sets.
/// </para>
/// <para>
/// <b>The rows come back TRACKED, and that is what the caller needs rather than an oversight.</b> A
/// reseal is a mutation of a loaded entity followed by <see cref="SaveAsync" />, so
/// <c>AsNoTracking</c> here would answer a chunk that appears to succeed and writes nothing. The
/// never-materialise rule <see cref="KeyRotationRepository" /> keeps is about
/// <c>wrapped_account_keys</c>, whose role holds no <c>DELETE</c> and where a tracked row EF later
/// cascades into dies with <c>42501</c>; these five tables are the ordinary budget-owned ones, each
/// carrying a column-listed <c>GRANT UPDATE</c> that now names <c>rotation_id</c>, and tracking them is
/// exactly what that grant is for.
/// </para>
/// <para>
/// <b>A row of another budget is absent from the answer rather than raising here.</b> The refusal is
/// the caller's, for the reason <see cref="INarrativeResealRepository" /> gives: a miss and a row that
/// simply was not asked for are the same observation at this layer, and only the caller holds the list
/// that tells them apart.
/// </para>
/// <para>
/// <b><see cref="SaveAsync" /> translates exactly one violation, and it is a rule it holds rather than
/// one it borrows.</b> A run re-seals a row under the incoming index key and frees its outgoing value; a
/// stale tab files a second row under that value; the chunk that re-seals the second row lands it on the
/// first one's new value and breaks the <c>(budget_id, name_key)</c> index. The table's own repository
/// catches the same index with a different answer — the create's <c>duplicate_name</c>, the rename's
/// <c>400</c> — and neither is the remedy here, so this save answers
/// <see cref="ConflictKind.RotationNameCollision" />. The filter names the four index constants and
/// nothing wider; a <c>42501</c> and every other constraint these tables carry still leave as the
/// <see cref="DbUpdateException" /> EF threw.
/// </para>
/// </remarks>
public sealed class NarrativeResealRepository(BudgetoidDbContext dbContext) : INarrativeResealRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, Account>> ListAccountsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        // Find/FindAsync is a banned symbol, so an identifier read is spelled as a predicate like every
        // other one in this assembly — and a set read could not use it anyway.
        List<Account> rows = await dbContext.Accounts
            .Where(account => ids.Contains(account.Id))
            .ToListAsync(cancellationToken);

        return Keyed(rows, account => account.Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, Payee>> ListPayeesAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        List<Payee> rows = await dbContext.Payees
            .Where(payee => ids.Contains(payee.Id))
            .ToListAsync(cancellationToken);

        return Keyed(rows, payee => payee.Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, CategoryGroup>> ListCategoryGroupsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        List<CategoryGroup> rows = await dbContext.CategoryGroups
            .Where(categoryGroup => ids.Contains(categoryGroup.Id))
            .ToListAsync(cancellationToken);

        return Keyed(rows, categoryGroup => categoryGroup.Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, Category>> ListCategoriesAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        List<Category> rows = await dbContext.Categories
            .Where(category => ids.Contains(category.Id))
            .ToListAsync(cancellationToken);

        return Keyed(rows, category => category.Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, Transaction>> ListTransactionsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        List<Transaction> rows = await dbContext.Transactions
            .Where(transaction => ids.Contains(transaction.Id))
            .ToListAsync(cancellationToken);

        return Keyed(rows, transaction => transaction.Id);
    }

    /// <inheritdoc />
    /// <exception cref="ConflictException">
    /// A re-sealed name lands on a blind-index value another row of the budget already holds, on any of
    /// the four name indexes a chunk can break. Spelled <c>rotation_name_collision</c>.
    /// </exception>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // ONE SAVE FOR WHATEVER THE CALLER RE-SEALED, and the transaction the caller opened is what
            // holds it together with the rest of its unit of work. Five saves — one per arm — would commit
            // four arms and leave the fifth to a refusal, which is the account half under each content key
            // the staging design exists to keep out of reach.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Named, and never on SQLSTATE alone: SaveChanges flushes every tracked row and not only the ones
        // a chunk re-sealed, so a stranger's 23505 matched on SQLSTATE would come back telling a client to
        // rename a row that broke nothing. Named by the four constants, and never by the shape of a name.
        // Two shapes are caught by a control today: an "IX_" prefix (IX_users_email begins that way too)
        // and a "_name" substring (IX_budgets_user_id_name carries one). A "_name_key" suffix is caught by
        // NO test, because these four are the only such indexes in the schema — the rule holds it, not a
        // test. A PostgresException carries exactly one ConstraintName, so listing all four is one filter
        // rather than four arms: the remedy does not vary with the table.
        //
        // The throw leaves the caller's executor delegate, whose transaction is disposed unfinished, so
        // the whole chunk rolls back — every arm, not only the one that collided. ConflictException is not
        // transient, so a retrying execution strategy does not replay the chunk into the same refusal.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: PayeeConfiguration.NameIndexName
                or AccountConfiguration.NameIndexName
                or CategoryGroupConfiguration.NameIndexName
                or CategoryConfiguration.NameIndexName,
        })
        {
            // Detach every pending reseal so none of it can ride a later SaveChanges on this context, the
            // detach-on-conflict every sibling repository does — here over the chunk rather than one row,
            // because the chunk is the unit that was refused.
            DiscardPendingReseals();
            throw RotationNameCollisionConflictException();
        }
    }

    /// <summary>
    /// Detaches every row of the five re-sealed sets still carrying a change the refused save did not
    /// write.
    /// </summary>
    private void DiscardPendingReseals()
    {
        List<EntityEntry> pending =
        [
            .. dbContext.ChangeTracker.Entries().Where(entry =>
                entry.State is not (EntityState.Unchanged or EntityState.Detached)
                && entry.Entity is Account or Payee or CategoryGroup or Category or Transaction),
        ];

        foreach (EntityEntry entry in pending)
        {
            entry.State = EntityState.Detached;
        }
    }

    // ConflictExceptionHandler renders this as the whole of ProblemDetails.Detail beside a Title fixed for
    // every 409 in the product, so this sentence is all a PERSON is told; the kind is what a client
    // branches on.
    //
    // IT NAMES NO ROW AND NO IDENTIFIER. The handler copies it verbatim, and the row already holding the
    // name is one the client can find by decrypting its own list — which it has to do anyway to show a
    // person the two names. It carries no SQLSTATE, constraint name or database text either.
    //
    // It deliberately does NOT say "send the outstanding chunks", which is RotationIncomplete's remedy
    // and is useless here: this chunk is refused the same way every time until a row is renamed. Nor does
    // it say "use the one that already exists", which is DuplicateName's and would fold two of the
    // person's rows into one. And it does not say "send the chunk again": the renamed row carries a new
    // name and a cleared stamp, so the refused chunk is stale and has to be collected and built afresh.
    private static ConflictException RotationNameCollisionConflictException() => new(
        "Two rows would end up with the same name under the new keys, so this chunk was refused and "
        + "nothing in it was written. Rename one of them, then carry on with the rotation.",
        ConflictKind.RotationNameCollision);

    /// <summary>
    /// The rows keyed on their identifier, which is how the port answers a lookup.
    /// </summary>
    /// <remarks>
    /// <c>ToDictionary</c> rather than a grouping that picks a winner: <c>id</c> is the primary key of
    /// all five tables, so a repeated key is a database that has lost that rule rather than a case to
    /// choose in — and throwing is the direction that says so.
    /// </remarks>
    private static IReadOnlyDictionary<Guid, TRow> Keyed<TRow>(
        List<TRow> rows,
        Func<TRow, Guid> identify) =>
        rows.ToDictionary(identify);
}
