using Application.KeyRotations;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class RotationCompletenessReadService(BudgetoidDbContext dbContext)
    : IRotationCompletenessReadService
{
    /// <inheritdoc />
    public async Task<bool> EveryNarrativeRowIsStampedAsync(
        Guid userId,
        Guid rotationId,
        CancellationToken cancellationToken = default)
    {
        // IT REFUSES RATHER THAN TRUNCATING, AND THIS IS THE EXPORT'S RULE IN THE EXPORT'S SPELLING.
        // The question is about an ACCOUNT. Five of the six arms below are scoped by the BudgetIsolation
        // query filter to the AMBIENT budget, which takes no argument and cannot be re-pointed part-way
        // through a request; only the budgets arm is scoped by owner. So on an account owning two
        // budgets this read would compare both budgets' name stamps against one budget's contents and
        // could answer "complete" with an entire second budget unrotated — after which the promotion
        // overwrites the live wrapped keys and that budget's whole narrative is unreadable, forever.
        //
        // ExportDataHandler makes exactly this refusal for exactly this reason, and
        // docs/business-logic/export.md is the authority for both. SET EQUALITY, IN BOTH DIRECTIONS, and
        // deliberately not Count > 1: owning a budget this request is not inside means rows are
        // invisible, while being inside a budget the user does not own means another budget's rows are
        // being reported as this account's progress. A count refuses only the first.
        //
        // ANSWERING false WAS THE OTHER OPTION AND IT IS WRONG. It would be indistinguishable from "rows
        // still to do", so the client would re-seal everything it can see and the gate would go on
        // refusing — the non-converging rotation docs/business-logic/key-rotation.md calls harder to
        // diagnose than a crash. A throw says the server cannot answer, which is what is true.
        //
        // THE ONE PLACE THIS DEPARTS FROM THE EXPORT IS THE LAYER, and it is deliberate. The export puts
        // the throw in its handler because it HAS one and that handler is its only caller. Rotation's
        // completion route is unbuilt, so a guard placed in a handler that does not exist yet guards
        // nothing, and the first handler somebody writes would have to remember it — for a mistake with
        // no repair path. Placed here it is a property of the port rather than of a caller's diligence.
        // It is not product policy leaking into Infrastructure: this is the implementation declaring
        // that its own reach does not cover the question its contract asks, which is what
        // InvalidOperationException is for.
        //
        // ONE EXTRA ROUND TRIP, PAID ONCE PER ROTATION. The owned ids have to be materialized to be
        // compared against the ambient budget, and the gate below is a scalar EXISTS, so the two cannot
        // be one statement. The export pays the same cost in the same order, and for the same reason:
        // refused before the expensive read, so a question that will not be answered costs the database
        // nothing but the cheapest of the two.
        Guid ambientBudgetId = dbContext.AmbientBudgetId;
        HashSet<Guid> ownedBudgetIds = [.. await dbContext.Budgets
            .Where(budget => budget.UserId == userId)
            .Select(budget => budget.Id)
            .ToListAsync(cancellationToken)];

        if (!ownedBudgetIds.SetEquals([ambientBudgetId]))
        {
            // Counts, never ids — GlobalExceptionHandler echoes this message and the stack trace into
            // the response body in Development, so an identifier here is an identifier handed to the
            // caller of a request that was refused precisely so that nothing would be.
            int ambientOwned = ownedBudgetIds.Contains(ambientBudgetId) ? 1 : 0;

            throw new RotationScopeException(
                $"The account owns {ownedBudgetIds.Count} budgets, of which {ambientOwned} is the one "
                + "this request operates inside; the gate can read the narrative rows of that budget "
                + "alone, and answering for part of an account is what lets the promotion destroy the "
                + "rest of it.");
        }

        // THE PREDICATE IS THE WHOLE OF THE REST OF THIS FILE, AND IT IS SPELLED != ON PURPOSE. The stamp is
        // nullable, and rotation_id <> @current over an untouched row answers NULL rather than true — so
        // the naive SQL drops every row no rotation has ever visited, which on a first rotation is the
        // entire account. The gate would answer "complete" before a chunk had run and the promotion would
        // destroy the only copies of the keys those rows are sealed under.
        //
        // MEASURED RATHER THAN ASSUMED: written as EF LINQ, row.RotationId != rotationId is SAFE. EF
        // Core's null compensation rewrites it into the IS DISTINCT FROM semantics — the SQL below reads
        // `rotation_id IS NULL OR rotation_id <> @rotationId`, which is what puts an unstamped row and a
        // stale-stamped row on the same side of the line. Both belong there: the first was never
        // rewritten, the second was rewritten by a run somebody abandoned.
        //
        // THE SPELLING THAT IS NOT SAFE IS RotationId.HasValue && RotationId.Value != rotationId. It
        // reads as a careful null guard, translates to a plain `rotation_id IS NOT NULL AND ...`, and
        // silently excludes every never-stamped row. Do not write it here in any form, and do not
        // "simplify" the comparisons below into it. RotationCompletenessTests'
        // Completeness_ForAnAccountThatHasNeverBeenRotated_IsFalse is the one case that catches it.
        //
        // PRESENCE-AWARE, AND ONLY TWO SETS NEED THE TEST. A row carrying no narrative value has nothing
        // to re-seal, so a chunk never visits it and a finished rotation leaves it unstamped. budgets.name
        // and transactions.description are the only two nullable columns that are the WHOLE of their row's
        // narrative — on category groups and categories a nullable description sits beside a required
        // name, and accounts and payees carry a required name and nothing else — so those four sets owe a
        // stamp on every row and carry no presence test at all. The test is not leniency: a note ADDED
        // mid-rotation by a second tab arrives as narrative-with-no-stamp and is outstanding here, which
        // is what it should be.
        //
        // THE OWNER PREDICATE IS ON budgets ALONE, AND ITS ABSENCE ON THE OTHER FIVE IS DELIBERATE. Those
        // five carry the BudgetIsolation query filter, which scopes them to the ambient budget and cannot
        // be switched off — IgnoreQueryFilters is a banned symbol. budgets carries no filter at all, so
        // this Where is the only read-side scoping there is; the user_isolation policy sits under it in
        // production and does not replace it, for the reason ExportReadService gives about its own budgets
        // predicate: a policy makes a wrong query answer EMPTY, not correct, and an empty answer here
        // reads as "complete". Do not drop it as redundant.
        //
        // ONE ROUND TRIP RATHER THAN SIX. The six sets are projected to their identifiers — the only
        // shape they have in common — and concatenated, so the provider emits a single statement whose
        // six arms are UNION ALL'd inside an EXISTS. Six AnyAsync calls would be six sequential round
        // trips on the one connection this context holds; either would be defensible for an operation
        // that runs once per rotation, and this one is chosen because the question is genuinely one
        // question. Nothing downstream sees the identifiers: they exist so the arms have a common
        // element type, and EXISTS never returns them. No AsNoTracking beside them, because a projection
        // to a Guid is untracked already and the extension does not apply to a non-class element type —
        // what keeps this read out of the change tracker is that no line reads an entity into memory.
        IQueryable<Guid> outstanding = dbContext.Budgets
            .Where(budget =>
                budget.UserId == userId
                && budget.Name != null
                && budget.RotationId != rotationId)
            .Select(budget => budget.Id)
            .Concat(dbContext.Accounts
                .Where(account => account.RotationId != rotationId)
                .Select(account => account.Id))
            .Concat(dbContext.Payees
                .Where(payee => payee.RotationId != rotationId)
                .Select(payee => payee.Id))
            .Concat(dbContext.CategoryGroups
                .Where(categoryGroup => categoryGroup.RotationId != rotationId)
                .Select(categoryGroup => categoryGroup.Id))
            .Concat(dbContext.Categories
                .Where(category => category.RotationId != rotationId)
                .Select(category => category.Id))
            .Concat(dbContext.Transactions
                .Where(transaction =>
                    transaction.Description != null
                    && transaction.RotationId != rotationId)
                .Select(transaction => transaction.Id));

        // Asked as "is anything outstanding" and negated, rather than as a count compared to another
        // count. EXISTS stops at the first row it finds, and a count would have to be believed against a
        // second count of what the account holds — two reads that can disagree, on a table that is being
        // written while they run.
        return !await outstanding.AnyAsync(cancellationToken);
    }
}
