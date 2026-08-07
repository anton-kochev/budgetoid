using Api.Infrastructure;
using Application.Abstractions;
using Application.Users.EnsureUser;

namespace IntegrationTests;

/// <summary>
/// What publishing a request's identity does to the ambient budget beside it, and how the budget gets
/// there afterwards.
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
/// Written over the real <see cref="CurrentUser" /> and the real
/// <see cref="HttpContextBudgetContext" /> rather than a fake of either. Both are the request-scoped
/// state itself, not a boundary — there is nothing to stub, and a stub would be asserting about the
/// stub.
/// </para>
/// <para>
/// <b>Why these live in the integration project although they need no container.</b> The subject is
/// three field assignments, so it would run happily in <c>UnitTests</c> — but only if that project
/// referenced <c>Api</c>, and referencing <c>Api</c> drags the whole web composition root (JwtBearer,
/// OpenTelemetry, Npgsql, the Aspire service defaults, CORS) into the one project whose value is being
/// free of them. Nothing in this repository enforces that boundary: there is no architecture-test
/// framework here, so a comment in the csproj is a convention with nothing behind it, and the next
/// person needing "just one Api type" in a unit test finds the reference already paid for. Here the
/// reference already exists for reasons of its own, and a test that opens no connection costs this
/// project nothing measurable.
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

    /// <summary>
    /// The other half of the pair: naming the budget the request runs against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The budget has to be publishable through this interface, because <c>ResolveUser</c> above clears
    /// it — a writer that can only clear the budget leaves no way to set one, so provisioning is forced
    /// to reach past the writer and assign <see cref="CurrentUser" /> itself. That is what
    /// <c>UserProvisioningMiddleware</c> does today, and it is why this type's own summary ("only what
    /// is injected this interface can name the request's identity") is false as written: there are two
    /// writers, and only one of them is this one.
    /// </para>
    /// <para>
    /// Called <b>after</b> <c>ResolveUser</c> and never before, for the reason stated above: the user
    /// publication clears whatever budget is standing, so a budget named first is a budget the rest of
    /// the request does not have. The order is a property of the caller, so it is pinned where the
    /// caller can be seen — see <c>UserProvisioningWriterTests</c>.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResolveBudget_PublishesTheBudgetItWasHanded()
    {
        // Arrange — a request whose identity is published and whose budget is not: exactly the state
        // ResolveUser leaves behind, which is the only state this call is ever made in.
        Guid userId = Guid.CreateVersion7();
        Guid budgetId = Guid.CreateVersion7();
        CurrentUser currentUser = new() { UserId = userId };
        IBudgetContext budgetContext = new HttpContextBudgetContext(currentUser);
        IUserContextWriter writer = new CurrentUserWriter(currentUser);

        // Act
        writer.ResolveBudget(budgetId);

        // Assert — the field and what the query filters read off it, because the field alone would be
        // satisfied by a budget the rest of the application never sees.
        await Assert.That(currentUser.BudgetId).IsEqualTo(budgetId);
        await Assert.That(budgetContext.BudgetId).IsEqualTo(budgetId);

        // And the identity is left exactly as it was found. Without this, a ResolveBudget that
        // republished or cleared the user would satisfy everything above while making the very pairing
        // this file exists to prevent — one account's id beside another's tenant — reachable again, this
        // time from the other side.
        await Assert.That(currentUser.UserId).IsEqualTo(userId);
    }
}
