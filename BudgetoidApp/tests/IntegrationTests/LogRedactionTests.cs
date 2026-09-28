using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Api.Infrastructure;
using Infrastructure.Persistence.Inventory;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// The census behind FR-034 and FR-035: no log record the API writes carries an email address, a
/// provider subject, a passkey handle or a narrative value.
/// </summary>
/// <remarks>
/// <para>
/// <b>The needles are read back from the database, never written down here.</b> Every value of every
/// column in <see cref="NeverLoggedColumns.All" />, as it stood at each snapshot the traffic asked for
/// and after it ran, in every rendering a record could carry it in (<see cref="LogNeedle" />). So a
/// ninth narrative column is searched the day the inventory classifies it (NFR-023), a value the
/// product derived rather than received is searched as the product holds it, and a value a later step
/// replaced or erased is still searched. Beside them, every identifying value the traffic <i>sent</i>,
/// under the column it would have filled — a refused duplicate's address is never stored, and is
/// exactly what a refusal path would log.
/// </para>
/// <para>
/// <b>Two hosts, because one cannot be both.</b> The main host repoints the provider's bearer scheme to
/// the test handler, which is what lets registration run at all; a token sent there never meets the
/// real <c>JwtBearer</c> handler. The second host keeps the real handler and hands it one signing key
/// and no metadata endpoint, so nothing reaches the network: a token signed with that key validates and
/// registers an account, and the same token with a forged signature is refused.
/// </para>
/// <para>
/// <b>What a green run does not cover</b>, so nobody infers it: a value re-encoded in a way
/// <see cref="LogNeedle" /> does not render (a JWT's base64url payload is one — the email inside a token
/// is not the email's own bytes), a record a provider writes outside <c>ILogger</c> (stdout, an
/// <c>EventSource</c>, an OTLP exporter's own path), and a branch of a route this traffic reaches
/// without taking. Which routes it reaches is a floor below, over the host's own route table.
/// </para>
/// </remarks>
public sealed class LogRedactionTests
{
    /// <summary>
    /// One never-logged column: how many distinct values the database held for it across every
    /// snapshot, how many the traffic sent, and their needles.
    /// </summary>
    private sealed record ColumnNeedles(string Column, int StoredValues, int SentValues, IReadOnlyList<LogNeedle> Needles);

    /// <summary>One value one never-logged column held at one snapshot.</summary>
    private sealed record StoredValue(string Column, object Value);

    /// <summary>
    /// Routes the census does not demand a 2xx from, each with the reason. Keyed as
    /// <see cref="RouteTally" /> keys a route.
    /// </summary>
    /// <remarks>
    /// Empty is the goal and a reason is owed per entry. A key here that the table no longer declares
    /// is itself a red, so the list cannot outlive the route it excused.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> UndrivenRoutes =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Test]
    public async Task LogRecords_CarryNoValueOfANeverLoggedColumn()
    {
        // Arrange
        await using PostgresTestHost host = new(
            usesApplicationAuthentication: true, repointsProviderSchemeToTestHandler: true);
        await host.StartAsync();

        LogRecorder appRecorder = new();
        LogRecorder bearerRecorder = new();
        RouteTally routes = new();
        ApiFactory app = host.CreateFactory(configureServices: services =>
        {
            appRecorder.AttachTo(services);
            routes.AttachTo(services);

            // As Production has it, so a body the JSON reader refuses is logged by the framework rather
            // than thrown to an exception handler that writes no record.
            services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
        });
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa) { KeyId = "log-census" };
        ApiFactory bearer = CreateRealBearerFactory(host, bearerRecorder, signingKey);

        // Act — the traffic, snapshotting the stored values before every step that replaces or erases
        // one; then the factories disposed so a record written at shutdown is in the snapshot; then the
        // values as they stand at the end.
        List<StoredValue> stored = [];
        List<string> moments = [];
        async Task SnapshotAsync(string moment)
        {
            moments.Add(moment);
            stored.AddRange(await ReadStoredValuesAsync(host.ConnectionString));
        }

        LogCensusTraffic.Run run;
        LogCensusTraffic.BearerRun bearerRun;
        IReadOnlySet<string> declared;
        await using (app)
        {
            run = await LogCensusTraffic.DriveAsync(host, app, appRecorder, SnapshotAsync);
            declared = RouteTally.Declared(app.Services);
        }

        await using (bearer)
        {
            bearerRun = await LogCensusTraffic.DriveProviderTokensAsync(
                bearer, bearerRecorder, signingKey, run.Subject, run.Email);
        }

        await SnapshotAsync("after the traffic");

        IReadOnlyList<CapturedLogRecord> appRecords = appRecorder.Snapshot();
        IReadOnlyList<CapturedLogRecord> bearerRecords = bearerRecorder.Snapshot();
        IReadOnlyList<LogCensusTraffic.SentValue> sent = [.. run.Sent, .. bearerRun.Sent];
        IReadOnlyList<ColumnNeedles> columns = await BuildNeedlesAsync(host.ConnectionString, stored, sent);
        LogNeedle[] needles = [.. columns.SelectMany(column => column.Needles)];
        IReadOnlyList<LogOffence> offences = LogCensus.Search([.. appRecords, .. bearerRecords], needles);
        IReadOnlySet<string> reached = routes.Reached();
        string[] undriven =
        [
            .. declared
                .Where(route => !reached.Contains(route) && !UndrivenRoutes.ContainsKey(route))
                .Order(StringComparer.Ordinal),
        ];

        Report(run, bearerRun, moments, appRecords, bearerRecords, columns, needles, offences, declared, undriven);

        // Assert — the precondition on the needles first: a short one matches harmless text by chance.
        string[] shortNeedles =
        [
            .. needles
                .Where(needle => needle.Value.Length < LogNeedle.MinimumLength)
                .Select(needle => $"{needle.Source} as {needle.Kind}: {needle.Value.Length} chars")
                .Distinct(StringComparer.Ordinal),
        ];
        await Assert.That(shortNeedles).IsEmpty();

        // The rule.
        await Assert.That(offences).IsEmpty();

        // Non-vacuity, every narrative column held a value before the reads and the export ran, so they
        // had it to carry. A column here is on a table nothing in LogCensusTraffic writes a row into.
        await Assert.That(run.EmptyBeforeTheReads).IsEmpty();

        // Non-vacuity, every column: a column the database never held a value for was searched for
        // nothing but what the traffic happened to send.
        string[] empty = [.. columns.Where(column => column.StoredValues == 0).Select(column => column.Column)];
        await Assert.That(empty).IsEmpty();

        // Non-vacuity, the needles against the two owners of the list rather than against the list
        // itself: every column the inventory calls narrative and every identifying entry produced a
        // needle, and no needle names a column outside them.
        string[] owed =
        [
            .. DataInventory.Of(ColumnClassification.Narrative).Select(entry => entry.Qualified)
                .Concat(NeverLoggedColumns.Entries.Select(entry => entry.Qualified)),
        ];
        string[] searched = [.. needles.Select(needle => needle.Source).Distinct(StringComparer.Ordinal)];
        await Assert.That(owed.Except(searched, StringComparer.Ordinal).ToArray()).IsEmpty();
        await Assert.That(searched.Except(owed, StringComparer.Ordinal).ToArray()).IsEmpty();

        // Non-vacuity, the sent values: every identifying entry was sent at least once, so a refused
        // value of each kind is a needle.
        string[] neverSent =
        [
            .. NeverLoggedColumns.Entries
                .Select(entry => entry.Qualified)
                .Where(column => !sent.Any(value => value.Column == column)),
        ];
        await Assert.That(neverSent).IsEmpty();

        // Non-vacuity, the route table: every route the host declares answered a 2xx at least once, and
        // every exemption still names a declared route.
        await Assert.That(undriven).IsEmpty();
        await Assert.That(UndrivenRoutes.Keys.Where(route => !declared.Contains(route)).ToArray()).IsEmpty();
        await Assert.That(declared.Count).IsGreaterThanOrEqualTo(DeclaredRouteFloor);

        // The duplicate handle was refused by the repository's catch, after the save, and by nothing
        // earlier: any other status means the unique violation on the exempt table never happened, and
        // the exception the census most needs to see was never raised.
        await Assert.That(string.Join(",", StatusesOf(run, LogCensusTraffic.DuplicateHandleStep))).IsEqualTo("409");

        // The erasure took the account every other step wrote into, so its values survive only in the
        // snapshots — and the snapshots are what kept the columns above non-empty.
        await Assert.That(string.Join(",", StatusesOf(run, LogCensusTraffic.ErasureStep))).IsEqualTo("204");

        // Non-vacuity, the main host: the recorder reached the framework, EF and the three places this
        // product writes a record of its own.
        await Assert.That(appRecords.Count).IsGreaterThanOrEqualTo(AppRecordFloor);
        await Assert.That(appRecords.Any(record => record.Category.StartsWith("Microsoft.AspNetCore.Hosting", StringComparison.Ordinal)))
            .IsTrue();
        await Assert.That(appRecords.Any(record => record.Category.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal)))
            .IsTrue();
        await Assert.That(appRecords.Count(record =>
                record.Category == typeof(GlobalExceptionHandler).FullName
                && record.Level == LogLevel.Error
                && record.Exception is not null))
            .IsGreaterThanOrEqualTo(2);
        await Assert.That(appRecords.Any(record => record.Category == typeof(PasskeyVerificationExceptionHandler).FullName))
            .IsTrue();
        await Assert.That(appRecords.Any(record => record.Category == typeof(RecoveryCodeRedemptionExceptionHandler).FullName))
            .IsTrue();

        // The malformed body reached a record, carrying the parser's exception. Only because this host
        // turns ThrowOnBadRequest off, as Production has it.
        await Assert.That(run.Steps
                .Single(step => step.Name == LogCensusTraffic.MalformedBodyStep)
                .Records.Any(record => record.Exception?.Contains(typeof(JsonException).FullName!, StringComparison.Ordinal) is true))
            .IsTrue();

        // Non-vacuity, the bearer host: it booted and served.
        await Assert.That(bearerRecords.Count).IsGreaterThanOrEqualTo(BearerRecordFloor);
        await Assert.That(bearerRecords.Any(record => record.Category.StartsWith("Microsoft.AspNetCore.Hosting", StringComparison.Ordinal)))
            .IsTrue();

        // The token validated: both registration legs name the provider scheme and nothing else, and this
        // host leaves that scheme on the real handler, so a 200 and a 201 are the handler's acceptance —
        // and the handler said so in a record of its own.
        LogCensusTraffic.Step validated = bearerRun.Steps.Single(step => step.Name == LogCensusTraffic.ValidatedTokenStep);
        await Assert.That(string.Join(",", validated.Statuses)).IsEqualTo("200,201");
        await Assert.That(validated.Records.Any(record =>
                record.Category == typeof(JwtBearerHandler).FullName && record.EventId == TokenValidationSucceededEventId))
            .IsTrue();

        // And the forged one did not, on the handler's own failure record.
        LogCensusTraffic.Step forged = bearerRun.Steps.Single(step => step.Name == LogCensusTraffic.ForgedTokenStep);
        await Assert.That(string.Join(",", forged.Statuses)).IsEqualTo("401");
        await Assert.That(forged.Records.Any(record =>
                record.Category == typeof(JwtBearerHandler).FullName && record.Exception is not null))
            .IsTrue();
    }

    /// <summary>
    /// The end-to-end control: a value of every identifying entry and of a narrative column, planted in
    /// a record through the host, is named by the census under the column it was read back from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Covers the half <c>LogRecorderTests</c> cannot: that the read-back turns every column the list
    /// names into needles that match what a record would carry, and that the report names the column.
    /// </para>
    /// <para>
    /// <b>The planted set is <see cref="NeverLoggedColumns.Entries" /> itself, plus one narrative
    /// column</b>, and is asserted planted before the offences are read — so a fourth entry is planted
    /// the day it is listed, and an entry this seeding leaves empty is a red rather than a skipped row.
    /// Each value is planted as the wire carries it: text as stored, bytes as base64url. The address is
    /// planted a second time re-cased, under a category of its own, because its column's collation is
    /// what makes that a match.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Census_ForValuesPlantedThroughTheHost_NamesEveryEntryAndANarrativeColumn()
    {
        // Arrange
        await using PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        LogRecorder recorder = new();
        ApiFactory factory = host.CreateFactory(configureServices: recorder.AttachTo);
        string email = $"{LogCensusTraffic.Marker("planted")}@Example.test";
        const string narrativeColumn = "accounts.name";
        string[] planted =
        [
            .. NeverLoggedColumns.Entries.Select(entry => entry.Qualified),
            narrativeColumn,
        ];

        List<string> unplantable = [];
        await using (factory)
        {
            ApiFactory.SignedInClient signedIn = await factory.CreateSignedInClientAsync(
                LogCensusTraffic.Marker("planted-subject"), email);
            string accountName = LogCensusTraffic.Marker("planted-account");
            (await signedIn.Client.PostAsJsonAsync("/api/accounts", new
            {
                id = Guid.CreateVersion7().ToString("D"),
                name = TestSupport.SealedNarrative.EncodedName(accountName),
                nameKey = TestSupport.SealedNarrative.EncodedIndex(accountName),
                type = "Checking",
                openingBalance = 1m,
                currencyCode = "USD",
            })).EnsureSuccessStatusCode();

            // Act — one value of every planted column, read back generically and logged as the wire
            // would carry it.
            ILoggerFactory loggers = factory.Services.GetRequiredService<ILoggerFactory>();
            IReadOnlyList<StoredValue> values = await ReadStoredValuesAsync(host.ConnectionString);
            foreach (string column in planted)
            {
                object? value = values.FirstOrDefault(stored => stored.Column == column)?.Value;
                string? rendering = value switch
                {
                    byte[] bytes => TestSupport.Base64UrlText.Encode(bytes),
                    string text => text,
                    _ => null,
                };

                if (rendering is null)
                {
                    unplantable.Add(column);
                    continue;
                }

                loggers.CreateLogger($"LogCensusControl.{column}").LogWarning("Planted {Value}", rendering);
            }

            loggers.CreateLogger("LogCensusControl.Recased").LogWarning("Planted {Address}", email.ToLowerInvariant());
        }

        IReadOnlyList<ColumnNeedles> columns =
            await BuildNeedlesAsync(host.ConnectionString, await ReadStoredValuesAsync(host.ConnectionString), []);
        IReadOnlyList<LogOffence> offences =
            LogCensus.Search(recorder.Snapshot(), [.. columns.SelectMany(column => column.Needles)]);

        // Assert — every column was plantable, and each one is named in the record it was planted in.
        await Assert.That(unplantable).IsEmpty();
        string[] unnamed =
        [
            .. planted.Where(column => !offences.Any(offence =>
                offence.Source == column
                && offence.Origin.StartsWith($"LogCensusControl.{column} ", StringComparison.Ordinal))),
        ];
        await Assert.That(unnamed).IsEmpty();
        await Assert.That(offences.Any(offence =>
                offence.Source == "users.email"
                && offence.Origin.StartsWith("LogCensusControl.Recased ", StringComparison.Ordinal)))
            .IsTrue();
    }

    /// <summary>
    /// Records the main host must reach. Measured at 6,965 and 6,973; set well below, because its job is to tell
    /// "the recorder saw the host" from "it saw nothing", not to pin a count every new route would move.
    /// </summary>
    private const int AppRecordFloor = 1000;

    /// <summary>Records the bearer host must reach. Measured at 293 and 296; set well below for the same reason.</summary>
    private const int BearerRecordFloor = 20;

    /// <summary>
    /// Routes the table must declare, so an enumeration that found nothing cannot pass the route floor
    /// by having nothing to demand. Measured at 49.
    /// </summary>
    private const int DeclaredRouteFloor = 40;

    /// <summary>
    /// <c>JwtBearerHandler</c>'s "Successfully validated the token." event, which it writes at
    /// <c>Debug</c> and only after the whole validation has passed.
    /// </summary>
    private const int TokenValidationSucceededEventId = 2;

    private static IReadOnlyList<int> StatusesOf(LogCensusTraffic.Run run, string step) =>
        run.Steps.Single(candidate => candidate.Name == step).Statuses;

    /// <summary>
    /// A factory over the same database whose provider scheme is answered by the real bearer handler,
    /// holding <paramref name="signingKey" /> and no metadata address, so validation runs whole without
    /// reaching the network.
    /// </summary>
    private static ApiFactory CreateRealBearerFactory(PostgresTestHost host, LogRecorder recorder, SecurityKey signingKey) =>
        new(
            host.AppConnectionString,
            configureServices: services =>
            {
                recorder.AttachTo(services);
                services.PostConfigure<JwtBearerOptions>(ProviderAuthentication.SchemeName, options =>
                {
                    OpenIdConnectConfiguration configuration = new() { Issuer = LogCensusTraffic.ProviderIssuer };
                    configuration.SigningKeys.Add(signingKey);
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
            },
            adminConnectionString: host.ConnectionString,
            usesApplicationAuthentication: true);

    /// <summary>
    /// Every value every never-logged column holds right now, read on the admin connection so
    /// row-level security hides nothing.
    /// </summary>
    private static async Task<IReadOnlyList<StoredValue>> ReadStoredValuesAsync(string adminConnectionString)
    {
        List<StoredValue> values = [];
        await using NpgsqlConnection admin = new(adminConnectionString);
        await admin.OpenAsync();

        foreach (NeverLoggedColumn column in NeverLoggedColumns.All)
        {
            await using NpgsqlCommand read = new(
                $"select distinct {LogCensusTraffic.Quote(column.Column)} "
                + $"from public.{LogCensusTraffic.Quote(column.Table)} "
                + $"where {LogCensusTraffic.Quote(column.Column)} is not null",
                admin);
            await using NpgsqlDataReader reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                values.Add(new StoredValue(column.Qualified, reader.GetValue(0)));
            }
        }

        return values;
    }

    /// <summary>
    /// The needles for every never-logged column: the distinct values stored across every snapshot and
    /// every value sent, each in every rendering.
    /// </summary>
    /// <remarks>
    /// A text column is compared ignoring case when its collation is nondeterministic — which is how the
    /// schema says two spellings are one value — and ordinally otherwise. Read off the catalog rather
    /// than keyed on a column name, so the rule follows the schema, and applied to a sent value exactly
    /// as to a stored one.
    /// </remarks>
    private static async Task<IReadOnlyList<ColumnNeedles>> BuildNeedlesAsync(
        string adminConnectionString,
        IReadOnlyList<StoredValue> stored,
        IReadOnlyList<LogCensusTraffic.SentValue> sent)
    {
        List<ColumnNeedles> columns = [];
        await using NpgsqlConnection admin = new(adminConnectionString);
        await admin.OpenAsync();

        foreach (NeverLoggedColumn column in NeverLoggedColumns.All)
        {
            await using NpgsqlCommand collation = new(
                """
                select coalesce(not coll.collisdeterministic, false)
                from pg_attribute a
                join pg_class c on c.oid = a.attrelid
                join pg_namespace n on n.oid = c.relnamespace
                left join pg_collation coll on coll.oid = a.attcollation
                where n.nspname = 'public' and c.relname = @table and a.attname = @column
                """,
                admin);
            collation.Parameters.AddWithValue("table", column.Table);
            collation.Parameters.AddWithValue("column", column.Column);
            bool ignoresCase = await collation.ExecuteScalarAsync() is true;

            Dictionary<string, object> storedValues = Distinct(
                stored.Where(value => value.Column == column.Qualified).Select(value => value.Value));
            Dictionary<string, object> sentValues = Distinct(
                sent.Where(value => value.Column == column.Qualified).Select(value => value.Value));

            List<LogNeedle> needles = [];
            foreach ((string key, object value) in storedValues.Concat(sentValues).DistinctBy(pair => pair.Key))
            {
                needles.AddRange(value switch
                {
                    byte[] bytes => LogNeedle.ForBytes(column.Qualified, bytes),
                    string text => LogNeedle.ForText(column.Qualified, text, ignoresCase),
                    _ => LogNeedle.ForText(column.Qualified, key[2..], ignoresCase),
                });
            }

            columns.Add(new ColumnNeedles(column.Qualified, storedValues.Count, sentValues.Count, needles));
        }

        return columns;
    }

    /// <summary>Values keyed on a form two equal values share: base64 for bytes, the text otherwise.</summary>
    private static Dictionary<string, object> Distinct(IEnumerable<object> values)
    {
        Dictionary<string, object> distinct = new(StringComparer.Ordinal);
        foreach (object value in values)
        {
            string key = value switch
            {
                byte[] bytes => $"b:{Convert.ToBase64String(bytes)}",
                string text => $"t:{text}",
                _ => $"o:{value}",
            };
            distinct.TryAdd(key, value);
        }

        return distinct;
    }

    /// <summary>
    /// Prints what the run examined: per step, per host, per column, per route. Never a needle's value.
    /// </summary>
    private static void Report(
        LogCensusTraffic.Run run,
        LogCensusTraffic.BearerRun bearerRun,
        IReadOnlyList<string> moments,
        IReadOnlyList<CapturedLogRecord> appRecords,
        IReadOnlyList<CapturedLogRecord> bearerRecords,
        IReadOnlyList<ColumnNeedles> columns,
        IReadOnlyList<LogNeedle> needles,
        IReadOnlyList<LogOffence> offences,
        IReadOnlySet<string> declared,
        IReadOnlyList<string> undriven)
    {
        Console.WriteLine($"Main host: {appRecords.Count} records. Bearer host: {bearerRecords.Count} records.");
        Console.WriteLine($"Filled from the inventory: {string.Join(", ", run.FilledFromTheInventory)}");
        Console.WriteLine($"Empty before the reads: {string.Join(", ", run.EmptyBeforeTheReads)}");
        Console.WriteLine($"Snapshots: {string.Join(" | ", moments)}");
        foreach (ColumnNeedles column in columns)
        {
            Console.WriteLine(
                $"  {column.Column}: {column.StoredValues} stored, {column.SentValues} sent, {column.Needles.Count} needles");
        }

        Console.WriteLine($"Needles: {needles.Count}, shortest {needles.Select(needle => needle.Value.Length).DefaultIfEmpty().Min()} chars.");
        Console.WriteLine($"Routes declared: {declared.Count}. Undriven: {undriven.Count}");
        foreach (string route in undriven)
        {
            Console.WriteLine($"  undriven {route}");
        }

        foreach (LogCensusTraffic.Step step in run.Steps.Concat(bearerRun.Steps))
        {
            IEnumerable<CapturedLogRecord> own = step.Records.Where(record =>
                !record.Category.StartsWith("Microsoft.AspNetCore.Hosting", StringComparison.Ordinal)
                && !record.Category.StartsWith("Microsoft.AspNetCore.Routing", StringComparison.Ordinal));
            Console.WriteLine(
                $"Step '{step.Name}' [{string.Join(",", step.Statuses)}]: {step.Records.Count} records, "
                + $"{step.Records.Count(record => record.Level >= LogLevel.Warning)} at Warning+, "
                + $"{step.Records.Count(record => record.Exception is not null)} with an exception");
            foreach (IGrouping<string, CapturedLogRecord> group in own
                         .Where(record => record.Level >= LogLevel.Information
                                          || record.Exception is not null
                                          || record.Category.StartsWith("Microsoft.AspNetCore.Authentication", StringComparison.Ordinal))
                         .GroupBy(record => $"{record.Category} {record.Level} {record.EventId}"))
            {
                string exception = group.First().Exception?.Split(':')[0] ?? string.Empty;
                Console.WriteLine($"    {group.Key} x{group.Count()} {exception}");
            }
        }

        Console.WriteLine($"Offences: {offences.Count}");
        foreach (LogOffence offence in offences)
        {
            Console.WriteLine($"  {offence}");
        }
    }
}
