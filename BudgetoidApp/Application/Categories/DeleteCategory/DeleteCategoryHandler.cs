using Application.Abstractions;
using Domain.Categories;
using Domain.Common;
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.Categories.DeleteCategory;

public sealed class DeleteCategoryHandler(ICategoryRepository repository)
    : ICommandHandler<DeleteCategoryCommand>
{
    public async Task HandleAsync(
        DeleteCategoryCommand command,
        CancellationToken cancellationToken = default)
    {
        Category? category = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (category is null)
        {
            throw new NotFoundException("Category was not found.");
        }

        if (await repository.HasTransactionsAsync(command.Id, cancellationToken))
        {
            throw new DomainValidationException(new Dictionary<string, string[]>
            {
                [nameof(command.Id)] = ["Category cannot be deleted because it has transactions."],
            });
        }

        await repository.DeleteAsync(category, cancellationToken);
    }
}
