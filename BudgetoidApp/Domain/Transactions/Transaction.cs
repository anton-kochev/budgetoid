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
    public string Description { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }

    public static Transaction Create(Guid userId, decimal amount, DateOnly date, string? description)
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

        var trimmedDescription = description?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedDescription))
        {
            errors[nameof(Description)] = ["Description is required."];
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
            CreatedAtUtc = DateTime.UtcNow,
        };
    }
}
