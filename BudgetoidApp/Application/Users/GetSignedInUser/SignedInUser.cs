namespace Application.Users.GetSignedInUser;

/// <summary>
/// The signed-in account as the caller is shown it: the address it signed up with, and nothing else.
/// </summary>
/// <remarks>
/// <b>No <c>Id</c> and no <c>CreatedAtUtc</c>, deliberately.</b> The client has never seen an internal
/// user id, because every tenancy value in this API is resolved server-side from the authenticated
/// subject and none is ever addressed by the client — the rule <c>BudgetRouteConstructionTests</c>
/// holds the routes to, and the one <c>ExportDataQuery</c> and <c>EraseAccountCommand</c> state for
/// their own inputs. Publishing an id that nothing displays is the first half of a client-supplied
/// tenancy parameter: once a caller holds one, the next request that accepts one has a value to carry.
/// </remarks>
public sealed record SignedInUser(string Email);
