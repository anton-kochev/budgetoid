using Domain.Entities;

namespace Application.Transactions.Queries.GetAccounts;

public record AccountDto
{
    public Guid Id { get; init; } = Guid.Empty;
    public Guid UserId { get; init; } = Guid.Empty;
    public string Currency { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

internal static class AccountExtensions
{
    public static AccountDto ToDto(this Account entity)
    {
        return new AccountDto
        {
            Id = Guid.Parse(entity.Id),
            Currency = entity.Currency,
            UserId = Guid.Parse(entity.UserId),
            Name = entity.Name
        };
    }

    public static IEnumerable<AccountDto> ToDto(this IEnumerable<Account> entities)
    {
        return entities.Select(e => e.ToDto());
    }
}
