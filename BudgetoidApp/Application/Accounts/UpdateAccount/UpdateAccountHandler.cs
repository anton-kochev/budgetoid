using Application.Abstractions;
using Domain.Accounts;
using Domain.Common;

namespace Application.Accounts.UpdateAccount;

public sealed class UpdateAccountHandler(IAccountRepository repository)
    : ICommandHandler<UpdateAccountCommand>
{
    public async Task HandleAsync(
        UpdateAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        Account? account = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (account is null)
        {
            throw new NotFoundException("Account was not found.");
        }

        account.Update(command.Name, command.Type, command.OpeningBalance);
        await repository.UpdateAsync(account, cancellationToken);
    }
}
