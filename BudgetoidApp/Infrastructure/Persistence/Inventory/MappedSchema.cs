using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Infrastructure.Persistence.Inventory;

/// <summary>One column the EF model maps, named by the table it sits on as well as by itself.</summary>
/// <remarks>
/// <para>
/// The table is the member that earns this type. Two readers of this enumerator flatten it straight
/// back out — the prohibited-column vocabulary's schema scan and the erasure-remnant vocabulary's —
/// because each asks a question about a <i>name</i>: an identifier spelling that would be a refusal
/// wherever it appeared. An inventory asks about a <i>column</i>, and a column is not
/// identified by its name. Two tables in this schema carry <c>name</c> and four carry
/// <c>created_at_utc</c>, so a list keyed on the bare column name is a list on which one written
/// argument silently covers several columns that were never argued about together.
/// </para>
/// <para>
/// <see cref="ClrType" /> is the type the model says the property holds, carried because the
/// classification built on this is a statement about what a column <i>contains</i> — a sealed
/// narrative field, an amount, an identifier — and the column name is the weakest available evidence
/// for that. It is the model-side type rather than the provider-side one deliberately: the provider
/// type of every sealed column is <c>byte[]</c>, which is precisely the distinction the next phase
/// needs to make and cannot make from bytes.
/// </para>
/// </remarks>
/// <param name="Table">The table name as the model maps it, which is how <c>pg_class</c> stores it.</param>
/// <param name="Column">The column name as the model maps it.</param>
/// <param name="ClrType">The type the model says the mapped property holds.</param>
public sealed record MappedColumn(string Table, string Column, Type ClrType)
{
    /// <summary>The pair as <c>table.column</c>, which is how the inventory names a column.</summary>
    /// <remarks>
    /// Computed from the two members rather than stored beside them, so it cannot disagree with them.
    /// A stored third member would let a value be constructed whose rendering names a column other
    /// than the one it carries, and every comparison downstream is made on this string.
    /// </remarks>
    public string Qualified => $"{Table}.{Column}";
}

/// <summary>One <c>CHECK</c> constraint the model declares, as its name and its predicate.</summary>
/// <remarks>
/// <para>
/// Both members, because the two answer different questions and only one of them is a fact about the
/// rule. <see cref="Sql" /> is the predicate the database will evaluate, which is what a gate compares
/// against a shape it expects. <see cref="Name" /> is what PostgreSQL reports on a violation and what
/// decides which of a column's checks fires first — alphabetically, not by declaration order — so it
/// is the member a failure message needs in order to name what is missing.
/// </para>
/// <para>
/// A record rather than the EF <c>ICheckConstraint</c> it is read from, so that
/// <see cref="StoredColumn" /> is a value a caller can build by hand. A type carrying live EF metadata
/// would make every negative control over a reader of it need a whole model to state one defect in,
/// and a control nobody can write cheaply is a control nobody writes.
/// </para>
/// </remarks>
/// <param name="Name">The constraint name as the database will hold it.</param>
/// <param name="Sql">The predicate exactly as the configuration spelled it.</param>
public sealed record StoredCheckConstraint(string Name, string Sql);

/// <summary>
/// One mapped column described the way the <i>store</i> sees it: what type the provider is handed,
/// what type the column is declared as, and the check constraints standing over the table it sits on.
/// </summary>
/// <remarks>
/// <para>
/// <b>A second type rather than three more members on <see cref="MappedColumn" />, and the reason is
/// that <see cref="MappedColumn" /> has readers that must not grow these.</b> Two vocabulary scans
/// read <see cref="MappedSchema.ColumnsOf" /> and flatten the table straight back out, because each
/// asks about an identifier <i>spelling</i>; a third member set describing storage would be dead
/// weight on both, and dead members on a widely-read record are how a type stops meaning one thing.
/// The heavier reason is <see cref="MappedColumn.ClrType" />, whose remarks argue for the
/// <i>model-side</i> type on purpose: the classification built on that record is a statement about
/// what a column contains, and the provider type of every sealed column is <c>byte[]</c>, which
/// erases exactly the distinction it needs. This record wants the opposite — it exists to ask whether
/// the store really does see bytes — so folding the two would put two contradictory answers about one
/// property behind two members of one value, where a reader picks whichever they reach first.
/// </para>
/// <para>
/// <b><see cref="TableCheckConstraints" /> is the table's and says so in its name.</b> Naming it "the
/// column's" would be the trap this whole phase exists to avoid: four tables carry a <c>name_key</c>
/// beside a <c>name</c>, and <c>CK_accounts_name_key_length</c> both starts with
/// <c>CK_accounts_name</c> and mentions <c>name</c> in its predicate, so every association rule
/// available at enumeration time — name prefix, SQL substring — hands the blind index's constraint to
/// the narrative column and reports a deleted <c>CK_accounts_name_length</c> as present. There is no
/// honest column-grained answer here, so this record does not pretend to one; associating a
/// constraint with a column is a judgement made against an expected shape, which is
/// <see cref="NarrativeEncryptionCoverage" />'s to make and is argued there.
/// </para>
/// </remarks>
/// <param name="Table">The table name as the model maps it, which is how <c>pg_class</c> stores it.</param>
/// <param name="Column">The column name as the model maps it.</param>
/// <param name="ClrType">
/// The type the model says the mapped property holds. <b>Nothing reads this, deliberately, and it is
/// kept rather than dropped.</b> No gate may assert on it: the narrative set is <i>derived</i> from
/// this type by <c>DataInventoryCoverageTests.Narrative_IsExactlyTheColumnsTypedForASealedValue</c>,
/// so a check over it would ask whether the columns chosen for being typed
/// <see cref="Domain.Security.NarrativeField" /> are typed that way, and could never fire. It stays
/// because this record's whole purpose is to set the model's answer beside the provider's for one
/// property — the contrast is what makes "the store really sees bytes" a claim rather than a
/// restatement — and because a fifth fact about a column belongs here rather than in a second record.
/// A reader must not take its presence as evidence that it is checked.
/// </param>
/// <param name="ProviderClrType">
/// The type the provider is actually handed for this property — the value converter's provider type
/// where there is a converter, the declared provider type where one was named, and otherwise
/// <paramref name="ClrType" /> itself.
/// </param>
/// <param name="StoreType">
/// The store type name the model states for this column, or <see langword="null" /> when it states
/// none.
/// </param>
/// <param name="TableCheckConstraints">
/// Every <c>CHECK</c> the model declares on <paramref name="Table" />, not a subset chosen for this
/// column.
/// </param>
public sealed record StoredColumn(
    string Table,
    string Column,
    Type ClrType,
    Type ProviderClrType,
    string? StoreType,
    IReadOnlyList<StoredCheckConstraint> TableCheckConstraints)
{
    /// <summary>The pair as <c>table.column</c>, which is how the inventory names a column.</summary>
    /// <remarks>
    /// Computed rather than stored, matching <see cref="MappedColumn.Qualified" /> and
    /// <see cref="ColumnClassificationEntry.Qualified" />, because a comparison against the inventory
    /// is made on this string and the three renderings have to be one rule.
    /// </remarks>
    public string Qualified => $"{Table}.{Column}";
}

/// <summary>
/// The schema as the EF model describes it: every property it maps, rendered as the table and column
/// the database holds it in.
/// </summary>
/// <remarks>
/// <para>
/// This is the one enumerator the data inventory is built on. It lives in production code rather
/// than in test support because a build gate cannot reference a test assembly — the same call
/// <c>ProhibitedColumnVocabulary</c> and <see cref="Provisioning.RowLevelSecurityCoverage" /> already
/// made, and for the same reason: the rule and the check that reads it have to be the same object,
/// or the copy nobody reads is the copy that rots.
/// </para>
/// <para>
/// <b>The design-time model, never <c>db.Model</c>.</b> The runtime model is read-optimized and
/// drops what only migrations consume, so a walk over it is a walk over a subset of the schema that
/// looks exactly like the whole of it. An inventory whose subject is quietly narrower than the schema
/// reports itself complete while leaving real columns unclassified, which is the one failure the
/// requirement exists to make impossible.
/// </para>
/// <para>
/// <b>Nothing here opens a connection.</b> The options carry a provider because building a relational
/// model needs one to decide table and column names at all; the connection string is a placeholder
/// and no query is ever issued through it. That is what lets the enumerator run in a unit test, in
/// the build, and inside a container-backed reconciliation without caring which of the three it is.
/// </para>
/// </remarks>
public static class MappedSchema
{
    /// <summary>
    /// The placeholder the design-time model is built against.
    /// </summary>
    /// <remarks>
    /// Never dialled: a relational model needs a provider to decide table and column names, and the
    /// provider needs a string, and that is the whole of what this is for. <b>The <c>Password=</c> it
    /// carries is inert and is not a credential</b> — it names no reachable host, no code path opens a
    /// connection from this string, and it grants nothing anywhere; a secret scanner will flag it, and
    /// this clause is the answer waiting for whoever chases that hit. It deliberately does not
    /// go through <see cref="DesignTimeDbContextFactory" />, which carries the same literal for the
    /// <c>dotnet ef</c> tooling. That factory is free to start sourcing its connection string from
    /// configuration — several do — at which point a build gate would depend on an environment
    /// variable in order to enumerate a model it never queries.
    /// </remarks>
    private const string UnusedConnectionString =
        "Host=localhost;Port=5432;Database=budgetoid;Username=postgres;Password=postgres";

    /// <summary>
    /// Builds the design-time model from the configurations, without touching a database.
    /// </summary>
    /// <remarks>
    /// The model outlives the context it was read through. EF caches it against the options, and it
    /// is a frozen metadata graph rather than a view over a live connection, so disposing the context
    /// here costs the caller nothing.
    /// </remarks>
    /// <returns>The model EF would generate migrations from.</returns>
    public static IModel DesignTimeModel()
    {
        DbContextOptions<BudgetoidDbContext> options =
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(UnusedConnectionString)
                .Options;

        using BudgetoidDbContext db = new(options);

        return db.GetService<IDesignTimeModel>().Model;
    }

    /// <summary>
    /// Every property the model maps to a table column, as the table and column pair it lands on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An entity type with no <see cref="StoreObjectIdentifier" /> of its own is skipped rather than
    /// guessed at: it maps to no table, so there is no table for its columns to be named by, and an
    /// entry naming a column on nothing is worse than an absent one — the reconciliation would report
    /// it as drift against a catalog that was never asked to hold it.
    /// </para>
    /// <para>
    /// <c>GetColumnName(table)</c> takes the store object rather than being read off the property,
    /// because the column name is a fact about the property <i>in a table</i>. The parameterless
    /// overload answers for the default table only, which is the same answer today and stops being so
    /// the first time anything is split across two.
    /// </para>
    /// </remarks>
    /// <param name="model">The model to walk, ordinarily <see cref="DesignTimeModel" />.</param>
    /// <returns>One entry per mapped column, in the model's own entity and property order.</returns>
    public static IReadOnlyList<MappedColumn> ColumnsOf(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        List<MappedColumn> columns = [];

        foreach (IEntityType entity in model.GetEntityTypes())
        {
            StoreObjectIdentifier? table = StoreObjectIdentifier.Create(entity, StoreObjectType.Table);
            string? tableName = entity.GetTableName();

            if (table is null || tableName is null)
            {
                continue;
            }

            foreach (IProperty property in entity.GetProperties())
            {
                string? column = property.GetColumnName(table.Value);

                if (column is not null)
                {
                    columns.Add(new MappedColumn(tableName, column, property.ClrType));
                }
            }
        }

        return columns;
    }

    /// <summary>
    /// Every mapped column again, this time carrying what the store sees: the provider type, the
    /// declared store type, and the check constraints standing over its table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A second walk rather than a richer <see cref="ColumnsOf" />, for the reason
    /// <see cref="StoredColumn" /> gives at its own declaration.</b> The two enumerators answer
    /// deliberately opposite questions about one property — what the model says it holds against what
    /// the provider is handed — and the sealed columns are exactly where those answers differ.
    /// </para>
    /// <para>
    /// <b>The effective provider type is read from three places in a fixed order, and no one of them
    /// is sufficient.</b> Measured against this model: <c>GetProviderClrType()</c> answers
    /// <see langword="null" /> for all eight narrative columns, because their converters are supplied
    /// as objects rather than through <c>HasConversion&lt;T&gt;()</c>, while
    /// <c>GetValueConverter()?.ProviderClrType</c> answers <c>byte[]</c> for every one of them; and
    /// <c>GetProviderClrType()</c> answers non-null for other columns, <c>accounts.type</c> among
    /// them. So the two accessors are complementary, and a reader taking either alone reports a
    /// narrative column as storing <see cref="Domain.Security.NarrativeField" /> — a defect invented by
    /// the misreading, since no configuration can put a column into that state: <i>removing</i> a
    /// narrative property's converter refuses the model build outright, so the state this misread
    /// imitates is one no schema reaches. The converter is consulted first
    /// because the converter is what actually runs when both answer. Falling through to
    /// <see cref="MappedColumn.ClrType" /> is <b>the ordinary case rather than the edge one</b> — 77 of
    /// the 110 mapped columns answer neither accessor, against 32 carrying a converter and exactly one
    /// naming a provider type — and it is not a guess dressed as an answer: a property with no
    /// converter and no declared provider type is handed to the provider as its own type, and that is
    /// the whole of the claim.
    /// </para>
    /// <para>
    /// <b>The store type is read through the store object, never through the parameterless
    /// overload.</b> The column name beside it already is, for the reason <see cref="ColumnsOf" />
    /// states: a fact about a property is a fact about that property <i>in a table</i>, and the two
    /// answers stop agreeing the first time anything is split across two tables.
    /// </para>
    /// <para>
    /// <b>The nullable arm of <see cref="StoredColumn.StoreType" /> is insurance with no known source
    /// in this schema, and the file should not imply otherwise.</b> Every one of the 110 mapped columns
    /// was measured to answer a store type, so nothing here has ever produced the
    /// <see langword="null" />; it has been exercised only by handing a reader a value built by hand.
    /// It is kept because the accessor's own signature admits it and because a gate meeting an absence
    /// should report it rather than compare against a string somebody invented — but it is a branch
    /// waiting for a case, not a case anybody has seen.
    /// </para>
    /// <para>
    /// The constraint list is built once per table and handed to every column on it. Shared because
    /// it is immutable and because it is the table's — copying it per column would suggest a
    /// per-column subset that this walk cannot honestly compute.
    /// </para>
    /// <para>
    /// <b>This walk's completeness rests on being held equal to <see cref="ColumnsOf" />, because its
    /// one consumer cannot notice a subset.</b>
    /// <see cref="NarrativeEncryptionCoverage.Compare" /> reads only the columns the inventory calls
    /// narrative, so a walk returning those eight and dropping the other hundred answers every
    /// question the FR-057 gate asks.
    /// <c>NarrativeEncryptionCoverageTests.StoredColumns_DescribeTheSameSchemaTheEnumeratorDoes</c>
    /// stands under it: the two walks' <see cref="StoredColumn.Qualified" /> sets compared in both
    /// directions, their counts held equal so a duplicated column cannot hide behind an equal set, and
    /// a non-emptiness floor asserted before either comparison, since two empty walks agree perfectly.
    /// </para>
    /// <para>
    /// <b>That case reaches every name and reaches <i>facts</i> on two columns.</b> It fixes the
    /// provider type, the store type and the constraint set of <c>accounts.name</c> and
    /// <c>transactions.amount</c> — a sealed column beside a <see langword="decimal" /> one, so neither
    /// answer can be standing in for the other — and says nothing about those three members on the
    /// remaining hundred and six. A walk handing <c>users.email</c> some other table's constraints
    /// agrees on
    /// every name, at the right count, and passes. Two columns are a floor, not a guarantee; the
    /// alternative is a second copy of the schema living in a test file.
    /// </para>
    /// <para>
    /// Two subset hazards are closed by construction rather than by that case: an entity mapping to no
    /// table is skipped rather than guessed at, for the reason <see cref="ColumnsOf" /> gives, and a
    /// narrative column lost by either route surfaces in <see cref="EncryptionCoverage.Unmapped" />.
    /// The design-time model is not a hazard here at all — <c>GetCheckConstraints</c> throws outright
    /// on the read-optimized one, naming <see cref="IDesignTimeModel" /> in the message, where
    /// <see cref="ColumnsOf" /> answers over it quietly with the same columns. Measured, both.
    /// </para>
    /// </remarks>
    /// <param name="model">The model to walk, ordinarily <see cref="DesignTimeModel" />.</param>
    /// <returns>One entry per mapped column, in the model's own entity and property order.</returns>
    public static IReadOnlyList<StoredColumn> StoredColumnsOf(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        List<StoredColumn> columns = [];

        foreach (IEntityType entity in model.GetEntityTypes())
        {
            StoreObjectIdentifier? table = StoreObjectIdentifier.Create(entity, StoreObjectType.Table);
            string? tableName = entity.GetTableName();

            if (table is null || tableName is null)
            {
                continue;
            }

            IReadOnlyList<StoredCheckConstraint> checks =
            [
                .. entity.GetCheckConstraints()
                    .Select(check => new StoredCheckConstraint(
                        check.Name ?? check.ModelName,
                        check.Sql)),
            ];

            foreach (IProperty property in entity.GetProperties())
            {
                string? column = property.GetColumnName(table.Value);

                if (column is not null)
                {
                    columns.Add(new StoredColumn(
                        tableName,
                        column,
                        property.ClrType,
                        ProviderTypeOf(property),
                        property.GetColumnType(table.Value),
                        checks));
                }
            }
        }

        return columns;
    }

    /// <summary>Every table the model maps, once each.</summary>
    /// <remarks>
    /// Derived from the same walk rather than read from <c>GetEntityTypes</c> directly, so a table
    /// this enumerator cannot reach columns on is a table it does not claim either. Two lists built
    /// from two walks can disagree about which tables exist, and the disagreement would surface as an
    /// unexplained gap in whatever compared them.
    /// </remarks>
    /// <param name="model">The model to walk, ordinarily <see cref="DesignTimeModel" />.</param>
    /// <returns>The distinct table names, in the order the model first reaches them.</returns>
    public static IReadOnlyList<string> TablesOf(IModel model) =>
    [
        .. ColumnsOf(model).Select(column => column.Table).Distinct(StringComparer.Ordinal),
    ];

    /// <summary>
    /// The type the provider is handed for <paramref name="property" />, from whichever of the three
    /// sources answers first.
    /// </summary>
    /// <remarks>
    /// The order is argued at <see cref="StoredColumnsOf" />, where it can be read beside the
    /// measurement that produced it.
    /// </remarks>
    private static Type ProviderTypeOf(IProperty property) =>
        property.GetValueConverter()?.ProviderClrType
        ?? property.GetProviderClrType()
        ?? property.ClrType;
}
