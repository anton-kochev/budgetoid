using Application.Users.ExportData;

namespace UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="IExportReadService" /> that reproduces the three behaviours the handler
/// above it depends on: a user lookup that answers <see langword="null" /> for an id no row carries,
/// an owned-budget list scoped to the owner and ordered the way the port documents, and a contents
/// read that answers whatever the test said the ambient budget holds.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than generated, which is the local convention — every fake in this folder is
/// a real type with real behaviour, and <c>InMemoryBudgetRepositoryTests</c> exists because a fake
/// that models a rule can get the rule wrong. What is modelled here is only what a handler test can
/// go red on: the <c>userId</c> predicate on both owner-scoped reads, and the
/// <c>(CreatedAtUtc, Id)</c> order <see cref="IExportReadService.ListOwnedBudgetsAsync" /> states as
/// contract. Without the predicate, a handler that passed the ambient budget's owner — or nothing at
/// all — where the signed-in user belongs would pass every test in this file.
/// </para>
/// <para>
/// <b>The contents are returned whole and unfiltered, and that asymmetry is the tenancy model rather
/// than a shortcut.</b> <see cref="IExportReadService.ReadAmbientBudgetContentsAsync" /> takes no
/// budget id in production either: the <c>BudgetIsolation</c> query filter and the
/// <c>budget_isolation</c> policy are what scope it, and neither is a thing a unit test has. Modelling
/// a scope here would be inventing one the production port cannot express — and it is precisely
/// because that read cannot be scoped by argument that the handler must refuse an owned set larger
/// than the ambient budget instead of attaching one budget's rows to several budgets.
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
    /// The account, or <see langword="null" /> when no row was seeded for
    /// <paramref name="userId" />.
    /// </summary>
    /// <remarks>
    /// The id is compared rather than ignored so that a seeded user cannot answer a request made for
    /// somebody else. A fake that returned its one row unconditionally would report the resolved
    /// identity as always found, which is the one thing the missing-row test needs to be able to
    /// distinguish.
    /// </remarks>
    public Task<ExportedUser?> FindUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(user?.Id == userId ? user : null);

    /// <summary>
    /// Every seeded budget <paramref name="userId" /> owns, ascending by
    /// <c>(CreatedAtUtc, Id)</c> — the order the port documents and the real read service issues.
    /// </summary>
    public Task<IReadOnlyList<ExportedBudget>> ListOwnedBudgetsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ExportedBudget> owned =
        [
            .. ownedBudgets
                .Where(budget => budget.UserId == userId)
                .OrderBy(budget => budget.CreatedAtUtc)
                .ThenBy(budget => budget.Id),
        ];

        return Task.FromResult(owned);
    }

    /// <inheritdoc />
    public Task<ExportedBudgetContents> ReadAmbientBudgetContentsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(contents);
}
