using Application.Abstractions;
using Domain.CategoryGroups;

namespace Application.CategoryGroups.CreateCategoryGroup;

public sealed class CreateCategoryGroupHandler(
    ICategoryGroupRepository repository,
    IUserContext userContext,
    TimeProvider timeProvider)
    : ICommandHandler<CreateCategoryGroupCommand, CategoryGroupDto>
{
    public async Task<CategoryGroupDto> HandleAsync(
        CreateCategoryGroupCommand command,
        CancellationToken cancellationToken = default)
    {
        int position = await repository.GetNextPositionAsync(cancellationToken);
        CategoryGroup categoryGroup = CategoryGroup.Create(
            userContext.UserId,
            command.Name,
            command.Description,
            position,
            timeProvider.GetUtcNow().UtcDateTime);

        await repository.AddAsync(categoryGroup, cancellationToken);
        return new CategoryGroupDto(
            categoryGroup.Id,
            categoryGroup.Name,
            categoryGroup.Description,
            categoryGroup.Position);
    }
}
