namespace Application.Abstractions;

/// <summary>
/// Runs an operation as a single atomic unit of work: every write it performs commits together, or
/// none of them does. Handlers that write through more than one repository need this, because each
/// repository saves on its own and a failure between two saves would otherwise leave the first one
/// committed with nothing left to justify it.
/// </summary>
public interface ITransactionalExecutor
{
    /// <summary>
    /// Runs <paramref name="operation"/> inside one transaction and returns its result. The
    /// transaction commits when the operation returns and rolls back if it throws.
    /// </summary>
    /// <param name="operation">
    /// The unit of work. It may be invoked more than once — a retrying provider strategy replays the
    /// whole unit after a transient failure, against a database that never saw the abandoned attempt
    /// — so it must be safe to repeat and must not depend on state left behind by an earlier one.
    /// The token it receives is the one the current attempt is running under, which is why it is a
    /// parameter rather than something the operation captures.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation and abandons the transaction.</param>
    Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="operation"/> inside one transaction, for a unit of work that produces no
    /// result. The transaction commits when the operation returns and rolls back if it throws.
    /// </summary>
    /// <param name="operation">
    /// The unit of work. It may be invoked more than once — a retrying provider strategy replays the
    /// whole unit after a transient failure, against a database that never saw the abandoned attempt
    /// — so it must be safe to repeat and must not depend on state left behind by an earlier one.
    /// The token it receives is the one the current attempt is running under, which is why it is a
    /// parameter rather than something the operation captures.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation and abandons the transaction.</param>
    // Implemented here rather than left to implementers: this form is definitionally the generic one
    // with the result discarded, so there is no decision for an implementation to make, and every
    // hand-written copy would be this same delegation — with the standing risk that one of them
    // quietly acquires different transaction semantics.
    Task ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync<object?>(
            async token =>
            {
                await operation(token);
                return null;
            },
            cancellationToken);
}
