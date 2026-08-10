using Application.Abstractions;

namespace UnitTests.Fakes;

/// <summary>
/// An <see cref="ITransactionalExecutor"/> that runs the unit of work a fixed number of times and
/// keeps only the last result, which is what a retrying provider strategy does after a transient
/// failure.
/// </summary>
/// <remarks>
/// <see cref="InMemoryTransactionalExecutor"/> runs the delegate once, so nothing it drives can show
/// whether that delegate is safe to repeat. Forcing the replay here is honest in a way faking the
/// transient failure itself would not be: the delegate really does run twice, against collaborators
/// that really do carry the first attempt's leftovers, so a delegate that has stopped discarding them
/// fails for the reason production would fail for.
/// <para>
/// <paramref name="rollBackAbandonedAttempt"/> is what the fakes cannot do for themselves, and it is
/// the other half of that honesty. The leftovers a replay must survive are the <b>change tracker's</b>;
/// the <b>rows</b> an abandoned attempt wrote are rolled back and were never there. Fakes hold their
/// rows in plain lists with nothing to undo, so a delegate whose statements are not idempotent — a
/// delete, above all — would meet the second attempt against a store that looks as though the first
/// attempt committed, which is a state production never produces. A caller whose delegate survives its
/// own row-level leftovers passes none of these and gets the plain replay; one that removes a row hands
/// in what putting it back means. The tracked leftovers are untouched either way, which is what keeps
/// the discard tests measuring the discard.
/// </para>
/// </remarks>
public sealed class RetryingTransactionalExecutor(int attempts, Func<Task>? rollBackAbandonedAttempt = null)
    : ITransactionalExecutor
{
    /// <summary>
    /// How many attempts have been started, so a collaborator can record which one it was reached on.
    /// Zero until the executor is entered, which is what tells a call made inside the delegate apart
    /// from one made before it.
    /// </summary>
    public int Attempts { get; private set; }

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempts);

        TResult result = default!;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            // Before the replay and never before the first attempt: the rows the abandoned attempt
            // wrote are put back exactly as ROLLBACK puts them back, and only for callers that asked.
            if (attempt > 0 && rollBackAbandonedAttempt is not null)
            {
                await rollBackAbandonedAttempt();
            }

            Attempts++;

            // The earlier attempts' results are thrown away exactly as a rolled-back attempt's are.
            // What they leave behind in the collaborators is not thrown away, and that is the point.
            result = await operation(cancellationToken);
        }

        return result;
    }
}
