using System;

namespace Budgetoid.Dto;

// ReSharper disable once ClassNeverInstantiated.Global
public record CreateTransactionCommand
{
    public Guid AccountId { get; init; }
    public decimal Amount { get; init; }
    public Guid CategoryId { get; init; }
    public string Comment { get; init; }
    public Guid CreatedBy { get; init; }
    public DateTime Date { get; init; }
    public Guid PayeeId { get; init; }
    public string[] Tags { get; init; }
}
