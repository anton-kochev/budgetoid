using System.Data.Common;
using Application.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.Persistence;

/// <summary>
/// Puts the signed-in user and the ambient budget on every connection the context opens, as the
/// PostgreSQL session settings <c>app.current_user_id</c> and <c>app.current_budget_id</c> that the
/// <c>user_isolation</c> and <c>budget_isolation</c> row-level security policies read. This is the
/// application half of a rule the database owns: the policies decide, this only tells them who is
/// asking.
/// </summary>
/// <remarks>
/// <para>
/// Connection-opened, rather than middleware or a command interceptor, because the connection is
/// the only thing the settings' lifetime can be tied to. EF Relational opens and closes it per
/// operation and nothing here pins it open, so a value written once in middleware evaporates when
/// the connection returns to the pool, and a <c>set_config</c> issued inside a transaction is undone
/// by a rollback. Retries re-open too, so this is also what survives
/// <c>NpgsqlRetryingExecutionStrategy</c>. It is also why the handlers that establish an identity
/// publish it into the request scope rather than issuing their own <c>set_config</c>: the identity
/// reaches the database on the next connection open, never mid-statement.
/// </para>
/// <para>
/// Both settings travel in one statement, so the session is never observable half-configured and
/// the second one costs no extra round-trip.
/// </para>
/// <para>
/// Both the sync and async overloads are overridden: some EF paths still open connections
/// synchronously, and an unoverridden one would hand a policied query a session naming nobody.
/// </para>
/// </remarks>
public sealed class SessionContextInterceptor(IBudgetContext budgetContext, IUserContext userContext)
    : DbConnectionInterceptor
{
    // set_config(..., false) — session scope, not SET LOCAL. Verified against PostgreSQL 17: most
    // operations here run in autocommit, and outside a transaction block SET LOCAL sets nothing and
    // only warns ("SET LOCAL can only be used in transaction blocks").
    private const string SetSessionContextSql =
        """
        select set_config('app.current_user_id', @user, false),
               set_config('app.current_budget_id', @budget, false)
        """;

    private const string UserParameterName = "user";

    private const string BudgetParameterName = "budget";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using DbCommand command = CreateSetSessionContextCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using DbCommand command = CreateSetSessionContextCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateSetSessionContextCommand(DbConnection connection)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = SetSessionContextSql;

        // The Resolved* accessors, never the throwing ones: unresolved is a legitimate state for both
        // — credential discovery runs before any identity exists, session authentication reads the
        // account's budget before a budget id does, and infrastructure scopes such as health checks
        // resolve neither — so
        // catching their throw would be exception-as-control-flow for a case that is not exceptional.
        //
        // Unresolved writes '' rather than skipping the setting, which would leave correctness
        // resting on Npgsql's pool reset having cleared the previous logical session's value. Writing
        // both unconditionally makes any policied query on such a connection fail loudly with 22P02
        // whatever the pool is configured to do, and is what stops a pooled connection carrying one
        // request's identity into the next.
        //
        // As text, because set_config takes text: bind the Guid and Npgsql infers uuid, which no
        // set_config overload accepts.
        AddTextParameter(command, UserParameterName, userContext.ResolvedUserId);
        AddTextParameter(command, BudgetParameterName, budgetContext.ResolvedBudgetId);

        return command;
    }

    private static void AddTextParameter(DbCommand command, string name, Guid? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value?.ToString() ?? string.Empty;
        command.Parameters.Add(parameter);
    }
}
