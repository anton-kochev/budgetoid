using Application.Users.EnsureUser;

namespace Api.Infrastructure;

/// <summary>
/// Writes into the same scoped <see cref="CurrentUser"/> that <see cref="HttpContextUserContext"/>
/// reads. Kept a type of its own rather than merged with the reader so that only what is injected
/// this interface can name the request's identity.
/// </summary>
public sealed class CurrentUserWriter(CurrentUser currentUser) : IUserContextWriter
{
    public void ResolveUser(Guid userId) => currentUser.UserId = userId;
}
