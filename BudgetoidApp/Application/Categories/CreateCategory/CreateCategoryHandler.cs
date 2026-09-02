using Application.Abstractions;
using Application.Passkeys;
using Domain.Categories;
using Domain.CategoryGroups;
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.Categories.CreateCategory;

public sealed class CreateCategoryHandler(
    ICategoryRepository repository,
    ICategoryGroupRepository categoryGroups,
    IBudgetContext budgetContext,
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
            budgetContext.BudgetId,
            categoryGroup.Id,
            command.Name,
            command.Description,
            position,
            timeProvider.GetUtcNow().UtcDateTime);

        await repository.AddAsync(category, cancellationToken);

        // THIS WAS ALWAYS A READ OF THE GROUP'S NAME AND THE FIX IS NOT TO OPEN IT.
        // category_groups.name is an AEAD envelope this server holds no key for; what the row carries is
        // handed on untouched, in the alphabet every binary member of this API crosses JSON in, and the
        // browser that asked for it is what turns it back into a name. Decoding here would need a key on
        // this side, which is the design the product exists to avoid - and a placeholder string would be
        // a lie the screen renders.
        //
        // The category's OWN name and description travel as text on the two lines above, because
        // categories.name and categories.description are not sealed yet. That is a statement about today
        // and the next slice removes it; CategoryDto's remarks carry the same warning.
        return new CategoryDto(
            category.Id,
            category.Name,
            category.Description,
            category.CategoryGroupId,
            PasskeyEncoding.Encode(categoryGroup.Name.Envelope.Span),
            category.Position);
    }
}
