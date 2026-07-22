using Application.Abstractions;
using Domain.Categories;
using Domain.CategoryGroups;
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.Categories.CreateCategory;

public sealed class CreateCategoryHandler(
    ICategoryRepository repository,
    ICategoryGroupRepository categoryGroups,
    IUserContext userContext,
    TimeProvider timeProvider)
    : ICommandHandler<CreateCategoryCommand, CategoryDto>
{
    public async Task<CategoryDto> HandleAsync(
        CreateCategoryCommand command,
        CancellationToken cancellationToken = default)
    {
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

        int position = await repository.GetNextPositionAsync(categoryGroup.Id, cancellationToken);
        Category category = Category.Create(
            userContext.UserId,
            categoryGroup.Id,
            command.Name,
            command.Description,
            position,
            timeProvider.GetUtcNow().UtcDateTime);

        await repository.AddAsync(category, cancellationToken);
        return new CategoryDto(
            category.Id,
            category.Name,
            category.Description,
            category.CategoryGroupId,
            categoryGroup.Name,
            category.Position);
    }
}
