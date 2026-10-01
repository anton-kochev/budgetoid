using Application.KeyRotations;
using Application.KeyRotations.BeginKeyRotation;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class RotationInventoryReadService(BudgetoidDbContext dbContext)
    : IRotationInventoryReadService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListOwnedBudgetIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        // THE OWNER PREDICATE IS THE ONLY READ-SIDE SCOPING THERE IS on this one set. budgets carries
        // no BudgetIsolation query filter — it is what registration writes and what session
        // authentication reads before any budget is ambient — so the Where below is written by hand or
        // it is not there at all. ExportReadService and RotationCompletenessReadService make and
        // document the same split, and the user_isolation policy underneath does not replace it: a
        // policy makes a wrong query answer EMPTY rather than correct, and an empty owned set here is a
        // refusal aimed at a caller who did nothing wrong.
        //
        // Projected to identifiers, so no Budget entity is materialised for a question about ids. No
        // AsNoTracking beside it, because a projection to a Guid is untracked already — what keeps this
        // out of the change tracker is that no line reads an entity into memory.
        await dbContext.Budgets
            .Where(budget => budget.UserId == userId)
            .Select(budget => budget.Id)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<RotationInventory> CountNarrativeRowsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // THE POPULATION IS RotationCompletenessReadService's POPULATION, EXPRESSED AS A COUNT RATHER
        // THAN AS AN EXISTS, and the two must not drift. That read asks whether any row CARRYING A
        // NARRATIVE VALUE is unstamped; this one answers how many such rows there are, because the
        // client divides its progress by it. Count a wider set and a rotation that genuinely finished
        // reports itself short on an ordinary account; count a narrower one and the client stops while
        // the gate is still refusing — the non-converging rotation docs/business-logic/key-rotation.md
        // calls harder to diagnose than a crash.
        //
        // PRESENCE-AWARE ON EXACTLY TWO SETS, for the reason the gate gives. budgets.name and
        // transactions.description are the only nullable columns that are the WHOLE of their row's
        // narrative — on category_groups and categories a nullable description sits beside a required
        // name, and accounts and payees carry a required name and nothing else — so the other four owe
        // a stamp on every row and carry no presence test at all.
        //
        // THE OWNER PREDICATE IS ON budgets ALONE, and its absence on the other five is deliberate:
        // those five carry the BudgetIsolation query filter, which scopes them to the ambient budget
        // and which IgnoreQueryFilters — a banned symbol — cannot switch off. The caller is responsible
        // for having established that the ambient budget is exactly what the account owns before it
        // asks; BeginKeyRotationHandler refuses with RotationScopeException when it is not.
        //
        // SIX ROUND TRIPS, PAID ONCE PER ROTATION. The completeness gate folds its six arms into one
        // statement because they are six halves of one EXISTS and share an element type; six counts
        // share nothing, and the single-statement spelling would be six scalar subqueries hung off a
        // contrived root — more machinery than the saved round trips are worth on a call that happens
        // once at the start of a run. Sequenced rather than run concurrently because this context holds
        // one connection.
        int budgets = await dbContext.Budgets
            .CountAsync(budget => budget.UserId == userId && budget.Name != null, cancellationToken);

        int accounts = await dbContext.Accounts.CountAsync(cancellationToken);
        int payees = await dbContext.Payees.CountAsync(cancellationToken);
        int categoryGroups = await dbContext.CategoryGroups.CountAsync(cancellationToken);
        int categories = await dbContext.Categories.CountAsync(cancellationToken);

        int transactions = await dbContext.Transactions
            .CountAsync(transaction => transaction.Description != null, cancellationToken);

        // Named arguments, because six ints in a row is the one call shape where a transposed pair
        // compiles, stores and is discovered as a progress bar that finishes early.
        return new RotationInventory(
            Accounts: accounts,
            Payees: payees,
            CategoryGroups: categoryGroups,
            Categories: categories,
            Transactions: transactions,
            Budgets: budgets);
    }
}
