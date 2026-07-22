using Application.Abstractions;
using Domain.CategoryGroups;
using Domain.Common;
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.CategoryGroups.DeleteCategoryGroup;

public sealed class DeleteCategoryGroupHandler(ICategoryGroupRepository repository)
    : ICommandHandler<DeleteCategoryGroupCommand>
{
    public async Task HandleAsync(
        DeleteCategoryGroupCommand command,
        CancellationToken cancellationToken = default)
    {
        CategoryGroup? categoryGroup = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (categoryGroup is null)
        {
            throw new NotFoundException("Category group was not found.");
        }

        if (await repository.HasCategoriesAsync(command.Id, cancellationToken))
        {
            throw new DomainValidationException(new Dictionary<string, string[]>
            {
                [nameof(command.Id)] = ["Category group cannot be deleted because it has categories."],
            });
        }

        await repository.DeleteAsync(categoryGroup, cancellationToken);
    }
}
