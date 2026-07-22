using Application.Abstractions;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Common;
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.Categories.PlaceCategory;

public sealed class PlaceCategoryHandler(
    ICategoryRepository repository,
    ICategoryGroupRepository categoryGroups)
    : ICommandHandler<PlaceCategoryCommand>
{
    public async Task HandleAsync(
        PlaceCategoryCommand command,
        CancellationToken cancellationToken = default)
    {
        Category? category = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (category is null)
        {
            throw new NotFoundException("Category was not found.");
        }

        CategoryGroup? categoryGroup = await categoryGroups.GetByIdAsync(
            command.CategoryGroupId,
            cancellationToken);
        if (categoryGroup is null)
        {
            throw new DomainValidationException(new Dictionary<string, string[]>
            {
                [nameof(command.CategoryGroupId)] = ["Category group was not found."],
            });
        }

        await repository.PlaceAsync(
            category,
            categoryGroup.Id,
            command.Position,
            cancellationToken);
    }
}
