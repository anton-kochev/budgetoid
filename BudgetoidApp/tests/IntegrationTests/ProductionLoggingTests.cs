using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntegrationTests;

/// <summary>
/// Pins FR-033: a Production or a Staging host writes no Information record for the request pipeline,
/// even under a configured default of Information, while Development keeps its request lines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it matters.</b> With no logging configuration at all, ASP.NET's default minimum is
/// Information, and <c>Microsoft.AspNetCore.Hosting.Diagnostics</c> writes a "Request starting" and a
/// "Request finished" line for every request — method, path, status and time — to stdout, and EF's
/// executed-command line sits at the same level. None of that is a narrative value, but it is a
/// per-request trail of what a person did and when, and the product owes nobody that trail.
/// </para>
/// <para>
/// <b>Why a configured default is part of the pin.</b> <c>AddFilter(category: null, Warning)</c> and
/// <c>SetMinimumLevel(Warning)</c> answer the same with no logging configuration at all; they part only
/// when a <c>Logging:LogLevel:Default</c> rule exists, because a minimum level applies only where no
/// rule matches. So the default-rule case carries <c>Logging:LogLevel:Default = Information</c>, and its
/// control carries a <em>category</em> rule the same way and watches it reopen that category — proof
/// that configuration injected through <see cref="ApiFactory" /> reaches the filter options at all, so
/// the default case's green is the floor's doing and not an injection that never landed.
/// </para>
/// <para>
/// <b>What this does not reach.</b> A <em>provider-scoped</em> rule —
/// <c>Logging:Console:LogLevel:Default = Information</c> — outranks a provider-less floor and reopens
/// it; that is how the filter's specificity works, and no case here asserts it closed. Only the
/// Production and Staging names are probed; another non-Development name rides on the same
/// <c>!IsDevelopment()</c> branch by argument, not by a case.
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
    /// routing's endpoint match, EF's executed command — plus <see cref="ArbitraryCategory" />, which
    /// makes the check deny-by-default rather than a list of known offenders. A category that could
    /// write at Information but was not seen doing so on real traffic, Npgsql's among them, is held
    /// by the arbitrary one rather than listed.
    /// </summary>
    private static readonly string[] ProbedCategories =
    [
        RequestPipelineCategory,
        "Microsoft.AspNetCore.Routing",
        "Microsoft.EntityFrameworkCore.Database.Command",
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
    public async Task NonDevelopmentHost_WithConfiguredDefaultOfInformation_StillEnablesNoInformationRecord()
    {
        // Arrange — the configuration an operator exports as Logging__LogLevel__Default=Information.
        // A minimum level would yield to this rule; the default rule Program adds after it does not.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using ApiFactory factory = new(
            host.AppConnectionString,
            environment: "Production",
            settings: new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Information" },
            adminConnectionString: host.ConnectionString);
        ILoggerFactory loggerFactory = factory.Services.GetRequiredService<ILoggerFactory>();

        // Act
        bool requestLineEnabled = loggerFactory.CreateLogger(RequestPipelineCategory).IsEnabled(LogLevel.Information);
        bool arbitraryEnabled = loggerFactory.CreateLogger(ArbitraryCategory).IsEnabled(LogLevel.Information);

        // Assert
        await Assert.That(requestLineEnabled).IsFalse();
        await Assert.That(arbitraryEnabled).IsFalse();
    }

    [Test]
    public async Task NonDevelopmentHost_WithConfiguredCategoryRule_ReopensThatCategoryOnly()
    {
        // Arrange — the control for the test above, injected the same way. A configured category rule
        // is longer than the floor and outranks it by design: it is how an operator opens one category
        // on purpose. Seeing it land proves configuration from the factory reaches the filter options.
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using ApiFactory factory = new(
            host.AppConnectionString,
            environment: "Production",
            settings: new Dictionary<string, string?> { ["Logging:LogLevel:Microsoft.AspNetCore"] = "Information" },
            adminConnectionString: host.ConnectionString);
        ILoggerFactory loggerFactory = factory.Services.GetRequiredService<ILoggerFactory>();

        // Act
        bool requestLineEnabled = loggerFactory.CreateLogger(RequestPipelineCategory).IsEnabled(LogLevel.Information);
        bool arbitraryEnabled = loggerFactory.CreateLogger(ArbitraryCategory).IsEnabled(LogLevel.Information);

        // Assert — the named category opens; a category it does not name keeps the floor.
        await Assert.That(requestLineEnabled).IsTrue();
        await Assert.That(arbitraryEnabled).IsFalse();
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
