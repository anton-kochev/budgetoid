using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Security;
using Domain.Transactions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        // ONE SAVE FOR WHATEVER THE CALLER RE-SEALED, and the transaction the caller opened is what
        // holds it together with the rest of its unit of work. Five saves — one per arm — would commit
        // four arms and leave the fifth to a refusal, which is the account half under each content key
        // the staging design exists to keep out of reach.
        dbContext.SaveChangesAsync(cancellationToken);

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
