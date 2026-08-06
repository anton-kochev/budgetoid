namespace Application.Users;

/// <summary>
/// Reads about the signed-in account that are for showing, not for deciding.
/// </summary>
/// <remarks>
/// A read service rather than a method on <c>IUserRepository</c>, following the same split the rest
/// of the application draws: the repository loads aggregates that rules are then applied to, and this
/// projects a single column for a response. Nothing here is an aggregate, and a rule keyed on what it
/// returns would be a rule reading a value chosen for display.
/// </remarks>
public interface IUserAccountReadService
{
    /// <summary>
    /// The email address stored for <paramref name="userId"/>, or <see langword="null"/> when no user
    /// row answers to that id.
    /// </summary>
    /// <remarks>
    /// <c>users</c> is policed by <c>user_isolation</c>, so this can only be called once the request
    /// has an identity — and it returns nothing for anyone else's id regardless of what is asked for.
    /// </remarks>
    Task<string?> FindEmailAsync(Guid userId, CancellationToken cancellationToken = default);
}
