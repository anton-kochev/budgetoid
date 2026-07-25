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

        return new Payee
        {
            Id = Guid.CreateVersion7(),
            BudgetId = budgetId,
            Name = trimmedName,
            CreatedAtUtc = createdAtUtc,
        };
    }
}
