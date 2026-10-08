using Application.Users.ExportData;

namespace UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="IExportReadService" /> that reproduces the three behaviours the handler
/// above it depends on: a user row that answers <see langword="null" /> for an id no row carries,
/// an owned-budget list scoped to the owner and ordered by creation instant, and contents that
/// answer whatever the test said the ambient budget holds — all three on the one snapshot the port
/// returns.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than generated, which is the local convention — every fake in this folder is
/// a real type with real behaviour, and <c>InMemoryBudgetRepositoryTests</c> exists because a fake
/// that models a rule can get the rule wrong. What is modelled here is only what a handler test can
/// go red on: the <c>userId</c> predicate on both owner-scoped parts of the snapshot, and the
/// ascending <c>CreatedAtUtc</c> order <see cref="IExportReadService.ReadSnapshotAsync" /> states as
/// contract. Without the predicate, a handler that passed the ambient budget's owner — or nothing at
/// all — where the signed-in user belongs would pass every test in this file.
/// </para>
/// <para>
/// <b>Ascending <c>CreatedAtUtc</c> is the contract; the <c>Id</c> beside it is a deterministic
/// tiebreaker whose collation is provider-defined and is <em>not</em> part of it.</b> This fake breaks
/// a tie through <see cref="Guid" />'s own comparison, which orders field-wise, while PostgreSQL
/// orders a <c>uuid</c> by its bytes — so two budgets sharing an instant may come back in different
/// orders here and in production, and no test may rest on which. Nothing in the product creates two
/// budgets in one instant today; the tiebreaker exists so that a fake and a query cannot each pick
/// their own arbitrary order within a run, not so a caller can predict one.
/// </para>
/// <para>
/// <b>The contents are returned whole and unfiltered, and that asymmetry is the tenancy model rather
/// than a shortcut.</b> The contents part of <see cref="IExportReadService.ReadSnapshotAsync" /> is
/// scoped by no argument in production either: the <c>BudgetIsolation</c> query filter and the
/// <c>budget_isolation</c> policy are what scope it, and neither is a thing a unit test has. Modelling
/// a scope here would be inventing one the production port cannot express — and it is precisely
/// because that read cannot be scoped by argument that the handler must refuse an owned set larger
/// than the ambient budget instead of attaching one budget's rows to several budgets.
/// </para>
/// <para>
/// The snapshot's consistency — that all three parts come from one database state — is not modelled
/// and cannot be: it is a property of the transaction in <c>ExportReadService</c>, held by
/// <c>DataExportSnapshotTests</c>.
/// </para>
/// </remarks>
public sealed class InMemoryExportReadService(
    ExportedUser? user,
    IReadOnlyList<ExportedBudget> ownedBudgets,
    ExportedBudgetContents contents) : IExportReadService
{
    /// <summary>
    /// An ambient budget holding nothing, for the tests whose subject is which budgets an export may
    /// contain rather than what is inside one.
    /// </summary>
    public static ExportedBudgetContents Empty { get; } = new([], [], [], [], []);

    /// <summary>
    /// The seeded account when it is <paramref name="userId" />'s, every seeded budget
    /// <paramref name="userId" /> owns ascending by <c>CreatedAtUtc</c> with <c>Id</c> as a
    /// deterministic tiebreaker, and the seeded contents.
    /// </summary>
    /// <remarks>
    /// The user id is compared rather than ignored so that a seeded user cannot answer a request made
    /// for somebody else. A fake that returned its one row unconditionally would report the resolved
    /// identity as always found, which is the one thing the missing-row test needs to be able to
    /// distinguish. The budget tiebreaker is not the contract and does not match the real read
    /// service's for rows sharing an instant: this compares a <see cref="Guid" /> field-wise,
    /// PostgreSQL compares a <c>uuid</c> by its bytes. What both promise is only that a given set comes
    /// back the same way twice.
    /// </remarks>
    public Task<ExportSnapshot> ReadSnapshotAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ExportedBudget> owned =
        [
            .. ownedBudgets
                .Where(budget => budget.UserId == userId)
                .OrderBy(budget => budget.CreatedAtUtc)
                .ThenBy(budget => budget.Id),
        ];

        return Task.FromResult(new ExportSnapshot(user?.Id == userId ? user : null, owned, contents));
    }
}
