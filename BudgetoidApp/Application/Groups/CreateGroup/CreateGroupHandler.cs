using Application.Abstractions;
using Domain.Groups;

namespace Application.Groups.CreateGroup;

public sealed class CreateGroupHandler(
    IGroupRepository repository,
    IUserContext userContext,
    TimeProvider timeProvider)
    : ICommandHandler<CreateGroupCommand, GroupDto>
{
    public async Task<GroupDto> HandleAsync(
        CreateGroupCommand command,
        CancellationToken cancellationToken = default)
    {
        Group group = Group.Create(
            userContext.UserId,
            command.Name,
            command.Description,
            timeProvider.GetUtcNow().UtcDateTime);

        await repository.AddAsync(group, cancellationToken);
        return new GroupDto(group.Id, group.Name, group.Description);
    }
}
