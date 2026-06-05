using Domain.Common;
using Domain.Entities;

namespace Application.Transactions.Queries.GetTransaction;

public record TransactionDto
{
    public Guid AccountId { get; set; } = Guid.Empty;
    public decimal Amount { get; set; }
    public Guid CategoryId { get; set; } = Guid.Empty;
    public string Comment { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public Guid Id { get; set; } = Guid.Empty;
    public string Payee { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
}

internal static class TransactionExtensions
{
    public static TransactionDto ToDto(this Transaction entity)
    {
        return new TransactionDto
        {
            AccountId = entity.AccountId,
            Amount = entity.Amount,
            CategoryId = entity.CategoryId,
            Comment = entity.Comment,
            Date = entity.Date.ToDateOnly(),
            Id = Guid.Parse(entity.Id),
            Payee = entity.Payee,
            Tags = entity.Tags
        };
    }

    public static IEnumerable<TransactionDto> ToDto(this IEnumerable<Transaction> entities)
    {
        return entities.Select(e => e.ToDto());
    }

    public static Transaction ToEntity(this TransactionDto dto, Guid userId)
    {
        return new Transaction
        {
            Id = dto.Id.ToString(),
            UserId = userId.ToString(),
            AccountId = dto.AccountId,
            Amount = dto.Amount,
            CategoryId = dto.CategoryId,
            Comment = dto.Comment,
            Date = dto.Date.ToDateTime(),
            Payee = dto.Payee,
            Tags = dto.Tags
        };
    }
}
