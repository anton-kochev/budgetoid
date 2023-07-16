using System;

namespace Budgetoid.Dto;

public record UpdateTransactionDto
{
    public Guid AccountId { get; init; }
    public decimal Amount { get; init; }
    public Guid CategoryId { get; init; }
    public string Comment { get; init; }
    public DateTime Date { get; init; }
    public string Payee { get; init; } = string.Empty;
    public string[] Tags { get; init; }
}
