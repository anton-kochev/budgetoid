using System.Reflection;
using Application.Users.ExportData;
using Domain.Accounts;
using Microsoft.Extensions.Logging;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// Which budgets an export may contain, and what the handler does when the answer is anything other
/// than "the one the request is operating inside".
/// </summary>
/// <remarks>
/// <para>
/// <b>The export refuses rather than truncates, and this file is where that decision lives.</b> The
/// five collection reads beneath the handler are scoped to the ambient budget by the
/// <c>BudgetIsolation</c> query filter and the <c>budget_isolation</c> policy, neither of which takes
/// an argument and neither of which can be re-pointed part-way through a request. So a user who owned
/// two budgets would be handed a document naming two budgets with one budget's accounts, categories,
/// payees and transactions attached to both — a file that reads as complete and is not. A silent
/// single-budget export <em>is</em> the truncation the requirement forbids, which is why the handler
/// throws instead of returning the part it can see.
/// </para>
/// <para>
/// <b>Set equality in both directions, not a count.</b> Two ways the owned set can fail to be exactly
/// the ambient budget, and each has a test: a budget owned that is not the ambient one, and an ambient
/// budget that is not owned. A <c>Count &gt; 1</c> guard satisfies the first and passes the second
/// while exporting a budget's contents under a row the user does not own, so the two are separate
/// tests rather than two assertions in one.
/// </para>
/// <para>
/// Unit tests over a hand-written fake rather than integration tests, because the refusal is a
/// decision made in the application layer from two values — the owned set and the ambient id — and
/// the database can only produce the second state (an ambient budget nobody owns) by being tampered
/// with. The HTTP half, that a refused export leaks no partial document, is
/// <c>DataExportRefusalTests</c>.
/// </para>
/// <para>
/// The handler is constructed positionally as
/// <c>(IUserContext, IBudgetContext, IExportReadService)</c>. <see cref="StubUserContext" /> and
/// <see cref="StubBudgetContext" /> are reused rather than restated: both already exist for exactly
/// this, and a second stub of a context whose strict accessor is derived on the interface is the drift
/// that derivation exists to prevent.
/// </para>
/// </remarks>
public sealed class ExportDataHandlerTests
{
    /// <summary>
    /// <b>The control for the two refusals below, and without it neither is worth anything.</b> A
    /// handler that threw <see cref="ExportCompletenessException" /> on every request — one whose gate
    /// was written the wrong way round, or one that never learned to answer at all — satisfies both
    /// refusal tests while the export does not work for anybody. This is the test that says the gate
    /// opens for the one world the product actually produces.
    /// </summary>
    /// <remarks>
    /// It also carries the non-vacuity the two refusals do not repeat. The fake, the two stubs and the
    /// construction are the same three lines in all three tests, so a fake that had stopped answering,
    /// or a handler wired to the wrong id, shows up as this test going red rather than as two refusals
    /// passing for a reason nobody chose.
    /// </remarks>
    [Test]
    public async Task HandleAsync_ForAnOwnerOfOnlyTheAmbientBudget_ReturnsThatBudget()
    {
        // Arrange — the only world the product can currently produce: one user, one budget, and that
        // budget is the ambient one. Contents are non-empty so a handler that answered a document
        // stripped of everything inside the budget could not pass here either.
        var userId = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();
        ExportedAccount account = AccountIn(budgetId);
        var readService = new InMemoryExportReadService(
            UserRow(userId),
            [BudgetRow(budgetId, userId)],
            new ExportedBudgetContents([account], [], [], [], []));
        var handler = new ExportDataHandler(
            new StubUserContext(userId),
            new StubBudgetContext(budgetId),
            readService);

        // Act
        ExportDocument document = await handler.HandleAsync(new ExportDataQuery());

        // Assert — the document names the account and the one budget, with the budget's contents
        // attached to it.
        await Assert.That(document.User.Id).IsEqualTo(userId);
        await Assert.That(document.Budgets.Count).IsEqualTo(1);
        await Assert.That(document.Budgets[0].Id).IsEqualTo(budgetId);
        await Assert.That(document.Budgets[0].Accounts.Count).IsEqualTo(1);
        await Assert.That(document.Budgets[0].Accounts[0].Id).IsEqualTo(account.Id);
    }

    /// <summary>
    /// A second owned budget makes the ambient budget's contents unattachable to the document as a
    /// whole, so the export refuses instead of shipping the half it can read.
    /// </summary>
    /// <remarks>
    /// Its control is
    /// <see cref="HandleAsync_ForAnOwnerOfOnlyTheAmbientBudget_ReturnsThatBudget" />.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheUserOwnsABudgetOtherThanTheAmbientOne_RefusesRatherThanTruncating()
    {
        // Arrange — two owned budgets, the ambient one among them. This is the state the day a second
        // budget becomes creatable, and the state a hand-written row produces today.
        var userId = Guid.CreateVersion7();
        var ambientBudgetId = Guid.CreateVersion7();
        var otherBudgetId = Guid.CreateVersion7();
        var readService = new InMemoryExportReadService(
            UserRow(userId),
            [BudgetRow(ambientBudgetId, userId), BudgetRow(otherBudgetId, userId, "Holiday")],
            InMemoryExportReadService.Empty);
        var handler = new ExportDataHandler(
            new StubUserContext(userId),
            new StubBudgetContext(ambientBudgetId),
            readService);

        // Act
        ExportCompletenessException exception =
            await ThrowsExportCompletenessExceptionAsync(() => handler.HandleAsync(new ExportDataQuery()));

        // Assert — that it refused at all is the whole claim. The message is deliberately not asserted:
        // it names counts and never ids, because the Development branch of GlobalExceptionHandler echoes
        // it into the response body, and pinning its wording here would make that constraint look like
        // a phrasing test.
        await Assert.That(exception).IsNotNull();
    }

    /// <summary>
    /// The direction a count-based guard passes, which is why it is a test of its own rather than a
    /// second assertion inside the one above.
    /// </summary>
    /// <remarks>
    /// One owned budget and an ambient budget that is not it. <c>owned.Count &gt; 1</c> is
    /// <see langword="false" /> here, so a handler written that way answers 200 with a document whose
    /// single budget row belongs to the user and whose accounts, categories, payees and transactions
    /// were read from a budget the document never names. The refusal has to be set equality in both
    /// directions for this to red, and this test is what forces the second direction to exist.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheAmbientBudgetIsNotOneTheUserOwns_Refuses()
    {
        // Arrange
        var userId = Guid.CreateVersion7();
        var ownedBudgetId = Guid.CreateVersion7();
        var ambientBudgetId = Guid.CreateVersion7();
        var readService = new InMemoryExportReadService(
            UserRow(userId),
            [BudgetRow(ownedBudgetId, userId)],
            InMemoryExportReadService.Empty);
        var handler = new ExportDataHandler(
            new StubUserContext(userId),
            new StubBudgetContext(ambientBudgetId),
            readService);

        // Act
        ExportCompletenessException exception =
            await ThrowsExportCompletenessExceptionAsync(() => handler.HandleAsync(new ExportDataQuery()));

        // Assert
        await Assert.That(exception).IsNotNull();
    }

    /// <summary>
    /// A resolved identity that answers to no <c>users</c> row is a broken invariant, not a missing
    /// resource — the account, its first credential and its default budget go in one
    /// <c>SaveChanges</c>, so there is no half-created state to serve a 404 for.
    /// </summary>
    /// <remarks>
    /// <b>This test pins behaviour that already exists rather than driving new behaviour.</b> The
    /// <c>?? throw</c> was written with the handler; what is added here is the guarantee that the
    /// refusal introduced alongside it did not quietly take this case over. The owned set is seeded to
    /// equal the ambient budget precisely so the completeness gate has nothing to object to: the only
    /// reason left to throw is the absent user row, and the assertion that the exception is not an
    /// <see cref="ExportCompletenessException" /> is what keeps the two failures from being reported as
    /// one.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenNoUserRowAnswersTheResolvedIdentity_Throws()
    {
        // Arrange — no user row, and an owned set that would otherwise satisfy the completeness gate.
        var userId = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();
        var readService = new InMemoryExportReadService(
            user: null,
            [BudgetRow(budgetId, userId)],
            InMemoryExportReadService.Empty);
        var handler = new ExportDataHandler(
            new StubUserContext(userId),
            new StubBudgetContext(budgetId),
            readService);

        // Act
        InvalidOperationException exception =
            await ThrowsInvalidOperationExceptionAsync(() => handler.HandleAsync(new ExportDataQuery()));

        // Assert — and that it is the broken-invariant throw rather than the completeness refusal,
        // which derives from the same base type and would otherwise satisfy the line above.
        await Assert.That(exception is ExportCompletenessException).IsFalse();
    }

    /// <summary>
    /// The handler that assembles an export may not be handed a logger by the container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it is holding is why.</b> <see cref="ExportDataHandler" /> is the one handler whose
    /// in-memory value is a complete copy of a person's finances — every account, category, payee and
    /// transaction they have ever recorded — with the address they signed up under attached to it. A
    /// single <c>LogDebug</c> of the document, or of the identity it was assembled for, copies that
    /// into a sink with a different retention policy, a different audience and a different erasure
    /// story from the database it was read out of. Adding one is the ordinary next step after a new
    /// endpoint ships, and no other gate in this repository reads a log line, so nothing else would go
    /// red.
    /// </para>
    /// <para>
    /// The shape is <c>ErasureLoggingTests</c>'s, deliberately: constructor parameters over every
    /// binding flag, offenders rendered as sentences rather than counted, and the subject reached
    /// through <c>typeof</c> so that a rename fails the build here instead of leaving a name lookup
    /// that quietly matches nothing. A private constructor is still a constructor
    /// <c>ActivatorUtilities</c> can be pointed at, which is why the non-public ones are read too.
    /// <see cref="ILoggerFactory" /> is refused beside <see cref="ILogger" /> because it is the other
    /// way to obtain one from the same container, and <see cref="ILogger{TCategoryName}" /> needs no
    /// arm of its own since it derives from <see cref="ILogger" />.
    /// </para>
    /// <para>
    /// <b>The scope is one type, and the three things it does not cover are not claimed.</b>
    /// <c>DataExportEndpoints</c> is the other place a logger would be reached for and is out of reach
    /// from here: it lives in <c>Api</c>, which this project deliberately does not reference — the
    /// reason is written on the <c>ProjectReference</c> block in <c>UnitTests.csproj</c> — and its
    /// route delegate would take a logger as a delegate parameter rather than through a constructor
    /// anyway. <c>ExportReadService</c> is left out because it is the shared kind of collaborator a
    /// logger on which is a different decision with a different conversation. And the framework logs
    /// on its own: ASP.NET Core, EF Core and the hosting stack all emit their own lines,
    /// <c>ServiceDefaults</c> wires <c>builder.Logging.AddOpenTelemetry</c> over the lot, and nothing
    /// here constrains any of it.
    /// </para>
    /// <para>
    /// It is green the day it is written — the handler has never taken a logger — so it is a pin
    /// rather than a driver. That is the only useful shape for it: the defect it exists to catch is
    /// one a later reader adds, and a test that only went red once would have to be written after the
    /// leak.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TheHandlerThatAssemblesAnExport_TakesNoLoggerDependency()
    {
        // Arrange — every constructor of the one type, non-public ones included.
        ParameterInfo[] parameters =
        [
            .. typeof(ExportDataHandler)
                .GetConstructors(AnyInstanceConstructor)
                .SelectMany(constructor => constructor.GetParameters()),
        ];

        // Act
        string[] loggerDependencies =
        [
            .. parameters
                .Where(parameter => IsALogger(parameter.ParameterType))
                .Select(Describe)
                .Order(StringComparer.Ordinal),
        ];

        // Assert — offenders as sentences rather than as a count, so a red names the parameter to
        // delete instead of reporting that a number moved.
        await Assert.That(loggerDependencies).IsEmpty();

        // Non-vacuity. The type is reached through typeof so it cannot vanish silently, but a
        // reflection query that came back with no parameters at all would satisfy the claim above
        // while measuring nothing.
        await Assert.That(parameters.Length).IsGreaterThan(0);
    }

    private const BindingFlags AnyInstanceConstructor =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>
    /// Whether a constructor parameter is a logger, or the factory a logger is obtained from.
    /// </summary>
    private static bool IsALogger(Type parameterType) =>
        typeof(ILogger).IsAssignableFrom(parameterType)
        || typeof(ILoggerFactory).IsAssignableFrom(parameterType);

    /// <summary>
    /// Renders one offending parameter as the sentence a reader has to act on: which type took it, and
    /// which parameter to delete.
    /// </summary>
    private static string Describe(ParameterInfo parameter) =>
        $"{parameter.Member.DeclaringType?.Name} takes "
        + $"{RenderType(parameter.ParameterType)} {parameter.Name}";

    /// <summary>
    /// Spells a type the way the declaration does, because <see cref="MemberInfo.Name" /> renders a
    /// generic as <c>ILogger`1</c> and the whole point of the message is that it reads back as the line
    /// to remove.
    /// </summary>
    private static string RenderType(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        string arguments = string.Join(", ", type.GetGenericArguments().Select(RenderType));

        return $"{name}<{arguments}>";
    }

    /// <summary>The account every test here exports, with no bearing on any assertion but its id.</summary>
    private static ExportedUser UserRow(Guid userId) =>
        new(userId, "owner@example.com", SeedInstant);

    /// <summary>
    /// One owned budget. The name defaults to <see langword="null" /> — the state provisioning leaves
    /// behind — and a second budget in the same test takes one, because the unique index over
    /// <c>(user_id, name)</c> is declared <c>NULLS NOT DISTINCT</c> and two nameless budgets for one
    /// owner is a row the database would refuse.
    /// </summary>
    private static ExportedBudget BudgetRow(Guid budgetId, Guid userId, string? name = null) =>
        new(budgetId, userId, name, BaseCurrencyCode: null, SeedInstant);

    private static ExportedAccount AccountIn(Guid budgetId) => new(
        Guid.CreateVersion7(),
        budgetId,
        "Checking",
        AccountType.Checking,
        OpeningBalance: 0m,
        "USD",
        SeedInstant);

    /// <summary>
    /// Fixed instant for every seeded row. Nothing here reads a clock, so a constant is honest where a
    /// <c>TimeProvider</c> would only be ceremony.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Runs <paramref name="action" /> and returns the refusal it threw, failing loudly when it threw
    /// nothing. Shaped like <c>CategoryGroupHandlerTests.ThrowsValidationExceptionAsync</c>, which is
    /// how this suite asserts a thrown exception.
    /// </summary>
    private static async Task<ExportCompletenessException> ThrowsExportCompletenessExceptionAsync(
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ExportCompletenessException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ExportCompletenessException.");
    }

    private static async Task<InvalidOperationException> ThrowsInvalidOperationExceptionAsync(
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected InvalidOperationException.");
    }
}
