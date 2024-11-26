using Application.Abstractions;
using MediatR;

namespace Application.Users.Queries.GetUser;

public record GetUserQuery(string Email) : IRequest<Guid>;

public sealed class GetUserHandler(IUsersRepository usersRepository)
    : IRequestHandler<GetUserQuery, Guid>
{
    public Task<Guid> Handle(GetUserQuery request, CancellationToken cancellationToken)
    {
        return usersRepository.GetUserIdAsync(request.Email, cancellationToken);
        //
        // if (userId != Guid.Empty)
        // {
        //     return userId;
        // }
        //
        // // This apparently breaks the CQRS pattern. However, it is a part of the internal system
        // // to get the user id connected to the email and create one if it does not exist.
        // User user = await mediator.Send(new CreateUserCommand(request.Email), cancellationToken);
        //
        // return user.Id;
    }
}
