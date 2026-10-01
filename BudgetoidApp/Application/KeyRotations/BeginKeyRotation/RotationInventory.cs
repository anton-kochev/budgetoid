namespace Application.KeyRotations.BeginKeyRotation;

/// <summary>
/// How many rows of each narrative-bearing table a rotation has to rewrite: the denominator the client
/// drives the rest of the run against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Counts and nothing else, and the closure is the rule rather than today's shape.</b> This is the
/// one place on this path where somebody reaches for "and the names of the payees, so the screen can
/// say which one it is on" — every narrative column in the product is a sealed envelope the server
/// cannot open, so the useful version of that member cannot exist and the useless version would ship
/// ciphertext to a progress bar. <c>BeginKeyRotationHandlerTests.RotationInventory_CarriesCountsAndNothingElse</c>
/// is a census over this type's public properties rather than an assertion about one call, because what
/// is being held is what may be <em>added</em>.
/// </para>
/// <para>
/// <b>Six members rather than a dictionary keyed on a table name.</b> A map would let a table go
/// missing without anything failing, and the client reads each of these as its own progress bar; a
/// member the response forgot is a rotation that reports itself finished with a table still to go.
/// </para>
/// <para>
/// <b>Presence-aware, for the completeness gate's reason.</b> The counts are of rows that <em>carry a
/// narrative value</em>, which is the same population <c>IRotationCompletenessReadService</c> asks
/// about — a transaction with no note has nothing to re-seal, a chunk never visits it, and counting it
/// here would give the client a denominator it can never reach.
/// </para>
/// </remarks>
/// <param name="Accounts">Rows of <c>accounts</c>, every one of which carries a required name.</param>
/// <param name="Payees">Rows of <c>payees</c>, every one of which carries a required name.</param>
/// <param name="CategoryGroups">Rows of <c>category_groups</c>, every one of which carries a required name.</param>
/// <param name="Categories">Rows of <c>categories</c>, every one of which carries a required name.</param>
/// <param name="Transactions">Rows of <c>transactions</c> carrying a description.</param>
/// <param name="Budgets">Rows of <c>budgets</c> carrying a name.</param>
public sealed record RotationInventory(
    int Accounts,
    int Payees,
    int CategoryGroups,
    int Categories,
    int Transactions,
    int Budgets);
