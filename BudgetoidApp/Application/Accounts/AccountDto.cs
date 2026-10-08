using Application.Currencies;
using Application.Passkeys;
using Domain.Accounts;

namespace Application.Accounts;

/// <summary>
/// One account as every account-reading route hands it back.
/// </summary>
/// <param name="Id">The row's identifier — also the associated data <paramref name="Name"/> was sealed against.</param>
/// <param name="Name">The <b>sealed</b> name as unpadded base64url; see the remarks.</param>
/// <param name="Type">The kind of account.</param>
/// <param name="OpeningBalance">The balance the ledger starts from.</param>
/// <param name="CreatedAtUtc">The creation instant, in UTC.</param>
/// <param name="CurrencyCode">The account's ISO 4217 code.</param>
/// <param name="CurrencyName">The currency's display name.</param>
/// <param name="CurrencySymbol">The currency's symbol.</param>
/// <param name="CurrencyMinorUnit">How many decimal places the currency admits.</param>
/// <remarks>
/// <para>
/// <b><see cref="Name"/> is still a <see cref="string"/> and no longer holds a name</b> — the treatment
/// <c>ExportedBudget.Name</c> already carries. The column is an AEAD envelope this server cannot open, so
/// what ships is that envelope in the one alphabet every binary member of this API crosses JSON in:
/// unpadded base64url, which the client's strict decoder already reads. Not
/// <c>System.Text.Json</c>'s own <see cref="byte"/><c>[]</c> handling, which emits padded standard
/// base64 — two spellings that disagree the first time somebody decodes one with the other.
/// </para>
/// <para>
/// <b>The blind index is on no read, and its absence is a decision rather than an omission.</b> The
/// requirement says the index is stored, not returned: a client recomputes it from the name it just
/// decrypted, under a key only it holds, and needs it solely to write. A member nobody reads is a
/// standing surface with no reason — and this one would hand every caller a deterministic, per-account
/// fingerprint of a name, which is the one property of the pair that survives having no key.
/// </para>
/// </remarks>
public sealed record AccountDto(
    Guid Id,
    string Name,
    AccountType Type,
    decimal OpeningBalance,
    DateTime CreatedAtUtc,
    string CurrencyCode,
    string CurrencyName,
    string CurrencySymbol,
    int CurrencyMinorUnit)
{
    public static AccountDto FromAccount(Account account, CurrencyDto currency) => new(
        account.Id,
        PasskeyEncoding.Encode(account.Name.Envelope.Span),
        account.Type,
        account.OpeningBalance,
        account.CreatedAtUtc,
        account.CurrencyCode,
        currency.Name,
        currency.Symbol,
        currency.MinorUnit);
}

public sealed record AccountListResponse(IReadOnlyList<AccountDto> Items);
