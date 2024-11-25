using Application.Abstractions;
using Domain.Entities;
using MediatR;

namespace Application.Users.Commands.CreateUser;

public record CreateUserCommand(string Email) : IRequest<User>;

public sealed class CreateUserHandler(IUsersRepository usersRepository) : IRequestHandler<CreateUserCommand, User>
{
    public Task<User> Handle(CreateUserCommand request, CancellationToken cancellationToken)
    {
        return usersRepository.CreateUserAsync(request.Email, cancellationToken);
    }
}
