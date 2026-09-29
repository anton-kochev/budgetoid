using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntegrationTests;

/// <summary>
/// Pins FR-033: any host outside Development writes no Information record for the request pipeline,
/// while Development keeps its request lines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it matters.</b> With no logging configuration at all, ASP.NET's default minimum is
/// Information, and <c>Microsoft.AspNetCore.Hosting.Diagnostics</c> writes a "Request starting" and a
/// "Request finished" line for every request — method, path, status and time — to stdout. EF's command
/// log and Npgsql sit at the same level. None of that is a narrative value, but it is a per-request
/// trail of what a person did and when, and the product owes nobody that trail.
/// </para>
/// <para>
/// <b>The probe asks the host's own <see cref="ILoggerFactory" />, and nothing is attached to it.</b>
/// <see cref="LogRecorder" /> adds a provider-specific Trace rule for every category, which outranks
/// every category rule the application writes — attached here it would answer "enabled" for the
/// recorder and hide the very rule under test. What is asked is <see cref="ILogger.IsEnabled" /> on
/// the aggregate logger, which is true when any registered provider would take the record: a single
/// provider left at Information is a line on stdout, so one is enough to fail.
/// </para>
/// <para>
/// <b>Environment variables reach this host.</b> <c>WebApplication.CreateBuilder</c> reads them, so a
/// <c>Logging__LogLevel__*</c> exported in the shell or the CI job lands in the configuration the
/// application filters on. None is set in this repository or its workflows today; one that appears
/// would move these answers for a reason outside the code under test.
/// </para>
/// </remarks>
public sealed class ProductionLoggingTests
{
    /// <summary>The category that writes one "Request starting" and one "Request finished" per request.</summary>
    private const string RequestPipelineCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    /// <summary>The category that writes the host's startup and shutdown lines.</summary>
    private const string HostLifetimeCategory = "Microsoft.Hosting.Lifetime";

    /// <summary>
    /// A category no rule could name on purpose. Only a default floor silences it, so a fix that
    /// lists the known categories one by one leaves it open.
    /// </summary>
    private const string ArbitraryCategory = "IntegrationTests.ArbitraryCategory.7f3c9e21";

    /// <summary>
    /// The categories that write an Information record on an ordinary request — the request lines,
    /// routing's endpoint match, EF's executed command, Npgsql — plus <see cref="ArbitraryCategory" />,
    /// which makes the check deny-by-default rather than a list of known offenders.
    /// </summary>
    private static readonly string[] ProbedCategories =
    [
        RequestPipelineCategory,
        "Microsoft.AspNetCore.Routing",
        "Microsoft.EntityFrameworkCore.Database.Command",
        "Npgsql",
        ArbitraryCategory,
    ];

    [Test]
    [Arguments("Production")]
    [Arguments("Staging")]
    public async Task NonDevelopmentHost_EnablesNoInformationRecordForTheRequestPipeline(string environment)
    {
        // Arrange — a real host over a real database, booted the way ConnectionStringOptionTests boots
        // one. Staging is here so a fix keyed on IsProduction() rather than !IsDevelopment() goes red.
        // Resolving a service is what builds and starts the host.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using ApiFactory factory = new(
            host.AppConnectionString,
            environment: environment,
            adminConnectionString: host.ConnectionString);
        ILoggerFactory loggerFactory = factory.Services.GetRequiredService<ILoggerFactory>();

        // Act
        List<string> enabledAtInformation =
        [
            .. ProbedCategories.Where(category =>
                loggerFactory.CreateLogger(category).IsEnabled(LogLevel.Information)),
        ];
        bool arbitraryWarningEnabled = loggerFactory.CreateLogger(ArbitraryCategory).IsEnabled(LogLevel.Warning);
        bool lifetimeEnabled = loggerFactory.CreateLogger(HostLifetimeCategory).IsEnabled(LogLevel.Information);

        // Assert — the offenders as a collection, so a red names every category still open. Warning
        // stays on, so a floor of Error or Critical, which would drop real warnings, fails. The lifetime
        // line is kept on purpose, and asserting it enabled also proves the probe can see an enabled
        // level at all.
        await Assert.That(enabledAtInformation).IsEmpty();
        await Assert.That(arbitraryWarningEnabled).IsTrue();
        await Assert.That(lifetimeEnabled).IsTrue();
    }

    [Test]
    public async Task DevelopmentHost_StillEnablesTheRequestLine()
    {
        // Arrange — the same probe on a Development host. The control for the test above: it shows the
        // probe is not blind to the request category, and that a fix scoped to Production left
        // Development's request lines where a developer expects them.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using ApiFactory factory = new(
            host.AppConnectionString,
            environment: "Development",
            adminConnectionString: host.ConnectionString);
        ILoggerFactory loggerFactory = factory.Services.GetRequiredService<ILoggerFactory>();

        // Act
        bool requestLineEnabled = loggerFactory.CreateLogger(RequestPipelineCategory).IsEnabled(LogLevel.Information);

        // Assert
        await Assert.That(requestLineEnabled).IsTrue();
    }
}
