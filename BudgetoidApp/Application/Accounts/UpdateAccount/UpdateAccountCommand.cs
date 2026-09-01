using Domain.Accounts;

namespace Application.Accounts.UpdateAccount;

/// <summary>
/// <c>PUT /api/accounts/{id}</c>: the row named by the route, the name the client re-sealed against that
/// row's existing identifier, the blind index over the same text, and the two members this server can
/// still read.
/// </summary>
/// <param name="Id">The row to replace, taken from the route.</param>
/// <param name="Name">The new sealed name as unpadded base64url.</param>
/// <param name="NameKey">The blind index over the same name as unpadded base64url.</param>
/// <param name="Type">The kind of account.</param>
/// <param name="OpeningBalance">The balance the ledger starts from.</param>
/// <remarks>
/// <b><see cref="Id"/> is a <see cref="Guid"/> here and a <see cref="string"/> on
/// <see cref="CreateAccount.CreateAccountCommand"/>, and the asymmetry is the point rather than an
/// oversight.</b> On a create the client is <em>choosing</em> an identifier and sealing against the
/// spelling it chose, so the one spelling this API can reproduce has to be the only one it accepts. On an
/// update the client re-seals against the row's <em>existing</em> id, which it read back from this API in
/// the one form <see cref="Guid"/> renders — lower case, hyphenated, unspaced. The text in the URL is
/// never the text anything was sealed under, so there is no spelling here to preserve and nothing for
/// <see cref="Application.Security.CanonicalIdentifier"/> to protect.
/// </remarks>
public sealed record UpdateAccountCommand(
    Guid Id,
    string Name,
    string NameKey,
    AccountType Type,
    decimal OpeningBalance);
