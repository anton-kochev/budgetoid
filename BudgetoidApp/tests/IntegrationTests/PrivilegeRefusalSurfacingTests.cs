using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// NFR-012: a write the database refuses on a privilege surfaces as a fault — <c>42501</c>, a 500 and
/// an Error record from the catch-all — and is never translated into a domain error.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this pins is an absence of translation</b>, which ADR 0004 argues: every column the role
/// cannot write is one no domain method reaches, so a <c>42501</c> at runtime is a missing grant and not
/// a caller's mistake. A 400 or a 409 would tell the client to change its request; a 500 tells the
/// operator to change the grant script. Until this class, the only evidence was a manual measurement
/// recorded in <c>CategoryIntegrationTests</c>.
/// </para>
/// <para>
/// <b>The two statements were picked for the catches already sitting on their path.</b>
/// <c>AccountRepository.UpdateAsync</c> catches a <c>23505</c> on the account name index, and
/// <c>PayeeRepository.AddAsync</c> catches <c>23505</c> on the primary key and on the name index. Each
/// is one widened filter away from swallowing a <c>42501</c> into a conflict or a validation problem,
/// and that widening is the regression this class exists to refuse.
/// </para>
/// <para>
/// <b>The <c>REVOKE</c> is safe only because every host has a database of its own.</b> Grants live on
/// the table, in a per-database catalog; <c>PostgresTestHost</c> clones a fresh database per test out of
/// the shared cluster's template, so the revocation dies with this test's database and no neighbour
/// sees it. The role itself is cluster-wide and is not touched.
/// </para>
/// <para>
/// <b>The log record is searched for the sealed values the request carried, and holds none.</b> The
/// record the catch-all writes carries the method, the path — an identifier, never a name — and the
/// exception chain, whose <c>PostgresException</c> names the table and the SQLSTATE and no row value;
/// EF does not put parameter values into its exceptions or its command-failure records without
/// sensitive-data logging, which this application never switches on. So asserting the SQLSTATE needs
/// no leak, and each case asserts the opposite of one: no record of the whole host carries the name
/// envelope or its blind index, in any rendering <see cref="LogNeedle" /> knows. That is a floor for
/// these two routes, not a substitute for <c>LogRedactionTests</c>, whose traffic forces two 500s but revokes no
/// privilege.
/// </para>
/// </remarks>
public sealed class PrivilegeRefusalSurfacingTests
{
    /// <summary>The title the catch-all writes, restated so a reworded handler is a red here.</summary>
    private const string FaultTitle = "An unexpected error occurred.";

    /// <summary>
    /// The account's whole <c>GRANT UPDATE</c> column list, restated from <c>app-role-grants.sql</c>.
    /// The Arrange also asks the catalog that nothing updatable is left, so a column added to the grant
    /// and not here reads as a failed precondition rather than as a green rename.
    /// </summary>
    private const string AccountUpdateColumns = "name, name_key, type, opening_balance, rotation_id";

    /// <summary>
    /// PUT onto an account whose every updatable column has been revoked answers 500 with the
    /// catch-all's problem document, leaves the row as it was, and logs the <c>42501</c> at Error.
    /// </summary>
    [Test]
    public async Task PutAccount_WhenTheRoleLacksTheColumnPrivilege_Answers500AndLogsThe42501()
    {
        // Arrange
        await using PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        LogRecorder recorder = new();
        ApiFactory factory = host.CreateFactory(configureServices: recorder.AttachTo);

        HttpResponseMessage response;
        JsonNode problem;
        string rowBefore;
        string rowAfter;
        bool updateStillHeld;
        Guid id = Guid.CreateVersion7();
        await using (factory)
        {
            (HttpClient client, _, _) = await factory.CreateSignedInClientAsync();
            HttpResponseMessage seeded = await client.PostAsJsonAsync("/api/accounts", new
            {
                id = id.ToString("D"),
                name = SealedNarrative.EncodedName("Checking"),
                nameKey = SealedNarrative.EncodedIndex("Checking"),
                type = "Checking",
                openingBalance = 100m,
                currencyCode = "USD",
            });
            seeded.EnsureSuccessStatusCode();
            rowBefore = await ReadAccountRowAsync(host.ConnectionString, id);

            await ExecuteAsync(
                host.ConnectionString,
                $"revoke update ({AccountUpdateColumns}) on accounts from budgetoid_app");
            updateStillHeld = await ScalarAsync<bool>(
                host.ConnectionString,
                "select has_any_column_privilege('budgetoid_app', 'accounts', 'UPDATE')");

            // Act — a rename, so the statement names columns a unique-name catch would care about.
            response = await client.PutAsJsonAsync($"/api/accounts/{id}", new
            {
                name = SealedNarrative.EncodedName("Savings"),
                nameKey = SealedNarrative.EncodedIndex("Savings"),
                type = "Savings",
                openingBalance = 25m,
            });
            problem = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
            rowAfter = await ReadAccountRowAsync(host.ConnectionString, id);
        }

        IReadOnlyList<CapturedLogRecord> records = recorder.Snapshot();

        // Assert — the revocation took, so the refusal below is the privilege's and nothing else's.
        await Assert.That(updateStillHeld).IsFalse();

        // A fault, in the catch-all's words, and neither of the two shapes a translation would wear.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(problem["title"]?.GetValue<string>()).IsEqualTo(FaultTitle);
        await Assert.That(problem[ConflictKindMember]).IsNull();
        await Assert.That(problem["errors"]).IsNull();

        // Nothing was written.
        await Assert.That(rowAfter).IsEqualTo(rowBefore);

        // The operator is told what happened: the catch-all logged it at Error, carrying the SQLSTATE.
        await Assert.That(FaultRecordsCarrying42501(records)).IsNotEmpty();

        // And the record told nobody what the account is called.
        await Assert.That(LogCensus.Search(records, NeedlesFor("accounts", "Checking", "Savings"))).IsEmpty();
    }

    /// <summary>
    /// POST of a payee onto a table the role may no longer insert into answers 500 with the catch-all's
    /// problem document, files no row, and logs the <c>42501</c> at Error.
    /// </summary>
    [Test]
    public async Task PostPayee_WhenTheRoleLacksInsert_Answers500AndLogsThe42501()
    {
        // Arrange
        await using PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        LogRecorder recorder = new();
        ApiFactory factory = host.CreateFactory(configureServices: recorder.AttachTo);

        HttpResponseMessage response;
        JsonNode problem;
        long rowsFiled;
        bool insertStillHeld;
        Guid id = Guid.CreateVersion7();
        await using (factory)
        {
            (HttpClient client, _, _) = await factory.CreateSignedInClientAsync();

            await ExecuteAsync(host.ConnectionString, "revoke insert on payees from budgetoid_app");
            insertStillHeld = await ScalarAsync<bool>(
                host.ConnectionString,
                "select has_table_privilege('budgetoid_app', 'payees', 'INSERT')");

            // Act
            response = await client.PostAsJsonAsync("/api/payees", new
            {
                id = id.ToString("D"),
                name = SealedNarrative.EncodedName("Starbucks"),
                nameKey = SealedNarrative.EncodedIndex("Starbucks"),
            });
            problem = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
            rowsFiled = await ScalarAsync<long>(
                host.ConnectionString, $"select count(*) from payees where id = '{id:D}'");
        }

        IReadOnlyList<CapturedLogRecord> records = recorder.Snapshot();

        // Assert — the revocation took.
        await Assert.That(insertStillHeld).IsFalse();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(problem["title"]?.GetValue<string>()).IsEqualTo(FaultTitle);
        await Assert.That(problem[ConflictKindMember]).IsNull();
        await Assert.That(problem["errors"]).IsNull();

        await Assert.That(rowsFiled).IsEqualTo(0L);

        await Assert.That(FaultRecordsCarrying42501(records)).IsNotEmpty();

        await Assert.That(LogCensus.Search(records, NeedlesFor("payees", "Starbucks"))).IsEmpty();
    }

    /// <summary>
    /// The conflict handler's extension member, restated rather than read from
    /// <c>ConflictExceptionHandler</c>: it is a wire spelling, and a rename there must not rename the
    /// absence asserted here.
    /// </summary>
    private const string ConflictKindMember = "conflictKind";

    /// <summary>
    /// Error records from the catch-all whose exception chain holds a <c>PostgresException</c> carrying
    /// <c>42501</c>. The chain is text by the time a test reads it — <see cref="LogRecorder" /> flattens
    /// it at write time — so the type and the SQLSTATE are matched as the recorder renders them:
    /// <c>Npgsql.PostgresException: 42501: …</c>.
    /// </summary>
    private static CapturedLogRecord[] FaultRecordsCarrying42501(IReadOnlyList<CapturedLogRecord> records) =>
    [
        .. records.Where(record =>
            record.Category == typeof(GlobalExceptionHandler).FullName
            && record.Level == LogLevel.Error
            && record.Exception is { } chain
            && chain.Contains(
                $"{typeof(PostgresException).FullName}: {PostgresErrorCodes.InsufficientPrivilege}:",
                StringComparison.Ordinal)),
    ];

    /// <summary>
    /// Every rendering of every name envelope and blind index the request carried or the row held.
    /// </summary>
    private static LogNeedle[] NeedlesFor(string table, params string[] labels) =>
    [
        .. labels.SelectMany(label => (IEnumerable<LogNeedle>)
        [
            .. LogNeedle.ForText($"{table}.name", SealedNarrative.EncodedName(label), ignoresCase: false),
            .. LogNeedle.ForBytes($"{table}.name", SealedNarrative.Name(label).Envelope.ToArray()),
            .. LogNeedle.ForText($"{table}.name_key", SealedNarrative.EncodedIndex(label), ignoresCase: false),
            .. LogNeedle.ForBytes($"{table}.name_key", SealedNarrative.BlindIndex(label).ToArray()),
        ]),
    ];

    /// <summary>
    /// The account row's every column, as one text, read on the admin connection so the role's own
    /// grants and policies are not between the assertion and the row.
    /// </summary>
    private static Task<string> ReadAccountRowAsync(string adminConnectionString, Guid id) =>
        ScalarAsync<string>(
            adminConnectionString,
            $"select row_to_json(a)::text from accounts a where id = '{id:D}'");

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
