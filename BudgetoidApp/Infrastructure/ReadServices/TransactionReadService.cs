using Application.Passkeys;
using Application.Transactions;
using Domain.Security;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

// Neither query builds the TransactionDto inside the Select any more. transactions.description,
// accounts.name, payees.name, categories.name and category_groups.name all carry a value converter, so
// the provider translates the property itself and the NarrativeField exists only once the row has
// materialized; encoding any of them in the projection would be a call the translator has to make sense
// of. AccountReadService, PayeeReadService, CategoryReadService, CategoryGroupReadService and
// ExportReadService take the same shape for the same reason.
//
// ALL FIVE NARRATIVE VALUES ARE SEALED AND ONE RULE COVERS THEM. This header used to say that three of
// the four names were sealed and one was not, and that a reader must not fold them into one rule; there
// is nothing left to fold. The transaction's own note joined them in the same slice as the category's
// name, so the set is uniform.
//
// The four envelopes bind to four DIFFERENT rows and the description binds to a fifth - this
// transaction's own - which no code here can enforce and a client must honour. TransactionDto's remarks
// carry that argument.
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
    /// One materialized row of either query, carrying all five narrative values as their columns hold
    /// them.
    /// </summary>
    /// <remarks>
    /// A named type rather than an anonymous one, and shared by both queries rather than declared twice:
    /// it is what makes <see cref="Shape"/> the single place a row becomes a wire shape, so a member
    /// added to one projection and not the other stops compiling instead of quietly shipping on one route
    /// only. That property is what a dropped <c>Description</c> runs into: the column is nullable, so a
    /// projection that stopped selecting it would ship <see langword="null"/> on every transaction and no
    /// constraint anywhere would notice — and nor would a case that only read a 201 body back, which is
    /// built from the entity the handler just wrote rather than from the column.
    /// </remarks>
    private sealed record Row(
        Guid Id,
        decimal Amount,
        DateOnly Date,
        NarrativeField? Description,
        DateTime CreatedAtUtc,
        Guid AccountId,
        NarrativeField AccountName,
        string CurrencyCode,
        string CurrencySymbol,
        Guid? PayeeId,
        NarrativeField? PayeeName,
        Guid? CategoryId,
        NarrativeField? CategoryName,
        Guid? CategoryGroupId,
        NarrativeField? CategoryGroupName);

    /// <summary>
    /// Turns one materialized row into the shape the wire carries, which is where all five sealed values
    /// become text.
    /// </summary>
    /// <remarks>
    /// Every envelope goes out in the one alphabet every binary member of this API crosses JSON in.
    /// <see cref="PasskeyEncoding"/> despite its name, because it is that alphabet's only implementation
    /// here and a second base64url encoder beside it is exactly the drift its neighbours argue against —
    /// and not <c>System.Text.Json</c>'s own <see cref="byte"/><c>[]</c> handling, which emits padded
    /// standard base64 the client's strict decoder refuses. A null payee, category and category group
    /// stay null: the members are absent on a transaction naming none of them, which is not the same as
    /// an envelope over an empty name. <b>The description's <c>?? string.Empty</c> is gone and must never
    /// come back</b> — it shipped while the column held text and a screen needed something to render, and
    /// it now hands a client a value it is asked to decode and cannot.
    /// </remarks>
    private static TransactionDto Shape(Row row) => new(
        row.Id,
        row.Amount,
        row.Date,
        row.Description is null ? null : PasskeyEncoding.Encode(row.Description.Envelope.Span),
        row.CreatedAtUtc,
        row.AccountId,
        PasskeyEncoding.Encode(row.AccountName.Envelope.Span),
        row.CurrencyCode,
        row.CurrencySymbol,
        row.PayeeId,
        row.PayeeName is null ? null : PasskeyEncoding.Encode(row.PayeeName.Envelope.Span),
        row.CategoryId,
        row.CategoryName is null ? null : PasskeyEncoding.Encode(row.CategoryName.Envelope.Span),
        row.CategoryGroupId,
        row.CategoryGroupName is null
            ? null
            : PasskeyEncoding.Encode(row.CategoryGroupName.Envelope.Span));
}
