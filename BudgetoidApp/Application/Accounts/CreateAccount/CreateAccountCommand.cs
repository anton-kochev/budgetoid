using Domain.Accounts;

namespace Application.Accounts.CreateAccount;

/// <summary>
/// The body of <c>POST /api/accounts</c>: an identifier the client minted, the name it sealed against
/// that identifier, the blind index it took over the same text, and the three members this server can
/// still read.
/// </summary>
/// <param name="Id">
/// The row's identifier, in the canonical spelling <see cref="Application.Security.CanonicalIdentifier"/>
/// accepts.
/// </param>
/// <param name="Name">The sealed name as unpadded base64url.</param>
/// <param name="NameKey">The blind index over the same name as unpadded base64url.</param>
/// <param name="Type">The kind of account.</param>
/// <param name="OpeningBalance">The balance the ledger starts from.</param>
/// <param name="CurrencyCode">The ISO 4217 code.</param>
/// <remarks>
/// <para>
/// <b><see cref="Id"/> is a <see cref="string"/> and must never become a <see cref="Guid"/>.</b> The
/// identifier is the associated data the client sealed <see cref="Name"/> against, and associated data is
/// rebuilt from where a ciphertext was found rather than carried inside it — so this API has to hand back
/// the same spelling it was sent, and therefore has to refuse the spellings it cannot reproduce. Bound as
/// a <see cref="Guid"/>, <c>System.Text.Json</c> folds <c>0199C3D4-…</c>, <c>{0199c3d4-…}</c> and the
/// canonical form to one value before any handler sees text, and the refusal
/// <see cref="Application.Security.CanonicalIdentifier"/> exists for becomes unwritable. It compiles,
/// every test that sends a canonical id passes, and it fails in a browser months later as a name that
/// will not open.
/// </para>
/// <para>
/// <b><see cref="Name"/> and <see cref="NameKey"/> are two members because they are two columns, and both
/// are opaque to this server.</b> Neither can be checked against the other here — recomputing an index
/// needs the account's index key, which lives in a browser — so the handler judges each for shape alone
/// and <see cref="Domain.Security.IndexedName"/> is what refuses half a name.
/// </para>
/// </remarks>
public sealed record CreateAccountCommand(
    string Id,
    string Name,
    string NameKey,
    AccountType Type,
    decimal OpeningBalance,
    string CurrencyCode);
