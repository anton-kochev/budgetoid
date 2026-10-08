using Application.Abstractions;

namespace Api.Infrastructure;

public sealed class HttpContextUserContext(CurrentUser currentUser) : IUserContext
{
    public Guid? ResolvedUserId => currentUser.UserId;
}
