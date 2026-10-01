using Application.Accounts;
using Application.Passkeys;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

// Both queries below inner-join Currencies: the accounts.currency_code FK (Restrict) guarantees a
// matching currency row always exists, so the join can neither drop an account from the list nor
// turn an account that exists into a 404.
//
// Neither builds the AccountDto inside the Select any more. accounts.name carries a value converter, so
// the provider translates the property itself and the NarrativeField exists only once the row has
// materialized; encoding it in the projection would be a call the translator has to make sense of. The
// shape is ExportReadService.ListOwnedBudgetsAsync's, deliberately, because it is the same problem.
public sealed class AccountReadService(BudgetoidDbContext dbContext) : IAccountReadService
{
    public async Task<IReadOnlyList<AccountDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // ORDERED BY THE CREATION INSTANT AND THEN BY ID, AND NOT BY NAME. `orderby account.Name` was
        // here and cannot stay: over a bytea column it orders by whatever the first differing byte is,
        // which after the version byte is the nonce — freshly drawn on every seal. That is stable within
        // one read and reshuffled by every save, so a list would silently reorder itself when an
        // unrelated account was renamed, and no ordering a person recognises would ever come back. A name
        // order can only be restored by the client, which is the only side that holds the text.
        //
        // The instant leads and the id breaks ties, the shape ExportReadService and CredentialReadService
        // already use: UUID v7 sorts by creation time under PostgreSQL's uuid byte order but not under
        // .NET's Guid.CompareTo, so an id-first ordering is one an in-memory implementation cannot
        // reproduce. IBudgetRepository.FindFirstForUserAsync states the same rule for the same reason.
        var rows = await (
                from account in dbContext.Accounts.AsNoTracking()
                join currency in dbContext.Currencies.AsNoTracking()
                    on account.CurrencyCode equals currency.Code
                orderby account.CreatedAtUtc, account.Id
                select new
                {
                    account.Id,
                    account.Name,
                    account.Type,
                    account.OpeningBalance,
                    account.CreatedAtUtc,
                    account.CurrencyCode,
                    CurrencyName = currency.Name,
                    currency.Symbol,
                    currency.MinorUnit,
                })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new AccountDto(
                row.Id,
                PasskeyEncoding.Encode(row.Name.Envelope.Span),
                row.Type,
                row.OpeningBalance,
                row.CreatedAtUtc,
                row.CurrencyCode,
                row.CurrencyName,
                row.Symbol,
                row.MinorUnit)),
        ];
    }

    public async Task<AccountDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await (
                from account in dbContext.Accounts.AsNoTracking()
                join currency in dbContext.Currencies.AsNoTracking()
                    on account.CurrencyCode equals currency.Code
                where account.Id == id
                select new
                {
                    account.Id,
                    account.Name,
                    account.Type,
                    account.OpeningBalance,
                    account.CreatedAtUtc,
                    account.CurrencyCode,
                    CurrencyName = currency.Name,
                    currency.Symbol,
                    currency.MinorUnit,
                })
            .FirstOrDefaultAsync(cancellationToken);

        // The envelope goes out as text in the one alphabet every binary member of this API crosses JSON
        // in. PasskeyEncoding despite its name, because it is that alphabet's only implementation here and
        // a second base64url encoder beside it is exactly the drift its neighbours argue against. Not
        // System.Text.Json's own byte[] handling, which emits padded standard base64 the client's strict
        // decoder refuses.
        return row is null
            ? null
            : new AccountDto(
                row.Id,
                PasskeyEncoding.Encode(row.Name.Envelope.Span),
                row.Type,
                row.OpeningBalance,
                row.CreatedAtUtc,
                row.CurrencyCode,
                row.CurrencyName,
                row.Symbol,
                row.MinorUnit);
    }
}
