using Application.Abstractions;
using Domain.Common;
using Domain.Groups;

namespace Application.Groups.UpdateGroup;

public sealed class UpdateGroupHandler(IGroupRepository repository)
    : ICommandHandler<UpdateGroupCommand>
{
    public async Task HandleAsync(
        UpdateGroupCommand command,
        CancellationToken cancellationToken = default)
    {
        Group? group = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (group is null)
        {
            throw new NotFoundException("Group was not found.");
        }

        group.Update(command.Name, command.Description);
        await repository.UpdateAsync(group, cancellationToken);
    }
}
