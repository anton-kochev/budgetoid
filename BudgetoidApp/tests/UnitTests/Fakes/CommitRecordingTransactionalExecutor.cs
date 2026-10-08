using Application.Abstractions;

namespace UnitTests.Fakes;

/// <summary>
/// Runs the unit of work once and records how it ended: returned is a commit, thrown is a rollback.
/// </summary>
/// <remarks>
/// That is the contract <see cref="ITransactionalExecutor"/> states, and it is what lets a test say a
/// refusal did not commit the writes made before it — a delegate that <em>returns</em> a refusal hands
/// the executor a commit of everything it did on the way there.
/// </remarks>
public sealed class CommitRecordingTransactionalExecutor : ITransactionalExecutor
{
    /// <summary>How many times a unit of work was started.</summary>
    public int Entered { get; private set; }

    /// <summary>How many units of work returned, which the real executor commits.</summary>
    public int Committed { get; private set; }

    /// <summary>How many units of work threw, which the real executor rolls back.</summary>
    public int RolledBack { get; private set; }

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        Entered++;

        try
        {
            TResult result = await operation(cancellationToken);
            Committed++;

            return result;
        }
        catch
        {
            RolledBack++;
            throw;
        }
    }
}
