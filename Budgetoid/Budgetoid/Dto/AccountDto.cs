using System;

namespace Budgetoid.Dto;

public record AccountDto
{
    public Guid Id { get; init; }
    public string Currency { get; init; }
    public string Name { get; init; }
}
