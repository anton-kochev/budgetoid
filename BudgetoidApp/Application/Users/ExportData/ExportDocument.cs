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
/// its rows — ship anyway, so that a completeness check over this document compares a table's columns
/// against a record's members with only the five nested collections on
/// <see cref="ExportedBudget" /> set aside, rather than against a list of agreed omissions. Those five
/// are the document's own nesting rather than anything the <c>budgets</c> row persists, and they are
/// the only members such a check has to account for.
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
/// <para>
/// The five collections are <c>init</c> members rather than positional parameters because the budget's
/// own columns and its contents are read by two different queries: the read service returns the row,
/// and the handler attaches what was read from inside it. They default to empty so a budget is never
/// half-constructed.
/// </para>
/// <para>
/// <b><see cref="Name" /> is still a <see cref="string" /> and no longer holds a name.</b> The column
/// is an AEAD envelope this server cannot open, so what ships is that envelope in the one alphabet
/// every binary member of this API crosses JSON in — unpadded base64url, which the client's strict
/// decoder already reads. The member keeps its name because the completeness check over this document
/// maps a table's columns onto a record's members, and because renaming it would say the export had
/// stopped carrying the column rather than that the column had changed shape. A reader of a saved file
/// needs the account's content key to get a name back out of it; the document declares
/// <c>SchemaVersion</c> so that a reader can tell which shape it is holding.
/// </para>
/// <para>
/// <b>The value equality a record advertises does not reach those five.</b> The synthesized
/// <see cref="object.Equals(object)" /> compares each member through
/// <see cref="EqualityComparer{T}.Default" />, which for an <see cref="IReadOnlyList{T}" /> is
/// reference equality — so two budgets holding equal rows in distinct list instances compare unequal
/// and hash differently, and <see cref="ExportDocument" /> inherits the same gap through its
/// <c>Budgets</c>. Compare the scalars, or the collections element-wise; comparing two of these values
/// whole answers a question about instances rather than about contents.
/// </para>
/// </remarks>
/// <param name="Id">The budget's identifier.</param>
/// <param name="UserId">The account that owns it.</param>
/// <param name="Name">
/// The budget's <b>sealed</b> name as unpadded base64url, or <see langword="null" /> for the budget
/// nobody named.
/// </param>
/// <param name="BaseCurrencyCode">The budget's base currency, or <see langword="null" />.</param>
/// <param name="CreatedAtUtc">The creation instant, in UTC.</param>
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

/// <summary>One account of a budget — the columns the <c>accounts</c> row carries and no more.</summary>
/// <param name="Id">The account's identifier.</param>
/// <param name="BudgetId">The budget that owns it.</param>
/// <param name="Name">
/// The account's <b>sealed</b> name as unpadded base64url, for the reason
/// <see cref="ExportedBudget.Name"/> gives about its own: the column is an AEAD envelope this server
/// cannot open, and the member keeps its name because the completeness check over this document maps a
/// table's columns onto a record's members.
/// </param>
/// <param name="Type">The kind of account.</param>
/// <param name="OpeningBalance">The balance the ledger starts from.</param>
/// <param name="CurrencyCode">The account's ISO 4217 code.</param>
/// <param name="CreatedAtUtc">The creation instant, in UTC.</param>
/// <remarks>
/// <b><c>name_key</c> is not here, and that is the one column this record deliberately omits.</b> The
/// blind index is derivable from the name by anybody holding the account's index key — which is exactly
/// who can read the file — and is meaningless to anybody who is not. Shipping it would put a
/// deterministic per-account fingerprint of every account name into a document the requirement asks to
/// be a copy of what a person owns, not of what the server needs to police it.
/// </remarks>
public sealed record ExportedAccount(
    Guid Id,
    Guid BudgetId,
    string Name,
    AccountType Type,
    decimal OpeningBalance,
    string CurrencyCode,
    DateTime CreatedAtUtc);

/// <summary>One category group of a budget — the columns the <c>category_groups</c> row carries, less one.</summary>
/// <param name="Id">The group's identifier.</param>
/// <param name="BudgetId">The budget that owns it.</param>
/// <param name="Name">
/// The group's <b>sealed</b> name as unpadded base64url, for the reason
/// <see cref="ExportedBudget.Name"/> gives about its own: the column is an AEAD envelope this server
/// cannot open, and the member keeps its name because the completeness check over this document maps a
/// table's columns onto a record's members.
/// </param>
/// <param name="Description">
/// The group's <b>sealed</b> note as unpadded base64url, or <see langword="null"/> where the group has
/// none. The first sealed free-text column in this document, and the null is carried rather than coerced
/// — a copy of somebody's data must keep "no note" and "a note they emptied" apart.
/// </param>
/// <param name="Position">Where the group sits in the person's own ordering.</param>
/// <param name="CreatedAtUtc">The creation instant, in UTC.</param>
/// <remarks>
/// <b><c>name_key</c> is not here, and it is the one column this record deliberately omits</b> — the
/// treatment <see cref="ExportedAccount"/> argues for its own and <see cref="ExportedPayee"/> repeats,
/// pointed at rather than copied a third time. There is deliberately no <c>description_key</c> to omit:
/// a description carries no blind index at all, because it is never looked up.
/// </remarks>
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

/// <summary>One payee of a budget — the columns the <c>payees</c> row carries, less one.</summary>
/// <param name="Id">The payee's identifier.</param>
/// <param name="BudgetId">The budget that owns it.</param>
/// <param name="Name">
/// The payee's <b>sealed</b> name as unpadded base64url, for the reason
/// <see cref="ExportedBudget.Name"/> gives about its own: the column is an AEAD envelope this server
/// cannot open, and the member keeps its name because the completeness check over this document maps a
/// table's columns onto a record's members.
/// </param>
/// <param name="CreatedAtUtc">The creation instant, in UTC.</param>
/// <remarks>
/// <b><c>name_key</c> is not here, and it is the one column this record deliberately omits</b> — the
/// treatment <see cref="ExportedAccount"/> already argues for its own. The blind index is derivable
/// from the name by anybody holding the account's index key, which is exactly who can read this file,
/// and is meaningless to anybody who is not. Shipping it would put a deterministic per-budget
/// fingerprint of every counterparty name into a document the requirement asks to be a copy of what a
/// person owns, not of what the server needs to police it — and on this table that fingerprint is the
/// more telling of the two, because a payee list is the set of counterparties one person deals with.
/// </remarks>
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
