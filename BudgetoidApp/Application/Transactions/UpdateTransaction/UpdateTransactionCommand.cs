using Application.Abstractions;

namespace Application.Transactions.UpdateTransaction;

/// <summary>
/// A partial edit of a transaction. Every mutable field is an <see cref="Optional{T}"/>: absent
/// leaves the current value alone, present replaces it, and present-and-null clears it. Amount, date
/// and account are declared over non-nullable types because a transaction without them is not a
/// transaction, so an explicit null for those is a bad request rather than a clear.
/// </summary>
public sealed record UpdateTransactionCommand(
    Guid Id,
    Optional<decimal> Amount,
    Optional<DateOnly> Date,
    Optional<Guid> AccountId,
    Optional<string?> Description,
    Optional<string?> PayeeName,
    Optional<Guid?> CategoryId);
