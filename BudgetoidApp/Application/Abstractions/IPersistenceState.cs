namespace Application.Abstractions;

/// <summary>
/// The in-memory copies of entities a unit of work has read or created. A handler touches this for
/// one reason only: to throw those copies away, which is what makes a delegate handed to
/// <see cref="ITransactionalExecutor"/> safe to run more than once.
/// </summary>
public interface IPersistenceState
{
    /// <summary>
    /// Forgets every entity read or created so far, so the next read materialises the row as the
    /// database currently holds it and nothing an abandoned attempt left pending is written by a
    /// later save.
    /// </summary>
    /// <remarks>
    /// Rolling a transaction back undoes the database and nothing else: an entity a discarded attempt
    /// mutated stays mutated, and one it queued for insert stays queued. Anything replayed against
    /// that leftover state is running against a world neither the database nor the caller is in.
    /// </remarks>
    void DiscardTrackedEntities();
}
