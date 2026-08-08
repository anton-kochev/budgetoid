using Domain.Accounts;

namespace Application.Users.ExportData;

/// <summary>
/// Everything the signed-in account owns, in the shape it is handed to the caller.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own records rather than the display DTOs beside them, and that is not duplication.</b>
/// Every existing read model is shaped for a screen: <c>TransactionDto</c> coerces a null description
/// to the empty string, <c>PayeeDto</c> carries only an id and a name, <c>AccountDto</c> denormalizes
/// the currency's name and symbol, and each of them orders for display. An export built on those
/// would ship a document that quietly disagrees with the rows it claims to be a copy of, and would
/// then drift every time a screen changed. These records carry the persisted columns and nothing
/// else.
/// </para>
/// <para>
/// The parent ids the nesting already implies — <c>userId</c> on a budget, <c>budgetId</c> on each of
/// its rows — ship anyway, so that a completeness check over this document is a straight column-set
/// comparison rather than one against a list of agreed omissions.
/// </para>
/// </remarks>
public sealed record ExportDocument(
    int SchemaVersion,
    ExportedUser User,
    IReadOnlyList<ExportedBudget> Budgets)
{
    /// <summary>
    /// The version every document written today declares.
    /// </summary>
    /// <remarks>
    /// A reader of a saved file has no other way to know which shape it is holding, and the file
    /// outlives the deployment that wrote it. A test asserting the version must assert the literal
    /// rather than this constant: one reading the constant can never fail on a version bump, which is
    /// the single change it exists to notice.
    /// </remarks>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>The account itself — the three columns the <c>users</c> row carries and no more.</summary>
public sealed record ExportedUser(Guid Id, string Email, DateTime CreatedAtUtc);

/// <summary>
/// One budget and everything filed under it.
/// </summary>
/// <remarks>
/// The five collections are <c>init</c> members rather than positional parameters because the budget's
/// own columns and its contents are read by two different queries: the read service returns the row,
/// and the handler attaches what was read from inside it. They default to empty so a budget is never
/// half-constructed.
/// </remarks>
public sealed record ExportedBudget(
    Guid Id,
    Guid UserId,
    string? Name,
    string? BaseCurrencyCode,
    DateTime CreatedAtUtc)
{
    public IReadOnlyList<ExportedAccount> Accounts { get; init; } = [];

    public IReadOnlyList<ExportedCategoryGroup> CategoryGroups { get; init; } = [];

    public IReadOnlyList<ExportedCategory> Categories { get; init; } = [];

    public IReadOnlyList<ExportedPayee> Payees { get; init; } = [];

    public IReadOnlyList<ExportedTransaction> Transactions { get; init; } = [];
}

public sealed record ExportedAccount(
    Guid Id,
    Guid BudgetId,
    string Name,
    AccountType Type,
    decimal OpeningBalance,
    string CurrencyCode,
    DateTime CreatedAtUtc);

public sealed record ExportedCategoryGroup(
    Guid Id,
    Guid BudgetId,
    string Name,
    string? Description,
    int Position,
    DateTime CreatedAtUtc);

public sealed record ExportedCategory(
    Guid Id,
    Guid BudgetId,
    Guid CategoryGroupId,
    string Name,
    string? Description,
    int Position,
    DateTime CreatedAtUtc);

public sealed record ExportedPayee(Guid Id, Guid BudgetId, string Name, DateTime CreatedAtUtc);

public sealed record ExportedTransaction(
    Guid Id,
    Guid BudgetId,
    Guid AccountId,
    decimal Amount,
    DateOnly Date,
    string? Description,
    Guid? PayeeId,
    Guid? CategoryId,
    DateTime CreatedAtUtc);
