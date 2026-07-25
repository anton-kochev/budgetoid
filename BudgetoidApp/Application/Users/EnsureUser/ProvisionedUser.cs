namespace Application.Users.EnsureUser;

/// <summary>
/// The outcome of provisioning: the internal user id and the id of the budget the request operates
/// in. Both are always populated — a provisioned user always owns at least its default budget.
/// </summary>
public sealed record ProvisionedUser(Guid UserId, Guid BudgetId);
