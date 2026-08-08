using Application.Abstractions;

namespace Application.Users.ExportData;

/// <summary>
/// Assembles the one document that is a complete copy of an account.
/// </summary>
/// <remarks>
/// <para>
/// <b>It takes no <c>ILogger</c> and no <c>ILoggerFactory</c>, and must never take one.</b> The value
/// it holds in memory is every transaction a person has recorded plus the address they signed up
/// with; a single <c>LogDebug</c> of the document, or of the id it was assembled for, copies the lot
/// into a sink with a different retention policy and a different audience from the database it came
/// from. No gate anywhere reads a log line from this path, so there is nothing to trade against.
/// </para>
/// <para>
/// A <see langword="null" /> user row on a resolved identity is a broken invariant rather than a state
/// the product can produce — the account, its first credential and its default budget go in one save —
/// so it throws rather than answering an empty document. Same policy
/// <c>IBudgetRepository.FindFirstForUserAsync</c> states for the budget beneath it.
/// </para>
/// <para>
/// <b>It refuses rather than truncates.</b> The five collection reads beneath it are scoped to the
/// ambient budget by the <c>BudgetIsolation</c> query filter and the <c>budget_isolation</c> policy,
/// neither of which takes an argument and neither of which can be re-pointed part-way through a
/// request. So unless the owned set is exactly the ambient budget the handler throws
/// <see cref="ExportCompletenessException" />: set equality in both directions, because a count-only
/// guard passes an ambient budget the user does not own and would file one budget's rows under
/// another's id.
/// </para>
/// </remarks>
public sealed class ExportDataHandler(
    IUserContext userContext,
    IBudgetContext budgetContext,
    IExportReadService readService)
    : IQueryHandler<ExportDataQuery, ExportDocument>
{
    public async Task<ExportDocument> HandleAsync(
        ExportDataQuery query,
        CancellationToken cancellationToken = default)
    {
        Guid userId = userContext.UserId;

        ExportedUser user = await readService.FindUserAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException(
                "The resolved identity for this request answers to no user row.");

        IReadOnlyList<ExportedBudget> owned =
            await readService.ListOwnedBudgetsAsync(userId, cancellationToken);

        Guid ambientBudgetId = budgetContext.BudgetId;
        HashSet<Guid> ownedIds = [.. owned.Select(budget => budget.Id)];

        // Set equality, not a count: the owned set must be exactly the budget this request operates
        // inside. Refused before the contents are read, so a document that will not be assembled costs
        // nobody's finances a round trip. Counts only in the message — ids reach the caller.
        if (!ownedIds.SetEquals([ambientBudgetId]))
        {
            int ambientOwned = ownedIds.Contains(ambientBudgetId) ? 1 : 0;

            throw new ExportCompletenessException(
                $"The signed-in user owns {ownedIds.Count} budgets, of which {ambientOwned} is the one "
                + "this request operates inside; the export can read the contents of that budget alone, "
                + "and a partial export is a truncation.");
        }

        ExportedBudgetContents contents =
            await readService.ReadAmbientBudgetContentsAsync(cancellationToken);

        // The owned set is the ambient budget and nothing else — the gate above is what makes that
        // true — so attaching the ambient budget's contents to every budget in the document attaches
        // them to the budget they were read from.
        IReadOnlyList<ExportedBudget> budgets =
        [
            .. owned.Select(budget => budget with
            {
                Accounts = contents.Accounts,
                CategoryGroups = contents.CategoryGroups,
                Categories = contents.Categories,
                Payees = contents.Payees,
                Transactions = contents.Transactions,
            }),
        ];

        return new ExportDocument(ExportDocument.CurrentSchemaVersion, user, budgets);
    }
}
