using Application.Passkeys;
using Application.Users.ExportData;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

/// <summary>
/// Reads an account and its budgets straight out of the tables, without going through any read model
/// shaped for a screen.
/// </summary>
/// <remarks>
/// Every query is <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}" />: nothing here
/// is mutated, and a tracked graph of an entire account is the one thing this path must not build.
/// </remarks>
public sealed class ExportReadService(BudgetoidDbContext dbContext) : IExportReadService
{
    /// <inheritdoc />
    public async Task<ExportedUser?> FindUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Projected as the Email value object rather than as Email.Value: the property carries a value
        // converter, so the provider translates the property itself and the unwrapping happens after
        // materialization. UserAccountReadService does the same, deliberately.
        //
        // The id predicate is not what scopes this — users is policed by user_isolation, so another
        // person's id comes back empty rather than theirs — but naming it makes the statement an index
        // seek rather than a scan the policy then filters.
        var row = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.Id, user.Email, user.CreatedAtUtc })
            .SingleOrDefaultAsync(cancellationToken);

        return row is null ? null : new ExportedUser(row.Id, row.Email.Value, row.CreatedAtUtc);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExportedBudget>> ListOwnedBudgetsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // budgets carries no BudgetIsolation query filter — it is what registration writes and what
        // session authentication reads before any
        // budget is ambient — so this predicate is the only read-side scoping there is, and the
        // user_isolation policy is what enforces it. Do not drop it on the grounds that the policy
        // covers it: the policy makes a wrong query answer empty, not correct.
        //
        // Projected into an anonymous row and shaped afterwards, the way FindUserAsync above handles
        // Email and for the same reason: name carries a value converter, so the provider translates the
        // property itself and the NarrativeField exists only once the row has materialized. Encoding it
        // inside the Select would be a call the translator has to make sense of.
        var rows = await dbContext.Budgets
            .AsNoTracking()
            .Where(budget => budget.UserId == userId)
            .OrderBy(budget => budget.CreatedAtUtc)
            .ThenBy(budget => budget.Id)
            .Select(budget => new
            {
                budget.Id,
                budget.UserId,
                budget.Name,
                budget.BaseCurrencyCode,
                budget.CreatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        // The envelope goes out as text in the one alphabet every binary member of this API crosses
        // JSON in. PasskeyEncoding is reached for despite its name because it is that alphabet's only
        // implementation here — CiphertextEnvelopeText decodes through it too — and a second base64url
        // encoder beside it is exactly the drift CiphertextEnvelopeText argues against. Not
        // System.Text.Json's own byte[] handling, which emits padded standard base64: the client's
        // decoder is strict base64url, so the two spellings would disagree on the day somebody reloads
        // a file they saved.
        return
        [
            .. rows.Select(row => new ExportedBudget(
                row.Id,
                row.UserId,
                row.Name is null ? null : PasskeyEncoding.Encode(row.Name.Envelope.Span),
                row.BaseCurrencyCode,
                row.CreatedAtUtc)),
        ];
    }

    /// <inheritdoc />
    public async Task<ExportedBudgetContents> ReadAmbientBudgetContentsAsync(
        CancellationToken cancellationToken = default)
    {
        // No budget_id predicate on any of the five, and that is deliberate. Every set below carries the
        // BudgetIsolation query filter, which reads IBudgetContext on the context instance running the
        // query, and the budget_isolation policy enforces the same boundary under it. A predicate here
        // would restate the tenancy in a third place and would be the one place a later reader could
        // widen without failing a policy test.
        //
        // Five round trips rather than one join: no entity in this model declares a navigation property,
        // so Include is unavailable and the document is stitched by id in the handler. They run in
        // sequence because one DbContext serves one command at a time.
        List<ExportedAccount> accounts = await dbContext.Accounts
            .AsNoTracking()
            .OrderBy(account => account.CreatedAtUtc)
            .ThenBy(account => account.Id)
            .Select(account => new ExportedAccount(
                account.Id,
                account.BudgetId,
                account.Name,
                account.Type,
                account.OpeningBalance,
                account.CurrencyCode,
                account.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        List<ExportedCategoryGroup> categoryGroups = await dbContext.CategoryGroups
            .AsNoTracking()
            .OrderBy(categoryGroup => categoryGroup.CreatedAtUtc)
            .ThenBy(categoryGroup => categoryGroup.Id)
            .Select(categoryGroup => new ExportedCategoryGroup(
                categoryGroup.Id,
                categoryGroup.BudgetId,
                categoryGroup.Name,
                categoryGroup.Description,
                categoryGroup.Position,
                categoryGroup.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        List<ExportedCategory> categories = await dbContext.Categories
            .AsNoTracking()
            .OrderBy(category => category.CreatedAtUtc)
            .ThenBy(category => category.Id)
            .Select(category => new ExportedCategory(
                category.Id,
                category.BudgetId,
                category.CategoryGroupId,
                category.Name,
                category.Description,
                category.Position,
                category.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        List<ExportedPayee> payees = await dbContext.Payees
            .AsNoTracking()
            .OrderBy(payee => payee.CreatedAtUtc)
            .ThenBy(payee => payee.Id)
            .Select(payee => new ExportedPayee(
                payee.Id,
                payee.BudgetId,
                payee.Name,
                payee.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        // Description, PayeeId and CategoryId are projected as they are persisted. TransactionDto
        // coerces a null description to the empty string because a screen has to render something; a
        // copy of somebody's data must not, or the difference between "no note" and "a blank note" is
        // gone from every file this writes.
        List<ExportedTransaction> transactions = await dbContext.Transactions
            .AsNoTracking()
            .OrderBy(transaction => transaction.CreatedAtUtc)
            .ThenBy(transaction => transaction.Id)
            .Select(transaction => new ExportedTransaction(
                transaction.Id,
                transaction.BudgetId,
                transaction.AccountId,
                transaction.Amount,
                transaction.Date,
                transaction.Description,
                transaction.PayeeId,
                transaction.CategoryId,
                transaction.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        // Wrapped rather than handed over as they were materialized. ToListAsync returns a List, and a
        // List behind ExportedBudgetContents' IReadOnlyList is castable back to one by anything holding
        // the value — a copy of somebody's data would be clearable through the interface that says it is
        // read-only. AsReadOnly is a wrapper per collection rather than a second materialization of five
        // collections that may run to thousands of rows, which is why it is preferred here to rebuilding
        // each one through a collection expression.
        return new ExportedBudgetContents(
            accounts.AsReadOnly(),
            categoryGroups.AsReadOnly(),
            categories.AsReadOnly(),
            payees.AsReadOnly(),
            transactions.AsReadOnly());
    }
}
