using Domain.Common;

namespace Domain.Payees;

public sealed class Payee
{
    private Payee()
    {
    }

    public Guid Id { get; private set; }
    public Guid BudgetId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }

    public static Payee Create(Guid budgetId, string name, DateTime createdAtUtc)
    {
        ValidateOrThrow(budgetId, name);

        return new Payee
        {
            Id = Guid.CreateVersion7(),
            BudgetId = budgetId,
            Name = name.Trim(),
            CreatedAtUtc = createdAtUtc,
        };
    }

    // A case-only rename ("starbucks" -> "Starbucks") on the same row is allowed and is the most
    // common use of this method. It does not collide with the case-insensitive unique index on
    // (budget_id, name): the row's own index entry is replaced in the same update, so the row is
    // never compared against its former self. No pre-check is needed here.
    public void Rename(string name)
    {
        ValidateOrThrow(BudgetId, name);

        Name = name.Trim();
    }

    private static void ValidateOrThrow(Guid budgetId, string? name)
    {
        var errors = new Dictionary<string, string[]>();
        string trimmedName = name?.Trim() ?? string.Empty;

        if (budgetId == Guid.Empty)
        {
            errors[nameof(BudgetId)] = ["Budget id is required."];
        }

        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            errors[nameof(Name)] = ["Name is required."];
        }
        else if (trimmedName.Length > 200)
        {
            errors[nameof(Name)] = ["Name must be 200 characters or fewer."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }
}
