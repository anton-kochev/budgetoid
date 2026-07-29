using Application.Abstractions;

namespace UnitTests.Fakes;

/// <summary>
/// Runs the operation directly. This is not a fake that neglects to model rollback: the unit fakes
/// keep their state in plain lists, where there is no transaction to open and nothing for a failure
/// to undo, so a pass-through is the whole of the behaviour there is to stand in for. Atomicity over
/// a real database is the integration suite's to prove.
/// </summary>
public sealed class InMemoryTransactionalExecutor : ITransactionalExecutor
{
    public Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default) => operation(cancellationToken);
}
