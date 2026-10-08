using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Stands in for a second email change of the SAME account to the SAME Google identity, committing
/// just ahead of this request's save: it retires the account's federated credential, files one under
/// the new subject, and moves the address — all on its own connection, in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// A sibling of <see cref="ConcurrentDeleteInterceptor" /> rather than another mode of it, because the
/// two differ in when they fire. That one fires on the first save it sees; this one is registered on an
/// API factory whose scoped contexts save many times before the email change does — passkey
/// registration, the re-authentication challenge, the session sweep — so it fires only on the save
/// whose change tracker holds a <see cref="Credential" /> marked Deleted. The email change's
/// <c>ApplyAsync</c> is the one save in that request that does.
/// </para>
/// <para>
/// <b>It does nothing until <see cref="Arm" /> names the account, and then fires once.</b> The
/// account and its credential exist only after the sign-in, which is after the factory carrying this
/// interceptor is built. Firing once keeps <see cref="Affected" /> meaning what a test reads it as:
/// proof that the race was staged and was not a no-op.
/// </para>
/// <para>
/// Only the asynchronous save is hooked, because that is the one the repository makes; a synchronous
/// save would leave <see cref="Affected" /> at zero, which the test reads.
/// </para>
/// <para>
/// The connection is its own and is the container superuser, for the reason
/// <see cref="MidReadCommitInterceptor" /> gives: the point is that <em>another session</em> committed,
/// and neither policy nor grant may stand between that write and the tables.
/// </para>
/// </remarks>
/// <param name="connectionString">The superuser connection string the racing change commits on.</param>
public sealed class RacingIdentityChangeInterceptor(string connectionString) : SaveChangesInterceptor
{
    private PendingChange? _pending;
    private int _fired;

    /// <summary>The id of the federated credential the racing change files.</summary>
    public Guid RacerCredentialId { get; } = Guid.CreateVersion7();

    /// <summary>
    /// How many rows the racing change touched: three once it has fired — one credential deleted, one
    /// filed, one address moved — and zero before.
    /// </summary>
    public int Affected { get; private set; }

    /// <summary>
    /// Arms the race: retire <paramref name="retiredCredentialId" />, file <see cref="RacerCredentialId" />
    /// for <paramref name="userId" /> under <paramref name="subject" />, and set the account's address to
    /// <paramref name="email" />.
    /// </summary>
    public void Arm(Guid userId, Guid retiredCredentialId, string subject, string email) =>
        _pending = new PendingChange(userId, retiredCredentialId, subject, email);

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (ShouldFire(eventData.Context) is { } pending)
        {
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using NpgsqlCommand command = BuildChange(connection, transaction, pending);
            Affected = await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    private PendingChange? ShouldFire(DbContext? context)
    {
        if (_pending is not { } pending || context is null)
        {
            return null;
        }

        bool retiresACredential = context.ChangeTracker
            .Entries<Credential>()
            .Any(entry => entry.State == EntityState.Deleted);

        if (!retiresACredential || Interlocked.Exchange(ref _fired, 1) == 1)
        {
            return null;
        }

        return pending;
    }

    /// <summary>
    /// Three statements in one batch inside one transaction, so the change commits whole and the
    /// affected row counts sum to <see cref="Affected" />. The delete comes first because
    /// <c>IX_credentials_user_id_federated</c> allows one federated row per account.
    /// </summary>
    private NpgsqlCommand BuildChange(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PendingChange pending)
    {
        NpgsqlCommand command = new(
            """
            delete from credentials where id = @retired_id;
            insert into credentials (id, user_id, type, provider, subject, created_at_utc)
            values (@racer_id, @user_id, 'federated', @provider, @subject, @created_at_utc);
            update users set email = @email where id = @user_id;
            """,
            connection,
            transaction);

        command.Parameters.AddWithValue("retired_id", pending.RetiredCredentialId);
        command.Parameters.AddWithValue("racer_id", RacerCredentialId);
        command.Parameters.AddWithValue("user_id", pending.UserId);
        command.Parameters.AddWithValue("provider", Credential.GoogleProvider);
        command.Parameters.AddWithValue("subject", pending.Subject);
        command.Parameters.AddWithValue("email", pending.Email);
        command.Parameters.AddWithValue("created_at_utc", DateTime.UtcNow);
        return command;
    }

    private sealed record PendingChange(Guid UserId, Guid RetiredCredentialId, string Subject, string Email);
}
