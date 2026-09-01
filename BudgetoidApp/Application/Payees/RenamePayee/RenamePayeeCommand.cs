namespace Application.Payees.RenamePayee;

/// <summary>
/// <c>PATCH /api/payees/{id}</c>: the row named by the route, the name the client re-sealed against that
/// row's existing identifier, and the blind index over the same text.
/// </summary>
/// <param name="Id">The row to rename, taken from the route.</param>
/// <param name="Name">The new sealed name as unpadded base64url.</param>
/// <param name="NameKey">The blind index over the same name as unpadded base64url.</param>
/// <remarks>
/// <para>
/// <b><see cref="Id"/> is a <see cref="Guid"/> here and a <see cref="string"/> on
/// <see cref="CreatePayee.CreatePayeeCommand"/>, and the asymmetry is the point rather than an
/// oversight.</b> On a create the client is <em>choosing</em> an identifier and sealing against the
/// spelling it chose, so the one spelling this API can reproduce has to be the only one it accepts. On a
/// rename the client re-seals against the row's <em>existing</em> id, which it read back from this API in
/// the one form <see cref="Guid"/> renders — lower case, hyphenated, unspaced. The text in the URL is
/// never the text anything was sealed under, so there is no spelling here to preserve and nothing for
/// <see cref="Application.Security.CanonicalIdentifier"/> to protect. The rule lives where an identifier
/// is chosen, and that is the create alone.
/// </para>
/// <para>
/// <b><see cref="Name"/> and <see cref="NameKey"/> are both required, and a rename that carried only the
/// first would be the mistake this pair exists to make unspellable.</b> The row would hold new ciphertext
/// under the previous name's index; see <see cref="Domain.Payees.Payee.Rename"/> for the two failures
/// that follow on this table and for why nothing on this side could ever notice either.
/// </para>
/// </remarks>
public sealed record RenamePayeeCommand(Guid Id, string Name, string NameKey);
