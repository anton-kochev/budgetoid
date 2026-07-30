using Application.Abstractions;
using Application.Currencies;
using Domain.Accounts;
using Domain.Common;

namespace Application.Accounts.UpdateAccount;

public sealed class UpdateAccountHandler(IAccountRepository repository, ICurrencyReadService currencies)
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

        // Resolved after the load because the currency to validate against is the account's own -
        // Update never changes it. The RESTRICT foreign key on accounts.currency_code guarantees the
        // row exists, so a miss is corruption rather than user error.
        CurrencyDto currency = await currencies.GetByCodeAsync(account.CurrencyCode, cancellationToken)
                               ?? throw new InvalidOperationException(
                                   $"Currency '{account.CurrencyCode}' for account '{account.Id}' was not found.");

        account.Update(command.Name, command.Type, command.OpeningBalance, currency.MinorUnit);
        await repository.UpdateAsync(account, cancellationToken);
    }
}
