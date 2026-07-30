using Application.Abstractions;
using Domain.CategoryGroups;
using Domain.Common;

namespace Application.CategoryGroups.UpdateCategoryGroup;

public sealed class UpdateCategoryGroupHandler(ICategoryGroupRepository repository)
    : ICommandHandler<UpdateCategoryGroupCommand>
{
    public async Task HandleAsync(
        UpdateCategoryGroupCommand command,
        CancellationToken cancellationToken = default)
    {
        CategoryGroup? categoryGroup = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (categoryGroup is null)
        {
            throw new NotFoundException("Category group was not found.");
        }

        categoryGroup.Update(command.Name, command.Description);
        await repository.UpdateAsync(categoryGroup, cancellationToken);
    }
}
