using System.Reflection;
using Application.Abstractions;
using Application.Users.ExportData;
using Domain.Accounts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// Pins the half of the Dependency Rule that the project graph provably cannot express: Api may
/// <b>compose</b> Infrastructure, and may not <b>consume</b> it.
/// </summary>
/// <remarks>
/// <para>
/// <c>Api → Infrastructure</c> is a legitimate reference — <c>Program.cs</c> has to call
/// <c>AddInfrastructure()</c>, register <c>SessionContextInterceptor</c> and configure the
/// <c>DbContext</c> — so <c>ProjectReferenceGraphTests</c> sees nothing wrong when an endpoint
/// reaches past the use case and injects a repository directly. Nothing in a csproj can tell those
/// two apart; only the route table can.
/// </para>
/// <para>
/// What is lost when an endpoint takes a port: the handler's validation, its budget scoping, and —
/// on the passkey routes — the ordering the assertion path depends on, where the identity is
/// published only after the signature verifies and the transaction opens only after that. None of
/// those live in the repository being injected.
/// </para>
/// <para>
/// <b>The scope is route delegate parameters, and widening it would neuter the rule.</b>
/// <c>Api/Infrastructure/HttpContextUserContext.cs</c> legitimately implements an Application port
/// and <c>Program.cs</c> legitimately names Infrastructure types; an assembly-wide version of this
/// test fires on both, and a guard that fires on correct code gets an exemption list within a week
/// and stops meaning anything.
/// </para>
/// <para>
/// The forbidden set is <b>derived</b> from the assemblies rather than listed here, so a repository
/// interface written next month is covered the day it is written rather than the day someone
/// remembers this file.
/// </para>
/// <para>
/// Sabotaged before it was believed: an <c>IAccountRepository</c> parameter added to one endpoint
/// delegate, which the failure named along with its route.
/// </para>
/// </remarks>
public sealed class CompositionBoundaryTests
{
    [Test]
    public async Task RouteDelegates_TakeNoPersistencePort()
    {
        // Arrange
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        HashSet<Type> forbidden = PersistencePorts();

        // Act — the delegate's MethodInfo is the first metadata item ASP.NET Core records for a
        // minimal-API route. Endpoints that are not minimal-API delegates (the health check) carry
        // none, which is why the count of inspected delegates is asserted below rather than assumed.
        RouteEndpoint[] endpoints = [.. dataSource.Endpoints.OfType<RouteEndpoint>()];
        int inspectedDelegates = endpoints.Count(
            endpoint => endpoint.Metadata.GetMetadata<MethodInfo>() is not null);

        string[] offenders =
        [
            .. endpoints
                .SelectMany(endpoint =>
                    (endpoint.Metadata.GetMetadata<MethodInfo>()?.GetParameters() ?? [])
                        .Where(parameter => forbidden.Contains(parameter.ParameterType))
                        .Select(parameter =>
                            $"{endpoint.RoutePattern.RawText} takes {parameter.ParameterType.Name}"))
                .Distinct()
                .Order(StringComparer.Ordinal),
        ];

        // Assert — joined rather than counted so a failure names the route and the port.
        await Assert.That(string.Join(", ", offenders)).IsEqualTo(string.Empty);

        // Two controls. An empty forbidden set, or a route table whose delegates stopped yielding a
        // MethodInfo, would each satisfy the line above while inspecting nothing at all.
        await Assert.That(forbidden.Count).IsGreaterThan(0);
        await Assert.That(inspectedDelegates).IsGreaterThan(0);
    }

    [Test]
    public async Task PersistencePorts_AreDerivedFromTheAssembliesRatherThanListed()
    {
        // Act
        HashSet<Type> forbidden = PersistencePorts();

        // Assert — a spot check that the derivation actually reaches both assemblies. Naming two
        // known ports is enough: if the Domain sweep or the Application sweep silently returned
        // nothing, the real test above would pass against a half-empty set and say so to nobody.
        await Assert.That(forbidden).Contains(typeof(IAccountRepository));
        await Assert.That(forbidden).Contains(typeof(IExportReadService));
        await Assert.That(forbidden).Contains(typeof(ITransactionalExecutor));
    }

    /// <summary>
    /// Every interface an endpoint must reach a use case to get to.
    /// </summary>
    /// <remarks>
    /// The naming sweeps carry the rule for the two families that have one. The three named
    /// individually are the ports whose names follow no pattern; they are listed rather than
    /// matched because inventing a suffix rule for three types would be a rule about spelling, and
    /// the next port added without the suffix would slip through it silently. A port added here
    /// with a fourth shape needs a line, and that is the intended cost.
    /// </remarks>
    private static HashSet<Type> PersistencePorts()
    {
        IEnumerable<Type> repositories = typeof(IAccountRepository).Assembly
            .GetTypes()
            .Where(type => type.IsInterface && type.Name.EndsWith("Repository", StringComparison.Ordinal));

        IEnumerable<Type> readServices = typeof(IExportReadService).Assembly
            .GetTypes()
            .Where(type => type.IsInterface && type.Name.EndsWith("ReadService", StringComparison.Ordinal));

        return
        [
            .. repositories,
            .. readServices,
            typeof(IPersistenceState),
            typeof(ITransactionalExecutor),
            typeof(IWebAuthnChallengeStore),
        ];
    }
}
