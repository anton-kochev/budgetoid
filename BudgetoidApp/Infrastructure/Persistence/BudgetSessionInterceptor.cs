using System.Data.Common;
using Application.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.Persistence;

/// <summary>
/// Puts the ambient budget on every connection the context opens, as the PostgreSQL session setting
/// <c>app.current_budget_id</c> that the <c>budget_isolation</c> row-level security policies read.
/// This is the application half of a rule the database owns: the policies decide, this only tells
/// them who is asking.
/// </summary>
/// <remarks>
/// <para>
/// Connection-opened, rather than middleware or a command interceptor, because the connection is
/// the only thing the setting's lifetime can be tied to. EF Relational opens and closes it per
/// operation and nothing here pins it open, so a value written once in middleware evaporates when
/// the connection returns to the pool, and a <c>set_config</c> issued inside a transaction is undone
/// by a rollback. Retries re-open too, so this is also what survives
/// <c>NpgsqlRetryingExecutionStrategy</c>.
/// </para>
/// <para>
/// Both the sync and async overloads are overridden: some EF paths still open connections
/// synchronously, and an unoverridden one would hand a policed query a session naming no budget.
/// </para>
/// </remarks>
public sealed class BudgetSessionInterceptor(IBudgetContext budgetContext) : DbConnectionInterceptor
{
    // set_config(..., false) — session scope, not SET LOCAL. Verified against PostgreSQL 17: most
    // operations here run in autocommit, and outside a transaction block SET LOCAL sets nothing and
    // only warns ("SET LOCAL can only be used in transaction blocks").
    private const string SetBudgetSql = "select set_config('app.current_budget_id', @budget, false)";

    private const string ParameterName = "budget";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using DbCommand command = CreateSetBudgetCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using DbCommand command = CreateSetBudgetCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateSetBudgetCommand(DbConnection connection)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = SetBudgetSql;

        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = ParameterName;

        // ResolvedBudgetId, never BudgetId: unresolved is a legitimate state — user provisioning
        // queries users and budgets before a budget id exists, and infrastructure scopes such as
        // health checks never resolve one, neither of which touches a policied table — so catching
        // BudgetId's throw would be exception-as-control-flow for a case that is not exceptional.
        //
        // Unresolved writes '' rather than skipping the statement, which would leave correctness
        // resting on Npgsql's pool reset having cleared the previous logical session's value.
        // Writing it makes any policied query on such a connection fail loudly with 22P02 whatever
        // the pool is configured to do — one round-trip on two non-tenant paths for defence in
        // depth on the security-critical one.
        //
        // As text, because set_config takes text: bind the Guid and Npgsql infers uuid, which no
        // set_config overload accepts.
        parameter.Value = budgetContext.ResolvedBudgetId?.ToString() ?? string.Empty;
        command.Parameters.Add(parameter);

        return command;
    }
}
