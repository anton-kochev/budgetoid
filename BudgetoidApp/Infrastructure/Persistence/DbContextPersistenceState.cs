using Application.Abstractions;

namespace Infrastructure.Persistence;

/// <summary>
/// The change tracker of the request-scoped <see cref="BudgetoidDbContext"/>, behind the one
/// operation the application layer is allowed to perform on it.
/// </summary>
/// <remarks>
/// Deliberately not folded into <see cref="DbContextTransactionalExecutor"/> as an automatic clear
/// before every attempt: two of the existing handlers mutate a tracked entity <i>before</i> they open
/// their transaction and rely on the save inside it to flush that mutation, so a blanket clear would
/// silently discard their write. Discarding is therefore something a unit of work asks for, at the
/// top of the delegate that needs it.
/// </remarks>
public sealed class DbContextPersistenceState(BudgetoidDbContext dbContext) : IPersistenceState
{
    /// <inheritdoc />
    public void DiscardTrackedEntities() => dbContext.ChangeTracker.Clear();
}
