using System.Reflection;
using Application.Users;
using Application.Users.GetSignedInUser;
using Microsoft.Extensions.Logging;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// What the handler behind <c>GET /api/me</c> does with the address it finds, and what it does when
/// it finds none — plus the standing refusal that it may never be handed a logger.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unit tests rather than endpoint tests, and the missing-row case is why.</b> Reaching that state
/// over HTTP would mean deleting the <c>users</c> row in the middle of a request that is already
/// authenticated as it, behind a credential that is still live — and the schema makes that
/// unconstructible: <c>credentials</c> cascades from <c>users</c> on delete, so a live credential over
/// a missing user row does not exist. The state is a broken invariant, not a state the product can
/// produce, so the only honest place to model it is here, where the read service is a stub that can be
/// told to answer nothing.
/// </para>
/// <para>
/// <see cref="HandleAsync_WhenTheUserRowHoldsAnAddress_ReturnsThatAddress" /> is the control for
/// <see cref="HandleAsync_WhenTheResolvedIdentityAnswersToNoUserRow_Throws" />, and without it the
/// refusal is worth nothing: a handler that threw unconditionally — one whose null check had been
/// written the wrong way round, or one that never reached the read service at all — satisfies the
/// throw perfectly. It takes a case that returns to tell a guard apart from a wall.
/// </para>
/// <para>
/// Neither of these two duplicates the endpoint tests. <c>SignedInUserEndpointTests</c> measures which
/// account a request arrives as, which is a property of the pipeline above the route; these measure
/// what the handler does with the identity it is handed, which is the only half a stub can speak to.
/// </para>
/// </remarks>
public sealed class GetSignedInUserHandlerTests
{
    [Test]
    public async Task HandleAsync_WhenTheUserRowHoldsAnAddress_ReturnsThatAddress()
    {
        // Arrange — one seeded row, and the read service is keyed by the same id the context resolves
        // to. That pairing is load-bearing: a handler reading anything other than the identity it was
        // given is answered null by the stub and throws, rather than being handed the one address a
        // single-valued fake would give to whoever asked.
        Guid userId = Guid.CreateVersion7();
        GetSignedInUserHandler handler = new(
            new StubUserContext(userId),
            new StubUserAccountReadService((userId, StoredAddress)));

        // Act
        SignedInUser user = await handler.HandleAsync(new GetSignedInUserQuery());

        // Assert
        await Assert.That(user.Email).IsEqualTo(StoredAddress);
    }

    [Test]
    public async Task HandleAsync_WhenTheResolvedIdentityAnswersToNoUserRow_Throws()
    {
        // Arrange — a resolved identity, and a read service that knows no rows at all. The identity is
        // deliberately present: an unresolved context is a different failure, raised a layer up by
        // IUserContext's own derivation, and reaching this handler through one would prove nothing
        // about the line under test.
        GetSignedInUserHandler handler = new(
            new StubUserContext(Guid.CreateVersion7()),
            new StubUserAccountReadService());

        // Act
        InvalidOperationException exception = await ThrowsInvalidOperationExceptionAsync(
            () => handler.HandleAsync(new GetSignedInUserQuery()));

        // Assert — the exact type, not merely an assignable one. ObjectDisposedException and several
        // other framework refusals derive from InvalidOperationException, so a catch clause alone would
        // report a collaborator falling over as the deliberate throw this test is about.
        await Assert.That(exception.GetType()).IsEqualTo(typeof(InvalidOperationException));
    }

    /// <summary>
    /// The handler behind <c>GET /api/me</c> may not be handed a logger by the container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it is holding is why.</b> The single value this handler carries is a person's email
    /// address — the identifier they are reachable at, and the one field <c>users</c> keeps about them
    /// beyond an id and a timestamp. A <c>LogInformation</c> naming the account a request was answered
    /// for copies that into a sink with a different retention policy, a different audience and a
    /// different erasure story from the database it was read out of, and an erasure that empties the
    /// table leaves the log line untouched. Adding one is the ordinary next step after a new endpoint
    /// ships, and no other gate in this repository reads a log line, so nothing else would go red.
    /// </para>
    /// <para>
    /// The shape is <c>ErasureLoggingTests</c>'s and <c>ExportDataHandlerTests</c>'s, deliberately:
    /// constructor parameters over every binding flag, offenders rendered as sentences rather than
    /// counted, and the subject reached through <c>typeof</c> so that a rename fails the build here
    /// instead of leaving a name lookup that quietly matches nothing. A private constructor is still a
    /// constructor <c>ActivatorUtilities</c> can be pointed at, which is why the non-public ones are
    /// read too. <see cref="ILoggerFactory" /> is refused beside <see cref="ILogger" /> because it is
    /// the other way to obtain one from the same container, and <see cref="ILogger{TCategoryName}" />
    /// needs no arm of its own since it derives from <see cref="ILogger" />.
    /// </para>
    /// <para>
    /// <b>The scope is one type, and what it leaves out is not claimed.</b> <c>SignedInUserEndpoints</c>
    /// is the other place a logger would be reached for and is out of reach from here: it lives in
    /// <c>Api</c>, which this project deliberately does not reference — the reason is written on the
    /// <c>ProjectReference</c> block in <c>UnitTests.csproj</c> — and its route delegate would take a
    /// logger as a delegate parameter rather than through a constructor anyway.
    /// <c>UserAccountReadService</c> is left out because it is the shared kind of collaborator a logger
    /// on which is a different decision with a different conversation. And the framework logs on its
    /// own: ASP.NET Core, EF Core and the hosting stack all emit their own lines,
    /// <c>ServiceDefaults</c> wires <c>builder.Logging.AddOpenTelemetry</c> over the lot, and nothing
    /// here constrains any of it.
    /// </para>
    /// <para>
    /// It is green the day it is written — the handler has never taken a logger — so it is a pin rather
    /// than a driver. That is the only useful shape for it: the defect it exists to catch is one a later
    /// reader adds, and a test that only went red once would have to be written after the leak.
    /// </para>
    /// </remarks>
    [Test]
    public async Task GetSignedInUserHandler_TakesNoLoggerDependency()
    {
        // Arrange — every constructor of the one type, non-public ones included.
        ParameterInfo[] parameters =
        [
            .. typeof(GetSignedInUserHandler)
                .GetConstructors(AnyInstanceConstructor)
                .SelectMany(constructor => constructor.GetParameters()),
        ];

        // Act
        string[] loggerDependencies = [.. LoggerDependenciesAmong(parameters)];

        // Assert — offenders as sentences rather than as a count, so a red names the parameter to
        // delete instead of reporting that a number moved.
        await Assert.That(loggerDependencies).IsEmpty();

        // Non-vacuity. The type is reached through typeof so it cannot vanish silently, but a
        // reflection query that came back with no parameters at all would satisfy the claim above while
        // measuring nothing.
        await Assert.That(parameters.Length).IsGreaterThan(0);
    }

    /// <summary>
    /// That the query the pin above runs reports a logger when one is there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pin passes over an empty result, and an empty result is what a broken filter produces too.
    /// <see cref="ProbeThatTakesALogger" /> is a type written to offend, so the day
    /// <see cref="IsALogger" /> stops recognising <see cref="ILogger{TCategoryName}" /> — a rewrite to
    /// an exact type comparison, a name check that misses a generic — this test goes red while the pin
    /// stays green and says nothing.
    /// </para>
    /// <para>
    /// The probe carries an innocent parameter beside the offending one, and the sentence below names
    /// only the logger. A filter that reported every parameter would satisfy "the probe is reported"
    /// and would report the handler's own two dependencies as leaks, so the pin above would be red for
    /// a reason that has nothing to do with logging.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TheLoggerQuery_ReportsAConstructorThatTakesOne()
    {
        // Arrange
        ParameterInfo[] parameters =
        [
            .. typeof(ProbeThatTakesALogger)
                .GetConstructors(AnyInstanceConstructor)
                .SelectMany(constructor => constructor.GetParameters()),
        ];

        // Act
        string[] loggerDependencies = [.. LoggerDependenciesAmong(parameters)];

        // Assert — the whole rendered sentence, joined, rather than a count: this is also where the
        // message the pin would print is checked to read back as the line somebody has to remove.
        await Assert.That(string.Join(", ", loggerDependencies))
            .IsEqualTo("ProbeThatTakesALogger takes ILogger<ProbeThatTakesALogger> logger");
    }

    /// <summary>
    /// A type that takes a logger on purpose, so that the filter behind the pin above is measured
    /// rather than assumed. It is never constructed and never registered; only its constructor's
    /// parameters are read.
    /// </summary>
    /// <remarks>
    /// The read service beside the logger is the innocent half: it makes the probe report exactly one
    /// offender instead of one per parameter, which is what tells a filter that recognises loggers
    /// apart from one that recognises everything.
    /// </remarks>
    private sealed class ProbeThatTakesALogger(
        IUserAccountReadService readService,
        ILogger<ProbeThatTakesALogger> logger)
    {
        public IUserAccountReadService ReadService { get; } = readService;

        public ILogger<ProbeThatTakesALogger> Logger { get; } = logger;
    }

    /// <summary>
    /// The address the control seeds and asserts. Deliberately not derived from the user id or from
    /// anything else the handler can see, so a handler that invented an address rather than reading the
    /// one stored could not agree with this by construction.
    /// </summary>
    private const string StoredAddress = "signed-in@budgetoid.test";

    private const BindingFlags AnyInstanceConstructor =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>
    /// The logger dependencies among <paramref name="parameters" />, rendered as the sentences a reader
    /// has to act on and ordered so a red reads the same way twice.
    /// </summary>
    private static IEnumerable<string> LoggerDependenciesAmong(IEnumerable<ParameterInfo> parameters) =>
        parameters
            .Where(parameter => IsALogger(parameter.ParameterType))
            .Select(Describe)
            .Order(StringComparer.Ordinal);

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

    /// <summary>
    /// Runs <paramref name="action" /> and returns the refusal it threw, failing loudly when it threw
    /// nothing. Shaped like <c>ExportDataHandlerTests.ThrowsInvalidOperationExceptionAsync</c>, which is
    /// how this suite asserts a thrown exception from an async call.
    /// </summary>
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
