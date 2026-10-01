using Application.Abstractions;

namespace UnitTests.Fakes;

/// <summary>
/// An <see cref="ITransactionalExecutor" /> that wraps another one and says, at any moment, whether
/// the unit of work is currently running.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists so that a collaborator can record where it was called from.</b> A handler that enters
/// the executor, does nothing inside the delegate and writes afterwards satisfies every count a test
/// can take — the executor ran, the write happened, the row is there — and is wrong in the one way
/// that matters: the write is outside the transaction, so nothing rolls it back. Paired with a fake
/// that asks <see cref="InsideUnitOfWork" /> at the moment it is written to, the two together say
/// which side of the delegate the call arrived on. <c>InMemoryPasskeyRepository.ObserveAtDelete</c> is
/// the same idea for a different question, and its remarks argue why a before-and-after pair of
/// counters cannot express an ordering.
/// </para>
/// <para>
/// <b>It wraps rather than replaces</b>, so the replay behaviour stays
/// <see cref="RetryingTransactionalExecutor" />'s and this type holds one fact and no policy. It also
/// means no existing fake had to change to make the observation possible.
/// </para>
/// <para>
/// <b>This models nothing about PostgreSQL.</b> The flag is set and cleared around the caller's own
/// delegate, so what a test reads back is the control flow the handler actually produced — not this
/// fake's opinion of what a transaction is. What it therefore cannot see is whether the write would
/// really have been rolled back: that needs a database, and it is the integration tier's.
/// </para>
/// </remarks>
public sealed class ObservedTransactionalExecutor(ITransactionalExecutor inner) : ITransactionalExecutor
{
    /// <summary>
    /// Whether the unit of work is running right now. False before it is entered, false again once it
    /// has returned — including between the attempts of a replay.
    /// </summary>
    public bool InsideUnitOfWork { get; private set; }

    public Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return inner.ExecuteAsync(
            async token =>
            {
                InsideUnitOfWork = true;

                try
                {
                    return await operation(token);
                }
                finally
                {
                    // Cleared however the attempt ended, so an abandoned attempt does not leave the
                    // flag standing for a write made after the executor returns.
                    InsideUnitOfWork = false;
                }
            },
            cancellationToken);
    }
}
