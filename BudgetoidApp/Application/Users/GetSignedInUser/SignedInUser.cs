namespace Application.Users.GetSignedInUser;

/// <summary>
/// The signed-in account as the caller is shown it: the address it signed up with, the budget the
/// request is operating inside, and nothing else.
/// </summary>
/// <param name="Email">The address the account is registered under, as <c>users</c> stores it — never
/// the one on the provider token the request arrived with.</param>
/// <param name="BudgetId">The ambient budget, read from <c>IBudgetContext.BudgetId</c> and rendered in
/// the hyphenated <c>D</c> form. It is the fourth field of the client's blind-index message.</param>
/// <remarks>
/// <para>
/// <b>Still no <c>Id</c> and no <c>CreatedAtUtc</c>, and the reason has changed shape rather than
/// weakened.</b> This record used to argue that the client may never hold <em>any</em> internal
/// identifier, because publishing one that nothing displays is the first half of a client-supplied
/// tenancy parameter — once a caller holds one, the next request that accepts one has a value to
/// carry. The second half of that argument still stands and is held by
/// <c>BudgetRouteConstructionTests</c>: no route in this API takes a budget or a user as a path
/// segment or a route parameter, so a client holding an identifier has nowhere to spend it. What
/// ended is the premise that nothing may be published at all.
/// </para>
/// <para>
/// <b>What earns publication is not that a screen displays the value.</b> Nothing renders a budget
/// identifier and nothing is going to. A member is earned when <em>the client cannot derive it and
/// cannot complete its half of a cryptographic contract without it</em>. The blind index over a name
/// is <c>budgetoid/blind-index/v1 ⌷ table ⌷ column ⌷ budgetId ⌷ normalized-name</c>, computed in a
/// browser under a key this server has never held: the grammar and the table-and-column pair are the
/// client's own constants, the name is what somebody typed, the index key is in custody — and the
/// budget is resolved server-side from the session cookie and named in no request and no other
/// response. Withhold it and no name can be written to a blind-indexed column at all. Publish it and
/// two budgets of one account stop producing byte-identical digests for one name, which is the
/// correlation an operator with full read access must not be able to make.
/// </para>
/// <para>
/// <b>A user id fails that test and stays unpublished, which is what makes the rule a rule.</b> It is
/// equally underivable and equally undisplayed, and no client-side computation needs it — so the only
/// thing publishing it would buy is a value a later route could be persuaded to accept. The same
/// refusal covers a session id, a credential id and <c>CreatedAtUtc</c>: underivable is half the
/// test, and load-bearing for something the browser must compute is the other half.
/// </para>
/// <para>
/// <b>The alternative that keeps the old sentence literally true is rejected on purpose.</b> A
/// separate <c>budgets.index_scope</c> column — a second identifier minted for the client to fold in,
/// so that the real budget id is still never published — costs an additive migration, a second wire
/// member, and a second value that must never change for the life of the account with nothing
/// anywhere holding it to that. Every name in the account is keyed on it, so one edit re-keys rows
/// nothing can find again, and the failure is silent: the unique index still works, the lookups just
/// come back empty. It buys a shadow identifier with the same reach as the one it replaces, invented
/// to keep a sentence intact. The sentence is what moves.
/// </para>
/// </remarks>
public sealed record SignedInUser(string Email, Guid BudgetId);
