using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Stands in for the winner of a race between two requests erasing one account: it commits a delete
/// on its own connection in the window between a repository's read and its
/// <c>SaveChangesAsync</c> — which is exactly where the losing request loses.
/// </summary>
/// <remarks>
/// <para>
/// Deterministic where a genuinely concurrent test would not be, and no weaker for it. A real race
/// arrives at this state by timing; hooking <see cref="ISaveChangesInterceptor" /> arrives at the
/// same state on every run, and the state is the whole of what the code under test responds to.
/// Nothing here fabricates the exception either: the rows really are gone by the time the DELETE
/// runs, so EF really does count zero affected rows where it expected one and really does raise
/// <c>DbUpdateConcurrencyException</c>.
/// </para>
/// <para>
/// The connection is its own, opened here rather than borrowed, because the point is that another
/// session committed. Running the delete on the context's own connection would place it inside
/// whatever transaction that context is in, where it could not be visible to the statement it is
/// meant to have got in front of.
/// </para>
/// <para>
/// It fires once. A context that saves twice would otherwise have its second save preceded by a
/// delete nobody asked for, and <see cref="Deleted" /> would stop meaning what a test reads it as —
/// which matters, because that count is how a test proves the arrangement was not a no-op.
/// </para>
/// </remarks>
public sealed class ConcurrentDeleteInterceptor(string connectionString, string sql, Guid id)
    : SaveChangesInterceptor
{
    private bool _fired;

    /// <summary>
    /// How many rows the out-of-band delete removed. Read by tests so that "the repository
    /// completed" can never be satisfied by an arrangement in which nothing was ever deleted.
    /// </summary>
    public int Deleted { get; private set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (_fired)
        {
            return result;
        }

        _fired = true;

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("id", id);
        Deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        return result;
    }
}
