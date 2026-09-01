namespace Application.Payees.CreatePayee;

/// <summary>
/// The body of <c>POST /api/payees</c>: an identifier the client minted, the name it sealed against that
/// identifier, and the blind index it took over the same text. There is no fourth member — a payee is a
/// name and nothing else.
/// </summary>
/// <param name="Id">
/// The row's identifier, in the canonical spelling <see cref="Application.Security.CanonicalIdentifier"/>
/// accepts.
/// </param>
/// <param name="Name">The sealed name as unpadded base64url.</param>
/// <param name="NameKey">The blind index over the same name as unpadded base64url.</param>
/// <remarks>
/// <para>
/// <b><see cref="Id"/> is a <see cref="string"/> and must never become a <see cref="Guid"/>.</b> The
/// identifier is the associated data the client sealed <see cref="Name"/> against, and associated data is
/// rebuilt from where a ciphertext was found rather than carried inside it, so this API has to hand back
/// the same spelling it was sent and therefore has to refuse the spellings it cannot reproduce. Bound as
/// a <see cref="Guid"/>, <c>System.Text.Json</c> folds <c>0199C3D4-…</c>, <c>{0199c3d4-…}</c> and the
/// canonical form to one value <em>before</em> any handler sees text, which does not make the check fail
/// — it makes the check unwritable. Every test that sends a canonical id still passes; what breaks is a
/// browser, months later, holding a name that will not open.
/// </para>
/// <para>
/// <b>An unopenable name costs more on this table than on any other, which is why the rule is restated
/// here rather than left to the neighbour that argues it first.</b> A payee whose name cannot be opened
/// is not one bad row in a list: the client resolves counterparties against the decrypted list, so a name
/// it cannot read is a name it cannot match, and it will mint a second payee for the same counterparty
/// the next time somebody names it. The duplication that <c>IX_payees_budget_id_name_key</c> exists to
/// refuse arrives anyway — under a different blind index, so the index does not fire, and nothing on this
/// side can tell that it should have.
/// </para>
/// <para>
/// <b><see cref="Name"/> and <see cref="NameKey"/> are two members because they are two columns, and both
/// are opaque to this server.</b> Neither can be checked against the other here — recomputing an index
/// needs the account's index key, which lives in a browser — so the handler judges each for shape alone
/// and <see cref="Domain.Security.IndexedName"/> is what refuses half a name.
/// </para>
/// </remarks>
public sealed record CreatePayeeCommand(string Id, string Name, string NameKey);
