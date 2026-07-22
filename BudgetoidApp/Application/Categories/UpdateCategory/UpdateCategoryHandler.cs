using Application.Abstractions;
using Domain.Categories;
using Domain.Common;

namespace Application.Categories.UpdateCategory;

public sealed class UpdateCategoryHandler(ICategoryRepository repository)
    : ICommandHandler<UpdateCategoryCommand>
{
    public async Task HandleAsync(
        UpdateCategoryCommand command,
        CancellationToken cancellationToken = default)
    {
        Category? category = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (category is null)
        {
            throw new NotFoundException("Category was not found.");
        }

        category.Update(command.Name, command.Description);
        await repository.UpdateAsync(category, cancellationToken);
    }
}
