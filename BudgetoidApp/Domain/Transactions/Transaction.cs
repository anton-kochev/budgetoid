using Domain.Common;

namespace Domain.Transactions;

public sealed class Transaction
{
    private const int MaxMinorUnit = 4;

    private Transaction()
    {
    }

    public Guid Id { get; private set; }
    public Guid BudgetId { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal Amount { get; private set; }
    public DateOnly Date { get; private set; }
    public string? Description { get; private set; }
    public Guid? PayeeId { get; private set; }
    public Guid? CategoryId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static Transaction Create(Guid budgetId, Guid accountId, decimal amount, int minorUnit, DateOnly date, string? description, DateTime createdAtUtc)
    {
        string? normalizedDescription = ValidateOrThrow(budgetId, accountId, amount, minorUnit, description);

        return new Transaction
        {
            Id = Guid.CreateVersion7(),
            BudgetId = budgetId,
            AccountId = accountId,
            Amount = amount,
            Date = date,
            Description = normalizedDescription,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Replaces the editable fields of the transaction. The budget, identity and creation time are
    /// not the caller's to rewrite, and payee and category have their own assign/clear methods.
    /// </summary>
    public void Update(Guid accountId, decimal amount, int minorUnit, DateOnly date, string? description)
    {
        string? normalizedDescription = ValidateOrThrow(BudgetId, accountId, amount, minorUnit, description);

        AccountId = accountId;
        Amount = amount;
        Date = date;
        Description = normalizedDescription;
    }

    /// <summary>
    /// Validates the fields shared by <see cref="Create"/> and <see cref="Update"/> and returns the
    /// normalized description, so the two paths cannot drift apart.
    /// </summary>
    private static string? ValidateOrThrow(Guid budgetId, Guid accountId, decimal amount, int minorUnit, string? description)
    {
        // The minor unit comes from the account's currency, which the database already bounds to
        // 0..4, so an out-of-range value is a broken caller rather than user input - and a bad one
        // makes the precision check below meaningless, so it fails fast instead of joining errors.
        ArgumentOutOfRangeException.ThrowIfNegative(minorUnit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minorUnit, MaxMinorUnit);

        var errors = new Dictionary<string, string[]>();

        if (budgetId == Guid.Empty)
        {
            errors[nameof(BudgetId)] = ["Budget id is required."];
        }

        if (accountId == Guid.Empty)
        {
            errors[nameof(AccountId)] = ["Account id is required."];
        }

        if (decimal.Round(amount, minorUnit) != amount)
        {
            errors[nameof(Amount)] = [minorUnit == 0
                ? "Amount must be a whole number."
                : $"Amount must have no more than {minorUnit} decimal places."];
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

        return trimmedDescription;
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

    // Clearing is a legitimate user action once a transaction can be edited, so it gets its own
    // method rather than being spelled AssignPayee(Guid.Empty) - which would force AssignPayee to
    // stop treating an empty id as a programmer error. Already-null is a no-op, not a throw.
    public void ClearPayee() => PayeeId = null;

    public void AssignCategory(Guid categoryId)
    {
        // An empty id here is a programmer/invariant error (the caller always passes a real
        // category id), not user-facing validation — so ArgumentException, not ValidationException.
        if (categoryId == Guid.Empty)
        {
            throw new ArgumentException("Category id is required.", nameof(categoryId));
        }

        CategoryId = categoryId;
    }

    // Same reasoning as ClearPayee: uncategorised is a state a user can ask for, and
    // AssignCategory(Guid.Empty) is not how they should have to ask for it.
    public void ClearCategory() => CategoryId = null;
}
