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
/// </remarks>
public sealed class RetryingTransactionalExecutor(int attempts) : ITransactionalExecutor
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
            Attempts++;

            // The earlier attempts' results are thrown away exactly as a rolled-back attempt's are.
            // What they leave behind in the collaborators is not thrown away, and that is the point.
            result = await operation(cancellationToken);
        }

        return result;
    }
}
