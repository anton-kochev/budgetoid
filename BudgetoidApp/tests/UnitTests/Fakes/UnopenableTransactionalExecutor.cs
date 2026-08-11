using Application.Abstractions;

namespace UnitTests.Fakes;

/// <summary>
/// The transaction that could not be opened: an <see cref="ITransactionalExecutor" /> that raises
/// before it ever invokes the unit of work.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is how a fake can say "the transaction is load-bearing" at all.</b>
/// <see cref="InMemoryTransactionalExecutor" /> and <see cref="RetryingTransactionalExecutor" /> both
/// run the delegate, so against either of them a handler that opened no transaction and simply ran its
/// own body inline is indistinguishable from one that did — the writes land either way, and every row
/// count agrees. What tells them apart is a transaction that never opens: the handler that goes through
/// this one writes nothing, and the handler that inlined its body writes everything and answers 200.
/// </para>
/// <para>
/// <b>It fails at the open rather than at the commit, and the choice is about what a fake can honestly
/// model.</b> A commit failure would have to undo rows the fakes hold in plain lists, which is the
/// caller's job (see <see cref="RetryingTransactionalExecutor" />) and would make the arrangement, not
/// the handler, responsible for the state being asserted. Failing to open needs no undoing: nothing ran.
/// Both are real — a connection lost between <c>BEGIN</c> and the first statement is this one — and both
/// leave the same observable behind, which is that a unit of work that did not commit wrote nothing.
/// </para>
/// <para>
/// <b>What it also measures is the ordering that has no other unit-level witness.</b> A handler
/// publishing its identity <em>before</em> opening the transaction has already published it when this
/// raises; one that publishes inside the delegate has not. Against a real database that ordering is the
/// difference between a working route and <c>22P02</c> on every request, because opening the transaction
/// opens the connection and that is when <c>SessionContextInterceptor</c> writes
/// <c>app.current_user_id</c> — a fact no in-memory fake reproduces, and this is the closest a unit test
/// gets to it.
/// </para>
/// </remarks>
public sealed class UnopenableTransactionalExecutor : ITransactionalExecutor
{
    /// <summary>
    /// How many times a unit of work was handed over, which is <b>never</b> how many ran.
    /// </summary>
    /// <remarks>
    /// Recorded so a test can tell "the handler asked for a transaction and was refused one" from "the
    /// handler never asked" — the two produce the same empty store, and only the second is the mutation
    /// worth catching.
    /// </remarks>
    public int ExecuteCallCount { get; private set; }

    public Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        ExecuteCallCount++;

        // Never invoked, which is the whole of what this type does.
        throw new TransactionUnavailableException();
    }
}

/// <summary>
/// What <see cref="UnopenableTransactionalExecutor" /> raises, as a type nothing in the application
/// declares.
/// </summary>
/// <remarks>
/// Its own type rather than an <see cref="InvalidOperationException" /> so a test asserting it escaped
/// is asserting that <em>this</em> escaped: a handler that caught the failure and answered with a
/// refusal of its own would otherwise be indistinguishable from one that let it through, and a
/// redemption that reported a database fault as "that code is not valid" would send somebody who still
/// holds nine good codes to throw the card away.
/// </remarks>
public sealed class TransactionUnavailableException()
    : Exception("The transaction could not be opened.");
