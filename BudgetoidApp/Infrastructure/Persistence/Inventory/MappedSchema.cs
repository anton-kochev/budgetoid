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
    /// provider needs a string, and that is the whole of what this is for. It deliberately does not
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
}
