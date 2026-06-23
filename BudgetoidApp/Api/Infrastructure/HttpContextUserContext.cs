using Application.Abstractions;

namespace Api.Infrastructure;

public sealed class HttpContextUserContext(CurrentUser currentUser) : IUserContext
{
    public Guid UserId => currentUser.UserId
        ?? throw new InvalidOperationException("The current application user has not been resolved.");
}
