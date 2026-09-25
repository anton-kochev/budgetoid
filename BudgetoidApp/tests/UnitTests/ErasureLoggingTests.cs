using System.Reflection;
using Application.Passkeys.Reauthentication;
using Application.Users.EraseAccount;
using Microsoft.Extensions.Logging;

namespace UnitTests;

/// <summary>
/// Neither type that carries out an erasure may be handed a logger by the container.
/// </summary>
/// <remarks>
/// <para>
/// Every other erasure guard reads a <b>name</b>. <c>ErasureRemnantVocabulary</c> classifies catalog
/// and model names, and <c>ErasureIrreversibilityTests</c> reads the route table. A log line has no
/// name for either of them to read, so an <c>ILogger&lt;EraseAccountHandler&gt;</c> writing the
/// erased account's id — the ordinary next step after a destructive endpoint ships, in the shape
/// <c>PasskeyVerificationExceptionHandler</c> already uses — would leave a durable record of the
/// deletion outside the database with every existing gate reporting green. This test is the one that
/// goes red on it.
/// </para>
/// <para>
/// <b>Exactly these two types, because they are the whole of the erasure command path that holds the
/// erased account's id in hand.</b> <see cref="EraseAccountHandler" /> reads
/// <c>IUserContext.UserId</c> and hands it to the user delete;
/// <see cref="PasskeyReauthentication" /> reads the same id to scope the key lookup and to compare
/// the user handle against. <c>BeginReauthenticationHandler</c>, the ceremony's other leg, is left
/// out for the same reason spelled the other way round: it takes no user context and so has no id to
/// write down.
/// </para>
/// <para>
/// <b>Three things it deliberately does not cover, and none of them is claimed.</b>
/// <c>AccountErasureEndpoints</c> is the third place a logger would be reached for, and it is out of
/// reach here: it lives in <c>Api</c>, which this project deliberately does not reference — the
/// reason is on the <c>ProjectReference</c> block in <c>UnitTests.csproj</c> — and its route
/// delegate would take a logger as a delegate parameter rather than through a constructor anyway.
/// The repositories the handler calls, <c>UserRepository</c> and <c>TransactionRepository</c>, are
/// shared collaborators every other path uses, so a logger on one of them is a different decision
/// with a different conversation and nothing here refuses it. And the framework logs on its own:
/// ASP.NET Core, EF Core and the hosting stack all emit their own lines, <c>ServiceDefaults</c>
/// wires <c>builder.Logging.AddOpenTelemetry</c> over the lot, and this test constrains none of it.
/// </para>
/// <para>
/// Constructor parameters, because that is the shape dependency injection has in this codebase —
/// both types below are primary constructors resolved by the container. <see cref="ILoggerFactory" />
/// is refused beside <see cref="ILogger" /> because it is the other way to obtain one through the
/// same container, and <see cref="ILogger{TCategoryName}" /> needs no arm of its own since it
/// derives from <see cref="ILogger" />.
/// </para>
/// <para>
/// The subjects are <c>typeof</c> rather than names looked up at runtime, which is what keeps the
/// query from going quietly empty: a type renamed, moved or deleted fails the build here instead of
/// leaving an assertion that passes over nothing.
/// </para>
/// </remarks>
public sealed class ErasureLoggingTests
{
    [Test]
    public async Task TheTypesThatCarryOutAnErasure_TakeNoLoggerDependency()
    {
        // Arrange — every constructor, non-public ones included. A private constructor is still a
        // constructor ActivatorUtilities can be pointed at, so reading only the public ones would
        // leave a way in that this test reports as absent.
        ParameterInfo[] parameters =
        [
            .. TypesThatCarryOutAnErasure
                .SelectMany(type => type.GetConstructors(AnyInstanceConstructor))
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

        // Assert — the offenders are rendered as sentences rather than counted, so a red names the
        // type and the parameter to remove instead of reporting that a number moved.
        await Assert.That(loggerDependencies).IsEmpty();

        // Non-vacuity. Both types are reached through typeof, so neither can vanish silently, but a
        // reflection query that came back with no parameters at all would satisfy the claim above
        // while measuring nothing.
        await Assert.That(parameters.Length).IsGreaterThan(0);
    }

    /// <summary>
    /// The two types the erasure command path is made of, both of which read the id of the account
    /// being erased.
    /// </summary>
    private static readonly Type[] TypesThatCarryOutAnErasure =
    [
        typeof(EraseAccountHandler),
        typeof(PasskeyReauthentication),
    ];

    private const BindingFlags AnyInstanceConstructor =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>
    /// Whether a constructor parameter is a logger, or the factory a logger is obtained from.
    /// </summary>
    private static bool IsALogger(Type parameterType) =>
        typeof(ILogger).IsAssignableFrom(parameterType)
        || typeof(ILoggerFactory).IsAssignableFrom(parameterType);

    /// <summary>
    /// Renders one offending parameter as the sentence a reader has to act on: which type took it,
    /// and which parameter to delete.
    /// </summary>
    private static string Describe(ParameterInfo parameter) =>
        $"{parameter.Member.DeclaringType?.Name} takes "
        + $"{RenderType(parameter.ParameterType)} {parameter.Name}";

    /// <summary>
    /// Spells a type the way the declaration does, because <see cref="MemberInfo.Name" /> renders a
    /// generic as <c>ILogger`1</c> and the whole point of the message is that it reads back as the
    /// line to remove.
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
}
