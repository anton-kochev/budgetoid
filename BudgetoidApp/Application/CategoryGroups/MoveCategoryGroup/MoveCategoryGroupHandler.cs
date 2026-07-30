using Application.Abstractions;
using Domain.CategoryGroups;
using Domain.Common;

namespace Application.CategoryGroups.MoveCategoryGroup;

public sealed class MoveCategoryGroupHandler(ICategoryGroupRepository repository)
    : ICommandHandler<MoveCategoryGroupCommand>
{
    public async Task HandleAsync(
        MoveCategoryGroupCommand command,
        CancellationToken cancellationToken = default)
    {
        CategoryGroup? categoryGroup = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (categoryGroup is null)
        {
            throw new NotFoundException("Category group was not found.");
        }

        await repository.MoveToPositionAsync(categoryGroup, command.Position, cancellationToken);
    }
}
