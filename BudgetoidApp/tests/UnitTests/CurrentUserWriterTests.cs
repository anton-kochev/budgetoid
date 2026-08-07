using Api.Infrastructure;
using Application.Abstractions;
using Application.Users.EnsureUser;

namespace UnitTests;

/// <summary>
/// What republishing a request's identity mid-flight does to the ambient budget beside it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CurrentUser" /> holds two values, and <c>SessionContextInterceptor</c> writes both —
/// <c>app.current_user_id</c> and <c>app.current_budget_id</c> — together, once, when a connection
/// opens. So a user id republished after that point does <b>not</b> move the budget: neither the
/// session variable already written on an open connection, nor the value <see cref="IBudgetContext" />
/// hands the EF query filters.
/// </para>
/// <para>
/// One path republishes. <c>CompleteAssertionHandler</c> names the account the passkey belongs to
/// once the signature verifies, and a caller holding a valid bearer token for account A may post an
/// assertion for account B: from that line onward the request names <b>B's user</b> with <b>A's
/// budget</b>. Nothing budget-scoped runs below it today, so the pairing is inert — but
/// <c>budget_isolation</c> is <c>FOR ALL</c>, which means the first budget-scoped statement added
/// there would be scoped to another tenant, match nothing, and report success having affected zero
/// rows. Silence is the whole problem: an isolation policy that refuses is a bug report, and one that
/// quietly matches nothing is a data-loss report nobody files.
/// </para>
/// <para>
/// The rule these tests pin is therefore that publishing a user <b>clears</b> the budget rather than
/// leaving a stale one. Cleared, <see cref="IBudgetContext.BudgetId" /> throws the day something
/// budget-scoped is added below that line, which is a red test rather than a silent no-op. The gap
/// is closed here, at the one writer, rather than at the caller: a rule living in the handler is a
/// rule the next handler to publish an identity has to remember.
/// </para>
/// <para>
/// A unit test over the real <see cref="CurrentUser" /> and the real
/// <see cref="HttpContextBudgetContext" /> rather than a fake of either. Both are the request-scoped
/// state itself, not a boundary — there is nothing to stub, and a stub would be asserting about the
/// stub.
/// </para>
/// </remarks>
public sealed class CurrentUserWriterTests
{
    /// <summary>
    /// The state every request is in by the time anything could republish an identity:
    /// <c>UserProvisioningMiddleware</c> resolves the user and the default budget together, so a
    /// resolved user with no budget beside it is not a state this test could start from honestly.
    /// </summary>
    private static CurrentUser ProvisionedRequest(Guid userId, Guid budgetId) =>
        new() { UserId = userId, BudgetId = budgetId };

    [Test]
    public async Task ResolveUser_ForAnotherAccount_ClearsTheAmbientBudget()
    {
        // Arrange — Alice's request, fully provisioned, about to be told it is really Bob's.
        Guid bobsUserId = Guid.CreateVersion7();
        CurrentUser currentUser = ProvisionedRequest(Guid.CreateVersion7(), Guid.CreateVersion7());
        IUserContextWriter writer = new CurrentUserWriter(currentUser);

        // Act
        writer.ResolveUser(bobsUserId);

        // Assert — the budget left behind is Alice's, and it belongs to the user that is no longer
        // published. Keeping it is what pairs one account's id with another's tenant.
        await Assert.That(currentUser.BudgetId).IsNull();
    }

    /// <summary>
    /// The consequence, stated where the rest of the application meets it.
    /// </summary>
    /// <remarks>
    /// The assertion above is about a field; this one is about what a budget-scoped caller gets, and
    /// it is the half that says why the field matters. A cleared budget makes the strict accessor
    /// throw, which is the loud failure the query filters are built to produce rather than the quiet
    /// zero-row success a stale one produces.
    /// </remarks>
    [Test]
    public async Task ResolveUser_ForAnotherAccount_LeavesTheBudgetContextUnresolved()
    {
        // Arrange
        CurrentUser currentUser = ProvisionedRequest(Guid.CreateVersion7(), Guid.CreateVersion7());
        IBudgetContext budgetContext = new HttpContextBudgetContext(currentUser);
        IUserContextWriter writer = new CurrentUserWriter(currentUser);

        // Act
        writer.ResolveUser(Guid.CreateVersion7());

        // Assert — the tolerant accessor answers null and the strict one refuses, which together are
        // the whole of "no budget has been resolved for this request".
        await Assert.That(budgetContext.ResolvedBudgetId).IsNull();
        await Assert.That(() => budgetContext.BudgetId).Throws<InvalidOperationException>();
    }

    /// <summary>
    /// The provable-fail control for both tests above.
    /// </summary>
    /// <remarks>
    /// Without it, a <c>ResolveUser</c> that cleared the budget by clearing everything — or that did
    /// nothing at all beyond nulling a field — satisfies them perfectly while publishing no identity,
    /// which would turn every policed statement below it into a <c>22P02</c> on an empty
    /// <c>app.current_user_id</c>.
    /// </remarks>
    [Test]
    public async Task ResolveUser_PublishesTheUserItWasHanded()
    {
        // Arrange
        Guid bobsUserId = Guid.CreateVersion7();
        CurrentUser currentUser = ProvisionedRequest(Guid.CreateVersion7(), Guid.CreateVersion7());
        IUserContext userContext = new HttpContextUserContext(currentUser);
        IUserContextWriter writer = new CurrentUserWriter(currentUser);

        // Act
        writer.ResolveUser(bobsUserId);

        // Assert
        await Assert.That(currentUser.UserId).IsEqualTo(bobsUserId);
        await Assert.That(userContext.ResolvedUserId).IsEqualTo(bobsUserId);
    }
}
