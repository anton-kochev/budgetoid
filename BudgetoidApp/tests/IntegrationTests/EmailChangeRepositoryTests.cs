using System.Security.Cryptography;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// What <see cref="EmailChangeRepository" /> reads and writes for one account's email change, measured
/// on the least-privilege connection with the account's identity on the session.
/// </summary>
/// <remarks>
/// <para>
/// <b>The app role, never the superuser, for every act.</b> The write surface this repository rests on
/// is a grant shape: <c>users</c> takes <c>UPDATE (email)</c> and nothing else, and <c>credentials</c>
/// takes <c>INSERT</c> and <c>DELETE</c> and no <c>UPDATE</c>. A superuser skips every privilege check,
/// so an update that also wrote <c>created_at_utc</c>, or rewrote a subject in place, would pass there
/// and die with <c>42501</c> in production. Arrangement and read-back go through the superuser, because
/// they are not what is measured and half the read-backs are about a row being absent.
/// </para>
/// <para>
/// <b>Every act loads and decides the way <c>ChangeEmailHandler</c> does</b> — through the repository's
/// own reads, then <see cref="FederatedIdentityChange.Decide" />, then a cleared change tracker whenever
/// a credential is retired, because the handler discards tracked entities after its session sweep. So
/// the retired credential reaches <see cref="EmailChangeRepository.ApplyAsync" /> detached on exactly the
/// paths it does in production.
/// </para>
/// </remarks>
public sealed class EmailChangeRepositoryTests
{
    private const string OwnSubject = "google-email-change-own";

    private const string OwnEmail = "email-change-own@example.com";

    private const string NewSubject = "google-email-change-new";

    private const string NewEmail = "email-change-new@example.com";

    /// <summary>
    /// The stranger's subject, and it sorts <b>before</b> <see cref="OwnSubject" /> on purpose.
    /// </summary>
    /// <remarks>
    /// A read that lost its owner predicate returns whichever federated row PostgreSQL reaches first,
    /// and the planner may walk <c>IX_credentials_provider_subject</c>, which is ordered by subject.
    /// Measured: with the stranger named <c>…-stranger</c>, after <c>…-own</c>, an owner-less
    /// <c>FirstOrDefault</c> read passed the find test. Sorting first in subject order, in heap order and
    /// in <c>user_id</c> order is what makes the stranger the row such a read returns.
    /// </remarks>
    private const string StrangerSubject = "google-email-change-another";

    private const string StrangerEmail = "email-change-stranger@example.com";

    /// <summary>
    /// The primary key over <c>credentials.id</c>, spelled out rather than read off a configuration —
    /// EF's convention names it and nothing declares a constant, and this file's subject is what
    /// PostgreSQL reports.
    /// </summary>
    private const string CredentialPrimaryKeyName = "PK_credentials";

    /// <summary>
    /// The instant <see cref="RepositoryTestHost" /> stamps every seeded row with, restated so the
    /// <c>created_at_utc</c> assertion compares against a value rather than against a second read.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The read hands back this account's federated credential and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both predicates are pinned by the order the rows are written in.</b> <c>credentials</c> is
    /// exempt from row-level security, so the owner in the <c>WHERE</c> is the only scope. The stranger
    /// is seeded first, so a read that dropped the owner predicate and took the first federated row
    /// hands over the stranger's credential. This account files a passkey <em>before</em> its federated
    /// credential, so a read that dropped the type predicate and took the account's first row hands over
    /// the passkey — which <c>Decide</c> would then refuse, or worse, retire.
    /// </para>
    /// <para>
    /// A <c>SingleOrDefault</c> without either predicate throws instead, which is red as well.
    /// </para>
    /// </remarks>
    [Test]
    public async Task FindFederatedCredentialAsync_ReturnsOnlyThisAccountsFederatedCredential()
    {
        // Arrange — a stranger first, then this account with its passkey ahead of its federated row.
        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedUserAsync(StrangerSubject, StrangerEmail);

        Guid userId = Guid.CreateVersion7();
        Guid federatedId;
        await using (BudgetoidDbContext seed = SuperuserDb(host))
        {
            seed.Users.Add(User.CreateWithId(userId, OwnEmail, SeedInstant));
            await seed.SaveChangesAsync();
        }

        await host.SeedPasskeyAsync(userId, RandomNumberGenerator.GetBytes(16));

        await using (BudgetoidDbContext seed = SuperuserDb(host))
        {
            Credential federated = Credential.CreateFederated(
                userId, Credential.GoogleProvider, OwnSubject, SeedInstant);
            seed.Credentials.Add(federated);
            await seed.SaveChangesAsync();
            federatedId = federated.Id;
        }

        Guid budgetId = await host.SeedAdditionalBudgetAsync(userId, "email-change-find");

        await using BudgetoidDbContext db = AppDb(host, userId, budgetId);
        EmailChangeRepository repository = new(db);

        // Act
        Credential? found = await repository.FindFederatedCredentialAsync(userId);

        // Assert
        await Assert.That(found).IsNotNull();
        if (found is null)
        {
            return;
        }

        await Assert.That(found.Id).IsEqualTo(federatedId);
        await Assert.That(found.UserId).IsEqualTo(userId);
        await Assert.That(found.Type).IsEqualTo(CredentialType.Federated);
        await Assert.That(found.Subject).IsEqualTo(OwnSubject);
    }

    /// <summary>
    /// A new subject retires the old credential, files the new one and moves the address, and all three
    /// land.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This settles whether EF orders the DELETE ahead of the INSERT in one save.</b>
    /// <c>IX_credentials_user_id_federated</c> allows one federated row per account, so an INSERT sent
    /// first collides with the row the DELETE is about to remove and this test reds with a <c>23505</c> on
    /// that index — or with <see cref="EmailChangeOutcome.FederatedCredentialMoved" />, if the repository
    /// maps that index and so dresses its own ordering up as a race.
    /// </para>
    /// <para>
    /// <c>created_at_utc</c> is read back because a detached <c>Update(user)</c> writes it, and the app
    /// role has no grant on it: that shape dies with <c>42501</c> here rather than passing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ApplyAsync_WithANewSubject_DeletesTheRetiredInsertsTheFiledAndUpdatesTheEmail_InOneSave()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnSubject, OwnEmail);

        await using BudgetoidDbContext db = AppDb(host, owner.UserId, owner.BudgetId);
        EmailChangeRepository repository = new(db);
        FederatedIdentityChange change = await DecideLikeTheHandlerAsync(
            db, repository, owner.UserId, NewSubject, NewEmail);
        Guid retiredId = (change.Retired ?? throw new InvalidOperationException("A new subject decided no retirement.")).Id;
        Guid filedId = (change.Filed ?? throw new InvalidOperationException("A new subject decided no filing.")).Id;

        // Act
        EmailChangeOutcome outcome = await repository.ApplyAsync(change, owner.UserId);

        // Assert
        await Assert.That(outcome).IsEqualTo(EmailChangeOutcome.Applied);

        await using BudgetoidDbContext verify = SuperuserDb(host);
        List<Credential> federated = await FederatedCredentialsOf(verify, owner.UserId);
        await Assert.That(federated.Count).IsEqualTo(1);
        await Assert.That(federated[0].Id).IsEqualTo(filedId);
        await Assert.That(federated[0].Subject).IsEqualTo(NewSubject);
        await Assert.That(await verify.Credentials.AnyAsync(credential => credential.Id == retiredId))
            .IsFalse();

        User stored = await verify.Users.SingleAsync(user => user.Id == owner.UserId);
        await Assert.That(stored.Email.Value).IsEqualTo(NewEmail);
        await Assert.That(stored.CreatedAtUtc).IsEqualTo(SeedInstant);
    }

    /// <summary>
    /// An address-only change writes one statement, to <c>users</c>, naming the address and nothing
    /// else.
    /// </summary>
    /// <remarks>
    /// Asserted on the wire through <see cref="StatementRecorder" />: no write touches <c>credentials</c>
    /// — there is nothing to retire or file — and the <c>UPDATE users</c> does not name
    /// <c>created_at_utc</c>, the column a detached <c>Update(user)</c> would drag along.
    /// </remarks>
    [Test]
    public async Task ApplyAsync_WithAnAddressOnlyChange_UpdatesOnlyTheEmail()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnSubject, OwnEmail);
        Guid federatedId = await host.FederatedCredentialIdAsync(owner.UserId);

        StatementRecorder recorder = new();
        await using BudgetoidDbContext db = AppDb(host, owner.UserId, owner.BudgetId, recorder);
        EmailChangeRepository repository = new(db);
        FederatedIdentityChange change = await DecideLikeTheHandlerAsync(
            db, repository, owner.UserId, OwnSubject, NewEmail);

        // Act
        EmailChangeOutcome outcome = await repository.ApplyAsync(change, owner.UserId);

        // Assert
        await Assert.That(outcome).IsEqualTo(EmailChangeOutcome.Applied);

        await Assert.That(recorder.WritesTo("credentials")).IsEmpty();
        IReadOnlyList<string> userWrites = recorder.WritesTo("users");
        await Assert.That(userWrites.Count).IsEqualTo(1);
        await Assert.That(userWrites[0].TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            .IsTrue();
        await Assert.That(userWrites[0]).Contains("email");
        await Assert.That(userWrites[0]).DoesNotContain("created_at_utc");

        await using BudgetoidDbContext verify = SuperuserDb(host);
        User stored = await verify.Users.SingleAsync(user => user.Id == owner.UserId);
        await Assert.That(stored.Email.Value).IsEqualTo(NewEmail);
        List<Credential> federated = await FederatedCredentialsOf(verify, owner.UserId);
        await Assert.That(federated.Count).IsEqualTo(1);
        await Assert.That(federated[0].Id).IsEqualTo(federatedId);
    }

    /// <summary>
    /// An address another account holds refuses the whole save: the credential swap riding along with
    /// it does not land either.
    /// </summary>
    /// <remarks>
    /// The change carries a <b>new subject</b> as well as the taken address, so "changes nothing" covers
    /// the credentials too. A repository that saved the credential swap and the address in two saves,
    /// with no transaction of its own, commits the swap and then answers
    /// <see cref="EmailChangeOutcome.EmailTaken" /> — the port says a refusal writes nothing. The subject
    /// is free, so the address is the one rule broken and the answer cannot turn on write order.
    /// </remarks>
    [Test]
    public async Task ApplyAsync_ToAnAddressAnotherAccountHolds_AnswersEmailTaken_AndChangesNothing()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner stranger = await host.SeedOwnerAsync(StrangerSubject, StrangerEmail);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnSubject, OwnEmail);
        Guid ownFederatedId = await host.FederatedCredentialIdAsync(owner.UserId);

        await using BudgetoidDbContext db = AppDb(host, owner.UserId, owner.BudgetId);
        EmailChangeRepository repository = new(db);
        FederatedIdentityChange change = await DecideLikeTheHandlerAsync(
            db, repository, owner.UserId, NewSubject, StrangerEmail);

        // Act
        EmailChangeOutcome outcome = await repository.ApplyAsync(change, owner.UserId);

        // Assert
        await Assert.That(outcome).IsEqualTo(EmailChangeOutcome.EmailTaken);

        await using BudgetoidDbContext verify = SuperuserDb(host);
        User stored = await verify.Users.SingleAsync(user => user.Id == owner.UserId);
        await Assert.That(stored.Email.Value).IsEqualTo(OwnEmail);

        List<Credential> federated = await FederatedCredentialsOf(verify, owner.UserId);
        await Assert.That(federated.Count).IsEqualTo(1);
        await Assert.That(federated[0].Id).IsEqualTo(ownFederatedId);
        await Assert.That(federated[0].Subject).IsEqualTo(OwnSubject);
        await Assert.That(await verify.Credentials.AnyAsync(credential => credential.Subject == NewSubject))
            .IsFalse();

        User strangerRow = await verify.Users.SingleAsync(user => user.Id == stranger.UserId);
        await Assert.That(strangerRow.Email.Value).IsEqualTo(StrangerEmail);
    }

    /// <summary>
    /// The address index is case-insensitive, so an address differing from another account's only in
    /// case is taken too.
    /// </summary>
    /// <remarks>
    /// <c>Decide</c> compares ordinally, so it calls this a change; the refusal is the collation's, and
    /// the repository has to translate it rather than let a <c>23505</c> escape as a 500.
    /// </remarks>
    [Test]
    public async Task ApplyAsync_ToAnAddressAnotherAccountHoldsInAnotherCase_AnswersEmailTaken()
    {
        // Arrange
        const string heldByStranger = "Taken.Case@Example.com";
        const string askedFor = "taken.case@EXAMPLE.COM";

        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedOwnerAsync(StrangerSubject, heldByStranger);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnSubject, OwnEmail);

        await using BudgetoidDbContext db = AppDb(host, owner.UserId, owner.BudgetId);
        EmailChangeRepository repository = new(db);
        FederatedIdentityChange change = await DecideLikeTheHandlerAsync(
            db, repository, owner.UserId, OwnSubject, askedFor);

        // Act
        EmailChangeOutcome outcome = await repository.ApplyAsync(change, owner.UserId);

        // Assert
        await Assert.That(outcome).IsEqualTo(EmailChangeOutcome.EmailTaken);

        await using BudgetoidDbContext verify = SuperuserDb(host);
        User stored = await verify.Users.SingleAsync(user => user.Id == owner.UserId);
        await Assert.That(stored.Email.Value).IsEqualTo(OwnEmail);
    }

    /// <summary>
    /// Re-casing the account's own address is a change that lands: the row does not collide with itself
    /// under the nondeterministic collation.
    /// </summary>
    /// <remarks>
    /// Stored as asked, byte for byte — the collation says who else may hold an address, not how this
    /// one is spelled.
    /// </remarks>
    [Test]
    public async Task ApplyAsync_ToTheSameAddressInAnotherCase_OnTheSameAccount_Succeeds()
    {
        // Arrange
        const string recased = "Email-Change-OWN@Example.com";

        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnSubject, OwnEmail);

        await using BudgetoidDbContext db = AppDb(host, owner.UserId, owner.BudgetId);
        EmailChangeRepository repository = new(db);
        FederatedIdentityChange change = await DecideLikeTheHandlerAsync(
            db, repository, owner.UserId, OwnSubject, recased);

        // Act
        EmailChangeOutcome outcome = await repository.ApplyAsync(change, owner.UserId);

        // Assert
        await Assert.That(outcome).IsEqualTo(EmailChangeOutcome.Applied);

        await using BudgetoidDbContext verify = SuperuserDb(host);
        User stored = await verify.Users.SingleAsync(user => user.Id == owner.UserId);
        await Assert.That(string.Equals(stored.Email.Value, recased, StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// A subject another account's credential holds refuses the save, and neither account moves.
    /// </summary>
    /// <remarks>
    /// The address in the change is free, so the provider-subject index is the one rule broken. This
    /// account keeps its old credential and address; the stranger keeps its credential, still one row
    /// on that subject.
    /// </remarks>
    [Test]
    public async Task ApplyAsync_WithASubjectAnotherAccountHolds_AnswersSubjectTaken_AndChangesNeitherAccount()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner stranger = await host.SeedOwnerAsync(StrangerSubject, StrangerEmail);
        Guid strangerFederatedId = await host.FederatedCredentialIdAsync(stranger.UserId);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnSubject, OwnEmail);
        Guid ownFederatedId = await host.FederatedCredentialIdAsync(owner.UserId);

        await using BudgetoidDbContext db = AppDb(host, owner.UserId, owner.BudgetId);
        EmailChangeRepository repository = new(db);
        FederatedIdentityChange change = await DecideLikeTheHandlerAsync(
            db, repository, owner.UserId, StrangerSubject, NewEmail);

        // Act
        EmailChangeOutcome outcome = await repository.ApplyAsync(change, owner.UserId);

        // Assert
        await Assert.That(outcome).IsEqualTo(EmailChangeOutcome.SubjectTaken);

        await using BudgetoidDbContext verify = SuperuserDb(host);
        User stored = await verify.Users.SingleAsync(user => user.Id == owner.UserId);
        await Assert.That(stored.Email.Value).IsEqualTo(OwnEmail);
        List<Credential> own = await FederatedCredentialsOf(verify, owner.UserId);
        await Assert.That(own.Count).IsEqualTo(1);
        await Assert.That(own[0].Id).IsEqualTo(ownFederatedId);

        List<Credential> onTheSubject = await verify.Credentials
            .Where(credential => credential.Type == CredentialType.Federated
                                 && credential.Subject == StrangerSubject)
            .ToListAsync();
        await Assert.That(onTheSubject.Count).IsEqualTo(1);
        await Assert.That(onTheSubject[0].Id).IsEqualTo(strangerFederatedId);
        await Assert.That(onTheSubject[0].UserId).IsEqualTo(stranger.UserId);
    }

    /// <summary>
    /// A retired credential somebody else already deleted answers
    /// <see cref="EmailChangeOutcome.FederatedCredentialMoved" /> rather than a 500, and files nothing.
    /// </summary>
    /// <remarks>
    /// <see cref="ConcurrentDeleteInterceptor" /> commits the delete on its own superuser connection in
    /// the window between the read and the save, so the repository's DELETE affects zero rows and EF
    /// raises <see cref="DbUpdateConcurrencyException" />. <see cref="ConcurrentDeleteInterceptor.Deleted" />
    /// is read so the arrangement cannot have been a no-op.
    /// </remarks>
    [Test]
    public async Task ApplyAsync_WhenTheRetiredCredentialWasAlreadyDeleted_AnswersFederatedCredentialMoved()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnSubject, OwnEmail);
        Guid ownFederatedId = await host.FederatedCredentialIdAsync(owner.UserId);

        ConcurrentDeleteInterceptor racer = new(
            host.ConnectionString, "delete from credentials where id = @id", ownFederatedId);
        await using BudgetoidDbContext db = AppDb(host, owner.UserId, owner.BudgetId, racer);
        EmailChangeRepository repository = new(db);
        FederatedIdentityChange change = await DecideLikeTheHandlerAsync(
            db, repository, owner.UserId, NewSubject, NewEmail);

        // Act
        EmailChangeOutcome outcome = await repository.ApplyAsync(change, owner.UserId);

        // Assert
        await Assert.That(racer.Deleted).IsEqualTo(1);
        await Assert.That(outcome).IsEqualTo(EmailChangeOutcome.FederatedCredentialMoved);

        await using BudgetoidDbContext verify = SuperuserDb(host);
        User stored = await verify.Users.SingleAsync(user => user.Id == owner.UserId);
        await Assert.That(stored.Email.Value).IsEqualTo(OwnEmail);
        await Assert.That(await verify.Credentials.AnyAsync(credential => credential.Subject == NewSubject))
            .IsFalse();
    }

    private const string RacerSubject = "google-email-change-racer";

    /// <summary>
    /// The realistic race: another change of this account already retired the same credential <b>and
    /// filed its own</b>. The answer is the same, and the winner's credential survives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here the loser's DELETE affects zero rows <em>and</em> its INSERT meets the winner's row on
    /// <c>IX_credentials_user_id_federated</c>. Both must read as
    /// <see cref="EmailChangeOutcome.FederatedCredentialMoved" />. Measured against a scratch
    /// implementation: what surfaces is the <c>23505</c> on that index, not the concurrency exception —
    /// dropping only the index's catch reds this test and nothing else in the file. So this is the test
    /// that holds the <c>IX_credentials_user_id_federated</c> mapping; the one above holds the
    /// concurrency catch.
    /// </para>
    /// <para>
    /// The racer's user id is written into the SQL as a literal because
    /// <see cref="ConcurrentDeleteInterceptor" /> binds one parameter; it is a <see cref="Guid" /> this
    /// test minted, never input.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ApplyAsync_WhenARacingChangeAlreadyReplacedTheRetiredCredential_AnswersFederatedCredentialMoved()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnSubject, OwnEmail);
        Guid ownFederatedId = await host.FederatedCredentialIdAsync(owner.UserId);

        string racerSql =
            "delete from credentials where id = @id; "
            + "insert into credentials (id, user_id, type, provider, subject, created_at_utc) "
            + $"values (gen_random_uuid(), '{owner.UserId}', 'federated', 'google', '{RacerSubject}', now())";
        ConcurrentDeleteInterceptor racer = new(host.ConnectionString, racerSql, ownFederatedId);
        await using BudgetoidDbContext db = AppDb(host, owner.UserId, owner.BudgetId, racer);
        EmailChangeRepository repository = new(db);
        FederatedIdentityChange change = await DecideLikeTheHandlerAsync(
            db, repository, owner.UserId, NewSubject, NewEmail);

        // Act
        EmailChangeOutcome outcome = await repository.ApplyAsync(change, owner.UserId);

        // Assert
        await Assert.That(outcome).IsEqualTo(EmailChangeOutcome.FederatedCredentialMoved);

        await using BudgetoidDbContext verify = SuperuserDb(host);
        List<Credential> federated = await FederatedCredentialsOf(verify, owner.UserId);
        await Assert.That(federated.Count).IsEqualTo(1);
        await Assert.That(federated[0].Subject).IsEqualTo(RacerSubject);
        User stored = await verify.Users.SingleAsync(user => user.Id == owner.UserId);
        await Assert.That(stored.Email.Value).IsEqualTo(OwnEmail);
    }

    /// <summary>
    /// A unique violation none of the three mapped names covers escapes, instead of being answered as
    /// one of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Staged on <c>PK_credentials</c>, the only other unique rule this save can reach.</b> The save
    /// writes one <c>users</c> column (<c>IX_users_email</c>, mapped) and inserts one <c>credentials</c>
    /// row, whose unique rules are the primary key, <c>AK_credentials_id_user_id_type</c>, and the two
    /// partial indexes (both mapped). The alternate key leads with <c>id</c>, so it cannot break without
    /// the primary key breaking too. That leaves the primary key.
    /// </para>
    /// <para>
    /// <b>How it is reached:</b> the filed credential's id is minted by <c>Credential.CreateFederated</c>
    /// inside <c>Decide</c>, so the test reads it off the decided change and files a bare passkey row
    /// under that id for a <em>stranger</em> — no provider, no subject, another account — so neither
    /// partial index and not the alternate key can be what refuses. In production this is only a UUIDv7
    /// collision, which is why a 500 naming the real constraint is the right answer rather than a
    /// confident, false 409.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ApplyAsync_WhenAnotherUniqueRuleIsBroken_LetsTheViolationEscape()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner stranger = await host.SeedOwnerAsync(StrangerSubject, StrangerEmail);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnSubject, OwnEmail);
        Guid ownFederatedId = await host.FederatedCredentialIdAsync(owner.UserId);

        await using BudgetoidDbContext db = AppDb(host, owner.UserId, owner.BudgetId);
        EmailChangeRepository repository = new(db);
        FederatedIdentityChange change = await DecideLikeTheHandlerAsync(
            db, repository, owner.UserId, NewSubject, NewEmail);

        await using (NpgsqlConnection admin = new(host.ConnectionString))
        {
            await admin.OpenAsync();
            await using NpgsqlCommand squat = new(
                "insert into credentials (id, user_id, type, created_at_utc) "
                + "values (@id, @user, 'passkey', now())",
                admin);
            squat.Parameters.AddWithValue(
                "id",
                (change.Filed ?? throw new InvalidOperationException("A new subject decided no filing.")).Id);
            squat.Parameters.AddWithValue("user", stranger.UserId);
            await squat.ExecuteNonQueryAsync();
        }

        // Act
        Exception? escaped = await CaptureAsync(() => repository.ApplyAsync(change, owner.UserId));

        // Assert
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateException>();
        await Assert.That(SqlStateOf(escaped)).IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That(ConstraintNameOf(escaped)).IsEqualTo(CredentialPrimaryKeyName);
        await Assert.That(ConstraintNameOf(escaped)).IsNotEqualTo(UserConfiguration.EmailIndexName);
        await Assert.That(ConstraintNameOf(escaped))
            .IsNotEqualTo(CredentialConfiguration.ProviderSubjectIndexName);
        await Assert.That(ConstraintNameOf(escaped))
            .IsNotEqualTo(CredentialConfiguration.FederatedPerUserIndexName);

        await using BudgetoidDbContext verify = SuperuserDb(host);
        User stored = await verify.Users.SingleAsync(user => user.Id == owner.UserId);
        await Assert.That(stored.Email.Value).IsEqualTo(OwnEmail);
        List<Credential> federated = await FederatedCredentialsOf(verify, owner.UserId);
        await Assert.That(federated.Count).IsEqualTo(1);
        await Assert.That(federated[0].Id).IsEqualTo(ownFederatedId);
    }

    /// <summary>
    /// A refused save inside the handler's transaction leaves that transaction usable: the re-read the
    /// handler runs next answers instead of failing with <c>25P02</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This settles the handler's savepoint claim.</b> <c>ChangeEmailHandler.RefusalFor</c> re-reads
    /// the credential after an <see cref="EmailChangeOutcome.EmailTaken" />, inside the same
    /// <c>ITransactionalExecutor</c> delegate, on the premise that EF takes a savepoint before a
    /// save inside an open transaction. Driven through <see cref="DbContextTransactionalExecutor" />, the
    /// production executor, on the app role.
    /// </para>
    /// <para>
    /// The read asks for the <em>stranger's</em> subject so the answer is a real id rather than a
    /// <see langword="null" /> that a swallowed failure could also produce. The change carries a new
    /// subject too, and the account's old credential is read back after the commit: the rollback to the
    /// savepoint has to have undone the DELETE, or the commit lands half a change.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ApplyAsync_AfterARefusedSave_LeavesTheTransactionUsable()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner stranger = await host.SeedOwnerAsync(StrangerSubject, StrangerEmail);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnSubject, OwnEmail);
        Guid ownFederatedId = await host.FederatedCredentialIdAsync(owner.UserId);

        await using BudgetoidDbContext db = AppDb(host, owner.UserId, owner.BudgetId);
        EmailChangeRepository repository = new(db);
        UserRepository users = new(db);
        DbContextTransactionalExecutor executor = new(db);

        // Act
        (EmailChangeOutcome outcome, Guid? holder, Exception? readFailure) = await executor.ExecuteAsync<(EmailChangeOutcome, Guid?, Exception?)>(
            async token =>
            {
                FederatedIdentityChange change = await DecideLikeTheHandlerAsync(
                    db, repository, owner.UserId, NewSubject, StrangerEmail);
                EmailChangeOutcome refused = await repository.ApplyAsync(change, owner.UserId, token);

                try
                {
                    Guid? found = await users.FindUserIdByFederatedCredentialAsync(
                        Credential.GoogleProvider, StrangerSubject, token);

                    return (refused, found, (Exception?)null);
                }
                catch (Exception exception)
                {
                    return (refused, (Guid?)null, exception);
                }
            });

        // Assert
        await Assert.That(outcome).IsEqualTo(EmailChangeOutcome.EmailTaken);
        await Assert.That(readFailure).IsNull();
        await Assert.That(holder).IsEqualTo(stranger.UserId);

        await using BudgetoidDbContext verify = SuperuserDb(host);
        List<Credential> federated = await FederatedCredentialsOf(verify, owner.UserId);
        await Assert.That(federated.Count).IsEqualTo(1);
        await Assert.That(federated[0].Id).IsEqualTo(ownFederatedId);
        User stored = await verify.Users.SingleAsync(user => user.Id == owner.UserId);
        await Assert.That(stored.Email.Value).IsEqualTo(OwnEmail);
    }

    /// <summary>
    /// A refused save leaves the context usable: the next save through it lands only its own change, and
    /// none of the refused one rides along.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it holds: the detach in the refusal arms</b>, the rule
    /// <c>RegistrationRepositoryTests.RegisterAsync_AfterARefusedRegistration_LeavesTheContextUsable</c>
    /// holds for that repository. Measured against a scratch implementation that skipped it: every other
    /// test in this file stayed green.
    /// </para>
    /// <para>
    /// The refused change retires the credential and files a new one; the next change is address-only,
    /// so nothing clears the tracker between the two. Left behind, the refused credential swap is still
    /// queued, and the second save commits it — the account moves to a Google identity the person was
    /// told did not attach. Today's handler discards tracked entities before any replay, so this is a
    /// statement about the next caller rather than about that one.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ApplyAsync_AfterARefusedSave_LeavesTheContextUsable()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedOwnerAsync(StrangerSubject, StrangerEmail);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnSubject, OwnEmail);
        Guid ownFederatedId = await host.FederatedCredentialIdAsync(owner.UserId);

        await using BudgetoidDbContext db = AppDb(host, owner.UserId, owner.BudgetId);
        EmailChangeRepository repository = new(db);
        FederatedIdentityChange refusedChange = await DecideLikeTheHandlerAsync(
            db, repository, owner.UserId, NewSubject, StrangerEmail);
        EmailChangeOutcome refused = await repository.ApplyAsync(refusedChange, owner.UserId);

        FederatedIdentityChange nextChange = await DecideLikeTheHandlerAsync(
            db, repository, owner.UserId, OwnSubject, NewEmail);

        // Act
        EmailChangeOutcome next = await repository.ApplyAsync(nextChange, owner.UserId);

        // Assert
        await Assert.That(refused).IsEqualTo(EmailChangeOutcome.EmailTaken);
        await Assert.That(next).IsEqualTo(EmailChangeOutcome.Applied);

        await using BudgetoidDbContext verify = SuperuserDb(host);
        User stored = await verify.Users.SingleAsync(user => user.Id == owner.UserId);
        await Assert.That(stored.Email.Value).IsEqualTo(NewEmail);
        List<Credential> federated = await FederatedCredentialsOf(verify, owner.UserId);
        await Assert.That(federated.Count).IsEqualTo(1);
        await Assert.That(federated[0].Id).IsEqualTo(ownFederatedId);
        await Assert.That(federated[0].Subject).IsEqualTo(OwnSubject);
    }

    /// <summary>
    /// A refused address is not sent again by the next save through the same context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it holds: the detach of the <see cref="User" /> in the refusal arms.</b> The repository
    /// moves the address on the tracked user before it saves, so after a refusal that entry still holds
    /// the taken address as <c>Modified</c>. Left tracked, the next <c>SaveChanges</c> anybody makes on this
    /// request-scoped context — a later repository's write, an audit row — sends the refused
    /// <c>UPDATE users</c> again. <c>ApplyAsync_AfterARefusedSave_LeavesTheContextUsable</c> cannot see
    /// this, because its second change moves the address again before it saves and overwrites the stale
    /// value.
    /// </para>
    /// <para>
    /// Address-only, so nothing clears the tracker between the two saves, and the later save is a bare
    /// <c>SaveChangesAsync</c> with nothing of its own pending: whatever reaches <c>users</c> on it is
    /// left over from the refusal. Measured on the wire through <see cref="StatementRecorder" />, and the
    /// address is read back as well.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ApplyAsync_AfterARefusedAddress_DoesNotResendItOnTheNextSave()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await host.SeedOwnerAsync(StrangerSubject, StrangerEmail);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnSubject, OwnEmail);

        StatementRecorder recorder = new();
        await using BudgetoidDbContext db = AppDb(host, owner.UserId, owner.BudgetId, recorder);
        EmailChangeRepository repository = new(db);
        FederatedIdentityChange change = await DecideLikeTheHandlerAsync(
            db, repository, owner.UserId, OwnSubject, StrangerEmail);
        EmailChangeOutcome refused = await repository.ApplyAsync(change, owner.UserId);
        int userWritesAfterRefusal = recorder.WritesTo("users").Count;

        // Act
        Exception? escaped = await CaptureAsync(() => db.SaveChangesAsync());

        // Assert
        await Assert.That(refused).IsEqualTo(EmailChangeOutcome.EmailTaken);
        await Assert.That(escaped).IsNull();
        await Assert.That(recorder.WritesTo("users").Count).IsEqualTo(userWritesAfterRefusal);

        await using BudgetoidDbContext verify = SuperuserDb(host);
        User stored = await verify.Users.SingleAsync(user => user.Id == owner.UserId);
        await Assert.That(stored.Email.Value).IsEqualTo(OwnEmail);
    }

    /// <summary>
    /// A concurrency failure over the account's <see cref="User" /> row escapes, instead of being
    /// answered as a moved credential.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it holds: the narrowing on the concurrency catch.</b> A
    /// <see cref="DbUpdateConcurrencyException" /> carries no SQLSTATE and no constraint name, so the
    /// catch has to narrow on what it can see: the change retires a credential, and every entry EF could
    /// not account for is that credential. Widen it to a bare
    /// <c>catch (DbUpdateConcurrencyException)</c> and an account erased mid-request is told another
    /// change of its Google identity landed first. That is a sentence about a race that never happened,
    /// and it swallows the real failure.
    /// </para>
    /// <para>
    /// <b>Staged the way production reaches it: an erasure of the same account commits between this
    /// request's read and its save.</b> <see cref="ConcurrentDeleteInterceptor" /> deletes the
    /// <c>users</c> row on its own superuser connection inside <c>SavingChanges</c>. The cascade takes the
    /// credentials, budget and manifest with it. The change is address-only, so the only write in the save
    /// is the <c>UPDATE users</c>. It matches nothing, and EF puts the shortfall on the <see cref="User" />
    /// entry. The entries are read off the escaping exception, the shape
    /// <c>KeyRotationRepositoryTests.PromoteAsync_WhenAStrangersRowLosesItsOwnRowCount_LetsTheFailureEscape</c>
    /// uses. "Something escaped" alone is also satisfied by a deleted catch.
    /// </para>
    /// <para>
    /// <b>What it does not pin.</b> Because the change retires nothing, the predicate's "a credential was
    /// retired" clause declines before its entries clause is read. An address-only change with the
    /// entries check dropped therefore still escapes here. The entries clause, and <c>All</c> against
    /// <c>Any</c>, would need a save that retires a credential and fails on another entity. Erasing the
    /// account cannot stage that, because the cascade removes the retired credential too, so the DELETE
    /// fails first and the conflict really is that credential.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ApplyAsync_WhenTheAccountIsErasedUnderneathTheSave_LetsTheConcurrencyFailureEscape()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnSubject, OwnEmail);

        ConcurrentDeleteInterceptor eraser = new(
            host.ConnectionString, "delete from users where id = @id", owner.UserId);
        await using BudgetoidDbContext db = AppDb(host, owner.UserId, owner.BudgetId, eraser);
        EmailChangeRepository repository = new(db);
        FederatedIdentityChange change = await DecideLikeTheHandlerAsync(
            db, repository, owner.UserId, OwnSubject, NewEmail);
        EmailChangeOutcome? answered = null;

        // Act
        Exception? escaped = await CaptureAsync(
            async () => answered = await repository.ApplyAsync(change, owner.UserId));

        // Assert
        await Assert.That(eraser.Deleted).IsEqualTo(1);
        await Assert.That(answered).IsNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateConcurrencyException>();

        IEnumerable<string> conflicting = escaped is DbUpdateConcurrencyException failure
            ? failure.Entries.Select(entry => entry.Entity.GetType().Name)
            : [];
        await Assert.That(conflicting).IsEquivalentTo(new[] { nameof(User) });

        await using BudgetoidDbContext verify = SuperuserDb(host);
        await Assert.That(await verify.Users.AnyAsync(user => user.Id == owner.UserId)).IsFalse();
    }

    /// <summary>
    /// Loads and decides the way <c>ChangeEmailHandler</c> does: the credential and the user through the
    /// repository, then <see cref="FederatedIdentityChange.Decide" />, then — when a credential is being
    /// retired — a cleared change tracker, standing in for the handler's discard after its session sweep.
    /// </summary>
    private static async Task<FederatedIdentityChange> DecideLikeTheHandlerAsync(
        BudgetoidDbContext db,
        EmailChangeRepository repository,
        Guid userId,
        string subject,
        string email)
    {
        Credential current = await repository.FindFederatedCredentialAsync(userId)
            ?? throw new InvalidOperationException("The arrangement seeded no federated credential.");
        User user = await repository.FindUserAsync(userId)
            ?? throw new InvalidOperationException("The arrangement seeded no user row.");

        FederatedIdentityChange change = FederatedIdentityChange.Decide(
            user, current, subject, email, DateTime.UtcNow);

        if (change.IsNoChange)
        {
            throw new InvalidOperationException("The arrangement decided no change; nothing would be applied.");
        }

        if (change.Retired is not null)
        {
            db.ChangeTracker.Clear();
        }

        return change;
    }

    private static Task<List<Credential>> FederatedCredentialsOf(BudgetoidDbContext db, Guid userId) =>
        db.Credentials
            .Where(credential => credential.UserId == userId && credential.Type == CredentialType.Federated)
            .ToListAsync();

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
    /// Deliberately untyped: which exception surfaces is the question.
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
    /// A context on the <b>least-privilege</b> connection with <c>SessionContextInterceptor</c> wired in,
    /// so the account's identity is on every connection it opens — the shape production runs in.
    /// </summary>
    private static BudgetoidDbContext AppDb(
        RepositoryTestHost host,
        Guid userId,
        Guid budgetId,
        params IInterceptor[] interceptors)
    {
        TestBudgetContext budgetContext = new(budgetId);
        TestUserContext userContext = new(userId);

        return new BudgetoidDbContext(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.AppConnectionString)
                .AddInterceptors([new SessionContextInterceptor(budgetContext, userContext), .. interceptors])
                .Options,
            budgetContext);
    }

    /// <summary>
    /// A context on the container superuser connection, for arranging and reading back — never for the
    /// act.
    /// </summary>
    private static BudgetoidDbContext SuperuserDb(RepositoryTestHost host) => new(
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
