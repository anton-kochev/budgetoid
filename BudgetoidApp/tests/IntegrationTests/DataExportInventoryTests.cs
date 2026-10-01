using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Infrastructure.Persistence.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// That the export carries every column the data inventory classifies narrative or arithmetic, and no
/// column it classifies excluded — checked against <see cref="DataInventory" /> and the live rows, never
/// against a list written here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rows are located by their primary key, never by where they sit in the document.</b> Every
/// inventory table is read whole on the container superuser, and each table's key comes from the EF
/// model (<see cref="ShapesOf" />). An object is a row of a table when one of its rows agrees with the
/// object on every key column, each judged by <see cref="Disagreement" />, so an uppercase or braced
/// uuid matches nothing. When several tables match, the ones with a column for every key the object
/// carries are kept; when several still remain, the one whose owed columns are exactly the object's
/// keys. What none of that settles is reported as unlocated or ambiguous (<see cref="Settle" />). A
/// budget matches <c>budgets</c> by its id and <c>factor_manifests</c> by its <c>userId</c>, and only
/// <c>budgets</c> has a column for every key. So the two tests over the live export carry no table
/// name, row path or member count in an assertion; the one literal is the root's
/// <c>schemaVersion</c>. A column added to the inventory is checked without an edit here, and a
/// collection moved to a different place in the document is still found.
/// </para>
/// <para>
/// <b>A column's member is its name in camelCase</b> — <see cref="WireName" />. The camelCase comes
/// from the <c>JsonSerializerDefaults.Web</c> defaults of the HTTP JSON options;
/// <c>ConfigureHttpJsonOptions</c> in <c>Api</c> only adds converters. The records are named after the
/// columns. A member counts as nesting, rather than as a column, only when it is an object some
/// table's key matches or a non-empty array of nothing but such objects; anything else is a key and
/// is judged against the columns.
/// </para>
/// <para>
/// <b>A value is compared in the one form the wire writes it in</b>, wherever that form is unique:
/// a uuid, a date, an instant, a string and a sealed column are compared as exact text, and an integer
/// as its exact digits, so a serializer writing a different form of the same value is caught. Money is
/// the exception and is compared as a number — see <see cref="Disagreement" />. The comparer's arms are
/// pinned by <see cref="Disagreement_AgreesWithTheOneWireFormOfEachTypeAndNothingElse" /> on values
/// written here, because the fixture reaches only the column types the schema has today.
/// </para>
/// <para>
/// The seeder is adapted from <c>DataExportCompletenessTests.FurnishTwoOfEachAsync</c> rather than
/// shared, which is the local convention stated in the remarks on
/// <c>ErasureAtomicityTests.FurnishAccountAsync</c>. It returns
/// nothing here, because nothing here keys on the ids it wrote. Everything is read as
/// <see cref="JsonNode" /> and never as a typed record, for the reason that file gives.
/// </para>
/// <para>
/// <b>No located row may leave two classified columns indistinguishable</b> — see
/// <see cref="Indistinguishable" />. In one table, two columns equal on every row are flagged, any
/// type. Across tables, a value on a table's only row is flagged when the other column holds it
/// anywhere, never for a uuid, since a foreign key holds its parent's id by design. Two tables of
/// several rows each are not compared: the export reads each table by itself. <b>A known gap:</b> a
/// projection hard-coding a literal that equals a fixture value still passes — writing
/// <c>BaseCurrencyCode = "GBP"</c> agrees with the fill. Every classified column must also be compared
/// on a value, and every nullable one on a null.
/// </para>
/// <para>
/// What these tests do not reach, on purpose: where a row sits and in what order, since rows are found
/// by key; rows of another tenant, which other test classes own; the value of <c>schemaVersion</c>; an
/// excluded value carried inside an owed member, which is left to the first test's exact value
/// compare; the scale money is written at, and a projection through <see cref="double" /> that
/// round-trips every fixture amount; and any table this fixture leaves empty. The export taken before
/// the fill is compared value for value but is spared the distinguishability and non-null guards,
/// because the registration seed gives the user and the budget one instant.
/// </para>
/// </remarks>
public sealed class DataExportInventoryTests
{
    private const string ExportPath = "/api/me/export";

    private const string Subject = "export-inventory-subject";

    /// <summary>
    /// Mixed case, so a projection that lowercases the address disagrees with the row. The domain's
    /// <c>Email.Create</c> only trims, so the row keeps this case.
    /// </summary>
    private const string MixedCaseEmail = "Export.Inventory@Example.COM";

    [Test]
    public async Task Export_CarriesEveryColumnTheInventoryClassifiesNarrativeOrArithmetic()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, Guid budgetId) =
            await host.Factory.CreateSignedInClientAsync(Subject, MixedCaseEmail);
        await FurnishTwoOfEachAsync(client);

        // Act — once while the columns no route writes still hold their null, and once after the fill.
        JsonNode unfilledDocument = await GetExportAsync(client);
        DatabaseSnapshot unfilledDatabase = await ReadDatabaseAsync(host);
        await FillColumnsNoRouteWritesAsync(host, userId, budgetId);
        JsonNode document = await GetExportAsync(client);
        DatabaseSnapshot database = await ReadDatabaseAsync(host);

        // Assert
        List<string> defects = [];
        IReadOnlyDictionary<string, TableShape> shapes = ShapesOf(MappedSchema.DesignTimeModel());
        Comparison unfilled = CompareToDatabase("before the fill", unfilledDocument, unfilledDatabase, shapes, userId, defects);
        Comparison filled = CompareToDatabase("after the fill", document, database, shapes, userId, defects);

        // Distinguishability: a value two columns share cannot tell which one the export read it from,
        // so a projection that wired one column to the other would still agree on every row.
        defects.AddRange(Indistinguishable(filled.LocatedRows, Classified().ToLookup(entry => entry.Table, StringComparer.Ordinal)));

        // Non-vacuity: a column only ever compared on a null agrees with a projection that dropped it,
        // so every classified column must have met a value on at least one row.
        foreach (ColumnClassificationEntry entry in Classified().Where(entry => !filled.ComparedOnAValue.Contains(entry.Qualified)))
        {
            defects.Add($"never compared on a value: {entry.Qualified}");
        }

        // And the other arm: a nullable column only ever compared on a value agrees with a projection
        // that coerced its null to something else, so each must have met a null on at least one row.
        foreach (ColumnClassificationEntry entry in Classified().Where(entry =>
                     database.Nullable.Contains(entry.Qualified)
                     && !unfilled.ComparedOnANull.Contains(entry.Qualified)
                     && !filled.ComparedOnANull.Contains(entry.Qualified)))
        {
            defects.Add($"never compared on a null: {entry.Qualified}");
        }

        await Assert.That(defects).IsEmpty();
    }

    [Test]
    public async Task Export_CarriesNoColumnTheInventoryClassifiesExcluded()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, Guid budgetId) =
            await host.Factory.CreateSignedInClientAsync(Subject, MixedCaseEmail);
        await FurnishTwoOfEachAsync(client);
        await FillColumnsNoRouteWritesAsync(host, userId, budgetId);

        // Act
        JsonNode document = await GetExportAsync(client);
        DatabaseSnapshot database = await ReadDatabaseAsync(host);

        // Assert
        List<string> defects = [];
        ILookup<string, ColumnClassificationEntry> inventory = DataInventory.Entries.ToLookup(entry => entry.Table);
        IReadOnlyDictionary<string, TableShape> shapes = ShapesOf(MappedSchema.DesignTimeModel());
        IReadOnlyList<LocatedObject> located = Locate(document, shapes, database);

        foreach (LocatedObject found in located)
        {
            // An empty collection is nesting nothing can locate, so it cannot be judged either way; it
            // is named for what it is rather than as a column nobody declared.
            string[] keys = [];
            foreach (string key in found.Keys)
            {
                if (found.Node[key] is JsonArray { Count: 0 })
                {
                    defects.Add($"empty collection: {found.Path}.{key} — seed a row so it can be located");
                }
                else
                {
                    keys = [.. keys, key];
                }
            }

            // The envelope is the one object that is nobody's row, and the one whose own keys are
            // written here.
            if (ReferenceEquals(found.Node, document))
            {
                if (!keys.ToHashSet(StringComparer.Ordinal).SetEquals(RootKeys))
                {
                    defects.Add(
                        $"root: carries [{string.Join(", ", keys)}] beside its nesting, "
                        + $"not exactly [{string.Join(", ", RootKeys)}]");
                }

                continue;
            }

            if (found.Row is null)
            {
                defects.Add(found.Candidates.Count == 0
                    ? $"unlocated: {found.Path} agrees with no inventory row on a whole primary key"
                    : $"{Describe(found, shapes)}: {found.Path}");
                continue;
            }

            string table = found.Row.Table;
            foreach (string key in keys.Where(key => !shapes[table].Owed.Contains(key)))
            {
                defects.Add($"{found.Path}.{key}: {WhyNotOwed(inventory[table], table, key)}");
            }
        }

        // Non-vacuity, first half: an exclusion was in reach. At least one excluded column sits on a
        // table the document carries rows of, so the key check above had something to refuse.
        HashSet<string> exportedTables = [.. located.Where(found => found.Row is not null).Select(found => found.Row!.Table)];
        if (!DataInventory.Of(ColumnClassification.Excluded).Any(entry => exportedTables.Contains(entry.Table)))
        {
            defects.Add("non-vacuity: no excluded column sits on a table the document carries rows of");
        }

        // Second half: a table the export owes nothing of holds rows this account owns, and none of
        // them reached the document. Without a row there, leaving the table out is not a decision
        // anybody could see.
        string[] whollyExcluded = [.. shapes.Values.Where(shape => shape.WhollyExcluded).Select(shape => shape.Name)];
        if (whollyExcluded.Sum(table => database.Tables[table].Rows.Count) == 0)
        {
            defects.Add(
                $"non-vacuity: no row exists in any wholly excluded table [{string.Join(", ", whollyExcluded)}]");
        }

        // Located to a wholly excluded table, or settled among nothing but such tables: either way the
        // export carried a row it owes nothing of.
        foreach (LocatedObject found in located.Where(found => IsLeak(found, shapes)))
        {
            defects.Add(
                $"exported: {found.Path} ({string.Join(", ", found.Candidates)}) "
                + "is a row of a table the inventory wholly excludes");
        }

        await Assert.That(defects).IsEmpty();
    }

    [Test]
    public async Task Disagreement_AgreesWithTheOneWireFormOfEachTypeAndNothingElse()
    {
        // Arrange
        Guid id = Guid.Parse("0199b2c4-7e5a-7c3d-9f10-2a3b4c5d6e7f");
        DateTime fractional = new DateTime(2026, 6, 26, 10, 15, 30, DateTimeKind.Utc).AddTicks(1_230_000);
        DateTime wholeSecond = new(2026, 6, 26, 10, 15, 30, DateTimeKind.Utc);
        byte[] bytes = [0x01, 0xfb, 0xff, 0x00];
        (DatabaseCell Cell, string Json, bool Agrees)[] cases =
        [
            (new(id, "uuid"), JsonSerializer.Serialize(id), true),
            (new(id, "uuid"), "\"0199b2c4-7e5a-7c3d-9f10-2a3b4c5d6e7f\"", true),
            (new(id, "uuid"), "\"0199B2C4-7E5A-7C3D-9F10-2A3B4C5D6E7F\"", false),
            (new(id, "uuid"), "\"{0199b2c4-7e5a-7c3d-9f10-2a3b4c5d6e7f}\"", false),
            (new(id, "uuid"), "\"0199b2c47e5a7c3d9f102a3b4c5d6e7f\"", false),
            (new("Checking", "text"), "\"Checking\"", true),
            (new("Checking", "text"), "\"checking\"", false),
            (new(-10.25m, "numeric"), "-10.25", true),
            (new(-10.25m, "numeric"), "-10.2500", true),
            (new(-10.25m, "numeric"), "-10.2", false),
            (new(-10.25m, "numeric"), "\"-10.25\"", false),
            (new(7, "integer"), "7", true),
            (new(7, "integer"), "7.0", false),
            (new(7, "integer"), "\"7\"", false),
            (new((short)7, "smallint"), "7", true),
            (new((short)7, "smallint"), "8", false),
            (new((short)7, "smallint"), "7.0", false),
            (new(9_007_199_254_740_993L, "bigint"), "9007199254740993", true),
            (new(9_007_199_254_740_993L, "bigint"), "9007199254740992", false),
            (new(9_007_199_254_740_993L, "bigint"), "\"9007199254740993\"", false),
            (new(true, "boolean"), "true", true),
            (new(true, "boolean"), "false", false),
            (new(false, "boolean"), "false", true),
            (new(true, "boolean"), "\"true\"", false),
            (new(true, "boolean"), "1", false),
            (new(new DateOnly(2026, 6, 26), "date"), JsonSerializer.Serialize(new DateOnly(2026, 6, 26)), true),
            (new(new DateOnly(2026, 6, 26), "date"), "\"2026-06-26T00:00:00\"", false),
            (new(fractional, "timestamp with time zone"), JsonSerializer.Serialize(fractional), true),
            (new(fractional, "timestamp with time zone"), "\"2026-06-26T10:15:30.123Z\"", true),
            (new(fractional, "timestamp with time zone"), "\"2026-06-26T10:15:30.1230000Z\"", false),
            (new(fractional, "timestamp with time zone"), "\"2026-06-26T10:15:30.123+00:00\"", false),
            (new(wholeSecond, "timestamp with time zone"), JsonSerializer.Serialize(wholeSecond), true),
            (new(wholeSecond, "timestamp with time zone"), "\"2026-06-26T10:15:30Z\"", true),
            (new(wholeSecond, "timestamp with time zone"), "\"2026-06-26T10:15:30.0000000Z\"", false),
            (new(bytes, "bytea"), JsonSerializer.Serialize(Base64UrlText.Encode(bytes)), true),
            (new(bytes, "bytea"), JsonSerializer.Serialize(Convert.ToBase64String(bytes)), false),
            (new(null, "text"), "null", true),
            (new(null, "text"), "\"\"", false),
        ];

        // Act
        List<string> defects = [];
        foreach ((DatabaseCell cell, string json, bool agrees) in cases)
        {
            string? disagreement = Disagreement(cell, JsonNode.Parse(json));
            if ((disagreement is null) != agrees)
            {
                defects.Add(agrees
                    ? $"refused the wire form: {cell.DataType} {json}: {disagreement}"
                    : $"accepted a non-wire form: {cell.DataType} {json}");
            }
        }

        // Assert
        await Assert.That(defects).IsEmpty();
    }

    [Test]
    public async Task Locate_FindsEachObjectByItsWholePrimaryKeyAndOneTable()
    {
        // Arrange
        Guid owner = Guid.Parse("0199b2c4-0000-7000-8000-000000000001");
        Guid otherOwner = Guid.Parse("0199b2c4-0000-7000-8000-000000000002");
        Guid firstLedger = Guid.Parse("0199b2c4-0000-7000-8000-000000000011");
        Guid secondLedger = Guid.Parse("0199b2c4-0000-7000-8000-000000000012");
        Guid entry = Guid.Parse("0199b2c4-0000-7000-8000-000000000021");
        Guid factor = Guid.Parse("0199b2c4-0000-7000-8000-000000000031");
        Guid otherFactor = Guid.Parse("0199b2c4-0000-7000-8000-000000000032");
        byte[] manifest = [0x01, 0xfb, 0xff];

        DatabaseRow ownerRow = HandBuiltRow("owners", ("id", owner));
        DatabaseRow firstLedgerRow = HandBuiltRow("ledgers", ("id", firstLedger), ("owner_id", owner));
        DatabaseRow secondLedgerRow = HandBuiltRow("ledgers", ("id", secondLedger), ("owner_id", owner));
        DatabaseRow entryRow = HandBuiltRow("entries", ("id", entry), ("ledger_id", firstLedger));
        DatabaseRow settingsRow = HandBuiltRow("ledger_settings", ("ledger_id", firstLedger), ("week_start", 1));
        DatabaseRow manifestRow = HandBuiltRow("owner_manifests", ("owner_id", owner), ("manifest", manifest));
        DatabaseRow rotationRow = HandBuiltRow("owner_rotations", ("owner_id", owner), ("epoch", 3));
        DatabaseRow sealRow = HandBuiltRow("pair_seals", ("owner_id", otherOwner), ("factor_id", factor));
        DatabaseRow twinARow = HandBuiltRow("twin_a", ("ledger_id", secondLedger), ("x", 5));
        DatabaseRow twinBRow = HandBuiltRow("twin_b", ("ledger_id", secondLedger), ("x", 5), ("y", 6));
        DatabaseSnapshot database = HandBuiltSnapshot(
            ownerRow, firstLedgerRow, secondLedgerRow, entryRow, settingsRow,
            manifestRow, rotationRow, sealRow, twinARow, twinBRow);

        // The tables a wrong answer would pick come first, so a locator that settles on the first
        // match settles on a wrong one.
        IReadOnlyDictionary<string, TableShape> shapes = new[]
        {
            HandBuiltShape("owner_manifests", ["owner_id"], ["owner_id", "manifest"], owed: []),
            HandBuiltShape("owner_rotations", ["owner_id"], ["owner_id", "epoch"], owed: []),
            HandBuiltShape("pair_seals", ["owner_id", "factor_id"], ["owner_id", "factor_id"], owed: []),
            HandBuiltShape("ledger_settings", ["ledger_id"], ["ledger_id", "week_start"], ["ledger_id", "week_start"]),
            HandBuiltShape("twin_a", ["ledger_id"], ["ledger_id", "x"], ["ledger_id", "x"]),
            HandBuiltShape("twin_b", ["ledger_id"], ["ledger_id", "x", "y"], ["ledger_id", "x", "y"]),
            HandBuiltShape("owners", ["id"], ["id"], ["id"]),
            HandBuiltShape("ledgers", ["id"], ["id", "owner_id"], ["id", "owner_id"]),
            HandBuiltShape("entries", ["id"], ["id", "ledger_id"], ["id", "ledger_id"]),
        }.ToDictionary(shape => shape.Name, StringComparer.Ordinal);

        (string Label, Func<JsonObject> Item, string Outcome, DatabaseRow? Row, bool Leak)[] cases =
        [
            ("ledger carrying ownerId",
                () => new JsonObject { ["id"] = Wire(firstLedger), ["ownerId"] = Wire(owner) },
                "ledgers", firstLedgerRow, false),
            ("entry carrying ledgerId",
                () => new JsonObject { ["id"] = Wire(entry), ["ledgerId"] = Wire(firstLedger) },
                "entries", entryRow, false),
            ("settings keyed on their ledger",
                () => new JsonObject { ["ledgerId"] = Wire(firstLedger), ["weekStart"] = 1 },
                "ledger_settings", settingsRow, false),
            ("twin carrying x",
                () => new JsonObject { ["ledgerId"] = Wire(secondLedger), ["x"] = 5 },
                "twin_a", twinARow, false),
            ("twin carrying x and y",
                () => new JsonObject { ["ledgerId"] = Wire(secondLedger), ["x"] = 5, ["y"] = 6 },
                "twin_b", twinBRow, false),
            ("twin key alone",
                () => new JsonObject { ["ledgerId"] = Wire(secondLedger) },
                "ambiguous between twin_a, twin_b; outside every one: []", null, false),
            ("manifest carrying its bytes",
                () => new JsonObject { ["ownerId"] = Wire(owner), ["manifest"] = Base64UrlText.Encode(manifest) },
                "owner_manifests", manifestRow, true),
            ("owner key alone",
                () => new JsonObject { ["ownerId"] = Wire(owner) },
                "ambiguous between owner_manifests, owner_rotations; outside every one: []", null, true),
            ("composite key with a wrong second half",
                () => new JsonObject { ["ownerId"] = Wire(otherOwner), ["factorId"] = Wire(otherFactor) },
                "unlocated", null, false),
            ("uppercase id",
                () => new JsonObject { ["id"] = Wire(owner).ToUpperInvariant() },
                "unlocated", null, false),
            ("ledger with a stray key",
                () => new JsonObject { ["id"] = Wire(firstLedger), ["ownerId"] = Wire(owner), ["stray"] = 1 },
                "ambiguous between ledgers, owner_manifests, owner_rotations; outside every one: [stray]",
                null, false),
            ("ledger nesting its entries",
                () => new JsonObject
                {
                    ["id"] = Wire(firstLedger),
                    ["ownerId"] = Wire(owner),
                    ["entries"] = new JsonArray(new JsonObject { ["id"] = Wire(entry), ["ledgerId"] = Wire(firstLedger) }),
                },
                "ledgers", firstLedgerRow, false),
        ];

        // Act
        List<string> defects = [];
        foreach ((string label, Func<JsonObject> item, string outcome, DatabaseRow? row, bool leak) in cases)
        {
            JsonObject document = new() { ["item"] = item() };
            LocatedObject found = Locate(document, shapes, database).Single(candidate => candidate.Path == "$.item");

            string actual = Describe(found, shapes);
            if (actual != outcome)
            {
                defects.Add($"{label}: expected {outcome}, located {actual}");
            }

            if (!ReferenceEquals(found.Row, row))
            {
                defects.Add($"{label}: located row {(found.Row is null ? "none" : RenderKey(found.Row, shapes))}");
            }

            if (IsLeak(found, shapes) != leak)
            {
                defects.Add($"{label}: {(leak ? "not reported" : "reported")} as a wholly excluded row");
            }
        }

        // Assert
        await Assert.That(defects).IsEmpty();
    }

    [Test]
    public async Task Indistinguishable_FlagsAPairOnlyWhenNoRowCanTellThemApart()
    {
        // Arrange
        Guid owner = Guid.Parse("0199b2c4-0000-7000-8000-000000000001");
        Guid firstLedger = Guid.Parse("0199b2c4-0000-7000-8000-000000000011");
        Guid secondLedger = Guid.Parse("0199b2c4-0000-7000-8000-000000000012");
        DateTime first = new(2026, 6, 26, 10, 15, 30, DateTimeKind.Utc);
        DateTime second = first.AddDays(1);
        DateTime third = first.AddDays(2);
        (string Label, Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, DatabaseCell>>> Rows, string[] Flagged)[] cases =
        [
            ("equal on every row",
                LocatedRows(("t", ["a", "b"], [[1, 1], [2, 2]])),
                ["t.a", "t.b"]),
            ("equal on some rows",
                LocatedRows(("t", ["a", "b"], [[1, 1], [2, 3]])),
                []),
            ("money 0 against opening balances holding one 0, both on several rows",
                LocatedRows(
                    ("accounts", ["opening_balance"], [[0m], [125.50m]]),
                    ("transactions", ["amount"], [[0m], [-10.25m]])),
                []),
            ("an instant shared between two tables of several rows",
                LocatedRows(
                    ("a", ["created_at_utc"], [[first], [second]]),
                    ("b", ["created_at_utc"], [[first], [third]])),
                []),
            ("a one-row string found in another table",
                LocatedRows(
                    ("budgets", ["base_currency_code"], [["USD"]]),
                    ("accounts", ["currency_code"], [["USD"], ["EUR"]])),
                ["budgets.base_currency_code", "accounts.currency_code"]),
            ("a one-row uuid carried as a foreign key",
                LocatedRows(
                    ("owners", ["id"], [[owner]]),
                    ("ledgers", ["id", "owner_id"], [[firstLedger, owner], [secondLedger, owner]])),
                []),
            ("null on both sides of a row is equal",
                LocatedRows(("t", ["a", "b"], [[null, null], ["x", "x"]])),
                ["t.a", "t.b"]),
            ("1.0 and 1 are one amount",
                LocatedRows(("t", ["a", "b"], [[1.0m, 1m], [2.50m, 2.5m]])),
                ["t.a", "t.b"]),
        ];

        // Act
        List<string> defects = [];
        foreach ((string label, Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, DatabaseCell>>> rows, string[] flagged) in cases)
        {
            ILookup<string, ColumnClassificationEntry> classified = rows
                .SelectMany(table => table.Value.SelectMany(row => row.Keys).Distinct()
                    .Select(column => ColumnClassificationEntry.Arithmetic(table.Key, column)))
                .ToLookup(entry => entry.Table, StringComparer.Ordinal);

            IReadOnlyList<string> found = Indistinguishable(rows, classified);

            bool expected = flagged.Length > 0
                ? found.Count == 1 && flagged.All(column => found[0].Contains(column, StringComparison.Ordinal))
                : found.Count == 0;
            if (!expected)
            {
                defects.Add($"{label}: expected [{string.Join(" and ", flagged)}], flagged [{string.Join(" | ", found)}]");
            }
        }

        // Assert
        await Assert.That(defects).IsEmpty();
    }

    /// <summary>The keys the envelope carries beside its nesting, and the only ones it may.</summary>
    private static readonly string[] RootKeys = ["schemaVersion"];

    /// <summary>
    /// What one pass of <see cref="CompareToDatabase" /> met: the columns it compared on a value, the
    /// ones it compared on a null, and the cells of every row it located, by table.
    /// </summary>
    private sealed record Comparison(
        IReadOnlySet<string> ComparedOnAValue,
        IReadOnlySet<string> ComparedOnANull,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, DatabaseCell>>> LocatedRows);

    /// <summary>
    /// Compares every classified column of every located row against its member, and each classified
    /// table's rows against the rows the document carries, adding what disagrees to
    /// <paramref name="defects" /> under <paramref name="pass" />.
    /// </summary>
    private static Comparison CompareToDatabase(
        string pass,
        JsonNode document,
        DatabaseSnapshot database,
        IReadOnlyDictionary<string, TableShape> shapes,
        Guid userId,
        List<string> defects)
    {
        // One account in the database, so a table's whole row set is exactly what this account owns.
        // The tables checked are every one holding a row whose whole key is the signed-in account's id,
        // rather than names written here.
        string[] accountTables =
        [
            .. shapes.Values
                .Where(shape => shape.PrimaryKey.Count == 1
                                && database.Tables.TryGetValue(shape.Name, out DatabaseTable? rows)
                                && rows.Rows.Any(row => row.Cells.TryGetValue(shape.PrimaryKey[0], out DatabaseCell? key)
                                                        && key.Value is Guid id
                                                        && id == userId))
                .Select(shape => shape.Name),
        ];
        if (accountTables.Length == 0)
        {
            defects.Add($"{pass}: precondition: the account id {userId} is no row's whole primary key");
        }

        foreach (string table in accountTables.Where(table => database.Tables[table].Rows.Count != 1))
        {
            defects.Add(
                $"{pass}: precondition: {table} holds {database.Tables[table].Rows.Count} rows, "
                + "not the one account this test signed in");
        }

        ILookup<string, ColumnClassificationEntry> classified = Classified().ToLookup(entry => entry.Table);
        IReadOnlyList<LocatedObject> located = Locate(document, shapes, database);

        foreach (IGrouping<string, ColumnClassificationEntry> table in classified)
        {
            if (!shapes.TryGetValue(table.Key, out TableShape? shape)
                || shape.PrimaryKey.Count == 0
                || !database.Tables.TryGetValue(table.Key, out DatabaseTable? rows))
            {
                defects.Add($"{pass}: no primary key: {table.Key} carries classified columns and cannot be located");
                continue;
            }

            foreach (ColumnClassificationEntry entry in table.Where(entry => !rows.ColumnTypes.ContainsKey(entry.Column)))
            {
                defects.Add($"{pass}: no column: {entry.Qualified} is in the inventory and not in the table");
            }

            // The rows the document carries for this table, against the rows the table holds. A located
            // row is a database row by construction, so what this can find is a row missing or repeated;
            // an object the document invented is unlocated, and that is the excluded-column test's to name.
            List<DatabaseRow> exported = [.. located.Where(found => found.Row?.Table == table.Key).Select(found => found.Row!)];
            foreach (DatabaseRow row in rows.Rows.Where(row => !exported.Contains(row, ReferenceEqualityComparer.Instance)))
            {
                defects.Add($"{pass}: row not exported: {RenderKey(row, shapes)}");
            }

            foreach (IGrouping<DatabaseRow, DatabaseRow> repeated in exported
                         .GroupBy<DatabaseRow, DatabaseRow>(row => row, ReferenceEqualityComparer.Instance)
                         .Where(group => group.Count() > 1))
            {
                defects.Add($"{pass}: row exported {repeated.Count()} times: {RenderKey(repeated.Key, shapes)}");
            }
        }

        HashSet<string> comparedOnAValue = new(StringComparer.Ordinal);
        HashSet<string> comparedOnANull = new(StringComparer.Ordinal);
        foreach (LocatedObject found in located)
        {
            if (found.Row is { } row)
            {
                foreach (ColumnClassificationEntry entry in classified[row.Table])
                {
                    string member = WireName(entry.Column);
                    if (!found.Node.ContainsKey(member))
                    {
                        defects.Add($"{pass}: missing: {entry.Qualified} as '{member}' on {found.Path}");
                        continue;
                    }

                    if (!row.Cells.TryGetValue(entry.Column, out DatabaseCell? cell))
                    {
                        // Already reported once per table as a column the inventory names and the
                        // table lacks.
                        continue;
                    }

                    if (Disagreement(cell, found.Node[member]) is { } disagreement)
                    {
                        defects.Add($"{pass}: value: {entry.Qualified} on {found.Path}: {disagreement}");
                    }

                    (cell.Value is null ? comparedOnANull : comparedOnAValue).Add(entry.Qualified);
                }
            }
        }

        Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, DatabaseCell>>> locatedRows = located
            .Where(found => found.Row is not null)
            .Select(found => found.Row!)
            .Distinct(ReferenceEqualityComparer.Instance)
            .Cast<DatabaseRow>()
            .GroupBy(row => row.Table, StringComparer.Ordinal)
            .ToDictionary(
                table => table.Key,
                IReadOnlyList<IReadOnlyDictionary<string, DatabaseCell>> (table) => [.. table.Select(row => row.Cells)],
                StringComparer.Ordinal);

        return new Comparison(comparedOnAValue, comparedOnANull, locatedRows);
    }

    /// <summary>Why a key on a row of <paramref name="table" /> is not one the export owes.</summary>
    private static string WhyNotOwed(IEnumerable<ColumnClassificationEntry> columns, string table, string key)
    {
        ColumnClassificationEntry[] entries = [.. columns];
        ColumnClassificationEntry? excluded = entries.FirstOrDefault(entry =>
            entry.Classification is ColumnClassification.Excluded && WireName(entry.Column) == key);

        if (entries.All(entry => entry.Classification is ColumnClassification.Excluded))
        {
            return excluded is null
                ? $"no classified column on {table}"
                : $"no classified column on {table} (excluded: {excluded.Qualified})";
        }

        return excluded is null ? $"no column: {table}.{key}" : $"excluded: {excluded.Qualified}";
    }

    /// <summary>The columns the export owes the person: every one classified narrative or arithmetic.</summary>
    private static IEnumerable<ColumnClassificationEntry> Classified() =>
        DataInventory.Entries.Where(entry =>
            entry.Classification is ColumnClassification.Narrative or ColumnClassification.Arithmetic);

    /// <summary>A column's member name on the wire: <c>base_currency_code</c> is <c>baseCurrencyCode</c>.</summary>
    private static string WireName(string column)
    {
        string[] words = column.Split('_');
        StringBuilder name = new(words[0]);
        foreach (string word in words.Skip(1).Where(word => word.Length > 0))
        {
            name.Append(char.ToUpperInvariant(word[0])).Append(word.AsSpan(1));
        }

        return name.ToString();
    }

    /// <summary>
    /// Why a member does not carry the value its column holds, or <see langword="null" /> when it does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comparison is picked by the CLR type Npgsql read the cell as, and a pair nothing below
    /// handles is a defect naming both sides — never a fallback to comparing rendered text, which would
    /// agree with a value of the wrong type that happened to print the same.
    /// </para>
    /// <para>
    /// Where the wire has one form for a value, the member's text must be that form exactly, because a
    /// lenient parse accepts what a strict reader of the saved file may refuse. A uuid is lowercase
    /// <c>D</c>; a string, which covers the enum columns, is itself; <c>date</c> comes back from Npgsql
    /// as a <see cref="DateOnly" /> (measured) and is <c>yyyy-MM-dd</c>; a <c>timestamptz</c> is a UTC
    /// <see cref="DateTime" /> written as <see cref="UtcWireForm" />; a sealed column is unpadded
    /// base64url — see <see cref="SealedDisagreement" />; an integer is its digits; a boolean is its
    /// literal. <see cref="Disagreement_AgreesWithTheOneWireFormOfEachTypeAndNothingElse" /> derives the
    /// uuid, date and instant forms from <see cref="JsonSerializer" /> itself.
    /// </para>
    /// <para>
    /// Money is the one exception, compared through <see cref="decimal" /> equality, which ignores
    /// scale: <c>-25</c> and <c>-25.0000</c> are one amount, and the scale a <c>numeric</c> column hands
    /// back is an artifact of the column, so there is no one text to hold the member to.
    /// </para>
    /// </remarks>
    private static string? Disagreement(DatabaseCell cell, JsonNode? member)
    {
        if (cell.Value is null)
        {
            return member is null ? null : $"database null, document {member.ToJsonString()}";
        }

        if (member is not JsonValue json)
        {
            return $"database {cell.DataType} {Render(cell.Value)}, document {member?.ToJsonString() ?? "null"}";
        }

        JsonValueKind kind = json.GetValueKind();
        if (cell.Value is byte[] sealedBytes)
        {
            return SealedDisagreement(cell.DataType, sealedBytes, json, kind);
        }

        bool? agrees = cell.Value switch
        {
            Guid expected => IsExactString(json, kind, expected.ToString("D")),
            string expected => IsExactString(json, kind, expected),
            DateOnly expected => IsExactString(
                json,
                kind,
                expected.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture)),
            DateTime expected when cell.DataType == "timestamp with time zone" =>
                expected.Kind == DateTimeKind.Utc && IsExactString(json, kind, UtcWireForm(expected)),
            decimal expected => kind == JsonValueKind.Number
                                && json.TryGetValue(out decimal actual)
                                && actual == expected,
            bool expected => kind == (expected ? JsonValueKind.True : JsonValueKind.False),
            short expected => IsExactNumber(json, kind, expected.ToString(CultureInfo.InvariantCulture)),
            int expected => IsExactNumber(json, kind, expected.ToString(CultureInfo.InvariantCulture)),
            long expected => IsExactNumber(json, kind, expected.ToString(CultureInfo.InvariantCulture)),
            _ => null,
        };

        return agrees switch
        {
            true => null,
            false => $"database {cell.DataType} {Render(cell.Value)}, document {json.ToJsonString()}",
            null => $"no comparison for database {cell.Value.GetType().Name} ({cell.DataType}) against a JSON {kind}",
        };
    }

    /// <summary>Whether the member is a JSON string whose text is exactly <paramref name="expected" />.</summary>
    private static bool IsExactString(JsonValue json, JsonValueKind kind, string expected) =>
        kind == JsonValueKind.String && string.Equals(json.GetValue<string>(), expected, StringComparison.Ordinal);

    /// <summary>
    /// Whether the member is a JSON number whose raw text is exactly <paramref name="digits" />, so
    /// <c>7.0</c> or <c>7e0</c> does not pass for <c>7</c>.
    /// </summary>
    private static bool IsExactNumber(JsonValue json, JsonValueKind kind, string digits) =>
        kind == JsonValueKind.Number && string.Equals(json.ToJsonString(), digits, StringComparison.Ordinal);

    /// <summary>
    /// The text <see cref="JsonSerializer" /> writes for a UTC <see cref="DateTime" />: round-trip form
    /// with the fraction's trailing zeros dropped, and the point with them when the fraction is zero.
    /// </summary>
    private static string UtcWireForm(DateTime instant) =>
        instant.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// A value for the distinguishability message: an instant to the tick, so two instants a message
    /// calls equal print as equal; anything else as <see cref="Render" /> writes it.
    /// </summary>
    private static string RenderValue(object value) =>
        value is DateTime instant ? instant.ToString("O", CultureInfo.InvariantCulture) : Render(value);

    /// <summary>A database value for a defect message; never used to decide agreement.</summary>
    private static string Render(object value) =>
        value is byte[] bytes
            ? Base64UrlText.Encode(bytes)
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// Why a sealed member is not the one canonical text of its column's bytes, or
    /// <see langword="null" /> when it is.
    /// </summary>
    /// <remarks>
    /// The text is compared, never decoded: a decoder accepts padding, and the client's strict decoder
    /// refuses it, so a file that decoded here could still be one the client cannot open.
    /// </remarks>
    private static string? SealedDisagreement(string dataType, byte[] expected, JsonValue json, JsonValueKind kind)
    {
        string canonical = Base64UrlText.Encode(expected);
        if (kind != JsonValueKind.String)
        {
            return $"database {dataType} \"{Trimmed(canonical)}\", document JSON {kind} {json.ToJsonString()}";
        }

        string actual = json.GetValue<string>();
        return string.Equals(actual, canonical, StringComparison.Ordinal)
            ? null
            : $"database {dataType} as unpadded base64url \"{Trimmed(canonical)}\" ({canonical.Length} chars), "
              + $"document \"{Trimmed(actual)}\" ({actual.Length} chars)";
    }

    /// <summary>
    /// A long text cut in the middle for a defect message, so both ends — where padding shows — stay.
    /// </summary>
    private static string Trimmed(string text) =>
        text.Length <= 32 ? text : $"{text[..12]}…{text[^12..]}";

    /// <summary>One cell as the superuser read it, with the type the server named for its column.</summary>
    private sealed record DatabaseCell(object? Value, string DataType);

    /// <summary>
    /// One row of one table. Its identity is the instance: a located object points at it, and two
    /// objects pointing at one instance are one row exported twice.
    /// </summary>
    private sealed record DatabaseRow(string Table, IReadOnlyDictionary<string, DatabaseCell> Cells);

    /// <summary>
    /// Every row of one inventory table, and the columns <c>select *</c> returned for it with the type
    /// the server named for each.
    /// </summary>
    private sealed record DatabaseTable(
        string Name,
        IReadOnlyDictionary<string, string> ColumnTypes,
        IReadOnlyList<DatabaseRow> Rows);

    /// <summary>
    /// What locating an object needs to know about one table: its primary key as column names, and the
    /// wire names of every column the inventory lists for it and of the ones the export owes.
    /// </summary>
    /// <remarks>Plain data, so a test can build one by hand.</remarks>
    private sealed record TableShape(
        string Name,
        IReadOnlyList<string> PrimaryKey,
        IReadOnlySet<string> Columns,
        IReadOnlySet<string> Owed)
    {
        /// <summary>Whether the export owes nothing of this table.</summary>
        public bool WhollyExcluded => Owed.Count == 0;
    }

    /// <summary>
    /// One object of the document and what locating it found.
    /// </summary>
    /// <param name="Path">Where the object sits, for defect text only.</param>
    /// <param name="Node">The object itself.</param>
    /// <param name="Keys">Its members that are not nesting, which are the ones judged as columns.</param>
    /// <param name="PrimaryKeyMatches">Every table one of whose rows agrees with it on the whole key.</param>
    /// <param name="Candidates">
    /// The tables left once the tie-breaks have run: one when the object is located, none when nothing
    /// matched, several when nothing could settle it.
    /// </param>
    /// <param name="Row">The row it is, when exactly one table is left.</param>
    private sealed record LocatedObject(
        string Path,
        JsonObject Node,
        IReadOnlyList<string> Keys,
        IReadOnlyList<string> PrimaryKeyMatches,
        IReadOnlyList<string> Candidates,
        DatabaseRow? Row);

    /// <summary>
    /// Each mapped table's primary key, read from the model, beside the columns the inventory lists for
    /// it.
    /// </summary>
    /// <remarks>
    /// The key's column names come through the table's <see cref="StoreObjectIdentifier" />, the same
    /// idiom <see cref="MappedSchema.ColumnsOf" /> uses, so a key column is named as the table holds it.
    /// </remarks>
    private static IReadOnlyDictionary<string, TableShape> ShapesOf(IModel model)
    {
        ILookup<string, ColumnClassificationEntry> inventory =
            DataInventory.Entries.ToLookup(entry => entry.Table, StringComparer.Ordinal);
        Dictionary<string, TableShape> shapes = new(StringComparer.Ordinal);

        foreach (IEntityType entity in model.GetEntityTypes())
        {
            StoreObjectIdentifier? table = StoreObjectIdentifier.Create(entity, StoreObjectType.Table);
            string? name = entity.GetTableName();
            if (table is null || name is null || shapes.ContainsKey(name))
            {
                continue;
            }

            string[] primaryKey =
            [
                .. (entity.FindPrimaryKey()?.Properties ?? [])
                    .Select(property => property.GetColumnName(table.Value))
                    .OfType<string>(),
            ];
            shapes[name] = new TableShape(
                name,
                primaryKey,
                inventory[name].Select(entry => WireName(entry.Column)).ToHashSet(StringComparer.Ordinal),
                inventory[name]
                    .Where(entry => entry.Classification is not ColumnClassification.Excluded)
                    .Select(entry => WireName(entry.Column))
                    .ToHashSet(StringComparer.Ordinal));
        }

        return shapes;
    }

    /// <summary>Every object in the document, each with what locating it found; the root is never located.</summary>
    /// <remarks>
    /// Children are visited before their parent, because whether a member is nesting is decided from
    /// its children: an object some table's key matches, or a non-empty array of nothing but such
    /// objects. Anything else — an empty array, an array holding one object nothing matches — is a key,
    /// and is judged against the columns like any other.
    /// </remarks>
    private static IReadOnlyList<LocatedObject> Locate(
        JsonNode document,
        IReadOnlyDictionary<string, TableShape> shapes,
        DatabaseSnapshot database)
    {
        List<LocatedObject> found = [];

        bool Matched(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject item:
                    List<string> keys = [];
                    foreach ((string key, JsonNode? value) in item)
                    {
                        if (!IsNesting(value, $"{path}.{key}"))
                        {
                            keys.Add(key);
                        }
                    }

                    LocatedObject located = ReferenceEquals(item, document)
                        ? new LocatedObject(path, item, keys, [], [], null)
                        : Settle(path, item, keys, shapes, database);
                    found.Add(located);
                    return located.PrimaryKeyMatches.Count > 0;
                case JsonArray items:
                    for (int index = 0; index < items.Count; index++)
                    {
                        Matched(items[index], $"{path}[{index}]");
                    }

                    return false;
                default:
                    return false;
            }
        }

        bool IsNesting(JsonNode? member, string path)
        {
            switch (member)
            {
                case JsonObject:
                    return Matched(member, path);
                case JsonArray items:
                    // Every element is visited, so one that is not a row cannot hide the rest from the walk.
                    bool[] matched = [.. items.Select((element, index) => element is JsonObject && Matched(element, $"{path}[{index}]"))];
                    return matched.Length > 0 && matched.All(match => match);
                default:
                    return false;
            }
        }

        Matched(document, "$");
        return found;
    }

    /// <summary>Which table an object is a row of, by the rule the class remarks state.</summary>
    private static LocatedObject Settle(
        string path,
        JsonObject item,
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, TableShape> shapes,
        DatabaseSnapshot database)
    {
        List<(TableShape Shape, DatabaseRow Row)> matches = [];
        foreach (TableShape shape in shapes.Values)
        {
            if (shape.PrimaryKey.Count == 0 || !database.Tables.TryGetValue(shape.Name, out DatabaseTable? table))
            {
                continue;
            }

            if (table.Rows.FirstOrDefault(row => AgreesOnPrimaryKey(row, shape.PrimaryKey, item)) is { } row)
            {
                matches.Add((shape, row));
            }
        }

        // Several tables match: keep the ones that have a column for every key the object carries.
        List<(TableShape Shape, DatabaseRow Row)> candidates = matches;
        if (candidates.Count > 1)
        {
            List<(TableShape Shape, DatabaseRow Row)> containing =
                [.. candidates.Where(match => keys.All(match.Shape.Columns.Contains))];
            if (containing.Count > 0)
            {
                candidates = containing;
            }
        }

        // Still several: keep the one whose owed columns are exactly the object's keys.
        if (candidates.Count > 1)
        {
            List<(TableShape Shape, DatabaseRow Row)> owedExactly =
                [.. candidates.Where(match => match.Shape.Owed.SetEquals(keys))];
            if (owedExactly.Count == 1)
            {
                candidates = owedExactly;
            }
        }

        return new LocatedObject(
            path,
            item,
            keys,
            [.. matches.Select(match => match.Shape.Name)],
            [.. candidates.Select(match => match.Shape.Name)],
            candidates.Count == 1 ? candidates[0].Row : null);
    }

    /// <summary>
    /// Whether the object carries every key column of the row, each in its one wire form.
    /// </summary>
    /// <remarks>
    /// Judged by <see cref="Disagreement" />, so an uppercase or braced uuid matches nothing and a
    /// <c>bytea</c> key goes through the base64url arm.
    /// </remarks>
    private static bool AgreesOnPrimaryKey(DatabaseRow row, IReadOnlyList<string> primaryKey, JsonObject item) =>
        primaryKey.All(column =>
            item.TryGetPropertyValue(WireName(column), out JsonNode? member)
            && row.Cells.TryGetValue(column, out DatabaseCell? cell)
            && cell.Value is not null
            && Disagreement(cell, member) is null);

    /// <summary>
    /// The table an object is a row of, or why it is none: <c>unlocated</c>, or <c>ambiguous</c> with the
    /// tables left and the keys no one of them has a column for.
    /// </summary>
    private static string Describe(LocatedObject found, IReadOnlyDictionary<string, TableShape> shapes)
    {
        if (found.Row is not null)
        {
            return found.Row.Table;
        }

        if (found.Candidates.Count == 0)
        {
            return "unlocated";
        }

        string[] outside = [.. found.Keys.Where(key => !found.Candidates.Any(table => shapes[table].Columns.Contains(key)))];
        return $"ambiguous between {string.Join(", ", found.Candidates.Order(StringComparer.Ordinal))}; "
               + $"outside every one: [{string.Join(", ", outside)}]";
    }

    /// <summary>
    /// Whether the object is a row the export owes nothing of: located to a wholly excluded table, or
    /// left among nothing but such tables.
    /// </summary>
    private static bool IsLeak(LocatedObject found, IReadOnlyDictionary<string, TableShape> shapes) =>
        found.Candidates.Count > 0 && found.Candidates.All(table => shapes[table].WhollyExcluded);

    /// <summary>A row for defect text: its table and its primary key tuple.</summary>
    private static string RenderKey(DatabaseRow row, IReadOnlyDictionary<string, TableShape> shapes) =>
        $"{row.Table} ({string.Join(", ", shapes[row.Table].PrimaryKey.Select(column =>
            $"{column}={(row.Cells[column].Value is { } value ? Render(value) : "null")}"))})";

    /// <summary>
    /// Every pair of classified columns no located row can tell apart, as a defect saying what to fix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A projection that wired one column to the other agrees with every row such a pair meets, so the
    /// value compare cannot see it. Two shapes are caught:
    /// </para>
    /// <para>
    /// <b>Same table, any type:</b> the two columns are equal on every located row, nulls included.
    /// Equal on some rows is not enough, because one row that differs is a row the wrong projection
    /// fails on.
    /// </para>
    /// <para>
    /// <b>Across tables, only from a table with one located row:</b> that row's non-null value appears
    /// anywhere in the other column. A one-row table is where a hard-wired read of another table's
    /// value would pass. A uuid is never compared across tables, because a foreign key holds its
    /// parent's id by design. Two tables of several rows each are not guarded: the export reads each
    /// table by itself, with no join that could put one table's value on another's row.
    /// </para>
    /// <para>
    /// Equality is the comparer's: bytes byte by byte, <see cref="decimal" /> blind to scale, and
    /// <see cref="DateTime" /> by ticks.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> Indistinguishable(
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, DatabaseCell>>> locatedRowsByTable,
        ILookup<string, ColumnClassificationEntry> classified)
    {
        ColumnClassificationEntry[] columns =
        [
            .. classified.SelectMany(table => table)
                .Where(entry => locatedRowsByTable.TryGetValue(entry.Table, out IReadOnlyList<IReadOnlyDictionary<string, DatabaseCell>>? rows)
                                && rows.Count > 0),
        ];

        List<string> flagged = [];
        for (int left = 0; left < columns.Length; left++)
        {
            for (int right = left + 1; right < columns.Length; right++)
            {
                ColumnClassificationEntry first = columns[left];
                ColumnClassificationEntry second = columns[right];

                if (first.Table == second.Table)
                {
                    if (locatedRowsByTable[first.Table].All(row => SameCell(row, first.Column, row, second.Column)))
                    {
                        flagged.Add(
                            $"indistinguishable: {first.Qualified} and {second.Qualified} are equal on every row "
                            + $"of {first.Table} — make one row differ");
                    }

                    continue;
                }

                foreach ((ColumnClassificationEntry source, ColumnClassificationEntry target) in new[] { (first, second), (second, first) })
                {
                    IReadOnlyList<IReadOnlyDictionary<string, DatabaseCell>> sourceRows = locatedRowsByTable[source.Table];
                    if (sourceRows is [var only]
                        && only.TryGetValue(source.Column, out DatabaseCell? cell)
                        && cell.Value is { } value and not Guid
                        && locatedRowsByTable[target.Table].Any(row => SameCell(only, source.Column, row, target.Column)))
                    {
                        flagged.Add(
                            $"indistinguishable: {source.Qualified} holds {RenderValue(value)} on the one row of "
                            + $"{source.Table}, and {target.Qualified} holds it too — give one of them another value");
                        break;
                    }
                }
            }
        }

        return flagged;
    }

    /// <summary>Whether two cells hold one value by the comparer's rules; a missing column holds none.</summary>
    private static bool SameCell(
        IReadOnlyDictionary<string, DatabaseCell> firstRow,
        string firstColumn,
        IReadOnlyDictionary<string, DatabaseCell> secondRow,
        string secondColumn) =>
        firstRow.TryGetValue(firstColumn, out DatabaseCell? first)
        && secondRow.TryGetValue(secondColumn, out DatabaseCell? second)
        && (first.Value, second.Value) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            (byte[] left, byte[] right) => left.AsSpan().SequenceEqual(right),
            (decimal left, decimal right) => left == right,
            (DateTime left, DateTime right) => left.Ticks == right.Ticks,
            var (left, right) => left.Equals(right),
        };

    private static string Wire(Guid id) => id.ToString("D");

    /// <summary>Hand-built located rows for <see cref="Indistinguishable" />, one table per tuple.</summary>
    private static Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, DatabaseCell>>> LocatedRows(
        params (string Table, string[] Columns, object?[][] Values)[] tables) =>
        tables.ToDictionary(
            table => table.Table,
            IReadOnlyList<IReadOnlyDictionary<string, DatabaseCell>> (table) =>
            [
                .. table.Values.Select(IReadOnlyDictionary<string, DatabaseCell> (values) =>
                    HandBuiltRow(table.Table, [.. table.Columns.Zip(values)]).Cells),
            ],
            StringComparer.Ordinal);

    private static DatabaseRow HandBuiltRow(string table, params (string Column, object? Value)[] cells) =>
        new(table, cells.ToDictionary(
            cell => cell.Column,
            cell => new DatabaseCell(cell.Value, cell.Value switch
            {
                Guid => "uuid",
                string => "text",
                int => "integer",
                decimal => "numeric",
                byte[] => "bytea",
                DateTime => "timestamp with time zone",
                _ => "unknown",
            }),
            StringComparer.Ordinal));

    private static DatabaseSnapshot HandBuiltSnapshot(params DatabaseRow[] rows) =>
        new(
            rows.GroupBy(row => row.Table, StringComparer.Ordinal).ToDictionary(
                table => table.Key,
                table => new DatabaseTable(
                    table.Key,
                    table.First().Cells.ToDictionary(cell => cell.Key, cell => cell.Value.DataType, StringComparer.Ordinal),
                    [.. table]),
                StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

    private static TableShape HandBuiltShape(
        string name,
        string[] primaryKey,
        string[] columns,
        string[] owed) =>
        new(
            name,
            primaryKey,
            columns.Select(WireName).ToHashSet(StringComparer.Ordinal),
            owed.Select(WireName).ToHashSet(StringComparer.Ordinal));

    /// <summary>
    /// Every inventory table, and every nullable column of the schema as <c>table.column</c>.
    /// </summary>
    private sealed record DatabaseSnapshot(
        IReadOnlyDictionary<string, DatabaseTable> Tables,
        IReadOnlySet<string> Nullable);

    /// <summary>
    /// Reads every table the inventory names with <c>select *</c> on the container superuser, and the
    /// nullable columns from <c>information_schema.columns</c>.
    /// </summary>
    /// <remarks>
    /// <c>select *</c> rather than a column list built from the inventory, so a column the inventory
    /// names and the table lacks is reported by name instead of stopping the read with <c>42703</c>.
    /// Superuser because <c>budget_isolation</c> is <c>FOR ALL</c> and this connection carries no ambient
    /// budget. The table name is quoted and interpolated because an identifier cannot be a parameter.
    /// </remarks>
    private static async Task<DatabaseSnapshot> ReadDatabaseAsync(PostgresTestHost host)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        Dictionary<string, DatabaseTable> tables = [];

        foreach (string table in DataInventory.Entries.Select(entry => entry.Table).Distinct(StringComparer.Ordinal))
        {
            await using NpgsqlCommand command = new(
                $"select * from public.{LogCensusTraffic.Quote(table)}", connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

            Dictionary<string, string> columnTypes = new(StringComparer.Ordinal);
            for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                columnTypes[reader.GetName(ordinal)] = reader.GetDataTypeName(ordinal);
            }

            List<DatabaseRow> rows = [];
            while (await reader.ReadAsync())
            {
                Dictionary<string, DatabaseCell> cells = new(StringComparer.Ordinal);
                for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    object value = reader.GetValue(ordinal);
                    cells[reader.GetName(ordinal)] = new DatabaseCell(
                        value is DBNull ? null : value,
                        reader.GetDataTypeName(ordinal));
                }

                rows.Add(new DatabaseRow(table, cells));
            }

            tables[table] = new DatabaseTable(table, columnTypes, rows);
        }

        HashSet<string> nullable = new(StringComparer.Ordinal);
        await using (NpgsqlCommand command = new(
                         "select table_name, column_name from information_schema.columns "
                         + "where table_schema = 'public' and is_nullable = 'YES'",
                         connection))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                nullable.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
            }
        }

        return new DatabaseSnapshot(tables, nullable);
    }

    /// <summary>
    /// Asks for the export and hands back the parsed body, refusing anything but a success.
    /// </summary>
    private static async Task<JsonNode> GetExportAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync(ExportPath);
        response.EnsureSuccessStatusCode();

        return (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))
            ?? throw new InvalidOperationException("The export answered an empty body.");
    }

    private const string CheckingName = "Checking";
    private const string SavingsName = "Savings";
    private const string ReserveName = "Dinar reserve";
    private const string EssentialsGroupName = "Essentials";
    private const string EssentialsGroupDescription = "Rent, food and the rest of the floor";
    private const string LifestyleGroupName = "Lifestyle";
    private const string LifestyleGroupDescription = "Everything that is a choice";
    private const string UnsortedGroupName = "Unsorted";
    private const string GroceriesCategoryName = "Groceries";
    private const string GroceriesCategoryDescription = "The weekly shop";
    private const string TransportCategoryName = "Transport";
    private const string TransportCategoryDescription = "Buses, trains and fuel";
    private const string LeisureCategoryName = "Leisure";
    private const string LeisureCategoryDescription = "Concerts and the cinema";
    private const string MiscellaneousCategoryName = "Miscellaneous";
    private const string CoffeeShopPayeeName = "Kaffeine";
    private const string TransitPayeeName = "City Transit";
    private const string CoffeeDescription = "Flat white";
    private const string BusPassDescription = "Monthly bus pass";
    private const string TransferFeeDescription = "Transfer fee";
    private const string CoffeeDate = "2026-06-26";
    private const string BusPassDate = "2026-07-01";
    private const string TransferFeeDate = "2026-07-03";
    private const string CashDate = "2026-07-04";

    /// <summary>
    /// Writes rows into every budget-owned table over HTTP — two or more of each — with every nullable
    /// column holding a value on some row and a null on another.
    /// </summary>
    /// <remarks>
    /// The third account is in KWD, whose minor unit is 3, so its opening balance and its transaction
    /// carry a third decimal: an export rounding money to cents disagrees with them, where it would
    /// agree with every two-decimal amount.
    /// </remarks>
    private static async Task FurnishTwoOfEachAsync(HttpClient client)
    {
        Guid checkingAccountId = await CreateAsync(client, "/api/accounts", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(CheckingName),
            nameKey = SealedNarrative.EncodedIndex(CheckingName),
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "USD",
        });
        Guid savingsAccountId = await CreateAsync(client, "/api/accounts", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(SavingsName),
            nameKey = SealedNarrative.EncodedIndex(SavingsName),
            type = "Savings",
            openingBalance = 125.50m,
            currencyCode = "EUR",
        });
        Guid reserveAccountId = await CreateAsync(client, "/api/accounts", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(ReserveName),
            nameKey = SealedNarrative.EncodedIndex(ReserveName),
            type = "Savings",
            openingBalance = 98765432.123m,
            currencyCode = "KWD",
        });

        Guid essentialsGroupId = await CreateAsync(client, "/api/category-groups", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(EssentialsGroupName),
            nameKey = SealedNarrative.EncodedIndex(EssentialsGroupName),
            description = SealedNarrative.EncodedDescription(EssentialsGroupDescription),
        });
        Guid lifestyleGroupId = await CreateAsync(client, "/api/category-groups", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(LifestyleGroupName),
            nameKey = SealedNarrative.EncodedIndex(LifestyleGroupName),
            description = SealedNarrative.EncodedDescription(LifestyleGroupDescription),
        });

        Guid groceriesCategoryId = await CreateAsync(client, "/api/categories", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(GroceriesCategoryName),
            nameKey = SealedNarrative.EncodedIndex(GroceriesCategoryName),
            description = SealedNarrative.EncodedDescription(GroceriesCategoryDescription),
            categoryGroupId = essentialsGroupId,
        });
        Guid transportCategoryId = await CreateAsync(client, "/api/categories", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(TransportCategoryName),
            nameKey = SealedNarrative.EncodedIndex(TransportCategoryName),
            description = SealedNarrative.EncodedDescription(TransportCategoryDescription),
            categoryGroupId = essentialsGroupId,
        });
        await CreateAsync(client, "/api/categories", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(LeisureCategoryName),
            nameKey = SealedNarrative.EncodedIndex(LeisureCategoryName),
            description = SealedNarrative.EncodedDescription(LeisureCategoryDescription),
            categoryGroupId = lifestyleGroupId,
        });

        // A group and a category with no description, so that column of each table is compared on a
        // null as well as on a value.
        Guid unsortedGroupId = await CreateAsync(client, "/api/category-groups", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(UnsortedGroupName),
            nameKey = SealedNarrative.EncodedIndex(UnsortedGroupName),
            description = (string?)null,
        });
        await CreateAsync(client, "/api/categories", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(MiscellaneousCategoryName),
            nameKey = SealedNarrative.EncodedIndex(MiscellaneousCategoryName),
            description = (string?)null,
            categoryGroupId = unsortedGroupId,
        });

        Guid coffeeShopPayeeId = await CreateAsync(client, "/api/payees", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(CoffeeShopPayeeName),
            nameKey = SealedNarrative.EncodedIndex(CoffeeShopPayeeName),
        });
        Guid transitPayeeId = await CreateAsync(client, "/api/payees", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(TransitPayeeName),
            nameKey = SealedNarrative.EncodedIndex(TransitPayeeName),
        });

        // These name a payee, a category and a description, so the three nullable columns of the table
        // are compared on a value.
        await CreateAsync(client, "/api/transactions", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            amount = -10.25m,
            date = CoffeeDate,
            accountId = checkingAccountId,
            description = SealedNarrative.EncodedDescription(CoffeeDescription),
            payeeId = coffeeShopPayeeId,
            categoryId = groceriesCategoryId,
        });
        await CreateAsync(client, "/api/transactions", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            amount = -25m,
            date = BusPassDate,
            accountId = savingsAccountId,
            description = SealedNarrative.EncodedDescription(BusPassDescription),
            payeeId = transitPayeeId,
            categoryId = transportCategoryId,
        });
        await CreateAsync(client, "/api/transactions", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            amount = -1234.567m,
            date = TransferFeeDate,
            accountId = reserveAccountId,
            description = SealedNarrative.EncodedDescription(TransferFeeDescription),
            payeeId = transitPayeeId,
            categoryId = transportCategoryId,
        });

        // And this one names none of the three, so each is compared on a null too.
        await CreateAsync(client, "/api/transactions", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            amount = -4.75m,
            date = CashDate,
            accountId = checkingAccountId,
            description = (string?)null,
            payeeId = (Guid?)null,
            categoryId = (Guid?)null,
        });
    }

    /// <summary>
    /// Gives a value to the two classified columns no route writes, so neither is compared only on a
    /// null, and moves the user's creation instant off the budget's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Narrative columns go through <see cref="LogCensusTraffic.FillEmptyNarrativeColumnsAsync" />,
    /// which finds them in the inventory; today that is <c>budgets.name</c>. Whatever it reports as still
    /// empty is a column on a table nothing wrote a row into, and it is refused here rather than left to
    /// surface as a non-vacuity line with no reason attached.
    /// </para>
    /// <para>
    /// <c>budgets.base_currency_code</c> has no write route at all — provisioning leaves it null and
    /// nothing sets it — so it is written on the superuser. <c>GBP</c> because the column is a foreign
    /// key into <c>currencies</c>, which seeds it, and no account carries it: a code an account also
    /// held would leave the budget's one row indistinguishable from that account's column.
    /// </para>
    /// </remarks>
    private static async Task FillColumnsNoRouteWritesAsync(PostgresTestHost host, Guid userId, Guid budgetId)
    {
        LogCensusTraffic.Fill fill = await LogCensusTraffic.FillEmptyNarrativeColumnsAsync(host.ConnectionString);
        await Assert.That(fill.StillEmpty).IsEmpty();

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "update budgets set base_currency_code = 'GBP' where id = @budget", connection);
        command.Parameters.AddWithValue("budget", budgetId);
        await Assert.That(await command.ExecuteNonQueryAsync()).IsEqualTo(1);

        // The registration seed writes one clock value to both the user and the budget, so an export
        // reading one column for the other would agree on every row. A day apart, the
        // distinguishability check in T1 can tell them apart.
        await using NpgsqlCommand nudge = new(
            "update users set created_at_utc = created_at_utc - interval '1 day' where id = @user", connection);
        nudge.Parameters.AddWithValue("user", userId);
        await Assert.That(await nudge.ExecuteNonQueryAsync()).IsEqualTo(1);
    }

    /// <summary>
    /// Posts <paramref name="body" /> and returns the id of the row it created, failing loudly on any
    /// status other than success.
    /// </summary>
    private static async Task<Guid> CreateAsync(HttpClient client, string path, object body)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

        return json["id"]!.GetValue<Guid>();
    }

    /// <summary>
    /// A host whose factory leaves the application's own authentication standing, because every request
    /// below authenticates from a session cookie.
    /// </summary>
    private static async Task<PostgresTestHost> StartSignedInHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }
}
