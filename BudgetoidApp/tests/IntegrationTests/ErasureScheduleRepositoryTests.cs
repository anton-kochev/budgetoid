using Domain.Erasure;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// What <see cref="ErasureScheduleRepository" /> answers when PostgreSQL refuses its insert: a second
/// schedule for one account reads back the first one's instant, and a violation of any other rule
/// escapes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The collision is staged rather than raced.</b> Two first requests from one account both find
/// nothing and both add; the loser's insert meets <c>PK_erasure_schedules</c>. Reaching that
/// interleaving with two concurrent HTTP requests is a coin flip that loses toward a false pass, so the
/// winner's row is committed first, on a connection of its own, and the add is made against it — which
/// is the loser's whole experience, reduced to the one statement it makes.
/// </para>
/// <para>
/// <b>The second rule broken is on another table, and that is not a preference.</b>
/// <see cref="ErasureScheduleConfiguration" /> gives <c>erasure_schedules</c> one unique rule, its
/// primary key, and one foreign key; a control staged on a neighbouring unique rule of the same table is
/// unreachable. So the intruder is the one <see cref="KeyRotationRepositoryTests" /> and
/// <see cref="PasskeyRepositoryTests" /> already use: a <c>users</c> row reusing a seeded account's
/// address, breaking <c>IX_users_email</c> with the same <c>23505</c>, tracked on the repository's own
/// context and put on the wire by the repository's own save. That is honest rather than contrived:
/// the save flushes the whole change tracker, and the context is request-scoped.
/// </para>
/// <para>
/// Driven on <see cref="RepositoryTestHost.ConnectionString" />, the container superuser, as
/// <see cref="KeyRotationRepositoryTests" /> is: half of what is asserted is that rows are
/// <b>absent</b>, and a policed connection reports a row that is there exactly as it reports one that is
/// not. What the role may and may not do on this table is <c>ErasureScheduleSchemaTests</c>' and
/// <c>AppRoleGrantMatrixTests</c>'.
/// </para>
/// </remarks>
public sealed class ErasureScheduleRepositoryTests
{
    /// <summary>
    /// The control for the two cases below: with nothing stored, the add stores the schedule it was
    /// handed and returns that instant.
    /// </summary>
    /// <remarks>
    /// Without it, an add that always threw would satisfy the escape case below, and one that always
    /// re-read would satisfy the collision case — neither tells a working insert apart from a broken one.
    /// </remarks>
    [Test]
    public async Task AddAsync_WhenNoRowExists_StoresAndReturnsTheGivenInstant()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync(GoogleSubject, OwnerEmail);
        await using BudgetoidDbContext db = CreateDb(host);
        ErasureScheduleRepository repository = new(db);
        ErasureSchedule schedule = ErasureSchedule.Request(userId, LaterRequestInstant, Delay);

        // Act
        ErasureSchedule stored = await repository.AddAsync(schedule);

        // Assert
        await Assert.That(stored.UserId).IsEqualTo(userId);
        await Assert.That(stored.TakesEffectAtUtc).IsEqualTo(LaterRequestInstant + Delay);
        await Assert.That(await StoredInstantsAsync(host, userId))
            .IsEquivalentTo(new[] { LaterRequestInstant + Delay });
    }

    /// <summary>
    /// A second schedule for an account that already holds one reads back the stored instant, and the
    /// table still holds one row carrying it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it holds: the translation half of the <c>catch</c> narrowed on
    /// <see cref="ErasureScheduleConfiguration.PrimaryKeyName" />.</b> Without it the loser of two racing
    /// first requests answers 500 to somebody whose erasure was in fact scheduled — or, if the
    /// translation answered the instant it computed instead of the one stored, tells them a date a few
    /// milliseconds later than the one the account holds. The two instants here are a day apart so the
    /// second failure cannot hide inside a rounding.
    /// </para>
    /// <para>
    /// <b>The context is saved once more afterwards, and that is the detach half.</b> The refused insert
    /// leaves an <c>Added</c> entry behind unless the catch takes it away, and the next save on the same
    /// request-scoped context — whatever it was for — would flush it again and meet the same key.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AddAsync_WhenARowAlreadyExists_ReturnsTheStoredInstant()
    {
        // Arrange — the winner's row, committed on a context of its own.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync(GoogleSubject, OwnerEmail);
        await using (BudgetoidDbContext winner = CreateDb(host))
        {
            winner.ErasureSchedules.Add(ErasureSchedule.Request(userId, EarlierRequestInstant, Delay));
            await winner.SaveChangesAsync();
        }

        await using BudgetoidDbContext db = CreateDb(host);
        ErasureScheduleRepository repository = new(db);
        ErasureSchedule loser = ErasureSchedule.Request(userId, LaterRequestInstant, Delay);

        // Act
        ErasureSchedule stored = await repository.AddAsync(loser);

        // Assert — the winner's instant, not the one this call was handed.
        await Assert.That(stored.UserId).IsEqualTo(userId);
        await Assert.That(stored.TakesEffectAtUtc).IsEqualTo(EarlierRequestInstant + Delay);

        // One row, carrying the winner's instant.
        await Assert.That(await StoredInstantsAsync(host, userId))
            .IsEquivalentTo(new[] { EarlierRequestInstant + Delay });

        // Nothing of the refused insert is left for a later save on this context to flush.
        Exception? later = await CaptureAsync(() => db.SaveChangesAsync());
        await Assert.That(later).IsNull();
    }

    /// <summary>
    /// A unique violation this repository does not model escapes, instead of being answered as a lost
    /// race over the account's schedule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it holds: the <c>ConstraintName</c> half of the <c>when</c> clause.</b> Widened to a bare
    /// <c>23505</c>, the catch would detach, re-read the account's schedule — find none, because the save
    /// rolled back — and either answer nothing as though it were a date or fall over on a null, for a
    /// rule on a table it does not write, keyed on a value a stranger chose.
    /// </para>
    /// <para>
    /// The SQLSTATE is asserted beside the constraint name, which keeps this a narrowing test rather
    /// than a test that any failure escapes: a <c>23503</c> from the foreign key would satisfy "not the
    /// primary key" without ever exercising the filter.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AddAsync_WhenTheConflictNamesAnotherConstraint_LetsItEscape()
    {
        // Arrange — an account with no schedule, so the primary key is free and the only rule broken is
        // the stranger's.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync(GoogleSubject, OwnerEmail);
        await using BudgetoidDbContext db = CreateDb(host);

        // The stranger's row, arriving with the seeded account's address. Tracked and never saved by this
        // test: the repository's own save is what puts it on the wire.
        Guid intruderId = Guid.CreateVersion7();
        db.Users.Add(User.CreateWithId(intruderId, OwnerEmail, SeedInstant));

        ErasureScheduleRepository repository = new(db);
        ErasureSchedule schedule = ErasureSchedule.Request(userId, LaterRequestInstant, Delay);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.AddAsync(schedule));

        // Assert — it escaped, as the violation the arrangement staged.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateException>();
        await Assert.That(SqlStateOf(escaped)).IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That(ConstraintNameOf(escaped)).IsEqualTo(UserEmailIndex);
        await Assert.That(ConstraintNameOf(escaped)).IsNotEqualTo(ErasureScheduleConfiguration.PrimaryKeyName);

        // Nothing landed, and the seeded account is untouched — without the second line, a save that
        // rolled the whole database back would satisfy the first.
        await Assert.That(await StoredInstantsAsync(host, userId)).IsEmpty();
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.Users.AnyAsync(user => user.Id == userId)).IsTrue();
        await Assert.That(await verify.Users.AnyAsync(user => user.Id == intruderId)).IsFalse();
    }

    /// <summary>
    /// With two accounts each holding a schedule, the read answers the named account's instant and not
    /// the other's.
    /// </summary>
    /// <remarks>
    /// <b>What it holds: the owner predicate in <c>FindAsync</c>.</b> On the app role the
    /// <c>user_isolation</c> policy would hide the stranger's row and cover for a missing predicate, so
    /// this runs on <see cref="RepositoryTestHost.ConnectionString" />, the superuser, where no policy
    /// applies and the entity carries no query filter — the predicate is the only thing choosing the row.
    /// The two instants are a day apart so the one answered names the account it came from.
    /// </remarks>
    [Test]
    public async Task FindAsync_ReturnsOnlyTheNamedAccountsSchedule()
    {
        // Arrange — two accounts, two schedules, committed on a context of their own.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid ownerId = await host.SeedUserAsync(GoogleSubject, OwnerEmail);
        Guid strangerId = await host.SeedUserAsync(StrangerGoogleSubject, StrangerEmail);
        await using (BudgetoidDbContext seed = CreateDb(host))
        {
            seed.ErasureSchedules.Add(ErasureSchedule.Request(strangerId, EarlierRequestInstant, Delay));
            seed.ErasureSchedules.Add(ErasureSchedule.Request(ownerId, LaterRequestInstant, Delay));
            await seed.SaveChangesAsync();
        }

        await using BudgetoidDbContext db = CreateDb(host);
        ErasureScheduleRepository repository = new(db);

        // Act
        ErasureSchedule? found = await repository.FindAsync(ownerId);

        // Assert
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.UserId).IsEqualTo(ownerId);
        await Assert.That(found.TakesEffectAtUtc).IsEqualTo(LaterRequestInstant + Delay);
    }

    private const string GoogleSubject = "google-erasure-schedule-owner";

    private const string StrangerGoogleSubject = "google-erasure-schedule-stranger";

    private const string StrangerEmail = "erasure-schedule-stranger@example.com";

    private const string OwnerEmail = "erasure-schedule-owner@example.com";

    /// <summary>
    /// The unique index <c>users.email</c> carries, spelled out rather than read off a configuration, for
    /// the reason <see cref="KeyRotationRepositoryTests" /> gives: what this file is about is what
    /// PostgreSQL <em>reports</em>.
    /// </summary>
    private const string UserEmailIndex = "IX_users_email";

    /// <summary>
    /// The delay every schedule here is filed with. Not the product's policy — nothing in the repository
    /// reads it — only a valid one.
    /// </summary>
    private static readonly TimeSpan Delay = TimeSpan.FromDays(7);

    /// <summary>The winner's request instant, a day before the loser's.</summary>
    private static readonly DateTime EarlierRequestInstant = new(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>The loser's request instant.</summary>
    private static readonly DateTime LaterRequestInstant = new(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// Fixed UTC instant for the intruder row. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Every <c>takes_effect_at_utc</c> filed under <paramref name="userId" />, read in raw SQL on the
    /// superuser connection so nothing a context tracked can answer for the table.
    /// </summary>
    private static async Task<DateTime[]> StoredInstantsAsync(RepositoryTestHost host, Guid userId)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select takes_effect_at_utc from erasure_schedules where user_id = @user_id", connection);
        command.Parameters.AddWithValue("user_id", userId);

        List<DateTime> instants = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            instants.Add(reader.GetFieldValue<DateTime>(0));
        }

        return [.. instants];
    }

    private static string? ConstraintNameOf(Exception? exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
            ? postgresException.ConstraintName
            : null;

    private static string? SqlStateOf(Exception? exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
            ? postgresException.SqlState
            : null;

    /// <summary>
    /// Runs <paramref name="action" /> and hands back whatever escaped, or <see langword="null" />.
    /// Untyped on purpose: which exception surfaces is the question.
    /// </summary>
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// A context with no ambient budget, which is safe because <c>erasure_schedules</c> carries no
    /// budget query filter — it is policed on the user.
    /// </summary>
    private static BudgetoidDbContext CreateDb(RepositoryTestHost host) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options);

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();

        return host;
    }
}
