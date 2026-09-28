using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// Stands in for a write that commits while the export is part-way through its reads: just before the
/// first command reading a chosen table is sent, it commits one change on its own connection — a new
/// account plus a transaction pointing at it, a second budget for the same owner, a category group
/// plus a category naming it plus a payee, or the deletion of an existing transaction.
/// </summary>
/// <remarks>
/// <para>
/// The export reads users, budgets, accounts, category groups, categories, payees and transactions, in
/// that order. A write landing between two of those reads is the case a consistent read has to
/// survive: without one, the document mixes the state before the write with the state after it.
/// Hooking the command arrives at that state on every run, where a genuinely concurrent writer would
/// arrive at it by luck.
/// </para>
/// <para>
/// The connection is its own, opened here rather than borrowed, for the reason
/// <see cref="ConcurrentDeleteInterceptor" /> gives: the point is that <em>another session</em>
/// committed. Written on the context's own connection, the change would sit inside whatever
/// transaction that context is in and would be visible to it by construction. The connection is the
/// container superuser, so neither policy nor grant stands between the write and the table.
/// </para>
/// <para>
/// <b>It does nothing until one of the <c>Arm…</c> methods says what to commit, and then fires
/// once.</b> The budget and the seeded rows are only known once the sign-in has seeded them, which is
/// after the factory carrying this interceptor has been built. Firing once keeps <see cref="Inserted" />
/// and <see cref="Deleted" /> meaning what a test reads them as — proof that the arrangement really
/// changed something and was not a no-op.
/// </para>
/// </remarks>
/// <param name="connectionString">The superuser connection string the out-of-band write runs on.</param>
/// <param name="table">
/// The table whose read the write is placed in front of, spelled as EF emits it after <c>FROM</c>.
/// </param>
/// <param name="afterTable">
/// When set, the interceptor only fires on a read of <paramref name="table" /> that comes after a read
/// of this table. This exists for <c>budgets</c>: session authentication reads <c>budgets</c> to find
/// the ambient budget before the endpoint runs, so the export's own budgets read is the one after its
/// <c>users</c> read, not the first one in the request.
/// </param>
public sealed class MidReadCommitInterceptor(
    string connectionString,
    string table = "transactions",
    string? afterTable = null) : DbCommandInterceptor
{
    private readonly Regex _readsTable = ReadOf(table);
    private readonly Regex? _readsAfterTable = afterTable is null ? null : ReadOf(afterTable);

    private PendingCommit? _pending;
    private bool _sawAfterTable;
    private int _fired;

    /// <summary>The account the insert mode creates.</summary>
    public Guid AccountId { get; } = Guid.CreateVersion7();

    /// <summary>The transaction the insert mode creates, pointing at <see cref="AccountId" />.</summary>
    public Guid TransactionId { get; } = Guid.CreateVersion7();

    /// <summary>The budget the budget-insert mode creates.</summary>
    public Guid BudgetId { get; } = Guid.CreateVersion7();

    /// <summary>The category group the category-set mode creates.</summary>
    public Guid CategoryGroupId { get; } = Guid.CreateVersion7();

    /// <summary>The category the category-set mode creates, filed under <see cref="CategoryGroupId" />.</summary>
    public Guid CategoryId { get; } = Guid.CreateVersion7();

    /// <summary>The payee the category-set mode creates.</summary>
    public Guid PayeeId { get; } = Guid.CreateVersion7();

    /// <summary>
    /// How many rows an insert mode committed: two for the account-and-transaction pair, one for a
    /// budget, three for the category set, zero before it fires or in delete mode.
    /// </summary>
    public int Inserted { get; private set; }

    /// <summary>
    /// How many rows the delete mode removed: one once it has fired, zero before or in insert mode.
    /// </summary>
    public int Deleted { get; private set; }

    /// <summary>The text of the command the write was placed in front of, once it has fired.</summary>
    public string? InterceptedCommandText { get; private set; }

    /// <summary>
    /// Arms the insert mode: commit <see cref="AccountId" /> and <see cref="TransactionId" /> into
    /// <paramref name="budgetId" />.
    /// </summary>
    public void ArmInsert(Guid budgetId) => _pending = new InsertPair(budgetId);

    /// <summary>
    /// Arms the budget-insert mode: commit <see cref="BudgetId" /> as a second budget owned by
    /// <paramref name="userId" />.
    /// </summary>
    public void ArmInsertBudget(Guid userId) => _pending = new InsertBudget(userId);

    /// <summary>
    /// Arms the category-set mode: commit <see cref="CategoryGroupId" />, <see cref="CategoryId" /> and
    /// <see cref="PayeeId" /> into <paramref name="budgetId" />.
    /// </summary>
    public void ArmInsertCategorySet(Guid budgetId) => _pending = new InsertCategorySet(budgetId);

    /// <summary>Arms the delete mode: delete the transaction <paramref name="transactionId" />.</summary>
    public void ArmDelete(Guid transactionId) => _pending = new DeleteTransaction(transactionId);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        if (ShouldFire(command) is { } pending)
        {
            using NpgsqlConnection connection = new(connectionString);
            connection.Open();
            using NpgsqlTransaction transaction = connection.BeginTransaction();
            using NpgsqlCommand write = BuildWrite(connection, transaction, pending);
            int affected = write.ExecuteNonQuery();
            transaction.Commit();
            Record(pending, affected);
        }

        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (ShouldFire(command) is { } pending)
        {
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using NpgsqlCommand write = BuildWrite(connection, transaction, pending);
            int affected = await write.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            Record(pending, affected);
        }

        return result;
    }

    /// <summary>
    /// A read whose <c>FROM</c> names <paramref name="name" /> as its source, the way EF spells it:
    /// <c>FROM transactions AS t</c>. Anchored on <c>FROM</c> so an <c>INSERT INTO …</c> carrying
    /// <c>RETURNING</c> — which EF also sends as a reader — does not match, and bounded on both sides
    /// so a table merely starting with the name does not either.
    /// </summary>
    private static Regex ReadOf(string name) =>
        new($"""\bFROM\s+"?{Regex.Escape(name)}"?(\s|$)""", RegexOptions.IgnoreCase);

    private PendingCommit? ShouldFire(DbCommand command)
    {
        if (_pending is not { } pending)
        {
            return null;
        }

        if (_readsAfterTable is not null && !_sawAfterTable)
        {
            _sawAfterTable = _readsAfterTable.IsMatch(command.CommandText);
            return null;
        }

        if (!_readsTable.IsMatch(command.CommandText) || Interlocked.Exchange(ref _fired, 1) == 1)
        {
            return null;
        }

        InterceptedCommandText = command.CommandText;
        return pending;
    }

    private void Record(PendingCommit pending, int affected)
    {
        switch (pending)
        {
            case InsertPair or InsertBudget or InsertCategorySet:
                Inserted = affected;
                break;
            case DeleteTransaction:
                Deleted = affected;
                break;
        }
    }

    private NpgsqlCommand BuildWrite(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PendingCommit pending) =>
        pending switch
        {
            InsertPair insert => BuildInsert(connection, transaction, insert.BudgetId),
            InsertBudget insert => BuildInsertBudget(connection, transaction, insert.UserId),
            InsertCategorySet insert => BuildInsertCategorySet(connection, transaction, insert.BudgetId),
            DeleteTransaction delete => BuildDelete(connection, transaction, delete.TransactionId),
            _ => throw new InvalidOperationException($"Unknown pending commit '{pending}'."),
        };

    /// <summary>
    /// Two inserts in one batch inside one transaction, so the pair commits together and the affected
    /// row counts sum to <see cref="Inserted" />. The account comes first because the transaction's
    /// composite foreign key names it.
    /// </summary>
    private NpgsqlCommand BuildInsert(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid budgetId)
    {
        const string Label = "Mid-export account";

        NpgsqlCommand command = new(
            """
            insert into accounts
                (id, budget_id, name, name_key, type, opening_balance, currency_code, created_at_utc)
            values
                (@account_id, @budget_id, @name, @name_key, 'Checking', 0, 'USD', @created_at_utc);
            insert into transactions
                (id, budget_id, account_id, amount, date, description, created_at_utc)
            values
                (@transaction_id, @budget_id, @account_id, -4.20, @date, null, @created_at_utc);
            """,
            connection,
            transaction);

        DateTime now = DateTime.UtcNow;
        command.Parameters.AddWithValue("account_id", AccountId);
        command.Parameters.AddWithValue("transaction_id", TransactionId);
        command.Parameters.AddWithValue("budget_id", budgetId);
        command.Parameters.AddWithValue("name", SealedNarrative.Name(Label).Envelope.ToArray());
        command.Parameters.AddWithValue("name_key", SealedNarrative.BlindIndex(Label).ToArray());
        command.Parameters.AddWithValue("date", DateOnly.FromDateTime(now));
        command.Parameters.AddWithValue("created_at_utc", now);
        return command;
    }

    /// <summary>
    /// A second, named budget for the owner. Named because <c>IX_budgets_user_id_name</c> is
    /// <c>UNIQUE … NULLS NOT DISTINCT</c> and the owner's first budget may be unnamed. Stamped now, so
    /// it is later than the provisioned budget and can never become the ambient one.
    /// </summary>
    private NpgsqlCommand BuildInsertBudget(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId)
    {
        NpgsqlCommand command = new(
            """
            insert into budgets (id, user_id, name, created_at_utc)
            values (@id, @user_id, @name, @created_at_utc)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", BudgetId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("name", SealedNarrative.Name("Mid-export budget").Envelope.ToArray());
        command.Parameters.AddWithValue("created_at_utc", DateTime.UtcNow);
        return command;
    }

    /// <summary>
    /// Three inserts in one batch inside one transaction: a group, a category naming it, and a payee.
    /// The group comes first because the category's composite foreign key names it. All three tables
    /// carry a <c>name_key</c> under a unique <c>(budget_id, name_key)</c> index, so each label is its
    /// own and differs from anything a test seeds.
    /// </summary>
    private NpgsqlCommand BuildInsertCategorySet(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid budgetId)
    {
        const string GroupLabel = "Mid-export group";
        const string CategoryLabel = "Mid-export category";
        const string PayeeLabel = "Mid-export payee";

        NpgsqlCommand command = new(
            """
            insert into category_groups
                (id, budget_id, name, name_key, description, position, created_at_utc)
            values
                (@group_id, @budget_id, @group_name, @group_name_key, null, 1, @created_at_utc);
            insert into categories
                (id, budget_id, category_group_id, name, name_key, description, position, created_at_utc)
            values
                (@category_id, @budget_id, @group_id, @category_name, @category_name_key, null, 0, @created_at_utc);
            insert into payees
                (id, budget_id, name, name_key, created_at_utc)
            values
                (@payee_id, @budget_id, @payee_name, @payee_name_key, @created_at_utc);
            """,
            connection,
            transaction);

        command.Parameters.AddWithValue("group_id", CategoryGroupId);
        command.Parameters.AddWithValue("category_id", CategoryId);
        command.Parameters.AddWithValue("payee_id", PayeeId);
        command.Parameters.AddWithValue("budget_id", budgetId);
        command.Parameters.AddWithValue("group_name", SealedNarrative.Name(GroupLabel).Envelope.ToArray());
        command.Parameters.AddWithValue("group_name_key", SealedNarrative.BlindIndex(GroupLabel).ToArray());
        command.Parameters.AddWithValue("category_name", SealedNarrative.Name(CategoryLabel).Envelope.ToArray());
        command.Parameters.AddWithValue("category_name_key", SealedNarrative.BlindIndex(CategoryLabel).ToArray());
        command.Parameters.AddWithValue("payee_name", SealedNarrative.Name(PayeeLabel).Envelope.ToArray());
        command.Parameters.AddWithValue("payee_name_key", SealedNarrative.BlindIndex(PayeeLabel).ToArray());
        command.Parameters.AddWithValue("created_at_utc", DateTime.UtcNow);
        return command;
    }

    private static NpgsqlCommand BuildDelete(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid transactionId)
    {
        NpgsqlCommand command = new("delete from transactions where id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", transactionId);
        return command;
    }

    private abstract record PendingCommit;

    private sealed record InsertPair(Guid BudgetId) : PendingCommit;

    private sealed record InsertBudget(Guid UserId) : PendingCommit;

    private sealed record InsertCategorySet(Guid BudgetId) : PendingCommit;

    private sealed record DeleteTransaction(Guid TransactionId) : PendingCommit;
}
