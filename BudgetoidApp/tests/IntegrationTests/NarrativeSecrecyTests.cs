using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Security;
using Infrastructure.Persistence.Provisioning;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The census that carries NFR-013: a connection holding the application role's credentials
/// <b>and</b> a valid budget session reads no narrative value in plaintext, anywhere in the database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written as a test rather than as an argument, because the requirement asks for the read to
/// happen.</b> There is a structural case that no narrative column can hold plaintext —
/// <see cref="NarrativeField" /> has no constructor, factory or conversion taking a
/// <see cref="string" />, so writing text into one does not compile — and that case is real and is
/// stated where it belongs. It does not discharge this criterion. What it covers is the eight columns
/// somebody remembered to type; what it says nothing about is a <em>copy</em> landing somewhere else,
/// which is the shape a leak actually takes.
/// </para>
/// <para>
/// <b>So the scan is over every column of every relation, and the width is the point.</b> A census
/// restricted to the eight narrative columns is green on the day a ninth column, an audit trail, a
/// denormalised projection or a log relation grows one — and green for the same reason it was green
/// before, which is that nobody added it to a list. <c>ErasureAtomicityTests</c> makes exactly this
/// argument about remnants: a leak with no name is caught by counting rather than by recognising.
/// </para>
/// <para>
/// <b>A <c>bytea</c> column rendered <c>::text</c> is hex, and that is the single most likely real
/// leak walking straight past a careless implementation of this test.</b> A write path that skipped
/// sealing and put UTF-8 bytes into <c>accounts.name</c> produces a row whose <c>name::text</c> is
/// <c>\x4e4152…</c> — a substring search over that text finds nothing, forever, while the plaintext
/// sits in the column. So a binary column is searched <b>as bytes</b>, with
/// <c>position(convert_to(marker, 'UTF8') in col) &gt; 0</c>, and every other column is searched as
/// text. <see cref="Scan_ReportsAPlaintextNameWrittenThroughARealRoute" /> writes that exact row
/// through the shipped route and asserts both halves in one run: the byte search names the column and
/// the text search does not. That pair is what keeps this paragraph from being a claim.
/// </para>
/// <para>
/// <b>Non-vacuity is asserted rather than assumed, four ways.</b> A catalog query that stopped
/// matching reports an empty offender set, which is byte-identical to the rule holding — the failure
/// mode <c>KeyMaterialSecrecyTests</c> names in its own remarks. This file answers it by asserting how
/// many relations the scan reached, that it reached the six that carry narrative rows, how many rows
/// it examined, and — the sharpest of the four — that every one of the eight narrative columns really
/// does hold an envelope of at least <see cref="CiphertextEnvelope.MinimumLength" /> bytes at the
/// moment the scan ran. Without that last one a seeding step that silently 400'd would leave the scan
/// looking at empty tables and reporting the requirement met.
/// </para>
/// <para>
/// <b>What this test can and cannot see, stated plainly, because the limit is not obvious.</b> The
/// marker plaintext never crosses the wire: the client seals it and sends an envelope, so the server
/// is never handed the words at all. That means the census cannot fail because of anything the
/// <em>server</em> does with a value it was given — it can only fail because a value arrived in a
/// readable shape and was stored. The two controls are therefore not decoration, they are where the
/// test's teeth are: each one puts a readable value in front of the same scan, through a path a
/// client really has, and demands the scan name it.
/// </para>
/// <para>
/// <b>Two further limits, recorded rather than closed, because a green run here is narrower than the
/// sentence at the top of this file.</b> First, the scan hunts for the marker's own UTF-8 bytes, so a
/// plaintext copy that was <em>re-encoded</em> on the way in — base64, hex, UTF-16, compressed, or
/// escaped into a JSON string — carries none of those bytes and is reported clean. Widening the search
/// to chase encodings is not the answer: the set of encodings is open, and a census that guessed at a
/// few of them would read as covering all of them. Second, the scan reads on the app role's own
/// session, which is the criterion's word and also its blind spot — a row a row-level security policy
/// hides from that session is not reported as <em>unread</em>, it is simply not among the rows counted,
/// so the run says read-and-clean over a database it saw part of. Neither limit has a control here and
/// neither should grow one; they are written down so the next reader does not infer coverage the run
/// does not have.
/// </para>
/// </remarks>
public sealed class NarrativeSecrecyTests
{
    /// <summary>
    /// The eight narrative columns, each with the plaintext this run hunts for in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One marker per field rather than one for all eight</b>, so a failure names the field whose
    /// words leaked and not merely the column that holds a leak. The two are different facts: a
    /// denormalised copy of a category's note in an audit column is <c>audit.payload</c> holding
    /// <c>categories.description</c>, and a report naming only the first sends the reader to the wrong
    /// write path.
    /// </para>
    /// <para>
    /// <b>The list is a claim about the schema and is checked against it.</b> A ninth narrative column
    /// arriving with no entry here would still be scanned — the scan reads the catalog and knows
    /// nothing about this list — but nothing would be written into it, so the presence assertion that
    /// makes the run non-vacuous would not cover it. That is the honest limit: the scan is derived, the
    /// <em>seeding</em> is authored, and only the seeding can be behind.
    /// </para>
    /// </remarks>
    private static readonly NarrativeMarker[] Markers =
    [
        new("budgets", "name", "narrative-marker-zq7-budget-name"),
        new("accounts", "name", "narrative-marker-zq7-account-name"),
        new("payees", "name", "narrative-marker-zq7-payee-name"),
        new("category_groups", "name", "narrative-marker-zq7-category-group-name"),
        new("category_groups", "description", "narrative-marker-zq7-category-group-description"),
        new("categories", "name", "narrative-marker-zq7-category-name"),
        new("categories", "description", "narrative-marker-zq7-category-description"),
        new("transactions", "description", "narrative-marker-zq7-transaction-description"),
    ];

    /// <summary>
    /// The relations the census must have reached, named so an empty offender set cannot be a scan
    /// that never looked. Every one of them carries a narrative column this run writes into.
    /// </summary>
    private static readonly string[] NarrativeRelations =
        [.. Markers.Select(marker => marker.Table).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    [Test]
    public async Task AppRoleWithABudgetSession_ReadsNoNarrativeValueInPlaintext()
    {
        // Arrange — a real API host, a real session, and one row in every table that carries a
        // narrative column. The rows go in through the shipped routes rather than through a seeder,
        // because the criterion is about what the product writes: a seeder calling the domain factories
        // would be this test agreeing with itself about a write path no request takes.
        await using PostgresTestHost host = await StartApiHostAsync();
        ApiFactory.SignedInClient session = await host.Factory.CreateSignedInClientAsync();

        // The markers are checked for pairwise containment before anything is written. If one were a
        // substring of another, a single leak would be reported as two fields and the sharper of the two
        // reports would be the wrong one — an attribution defect that no assertion downstream can see.
        await Assert.That(OverlappingMarkers()).IsEmpty();

        // And each sealed envelope is checked NOT to carry its own marker. This is the precondition
        // that makes a green run mean something: SealedNarrative's filler is a function of the label, so
        // an envelope that happened to contain the label verbatim would make this census red for a
        // reason that is about the fixture rather than about the product. Measured here rather than
        // reasoned from the filler's arithmetic, because the filler is free to change.
        await Assert.That(MarkersVisibleInTheirOwnEnvelopes()).IsEmpty();

        await SeedNarrativeRowsThroughTheApiAsync(session.Client);

        // budgets.name has no write route — there is no budget-naming endpoint on the route table — so
        // the one narrative column no request can reach is seeded through the same domain factory a
        // provisioner would use. It is a SECOND budget beside the account's nameless default; `budgets`
        // is policed on app.current_user_id rather than on a budget, so both rows are visible to the
        // session opened below and the scan reads them both.
        await RepositoryTestHost.SeedAdditionalBudgetOnAsync(
            host.ConnectionString, session.UserId, MarkerFor("budgets", "name"));

        // Act — the connection the criterion names: the least-privilege role, carrying the signed-in
        // user and the ambient budget exactly as SessionContextInterceptor puts them on a request's
        // connection. Both settings, because the two isolation axes read different ones and a session
        // naming only a budget is a state production cannot produce.
        await using NpgsqlConnection app =
            await OpenAppSessionAsync(host, session.UserId, session.BudgetId);
        NarrativeScan scan = await ScanAsync(app, readsBinaryColumnsAsBytes: true);

        // Assert — joined rather than counted, so a red run hands the reviewer the column and the field
        // whose words are sitting in it instead of a number.
        await Assert.That(string.Join(Environment.NewLine, scan.Offenders)).IsEqualTo(string.Empty);

        // Fail closed on a relation the scan could not read at all. Every relation in `public` carries a
        // SELECT grant for this role, so a refusal here is either a grant that moved or a column type
        // the probe cannot express — both of which would otherwise shrink the census in silence.
        await Assert.That(string.Join(Environment.NewLine, scan.Unscannable)).IsEqualTo(string.Empty);

        // Non-vacuity. Everything above is satisfied by a scan that read nothing, which is exactly what
        // a catalog query that stopped matching produces. Printed the way the sibling censuses print
        // theirs, so a reader of a red run can see what was examined.
        Console.WriteLine(
            $"Narrative scan: {scan.Relations.Count} relations, {scan.Columns} columns, "
            + $"{scan.RowsExamined} rows. {string.Join(", ", scan.Relations)}");

        // Floors rather than counts, and deliberately loose ones: the schema is measured today at 17
        // relations, 95 columns and 28 rows on this arrangement, and these three exist to separate "the
        // scan read the database" from "the scan read nothing" rather than to pin a shape. Pinning the
        // shape is AppRoleGrantMatrixTests' and RlsCoverageTests' job, and a census that reddened every
        // time a table was added would be answered by raising the number, which is how a guard becomes
        // a chore. The named relations below are the half that is specific.
        await Assert.That(scan.Relations.Count).IsGreaterThan(10);
        await Assert.That(scan.Columns).IsGreaterThan(50);
        await Assert.That(scan.RowsExamined).IsGreaterThan(0L);
        foreach (string relation in NarrativeRelations)
        {
            await Assert.That(scan.Relations).Contains(relation);
        }

        // The sharpest of the four, and the one that makes the other three more than arithmetic: every
        // narrative column really is holding an envelope right now, read back over the same connection
        // the scan ran on. A seeding step that 400'd would otherwise leave the scan looking at empty
        // tables and reporting NFR-013 met over a database with nothing in it.
        IReadOnlyList<string> absent = await NarrativeColumnsWithNoEnvelopeAsync(app);
        await Assert.That(string.Join(", ", absent)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// The control for the binary half: a name written through the shipped route in plaintext is named
    /// by the scan, and a text-only search over the same database sees nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A legal request, which is the whole reason it is the right control.</b> The server cannot tell
    /// a sealed envelope from a plaintext one — it holds no key — so all it judges is the version byte
    /// and the length band. A body carrying <c>0x01</c> followed by the marker's own UTF-8 bytes
    /// satisfies both, answers 201, and lands a row whose <c>name</c> column is readable by anybody with
    /// the app role's password. That is precisely the defect NFR-013 is about, and it is reachable from
    /// a browser today.
    /// </para>
    /// <para>
    /// <b>Both scans run over one database in one test, and that is what makes this a statement about
    /// the probe rather than two statements about two databases.</b> The byte search names
    /// <c>payees.name</c>; the text search — the naive implementation, kept here only so it can be shown
    /// to be wrong — names nothing at all, because <c>name::text</c> renders the same bytes as
    /// <c>\x014e4152…</c> and the marker is not a substring of its own hex.
    /// </para>
    /// <para>
    /// <c>payees.name_key</c> is the innocent neighbour on the same row: it is a digest over the marker
    /// and carries none of its bytes, so a probe that reported every column it looked at would name it
    /// too.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Scan_ReportsAPlaintextNameWrittenThroughARealRoute()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        ApiFactory.SignedInClient session = await host.Factory.CreateSignedInClientAsync();
        string marker = MarkerFor("payees", "name");

        HttpResponseMessage created = await session.Client.PostAsJsonAsync("/api/payees", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = Base64UrlText.Encode(PlaintextWearingAnEnvelopesClothing(marker)),
            nameKey = SealedNarrative.EncodedIndex(marker),
        });
        created.EnsureSuccessStatusCode();

        // Act — the same scan the census runs, twice, differing only in how a binary column is read.
        await using NpgsqlConnection app =
            await OpenAppSessionAsync(host, session.UserId, session.BudgetId);
        NarrativeScan asBytes = await ScanAsync(app, readsBinaryColumnsAsBytes: true);
        NarrativeScan asTextOnly = await ScanAsync(app, readsBinaryColumnsAsBytes: false);

        // Assert — the offence is named, with the field whose words it is.
        await Assert.That(asBytes.Offenders).Contains(Offence("payees", "name", marker));

        // The neighbour is not, so this cannot be passing because the probe reports everything.
        await Assert.That(asBytes.Offenders).DoesNotContain(Offence("payees", "name_key", marker));

        // And the hex trap, proven rather than described: the naive scan is green over the same row.
        await Assert.That(string.Join(Environment.NewLine, asTextOnly.Offenders)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// The control for the text half: a readable value in a <c>text</c> column is named by the scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Over a real relation rather than a throwaway one, deliberately.</b> A probe table would prove
    /// the predicate can fire and would say nothing about whether the scan reaches the relations that
    /// exist — and reaching them is half of what the census claims. <c>users.email</c> is the shipped
    /// text column an app-role session can read (<c>user_isolation</c> keys it to the same setting the
    /// session carries), it is not narrative, and nothing in the product would ever put a category's
    /// words there. Which is the point: the scan does not know that, and must report it anyway.
    /// </para>
    /// <para>
    /// Written on the <b>elevated</b> connection because no route rewrites an address, and this control
    /// is about the probe rather than about a write path. <c>users.id</c> is the innocent neighbour on
    /// the same row.
    /// </para>
    /// <para>
    /// <b>The column is <c>users.email</c> for a second reason, and it is the sharper of the two.</b> It
    /// is the one column left in the schema carrying the <c>case_insensitive</c> collation — the other
    /// four went when their columns were sealed — and PostgreSQL will not run a substring search under a
    /// nondeterministic collation at all. <see cref="Predicate" /> forces <c>C</c> to remove that, and
    /// this control is what holds the fix: without the <c>collate</c>, the leak below is either an
    /// unscannable relation or, worse and measured, a silent <c>false</c>. The marker is deliberately
    /// <em>shorter</em> than the value it is hidden in, which is the case that answered <c>false</c>
    /// rather than raising.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Scan_ReportsAPlaintextValueInATextColumn()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        ApiFactory.SignedInClient session = await host.Factory.CreateSignedInClientAsync();
        string marker = MarkerFor("categories", "description");

        await using (NpgsqlConnection admin = new(host.ConnectionString))
        {
            await admin.OpenAsync();
            await using NpgsqlCommand leak = new(
                "update users set email = @email where id = @id", admin);
            leak.Parameters.AddWithValue("email", $"{marker}@example.com");
            leak.Parameters.AddWithValue("id", session.UserId);
            await leak.ExecuteNonQueryAsync();
        }

        // Act
        await using NpgsqlConnection app =
            await OpenAppSessionAsync(host, session.UserId, session.BudgetId);
        NarrativeScan scan = await ScanAsync(app, readsBinaryColumnsAsBytes: true);

        // Assert
        await Assert.That(scan.Offenders).Contains(Offence("users", "email", marker));
        await Assert.That(scan.Offenders).DoesNotContain(Offence("users", "id", marker));
    }

    /// <summary>
    /// The control for the rest of the third branch: a readable value in a column that is neither
    /// <c>text</c> nor <c>bytea</c> is named by the scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two controls above leave a hole this one closes.</b> One of them puts its leak in a
    /// <c>bytea</c> column and the other in a <c>varchar</c> one, so between them they exercise the
    /// first arm of <see cref="Predicate" /> and the <em>text-shaped</em> corner of the third. The
    /// third arm's actual claim is much wider — it is the arm that catches <c>uuid</c>, <c>jsonb</c>,
    /// enums, numerics, timestamps and arrays, because PostgreSQL renders every one of them into a
    /// text form on the way through <c>::text</c>. Nothing proved it fires there. Measured: a fourth
    /// arm reading
    /// <c>if (column.TypeName is not ("text" or "varchar" or "bpchar" or "bytea")) return "false";</c>
    /// passes both controls above and the whole census, and reddens only this one. It would have
    /// turned an audit table's <c>jsonb</c> payload into a column the census reported as looked at
    /// and clean.
    /// </para>
    /// <para>
    /// <b>Over a relation this test creates, and that is forced rather than convenient.</b> The
    /// sibling control argues for a real column and is right to: reaching the relations that exist is
    /// half of what the census claims, and it has two controls making that half. But the shipped
    /// schema carries no column of a third-branch type that can hold a marker at all — measured off
    /// the baseline migration, the non-text types in it are <c>uuid</c>, <c>timestamptz</c>,
    /// <c>date</c>, <c>numeric</c>, <c>integer</c> and <c>bigint</c>, and none of them can be made to
    /// render any of the eight markers. So the choice is between a relation nobody ships and no
    /// control, and a synthetic relation is the honest one. It is also the shape the class remarks
    /// name as the leak this census exists for — an audit trail or a denormalised projection that
    /// arrives later — which is why it is <c>jsonb</c> and <c>text[]</c> rather than an invented type.
    /// </para>
    /// <para>
    /// <b><c>tags</c> carries the array claim, which had no control either.</b> <c>ScanAsync</c>'s
    /// remarks assert that an array of a text-ish type is covered by the third arm because PostgreSQL
    /// renders its elements into the array's text form. That is now measured rather than reasoned.
    /// The <c>bytea[]</c> arm is still unexercised — no relation in this schema has one, and it cannot
    /// be given a control that is about anything but itself.
    /// </para>
    /// <para>
    /// Per-test databases are what make this safe: <c>PostgresTestHost</c> clones one database per
    /// test out of the template, so a table created in <c>public</c> here is invisible to every other
    /// test in the assembly, including the census. <c>id</c> is the innocent neighbour on the same
    /// row, and the empty-<c>Unscannable</c> assertion is doing two jobs — it proves the <c>GRANT</c>
    /// landed, and it is the negative control for the column-skipping report added beside the probe
    /// builder.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Scan_ReportsAPlaintextValueInAColumnOfNeitherTextNorBinaryType()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        ApiFactory.SignedInClient session = await host.Factory.CreateSignedInClientAsync();
        string payloadMarker = MarkerFor("category_groups", "description");
        string tagMarker = MarkerFor("transactions", "description");

        await using (NpgsqlConnection admin = new(host.ConnectionString))
        {
            await admin.OpenAsync();

            await using (NpgsqlCommand create = new(
                $"""
                create table public.{ProbeRelation} (
                    id uuid not null primary key,
                    payload jsonb not null,
                    tags text[] not null)
                """,
                admin))
            {
                await create.ExecuteNonQueryAsync();
            }

            await using (NpgsqlCommand leak = new(
                $"""
                insert into public.{ProbeRelation} (id, payload, tags)
                values (gen_random_uuid(), jsonb_build_object('note', @payload), array[@tag]::text[])
                """,
                admin))
            {
                leak.Parameters.AddWithValue("payload", payloadMarker);
                leak.Parameters.AddWithValue("tag", tagMarker);
                await leak.ExecuteNonQueryAsync();
            }

            // Without this the relation lands in Unscannable rather than in Offenders, and the test
            // would be red for a reason that is about the grant instead of about the predicate.
            await using NpgsqlCommand grant = new(
                $"grant select on public.{ProbeRelation} to {DatabaseProvisioning.AppRoleName}", admin);
            await grant.ExecuteNonQueryAsync();
        }

        // Act
        await using NpgsqlConnection app =
            await OpenAppSessionAsync(host, session.UserId, session.BudgetId);
        NarrativeScan scan = await ScanAsync(app, readsBinaryColumnsAsBytes: true);

        // Assert — both offences are named, each by the field whose words are sitting in it.
        await Assert.That(scan.Offenders).Contains(Offence(ProbeRelation, "payload", payloadMarker));
        await Assert.That(scan.Offenders).Contains(Offence(ProbeRelation, "tags", tagMarker));

        // The neighbour is not, so this cannot be passing because the probe reports everything.
        await Assert.That(scan.Offenders).DoesNotContain(Offence(ProbeRelation, "id", payloadMarker));

        // And every column of the new relation was reached: an ungranted table or a column the probe
        // builder declined would both show up here rather than shrinking the census in silence.
        await Assert.That(string.Join(Environment.NewLine, scan.Unscannable)).IsEqualTo(string.Empty);
        await Assert.That(scan.Relations).Contains(ProbeRelation);
    }

    /// <summary>
    /// The relation <see cref="Scan_ReportsAPlaintextValueInAColumnOfNeitherTextNorBinaryType" />
    /// creates. Named so it cannot be mistaken for something the product ships.
    /// </summary>
    private const string ProbeRelation = "narrative_scan_probe";

    /// <summary>One narrative column and the plaintext this run puts behind it.</summary>
    private sealed record NarrativeMarker(string Table, string Column, string Marker)
    {
        public string Qualified => $"{Table}.{Column}";
    }

    /// <summary>What one pass over the database found, and what it looked at while finding it.</summary>
    /// <remarks>
    /// The three members beside <see cref="Offenders" /> are not diagnostics. An empty offender list is
    /// what both a clean database and a broken query produce, so the assertions that separate those two
    /// cases are made over these — see the class remarks.
    /// </remarks>
    private sealed record NarrativeScan(
        IReadOnlyList<string> Offenders,
        IReadOnlyList<string> Relations,
        int Columns,
        long RowsExamined,
        IReadOnlyList<string> Unscannable);

    /// <summary>One column of one relation, as the catalog spells it.</summary>
    private sealed record CatalogColumn(string Relation, string Column, string TypeName);

    /// <summary>
    /// Every relation in <c>public</c> and every column it carries, with the column's type, read on the
    /// application role's own connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The relation kinds and the schema confinement come from
    /// <see cref="RowLevelSecurityCoverage.RowBearingRelationInPublicPredicate" /> rather than being
    /// spelled here, for the reason <c>KeyMaterialSecrecyTests</c> and <c>DataMinimizationSchemaTests</c>
    /// both give: a copy that misses a widening reports green over exactly the relation kinds the
    /// widening was for. A materialized view holding a decrypted projection is discovered by this
    /// predicate and would be skipped by <c>relkind = 'r'</c>.
    /// </para>
    /// <para>
    /// <b>Read on the app connection rather than the superuser one, and that is the criterion's own
    /// word.</b> The catalogs are world-readable, so the two answer identically — but the requirement is
    /// about what a holder of the app role's credentials can reach, and a census that borrowed the
    /// superuser to decide what to look at would be describing a reach nobody has.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<CatalogColumn>> ReadColumnsAsync(NpgsqlConnection connection)
    {
        const string sql =
            $"""
            select c.relname::text, a.attname::text, t.typname::text
            from pg_attribute a
            join pg_class c on c.oid = a.attrelid
            join pg_namespace n on n.oid = c.relnamespace
            join pg_type t on t.oid = a.atttypid
            where {RowLevelSecurityCoverage.RowBearingRelationInPublicPredicate}
              and a.attnum > 0
              and not a.attisdropped
            order by c.relname, a.attname
            """;

        await using NpgsqlCommand command = new(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<CatalogColumn> columns = [];
        while (await reader.ReadAsync())
        {
            columns.Add(new CatalogColumn(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return columns;
    }

    /// <summary>
    /// Looks for every marker in every column of every relation, on the connection it is handed.
    /// </summary>
    /// <param name="connection">The app-role session the criterion names.</param>
    /// <param name="readsBinaryColumnsAsBytes">
    /// Whether a <c>bytea</c> column is searched as bytes. <see langword="false" /> is the naive
    /// implementation — every column cast to text — and exists only so
    /// <see cref="Scan_ReportsAPlaintextNameWrittenThroughARealRoute" /> can show it green over a row
    /// the real one reports. Nothing else may call it that way.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>One statement per relation, not one per column.</b> A hundred columns times eight markers is
    /// eight hundred predicates; as aggregates over one scan of each table that is seventeen round trips
    /// rather than eight hundred, and every marker keeps its own counter, so attribution survives the
    /// batching.
    /// </para>
    /// <para>
    /// <b>Three predicates, because there are three ways a column can hold bytes.</b> A <c>bytea</c> is
    /// searched with <c>position(convert_to(marker,'UTF8') in col)</c> — the hex trap the class remarks
    /// describe. An array of <c>bytea</c> is unnested and each element searched the same way: an array of
    /// envelopes holds a leak exactly as one envelope does, and no argument about the scalar covers it.
    /// Everything else is cast to text, which covers <c>text</c>, <c>varchar</c>, <c>uuid</c>, enums,
    /// numerics, timestamps, JSON and arrays of any of them, because PostgreSQL renders an array's
    /// elements into its text form.
    /// </para>
    /// <para>
    /// <b>The text leg folds case and the byte leg does not, and the asymmetry is deliberate.</b> A
    /// leaked value re-cased on the way into a text column is still that value; a byte search cannot
    /// fold anything without deciding an encoding for bytes that may not be text at all. The markers are
    /// lower-case throughout, so the fold only ever widens what the text leg can see.
    /// </para>
    /// <para>
    /// A relation the connection cannot read is reported rather than skipped. Skipping is how a census
    /// shrinks in silence, and the caller asserts the reported set is empty.
    /// </para>
    /// </remarks>
    private static async Task<NarrativeScan> ScanAsync(
        NpgsqlConnection connection,
        bool readsBinaryColumnsAsBytes)
    {
        IReadOnlyList<CatalogColumn> catalog = await ReadColumnsAsync(connection);
        List<string> offenders = [];
        List<string> relations = [];
        List<string> unscannable = [];
        long rowsExamined = 0;
        int columnsExamined = 0;

        foreach (IGrouping<string, CatalogColumn> relation in
                 catalog.GroupBy(column => column.Relation, StringComparer.Ordinal))
        {
            CatalogColumn[] columns = [.. relation];
            (string sql, (CatalogColumn Column, NarrativeMarker Marker)[] probes) =
                BuildRelationProbe(relation.Key, columns, readsBinaryColumnsAsBytes);

            relations.Add(relation.Key);

            // Counted off the probes rather than off the catalog listing, and the difference is the
            // whole value of the number. The floor asserted over it is a claim about what the scan
            // LOOKED AT; incremented from columns.Length it would report ninety-five examined columns
            // while a probe builder that had stopped emitting a predicate for some type searched none
            // of them — the same arithmetic whether or not anything was ever asked about the column.
            HashSet<string> probed = new(probes.Select(probe => probe.Column.Column), StringComparer.Ordinal);
            columnsExamined += probed.Count;

            // And a catalog column that got no probe is REPORTED, for the same reason the unreadable
            // relation below is. The floor is deliberately loose — dropping a dozen columns out of
            // ninety-five leaves it green — so counting honestly is not on its own enough to make a
            // skipped column visible. This is derived from the catalog rather than authored, so a
            // future arm of BuildRelationProbe that declines a column is caught without anybody
            // remembering to say so. The caller asserts this set is empty.
            foreach (CatalogColumn skipped in columns.Where(column => !probed.Contains(column.Column)))
            {
                unscannable.Add($"{skipped.Relation}.{skipped.Column} — no probe was built for this column");
            }

            await using NpgsqlCommand command = new(sql, connection);
            for (int index = 0; index < Markers.Length; index++)
            {
                command.Parameters.AddWithValue($"m{index}", Markers[index].Marker);
            }

            try
            {
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    // This branch cannot fire against the probe as BuildRelationProbe builds it: the
                    // statement is a bare aggregate with no GROUP BY, so PostgreSQL answers exactly
                    // one row even over an empty relation. It is kept as a belt against a future probe
                    // shape — a GROUP BY, a LIMIT, a set-returning arm — because the alternative is
                    // reading GetInt64 off a reader that is not on a row, which throws somewhere less
                    // legible. Measured on postgres:17.10 over an empty table, an empty view and an
                    // empty materialized view — the row-bearing kinds the discovery predicate admits
                    // that could plausibly be empty: one row every time. Do not read a green run as
                    // this line having been exercised. The catch below is uncovered for the same kind
                    // of reason — nothing here makes a relation unreadable — and both are belts.
                    unscannable.Add($"{relation.Key} — the probe returned no row");
                    continue;
                }

                rowsExamined += reader.GetInt64(0);
                for (int index = 0; index < probes.Length; index++)
                {
                    if (reader.GetInt64(index + 1) > 0)
                    {
                        offenders.Add(Offence(
                            probes[index].Column.Relation,
                            probes[index].Column.Column,
                            probes[index].Marker.Marker));
                    }
                }
            }
            catch (PostgresException exception)
            {
                unscannable.Add($"{relation.Key} — {exception.SqlState}: {exception.MessageText}");
            }
        }

        offenders.Sort(StringComparer.Ordinal);

        return new NarrativeScan(offenders, relations, columnsExamined, rowsExamined, unscannable);
    }

    /// <summary>
    /// One aggregate query over one relation: how many rows it holds, and one counter per
    /// (column, marker) pair.
    /// </summary>
    private static (string Sql, (CatalogColumn Column, NarrativeMarker Marker)[] Probes) BuildRelationProbe(
        string relation,
        IReadOnlyList<CatalogColumn> columns,
        bool readsBinaryColumnsAsBytes)
    {
        List<(CatalogColumn, NarrativeMarker)> probes = [];
        StringBuilder sql = new("select count(*)::bigint");

        foreach (CatalogColumn column in columns)
        {
            for (int index = 0; index < Markers.Length; index++)
            {
                sql.Append(", count(*) filter (where ")
                    .Append(Predicate(column, $"@m{index}", readsBinaryColumnsAsBytes))
                    .Append(")::bigint");
                probes.Add((column, Markers[index]));
            }
        }

        sql.Append(" from public.").Append(Quote(relation));

        return (sql.ToString(), [.. probes]);
    }

    /// <summary>
    /// Whether one column of one row holds <paramref name="marker" />. See
    /// <see cref="ScanAsync" /> for why there are three arms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>collate "C"</c> on the text leg is load-bearing and was measured, not added for tidiness.</b>
    /// PostgreSQL refuses a substring search under a <em>nondeterministic</em> collation — <c>0A000
    /// nondeterministic collations are not supported for substring searches</c> — and
    /// <c>users.email</c> is the one column in this schema still carrying <c>case_insensitive</c>. Every
    /// other candidate left when its column was sealed, so this is a one-column trap with a
    /// one-relation blast radius, and it is exactly the shape that shrinks a census in silence.
    /// </para>
    /// <para>
    /// <b>It is worse than a refusal, because the refusal is data-dependent.</b> Measured on
    /// postgres:17: the same predicate over the same column raised <c>0A000</c> when the value was
    /// longer than the marker and answered <c>false</c> when it was shorter — PostgreSQL never reaches
    /// the collation check on a needle that cannot fit. So the first version of this file scanned
    /// <c>users</c> "successfully" against every marker, reported no offender and no unscannable
    /// relation, and would have gone on doing that for as long as no seeded email happened to be long
    /// enough. Forcing the comparison into <c>C</c> removes the branch rather than catching it.
    /// </para>
    /// <para>
    /// The collation is applied <em>inside</em> <c>lower</c> rather than to its result, so the case fold
    /// is performed under a deterministic collation too. On ASCII markers the two spellings agree; the
    /// inner one is chosen because it leaves no operator on this line reading a nondeterministic
    /// collation at all.
    /// </para>
    /// </remarks>
    private static string Predicate(CatalogColumn column, string marker, bool readsBinaryColumnsAsBytes)
    {
        string quoted = Quote(column.Column);

        if (readsBinaryColumnsAsBytes && column.TypeName is "bytea")
        {
            return $"position(convert_to({marker}::text, 'UTF8') in {quoted}) > 0";
        }

        if (readsBinaryColumnsAsBytes && column.TypeName is "_bytea")
        {
            return $"coalesce((select bool_or(position(convert_to({marker}::text, 'UTF8') in element) > 0) "
                + $"from unnest({quoted}) as element), false)";
        }

        return $"position(lower({marker}::text) in lower({quoted}::text collate \"C\")) > 0";
    }

    /// <summary>
    /// The eight narrative columns that are <b>not</b> holding an envelope on this connection right now.
    /// </summary>
    /// <remarks>
    /// The floor is <see cref="CiphertextEnvelope.MinimumLength" /> rather than one byte, because a
    /// column holding fewer bytes than the framing needs is not an envelope whatever else it is — and a
    /// column holding NULL is counted as absent by the same predicate, which is what a seeding step that
    /// quietly failed leaves behind.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> NarrativeColumnsWithNoEnvelopeAsync(
        NpgsqlConnection connection)
    {
        List<string> absent = [];

        foreach (NarrativeMarker marker in Markers)
        {
            string sql =
                $"select count(*)::bigint from public.{Quote(marker.Table)} "
                + $"where octet_length({Quote(marker.Column)}) >= @floor";
            await using NpgsqlCommand command = new(sql, connection);
            command.Parameters.AddWithValue("floor", CiphertextEnvelope.MinimumLength);

            if ((long)(await command.ExecuteScalarAsync())! == 0L)
            {
                absent.Add($"{marker.Qualified} holds no envelope");
            }
        }

        return absent;
    }

    /// <summary>
    /// Writes one row into every table carrying a narrative column, through the shipped routes.
    /// </summary>
    /// <remarks>
    /// Every response is checked, and checked here rather than by the caller, because a 400 on any one
    /// of these leaves the scan looking at a table with nothing in it — and the census would then report
    /// the requirement met over an empty database. <c>budgets</c> is absent because the route table
    /// offers no way to name a budget; its seeding is argued at the call site.
    /// </remarks>
    private static async Task SeedNarrativeRowsThroughTheApiAsync(HttpClient client)
    {
        var accountId = Guid.CreateVersion7();
        string accountName = MarkerFor("accounts", "name");
        (await client.PostAsJsonAsync("/api/accounts", new
        {
            id = accountId.ToString("D"),
            name = SealedNarrative.EncodedName(accountName),
            nameKey = SealedNarrative.EncodedIndex(accountName),
            type = "Checking",
            openingBalance = 100m,
            currencyCode = "USD",
        })).EnsureSuccessStatusCode();

        var payeeId = Guid.CreateVersion7();
        string payeeName = MarkerFor("payees", "name");
        (await client.PostAsJsonAsync("/api/payees", new
        {
            id = payeeId.ToString("D"),
            name = SealedNarrative.EncodedName(payeeName),
            nameKey = SealedNarrative.EncodedIndex(payeeName),
        })).EnsureSuccessStatusCode();

        var groupId = Guid.CreateVersion7();
        string groupName = MarkerFor("category_groups", "name");
        (await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = groupId.ToString("D"),
            name = SealedNarrative.EncodedName(groupName),
            nameKey = SealedNarrative.EncodedIndex(groupName),
            description =
                SealedNarrative.EncodedDescription(MarkerFor("category_groups", "description")),
        })).EnsureSuccessStatusCode();

        var categoryId = Guid.CreateVersion7();
        string categoryName = MarkerFor("categories", "name");
        (await client.PostAsJsonAsync("/api/categories", new
        {
            id = categoryId.ToString("D"),
            name = SealedNarrative.EncodedName(categoryName),
            nameKey = SealedNarrative.EncodedIndex(categoryName),
            description = SealedNarrative.EncodedDescription(MarkerFor("categories", "description")),
            categoryGroupId = groupId,
        })).EnsureSuccessStatusCode();

        HttpResponseMessage transaction = await client.PostAsJsonAsync("/api/transactions", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            amount = -42.50m,
            date = "2026-06-12",
            accountId,
            payeeId,
            categoryId,
            description =
                SealedNarrative.EncodedDescription(MarkerFor("transactions", "description")),
        });
        transaction.EnsureSuccessStatusCode();

        // Read back rather than trusting the 201, because every body above is built from the entity the
        // handler wrote and is therefore a picture of the request. One list read proves the rows landed.
        JsonNode listed = (await JsonNode.ParseAsync(await client.GetStreamAsync("/api/transactions")))!;
        if (listed["items"]!.AsArray().Count != 1)
        {
            throw new InvalidOperationException(
                "Seeding wrote no transaction; the census would have scanned an empty table.");
        }
    }

    /// <summary>
    /// A value the server accepts as a narrative field and a reader can read: the version byte the
    /// framing requires, then <paramref name="marker" /> in the clear, padded to the framing's floor.
    /// </summary>
    /// <remarks>
    /// <b>This is a legal request body and that is the point.</b> The server holds no key, so what it
    /// judges is a version byte and a length band — it cannot tell this from a real envelope, and never
    /// claimed to. Any client can send it today, which is what makes it the honest control for the
    /// binary half rather than a probe table nothing could produce.
    /// </remarks>
    private static byte[] PlaintextWearingAnEnvelopesClothing(string marker)
    {
        byte[] words = Encoding.UTF8.GetBytes(marker);
        byte[] envelope = new byte[Math.Max(CiphertextEnvelope.MinimumLength, 1 + words.Length)];
        envelope[0] = CiphertextEnvelope.Version;
        words.CopyTo(envelope, 1);

        return envelope;
    }

    /// <summary>Pairs of markers where one contains the other. See the census's Arrange.</summary>
    private static IReadOnlyList<string> OverlappingMarkers() =>
    [
        .. from left in Markers
           from right in Markers
           where !ReferenceEquals(left, right)
                 && right.Marker.Contains(left.Marker, StringComparison.Ordinal)
           select $"{left.Qualified}'s marker is inside {right.Qualified}'s",
    ];

    /// <summary>
    /// Narrative fields whose sealed value carries their own marker in the clear. Must be empty, or the
    /// census is red about its own fixture.
    /// </summary>
    private static IReadOnlyList<string> MarkersVisibleInTheirOwnEnvelopes() =>
    [
        .. from marker in Markers
           let sealedValue = marker.Column is "description"
               ? SealedNarrative.Description(marker.Marker)
               : SealedNarrative.Name(marker.Marker)
           where sealedValue.Envelope.Span.IndexOf(Encoding.UTF8.GetBytes(marker.Marker)) >= 0
           select $"{marker.Qualified}'s envelope carries its own label",
    ];

    private static string MarkerFor(string table, string column) =>
        Markers.Single(marker =>
            string.Equals(marker.Table, table, StringComparison.Ordinal)
            && string.Equals(marker.Column, column, StringComparison.Ordinal)).Marker;

    /// <summary>
    /// One line of the offender report. Built in one place so the census and both controls cannot
    /// disagree about the shape of a report a reviewer will grep for.
    /// </summary>
    private static string Offence(string relation, string column, string marker) =>
        $"{relation}.{column} holds the plaintext '{marker}'";

    /// <summary>
    /// A catalog identifier as SQL. Doubling the quote is what keeps a relation like
    /// <c>__EFMigrationsHistory</c> — mixed case, unquoted in the catalog — addressable at all.
    /// </summary>
    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>
    /// Opens the connection the criterion names: the least-privilege role with the signed-in user and
    /// the ambient budget on the session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Spelled here rather than borrowed from <c>RepositoryTestHost.OpenAppConnectionAsync</c></b>,
    /// because the two hosts are different objects and this census needs the one that carries the API —
    /// the rows have to be written through real routes. Everything that member's remarks argue holds
    /// unchanged: both settings always, <c>set_config(..., false)</c> rather than <c>SET LOCAL</c>
    /// because these statements travel in autocommit, and the value as text because no
    /// <c>set_config</c> overload takes a <c>uuid</c>.
    /// </para>
    /// <para>
    /// A session naming only one of the two is not a state production can produce, and a scan run in one
    /// would meet <c>''::uuid</c> in the first policed relation it touched and fail with <c>22P02</c>
    /// rather than reporting a smaller census — loud, which is the right failure but not one to rely on.
    /// </para>
    /// </remarks>
    private static async Task<NpgsqlConnection> OpenAppSessionAsync(
        PostgresTestHost host,
        Guid userId,
        Guid budgetId)
    {
        NpgsqlConnection connection = new(host.AppConnectionString);

        try
        {
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(
                "select set_config('app.current_user_id', @user, false), "
                + "set_config('app.current_budget_id', @budget, false)",
                connection);
            command.Parameters.AddWithValue("user", userId.ToString());
            command.Parameters.AddWithValue("budget", budgetId.ToString());
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return connection;
    }

    private static async Task<PostgresTestHost> StartApiHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();

        return host;
    }
}
