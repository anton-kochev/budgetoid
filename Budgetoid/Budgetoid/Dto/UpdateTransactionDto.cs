using System;

namespace Budgetoid.Dto;

public record UpdateTransactionDto(
    Guid AccountId,
    decimal Amount,
    Guid CategoryId,
    string Comment,
    DateTime Date,
    string Payee,
    string[] Tags
);
