using Application.Abstractions;
using Domain.Common;
using Domain.Groups;
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.Groups.DeleteGroup;

public sealed class DeleteGroupHandler(IGroupRepository repository)
    : ICommandHandler<DeleteGroupCommand>
{
    public async Task HandleAsync(
        DeleteGroupCommand command,
        CancellationToken cancellationToken = default)
    {
        Group? group = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (group is null)
        {
            throw new NotFoundException("Group was not found.");
        }

        if (await repository.HasTransactionsAsync(command.Id, cancellationToken))
        {
            throw new DomainValidationException(new Dictionary<string, string[]>
            {
                [nameof(command.Id)] = ["Group cannot be deleted because it has transactions."],
            });
        }

        await repository.DeleteAsync(group, cancellationToken);
    }
}
