using Domain.Common;

namespace Domain.Transactions;

public sealed class Transaction
{
    private Transaction()
    {
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public decimal Amount { get; private set; }
    public DateOnly Date { get; private set; }
    public string? Description { get; private set; }
    public Guid? PayeeId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static Transaction Create(Guid userId, decimal amount, DateOnly date, string? description, DateTime createdAtUtc)
    {
        var errors = new Dictionary<string, string[]>();

        if (userId == Guid.Empty)
        {
            errors[nameof(UserId)] = ["User id is required."];
        }

        if (amount == 0)
        {
            errors[nameof(Amount)] = ["Amount must be non-zero."];
        }
        else if (decimal.Round(amount, 2) != amount)
        {
            errors[nameof(Amount)] = ["Amount must have no more than 2 decimal places."];
        }
        else if (Math.Abs(amount) > 1_000_000_000m)
        {
            errors[nameof(Amount)] = ["Amount must be less than or equal to 1000000000 in absolute value."];
        }

        // Description is optional: a blank value becomes null. Only the length cap is enforced.
        var trimmedDescription = description?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedDescription))
        {
            trimmedDescription = null;
        }
        else if (trimmedDescription.Length > 500)
        {
            errors[nameof(Description)] = ["Description must be 500 characters or fewer."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return new Transaction
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Amount = amount,
            Date = date,
            Description = trimmedDescription,
            CreatedAtUtc = createdAtUtc,
        };
    }

    public void AssignPayee(Guid payeeId)
    {
        // An empty id here is a programmer/invariant error (the caller always passes a real
        // payee id), not user-facing validation — so ArgumentException, not ValidationException.
        if (payeeId == Guid.Empty)
        {
            throw new ArgumentException("Payee id is required.", nameof(payeeId));
        }

        PayeeId = payeeId;
    }
}
