using System;

namespace Budgetoid.Dto;

public record TransactionDto
{
    public Guid AccountId { get; init; }
    public decimal Amount { get; init; }
    public Guid CategoryId { get; init; }
    public string Comment { get; init; }
    public DateOnly Date { get; init; }
    public Guid Id { get; init; }
    public Guid PayeeId { get; init; }
    public string[] Tags { get; init; }
}
