using Application.Passkeys;
using Application.Transactions;
using Domain.Security;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

// Neither query builds the TransactionDto inside the Select any more. accounts.name and payees.name both
// carry a value converter, so the provider translates the property itself and the NarrativeField exists
// only once the row has materialized; encoding either in the projection would be a call the translator
// has to make sense of. AccountReadService and ExportReadService take the same shape for the same reason.
//
// Two of the four names are sealed and two are not. The account's and the payee's leave the Select and
// are encoded in Shape; the category's and the category group's stay inside it, because those columns
// are still readable text. A reader must not fold the four into one rule — the payee joined the sealed
// pair, it did not make the set uniform.
public sealed class TransactionReadService(BudgetoidDbContext dbContext) : ITransactionReadService
{
    public async Task<IReadOnlyList<TransactionDto>> GetAllWithPayeeAsync(
        CancellationToken cancellationToken = default)
    {
        List<Row> rows = await (
                from transaction in dbContext.Transactions.AsNoTracking()
                join account in dbContext.Accounts.AsNoTracking()
                    on transaction.AccountId equals account.Id
                join currency in dbContext.Currencies.AsNoTracking()
                    on account.CurrencyCode equals currency.Code
                join payee in dbContext.Payees.AsNoTracking()
                    on transaction.PayeeId equals (Guid?)payee.Id into payees
                from payee in payees.DefaultIfEmpty()
                join category in dbContext.Categories.AsNoTracking()
                    on transaction.CategoryId equals (Guid?)category.Id into categories
                from category in categories.DefaultIfEmpty()
                join categoryGroup in dbContext.CategoryGroups.AsNoTracking()
                    on category.CategoryGroupId equals categoryGroup.Id into categoryGroups
                from categoryGroup in categoryGroups.DefaultIfEmpty()
                orderby transaction.Date descending, transaction.CreatedAtUtc descending
                select new Row(
                    transaction.Id,
                    transaction.Amount,
                    transaction.Date,
                    transaction.Description,
                    transaction.CreatedAtUtc,
                    transaction.AccountId,
                    account.Name,
                    account.CurrencyCode,
                    currency.Symbol,
                    transaction.PayeeId,
                    payee == null ? null : payee.Name,
                    transaction.CategoryId,
                    category == null ? null : category.Name,
                    categoryGroup == null ? null : categoryGroup.Id,
                    categoryGroup == null ? null : categoryGroup.Name))
            .ToListAsync(cancellationToken);

        return [.. rows.Select(Shape)];
    }

    // Same joins as the list query, so one transaction reads exactly as it does in the list. The
    // account and currency joins are inner: transactions.account_id and accounts.currency_code are
    // both non-null FKs, and the account FK is scoped to the transaction's own budget, so the
    // budget filter on Accounts can never hide the account of a visible transaction.
    public async Task<TransactionDto?> GetByIdWithPayeeAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Row? row = await (
                from transaction in dbContext.Transactions.AsNoTracking()
                join account in dbContext.Accounts.AsNoTracking()
                    on transaction.AccountId equals account.Id
                join currency in dbContext.Currencies.AsNoTracking()
                    on account.CurrencyCode equals currency.Code
                join payee in dbContext.Payees.AsNoTracking()
                    on transaction.PayeeId equals (Guid?)payee.Id into payees
                from payee in payees.DefaultIfEmpty()
                join category in dbContext.Categories.AsNoTracking()
                    on transaction.CategoryId equals (Guid?)category.Id into categories
                from category in categories.DefaultIfEmpty()
                join categoryGroup in dbContext.CategoryGroups.AsNoTracking()
                    on category.CategoryGroupId equals categoryGroup.Id into categoryGroups
                from categoryGroup in categoryGroups.DefaultIfEmpty()
                where transaction.Id == id
                select new Row(
                    transaction.Id,
                    transaction.Amount,
                    transaction.Date,
                    transaction.Description,
                    transaction.CreatedAtUtc,
                    transaction.AccountId,
                    account.Name,
                    account.CurrencyCode,
                    currency.Symbol,
                    transaction.PayeeId,
                    payee == null ? null : payee.Name,
                    transaction.CategoryId,
                    category == null ? null : category.Name,
                    categoryGroup == null ? null : categoryGroup.Id,
                    categoryGroup == null ? null : categoryGroup.Name))
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : Shape(row);
    }

    /// <summary>
    /// One materialized row of either query, carrying the account's and the payee's names as their
    /// columns hold them.
    /// </summary>
    /// <remarks>
    /// A named type rather than an anonymous one, and shared by both queries rather than declared twice:
    /// it is what makes <see cref="Shape"/> the single place a row becomes a wire shape, so a member
    /// added to one projection and not the other stops compiling instead of quietly shipping on one route
    /// only. <c>Description</c> stays nullable here — the coercion to the empty string is the DTO's, and
    /// doing it in the projection would be a second owner for a rule the export deliberately does not
    /// follow.
    /// </remarks>
    private sealed record Row(
        Guid Id,
        decimal Amount,
        DateOnly Date,
        string? Description,
        DateTime CreatedAtUtc,
        Guid AccountId,
        NarrativeField AccountName,
        string CurrencyCode,
        string CurrencySymbol,
        Guid? PayeeId,
        NarrativeField? PayeeName,
        Guid? CategoryId,
        string? CategoryName,
        Guid? CategoryGroupId,
        string? CategoryGroupName);

    /// <summary>
    /// Turns one materialized row into the shape the wire carries, which is where the two sealed names
    /// become text.
    /// </summary>
    /// <remarks>
    /// Both envelopes go out in the one alphabet every binary member of this API crosses JSON in.
    /// <see cref="PasskeyEncoding"/> despite its name, because it is that alphabet's only implementation
    /// here and a second base64url encoder beside it is exactly the drift its neighbours argue against —
    /// and not <c>System.Text.Json</c>'s own <see cref="byte"/><c>[]</c> handling, which emits padded
    /// standard base64 the client's strict decoder refuses. A null payee stays null: the member is
    /// absent on a transaction naming no payee, which is not the same as an envelope over an empty name.
    /// </remarks>
    private static TransactionDto Shape(Row row) => new(
        row.Id,
        row.Amount,
        row.Date,
        row.Description ?? string.Empty,
        row.CreatedAtUtc,
        row.AccountId,
        PasskeyEncoding.Encode(row.AccountName.Envelope.Span),
        row.CurrencyCode,
        row.CurrencySymbol,
        row.PayeeId,
        row.PayeeName is null ? null : PasskeyEncoding.Encode(row.PayeeName.Envelope.Span),
        row.CategoryId,
        row.CategoryName,
        row.CategoryGroupId,
        row.CategoryGroupName);
}
