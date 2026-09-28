using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Infrastructure.Persistence.Inventory;
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
/// <b>Rows are located by their <c>id</c> value, never by where they sit in the document.</b> Every
/// inventory table whose <c>id</c> column is a <c>uuid</c> is read whole on the container superuser, and
/// each object in the document whose <c>id</c> is the canonical text of one of those rows' ids is that
/// table's row. So no table name, JSON path or member count appears in an assertion: a column added to
/// the inventory is checked without an edit here, and a collection moved to a different place in the
/// document is still found. A table with no <c>id</c>, or one that is not a <c>uuid</c>, cannot be
/// located; that is a defect only when the table carries a classified column.
/// </para>
/// <para>
/// <b>A column's member is its name in camelCase</b> — <see cref="WireName" /> — because that is what
/// <c>ConfigureHttpJsonOptions</c> in <c>Api</c> produces from the record members, and the records are
/// named after the columns. A member counts as nesting, rather than as a column, only when it is a
/// located row or a non-empty array of nothing but located rows; anything else is a key and is judged
/// against the columns.
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
/// The seeder is duplicated from <c>DataExportCompletenessTests.FurnishTwoOfEachAsync</c> rather than
/// shared, which is the local convention stated in the remarks on
/// <c>ErasureAtomicityTests.FurnishAccountAsync</c>. It returns
/// nothing here, because nothing here keys on the ids it wrote. Everything is read as
/// <see cref="JsonNode" /> and never as a typed record, for the reason that file gives.
/// </para>
/// <para>
/// No two classified instant or money columns may share a compared value, because a shared value
/// cannot show which column the export read. Ids and strings are left out: foreign keys and currency
/// codes repeat by design. Every classified column must be compared on a value, and every nullable one
/// also on a null.
/// </para>
/// <para>
/// What these tests do not reach, on purpose: where a row sits and in what order, since rows are found
/// by id; rows of another tenant, which other test classes own; the value of <c>schemaVersion</c>; an
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

    /// <summary>The column every located row is found by.</summary>
    private const string IdColumn = "id";

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
        Comparison unfilled = CompareToDatabase("before the fill", unfilledDocument, unfilledDatabase, userId, defects);
        Comparison filled = CompareToDatabase("after the fill", document, database, userId, defects);

        // Distinguishability: a value two columns share cannot tell which one the export read it from,
        // so a projection that wired one column to the other would still agree on every row.
        string[] distinguished = [.. filled.Values.Keys.Order(StringComparer.Ordinal)];
        for (int first = 0; first < distinguished.Length; first++)
        {
            for (int second = first + 1; second < distinguished.Length; second++)
            {
                foreach (object shared in filled.Values[distinguished[first]]
                             .Intersect(filled.Values[distinguished[second]]))
                {
                    defects.Add(
                        $"indistinguishable: {distinguished[first]} and {distinguished[second]} "
                        + $"both hold {RenderDistinguishable(shared)}");
                }
            }
        }

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
        IReadOnlyList<LocatedObject> located = Locate(document, database);

        foreach (LocatedObject found in located)
        {
            // An empty collection is nesting nothing can locate, so it cannot be judged either way; it
            // is named for what it is rather than as a column nobody declared.
            string[] keys = [];
            foreach ((string key, JsonNode? value) in found.Node)
            {
                if (value is JsonArray { Count: 0 })
                {
                    defects.Add($"empty collection: {found.Path}.{key} — seed a row so it can be located");
                }
                else if (!IsNesting(value, database))
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

            if (found.Rows.Count != 1)
            {
                defects.Add(found.Rows.Count == 0
                    ? $"unlocated: {found.Path} is no row of any inventory table"
                    : $"ambiguous: {found.Path} is a row of {string.Join(" and ", found.Rows.Select(row => row.Table))}");
                continue;
            }

            string table = found.Rows[0].Table;
            HashSet<string> owed =
            [
                .. inventory[table]
                    .Where(entry => entry.Classification is not ColumnClassification.Excluded)
                    .Select(entry => WireName(entry.Column)),
            ];
            foreach (string key in keys.Where(key => !owed.Contains(key)))
            {
                defects.Add($"{found.Path}.{key}: {WhyNotOwed(inventory[table], table, key)}");
            }
        }

        // Non-vacuity, first half: an exclusion was in reach. At least one excluded column sits on a
        // table the document carries rows of, so the key check above had something to refuse.
        HashSet<string> exportedTables =
        [
            .. located.Where(found => found.Rows.Count == 1).Select(found => found.Rows[0].Table),
        ];
        if (!DataInventory.Of(ColumnClassification.Excluded).Any(entry => exportedTables.Contains(entry.Table)))
        {
            defects.Add("non-vacuity: no excluded column sits on a table the document carries rows of");
        }

        // Second half: a table the export owes nothing of holds rows this account owns, and none of
        // them reached the document. Without a row there, leaving the table out is not a decision
        // anybody could see.
        string[] whollyExcluded =
        [
            .. inventory
                .Where(table => table.All(entry => entry.Classification is ColumnClassification.Excluded))
                .Select(table => table.Key)
                .Where(table => database.Tables[table].HasId),
        ];
        if (whollyExcluded.Sum(table => database.Tables[table].Rows.Count) == 0)
        {
            defects.Add(
                $"non-vacuity: no row exists in any wholly excluded table with a uuid id "
                + $"[{string.Join(", ", whollyExcluded)}]");
        }

        foreach (LocatedObject found in located.Where(found => found.Rows.Any(row => whollyExcluded.Contains(row.Table))))
        {
            defects.Add($"exported: {found.Path} is a row of a table the inventory wholly excludes");
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

    /// <summary>The keys the envelope carries beside its nesting, and the only ones it may.</summary>
    private static readonly string[] RootKeys = ["schemaVersion"];

    /// <summary>
    /// What one pass of <see cref="CompareToDatabase" /> met: the columns it compared on a value, the
    /// ones it compared on a null, and the distinguishable values each column held.
    /// </summary>
    private sealed record Comparison(
        IReadOnlySet<string> ComparedOnAValue,
        IReadOnlySet<string> ComparedOnANull,
        IReadOnlyDictionary<string, HashSet<object>> Values);

    /// <summary>
    /// Compares every classified column of every located row against its member, and each classified
    /// table's id set against the rows the document carries, adding what disagrees to
    /// <paramref name="defects" /> under <paramref name="pass" />.
    /// </summary>
    private static Comparison CompareToDatabase(
        string pass,
        JsonNode document,
        DatabaseSnapshot database,
        Guid userId,
        List<string> defects)
    {
        // One account in the database, so a table's whole id set is exactly what this account owns.
        // The table is the one holding the signed-in account's id rather than a name written here.
        if (!database.ById.TryGetValue(userId, out IReadOnlyList<DatabaseRow>? owners) || owners.Count != 1)
        {
            defects.Add($"{pass}: precondition: the account id {userId} is not exactly one row in the database");
        }
        else if (database.Tables[owners[0].Table].Rows.Count != 1)
        {
            defects.Add(
                $"{pass}: precondition: {owners[0].Table} holds {database.Tables[owners[0].Table].Rows.Count} rows, "
                + "not the one account this test signed in");
        }

        ILookup<string, ColumnClassificationEntry> classified = Classified().ToLookup(entry => entry.Table);
        IReadOnlyList<LocatedObject> located = Locate(document, database);

        foreach (IGrouping<string, ColumnClassificationEntry> table in classified)
        {
            if (!database.Tables.TryGetValue(table.Key, out DatabaseTable? rows) || !rows.HasId)
            {
                defects.Add($"{pass}: no uuid id: {table.Key} carries classified columns and cannot be located");
                continue;
            }

            foreach (ColumnClassificationEntry entry in table.Where(entry => !rows.ColumnTypes.ContainsKey(entry.Column)))
            {
                defects.Add($"{pass}: no column: {entry.Qualified} is in the inventory and not in the table");
            }

            // The ids the document carries for this table, against the ids the table holds. A located id
            // is a database id by construction, so what this can find is a row missing or repeated; an
            // object the document invented is unlocated, and that is the excluded-column test's to name.
            List<Guid> exported =
            [
                .. located.SelectMany(found => found.Rows)
                    .Where(row => row.Table == table.Key)
                    .Select(row => row.Id),
            ];
            foreach (Guid id in rows.Ids.Except(exported))
            {
                defects.Add($"{pass}: row not exported: {table.Key} {id}");
            }

            foreach (IGrouping<Guid, Guid> repeated in exported.GroupBy(id => id).Where(group => group.Count() > 1))
            {
                defects.Add($"{pass}: row exported {repeated.Count()} times: {table.Key} {repeated.Key}");
            }
        }

        HashSet<string> comparedOnAValue = new(StringComparer.Ordinal);
        HashSet<string> comparedOnANull = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<object>> comparedValues = new(StringComparer.Ordinal);
        foreach (LocatedObject found in located)
        {
            foreach (DatabaseRow row in found.Rows)
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

                    if (DistinguishableValue(cell) is { } value)
                    {
                        if (!comparedValues.TryGetValue(entry.Qualified, out HashSet<object>? values))
                        {
                            comparedValues[entry.Qualified] = values = [];
                        }

                        values.Add(value);
                    }
                }
            }
        }

        return new Comparison(comparedOnAValue, comparedOnANull, comparedValues);
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
    /// The value a cell contributes to the distinguishability check, or <see langword="null" /> when its
    /// type is not one the check covers.
    /// </summary>
    /// <remarks>
    /// Instants and money only. A <see cref="Guid" /> or a <see cref="string" /> is left out because
    /// foreign keys and currency codes repeat another column's value by design. Boxed
    /// <see cref="DateTime" /> equality is tick equality and boxed <see cref="decimal" /> equality ignores
    /// scale, which is the same agreement <see cref="Disagreement" /> applies.
    /// </remarks>
    private static object? DistinguishableValue(DatabaseCell cell) =>
        cell.Value switch
        {
            DateTime instant when cell.DataType == "timestamp with time zone" => instant,
            decimal amount => amount,
            _ => null,
        };

    private static string RenderDistinguishable(object value) =>
        value is DateTime instant
            ? instant.ToString("O", CultureInfo.InvariantCulture)
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

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

    /// <summary>One row of one table, keyed by its <c>id</c>.</summary>
    private sealed record DatabaseRow(string Table, Guid Id, IReadOnlyDictionary<string, DatabaseCell> Cells);

    /// <summary>
    /// Every row of one inventory table, and the columns <c>select *</c> returned for it with the type
    /// the server named for each.
    /// </summary>
    private sealed record DatabaseTable(
        string Name,
        IReadOnlyDictionary<string, string> ColumnTypes,
        IReadOnlyList<IReadOnlyDictionary<string, DatabaseCell>> Rows)
    {
        /// <summary>
        /// Whether the table's rows can be located: it has an <c>id</c> and that <c>id</c> is a
        /// <c>uuid</c>. Any other <c>id</c> is treated as none.
        /// </summary>
        public bool HasId => ColumnTypes.TryGetValue(IdColumn, out string? type) && type == "uuid";

        public IEnumerable<Guid> Ids => HasId ? Rows.Select(row => (Guid)row[IdColumn].Value!) : [];
    }

    /// <summary>
    /// Every inventory table, every locatable row indexed by its id, and every nullable column of the
    /// schema as <c>table.column</c>.
    /// </summary>
    private sealed record DatabaseSnapshot(
        IReadOnlyDictionary<string, DatabaseTable> Tables,
        IReadOnlyDictionary<Guid, IReadOnlyList<DatabaseRow>> ById,
        IReadOnlySet<string> Nullable);

    /// <summary>
    /// One object of the document, where it sits, and every row whose id it carries — none for the root
    /// and for an object nothing locates, more than one only if two tables share an id value.
    /// </summary>
    private sealed record LocatedObject(string Path, JsonObject Node, IReadOnlyList<DatabaseRow> Rows);

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
        Dictionary<Guid, List<DatabaseRow>> byId = [];

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

            bool locatable = columnTypes.TryGetValue(IdColumn, out string? idType) && idType == "uuid";
            List<IReadOnlyDictionary<string, DatabaseCell>> rows = [];
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

                rows.Add(cells);

                if (locatable)
                {
                    Guid key = (Guid)cells[IdColumn].Value!;
                    if (!byId.TryGetValue(key, out List<DatabaseRow>? sharing))
                    {
                        byId[key] = sharing = [];
                    }

                    sharing.Add(new DatabaseRow(table, key, cells));
                }
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

        return new DatabaseSnapshot(
            tables,
            byId.ToDictionary(pair => pair.Key, IReadOnlyList<DatabaseRow> (pair) => pair.Value),
            nullable);
    }

    /// <summary>Every object in the document, the root first, each with the rows its id names.</summary>
    private static IReadOnlyList<LocatedObject> Locate(JsonNode document, DatabaseSnapshot database)
    {
        List<LocatedObject> found = [];

        void Visit(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject item:
                    found.Add(new LocatedObject(path, item, RowsNamedBy(item, database)));
                    foreach ((string key, JsonNode? value) in item)
                    {
                        Visit(value, $"{path}.{key}");
                    }

                    break;
                case JsonArray items:
                    for (int index = 0; index < items.Count; index++)
                    {
                        Visit(items[index], $"{path}[{index}]");
                    }

                    break;
            }
        }

        Visit(document, "$");
        return found;
    }

    /// <summary>
    /// The rows whose id this object's <c>id</c> member carries in its one wire form — lowercase
    /// <c>D</c> — or none.
    /// </summary>
    /// <remarks>
    /// The text must be the canonical rendering of the id it parses to, so an id written in upper case
    /// or braces locates nothing and surfaces as an unlocated object and an unexported row.
    /// </remarks>
    private static IReadOnlyList<DatabaseRow> RowsNamedBy(JsonObject item, DatabaseSnapshot database) =>
        item.TryGetPropertyValue(WireName(IdColumn), out JsonNode? id)
        && id is JsonValue value
        && value.GetValueKind() == JsonValueKind.String
        && value.GetValue<string>() is { } text
        && Guid.TryParseExact(text, "D", out Guid key)
        && string.Equals(text, key.ToString("D"), StringComparison.Ordinal)
        && database.ById.TryGetValue(key, out IReadOnlyList<DatabaseRow>? rows)
            ? rows
            : [];

    /// <summary>
    /// Whether a member is nesting rather than a column: a located row, or a non-empty array of nothing
    /// but located rows.
    /// </summary>
    /// <remarks>
    /// The single-object arm exists because the root carries the account as one object rather than as an
    /// array of one. Anything short of this — an empty array, an array holding one unlocated object — is
    /// a key, and is judged against the columns like any other.
    /// </remarks>
    private static bool IsNesting(JsonNode? member, DatabaseSnapshot database) =>
        member switch
        {
            JsonObject item => RowsNamedBy(item, database).Count > 0,
            JsonArray { Count: > 0 } items => items.All(element =>
                element is JsonObject item && RowsNamedBy(item, database).Count > 0),
            _ => false,
        };

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
    /// nothing sets it — so it is written on the superuser. <c>USD</c> because the column is a foreign
    /// key into <c>currencies</c> and the seeded checking account already proves that row exists.
    /// </para>
    /// </remarks>
    private static async Task FillColumnsNoRouteWritesAsync(PostgresTestHost host, Guid userId, Guid budgetId)
    {
        LogCensusTraffic.Fill fill = await LogCensusTraffic.FillEmptyNarrativeColumnsAsync(host.ConnectionString);
        await Assert.That(fill.StillEmpty).IsEmpty();

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "update budgets set base_currency_code = 'USD' where id = @budget", connection);
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
