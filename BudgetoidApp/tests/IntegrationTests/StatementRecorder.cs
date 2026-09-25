using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace IntegrationTests;

/// <summary>
/// Records the text of every command a <see cref="Microsoft.EntityFrameworkCore.DbContext" /> sends,
/// so a test can assert on the statement PostgreSQL was handed rather than on what EF's change
/// tracker says it believes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The wire and not the tracker, deliberately.</b> The same claims could be written against
/// <c>ChangeTracker</c> or <c>Entry(…).Property(…).IsModified</c>, and they would be assertions about
/// an EF API. What a test can observe about a change-tracking rule without leaving the product's own
/// terms is which columns an <c>UPDATE</c> names and whether a statement is sent at all — both of
/// which arrive here, in the text.
/// </para>
/// <para>
/// All three execution paths are overridden because which one EF takes is its own business: a write
/// batch that reads nothing back goes out as a non-query, one carrying <c>RETURNING</c> goes out as a
/// reader, and a test written to one of them would go quiet the day a provider or a version chose the
/// other. Both the sync and async halves are here for the same reason.
/// </para>
/// <para>
/// The collection is concurrent because interceptors are shared by every command a context issues and
/// nothing here promises they arrive on one thread. It records reads as well as writes; a caller that
/// wants only the writes filters for them, which keeps this type free of any opinion about what a test
/// is looking for.
/// </para>
/// </remarks>
public sealed class StatementRecorder : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _statements = new();

    /// <summary>
    /// Every statement sent so far, in the order the interceptor saw them.
    /// </summary>
    public IReadOnlyList<string> Statements => [.. _statements];

    /// <summary>
    /// The statements that write to <paramref name="table" />: the <c>UPDATE</c>s, <c>INSERT</c>s and
    /// <c>DELETE</c>s naming it, with every read dropped.
    /// </summary>
    /// <param name="table">The unquoted table name, as the configuration spells it.</param>
    /// <remarks>
    /// A prefix test on the trimmed statement rather than a search anywhere in the text, so a
    /// <c>SELECT</c> that happens to mention the word <c>update</c> in a column name cannot be read as
    /// a write.
    /// </remarks>
    public IReadOnlyList<string> WritesTo(string table) =>
    [
        .. _statements.Where(statement =>
            {
                string trimmed = statement.TrimStart();
                return (trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                        || trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                        || trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
                    && statement.Contains(table, StringComparison.Ordinal);
            }),
    ];

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    private void Record(DbCommand command) => _statements.Enqueue(command.CommandText);
}
