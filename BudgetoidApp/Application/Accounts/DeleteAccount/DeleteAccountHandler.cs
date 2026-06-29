using Application.Abstractions;
using Domain.Accounts;
using Domain.Common;
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.Accounts.DeleteAccount;

public sealed class DeleteAccountHandler(IAccountRepository repository)
    : ICommandHandler<DeleteAccountCommand>
{
    public async Task HandleAsync(
        DeleteAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        Account? account = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (account is null)
        {
            throw new NotFoundException("Account was not found.");
        }

        if (await repository.HasTransactionsAsync(command.Id, cancellationToken))
        {
            throw new DomainValidationException(new Dictionary<string, string[]>
            {
                [nameof(command.Id)] = ["Account cannot be deleted because it has transactions."],
            });
        }

        await repository.DeleteAsync(account, cancellationToken);
    }
}
