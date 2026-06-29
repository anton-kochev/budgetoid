using Application.Abstractions;
using Application.Currencies;
using Domain.Accounts;
using DomainValidationException = Domain.Common.ValidationException;

namespace Application.Accounts.CreateAccount;

public sealed class CreateAccountHandler(
    IAccountRepository repository,
    ICurrencyReadService currencies,
    IUserContext userContext,
    TimeProvider timeProvider)
    : ICommandHandler<CreateAccountCommand, AccountDto>
{
    public async Task<AccountDto> HandleAsync(
        CreateAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        CurrencyDto? currency = await currencies.GetByCodeAsync(command.CurrencyCode, cancellationToken);
        if (currency is null)
        {
            throw new DomainValidationException(new Dictionary<string, string[]>
            {
                [nameof(command.CurrencyCode)] = ["Currency was not found."],
            });
        }

        Account account = Account.Create(
            userContext.UserId,
            command.Name,
            command.Type,
            command.OpeningBalance,
            currency.Code,
            timeProvider.GetUtcNow().UtcDateTime);

        await repository.AddAsync(account, cancellationToken);
        return AccountDto.FromAccount(account, currency);
    }
}
