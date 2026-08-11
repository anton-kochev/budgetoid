using Domain.Common;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Who <see cref="PasskeyRepository" /> is allowed to speak for when PostgreSQL refuses one of its
/// writes: the delete's answer when the passkey it was handed is no longer in the table, and the
/// registration's answer when a handle is already spoken for — together with the controls that keep
/// each of those from being said about somebody else's rule.
/// </summary>
/// <remarks>
/// <para>
/// Turning a persistence failure into a domain answer is Infrastructure's job, and this is the lowest
/// layer that can be measured doing it. <c>UserRepository.DeleteAsync</c> and
/// <c>TransactionRepository.DeleteAllForAmbientBudgetAsync</c> already catch the same EF exception in
/// the same place, and <see cref="UserRepositoryTests" /> is where that catch is tested; this file is
/// the passkey equivalent, written to the same shape rather than to a second one.
/// </para>
/// <para>
/// <b>The two narrowings this class holds are narrowed on different things, and only one of them has a
/// name to narrow on.</b> <see cref="PasskeyRepository.TryAddAsync" /> filters a <c>23505</c> by
/// <see cref="PasskeyPublicKeyConfiguration.WebAuthnCredentialIdIndexName" />;
/// <see cref="PasskeyRepository.DeletePasskeyAsync" /> filters a concurrency conflict, which carries no
/// SQLSTATE and no constraint name, by the <i>entries</i>. Both are the same claim in the end — this
/// repository speaks only for the rule it models — and both are pinned here in both directions, so
/// neither can be satisfied by deleting the <c>catch</c> nor by widening it to the bare exception type.
/// </para>
/// <para>
/// <b>The mis-attribution mechanism is <c>RepositoryConstraintAttributionTests</c>', not a second
/// one.</b> <c>SaveChangesAsync</c> flushes everything the scoped context is tracking, not only the
/// entity the repository was handed, so
/// <see cref="TryAddAsync_WhenATrackedRowBreaksAnotherUniqueIndex_LetsTheViolationEscape" /> tracks one
/// unrelated row that breaks a <i>different</i> unique index carrying the <i>same</i> <c>23505</c>, and
/// then hands the repository a passkey that is beyond reproach. Read that file's remarks for the
/// argument; what is written out here is only why this repository's half lives beside its method
/// instead — the same placement decision <c>UserRepositoryTests</c> and
/// <c>RecoveryCodeRepositoryTests</c> record, which that file states as covering "the five repositories
/// reachable through a budget" and no others. Nothing here is reachable through a budget: a credential
/// belongs to a person, and the seeding, the intruder and the context all have to be built without an
/// ambient budget.
/// </para>
/// <para>
/// It cannot be written above this layer, and the attempt is what makes the placement worth stating.
/// Naming <c>DbUpdateConcurrencyException</c> in a handler puts the EF assembly on
/// <c>Application.csproj</c> — a reference pointing the wrong way down a dependency direction that
/// runs <c>Infrastructure → Application</c> — and a fake standing in for
/// <see cref="IPasskeyRepository" /> could only raise that type by modelling something the port never
/// surfaces. What the port promises is the line below: a credential that is already gone raises
/// <see cref="NotFoundException" />.
/// </para>
/// <para>
/// Every row is written and removed on <see cref="RepositoryTestHost.ConnectionString" /> — the
/// container superuser. What is measured here is the repository's own translation, not the isolation
/// policies; <c>RlsIsolationTests</c> owns those.
/// </para>
/// </remarks>
public sealed class PasskeyRepositoryTests
{
    [Test]
    public async Task DeletePasskeyAsync_WhenTheRowIsAlreadyGone_ThrowsNotFound()
    {
        // Arrange — a real passkey, read back through FindPasskeyCredentialAsync, the one query that
        // materialises a Credential the table actually holds and which names the owner and the type in
        // its predicate, and then removed out of band on a separate connection. That lookup is not the
        // only way to obtain a Credential: CreateFederated and CreatePasskey are both public. Each of
        // them mints its own Guid.CreateVersion7() though, so a fabricated instance names no existing
        // row — which leaves the guarantee at "naming an existing row of the caller's choosing takes a
        // new query on PasskeyRepository", a rule review enforces over one class rather than something
        // the type prevents. It has to be enforced, because credentials is exempt from row-level
        // security and this predicate is the entire scope of the delete below.
        //
        // The arrangement here is that distinction made concrete, and worth reading as one: the scoped
        // lookup yields a credential naming a real row, the out-of-band delete takes the row away, and
        // what is left is a tracked entity naming a row that no longer exists — the state an unscoped
        // source would hand the delete for free. It is also precisely what a lost delete race leaves
        // behind: both requests resolve the
        // credential, both clear the last-passkey floor, and the loser's DELETE matches zero rows
        // where EF expected one. No race is arranged and none should be — interleaving two
        // transactions at a chosen statement buys a timing-dependent test for a branch whose entire
        // input is this state, and a flaky assertion about a rare path is worse than none.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId);
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new PasskeyRepository(db);
        Credential credential = await repository.FindPasskeyCredentialAsync(credentialId, userId)
            ?? throw new InvalidOperationException(
                "The seeded passkey was not readable through the repository before the act.");
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        int removedByTheWinner = await DeleteCredentialAsync(admin, credentialId);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.DeletePasskeyAsync(credential));

        // Assert — the premise first. A run in which the out-of-band delete matched nothing would be
        // arranging the opposite of this test, and could still go green against a repository that
        // threw for some entirely unrelated reason.
        await Assert.That(removedByTheWinner).IsEqualTo(1);

        // 404 is the honest answer, and it is a decision rather than a convenience. The row is gone,
        // which is what this route means by "not found", and it is the answer the caller would have
        // received a moment earlier had its own lookup run after the winner's delete instead of
        // before — so the two orderings of one pair of requests become indistinguishable, which is
        // what makes a client's retry safe. A retry would find nothing, so there is nothing left to
        // retry; a 409 would say the work could not be done and invite exactly that retry at work
        // already completed. Letting the DbUpdateConcurrencyException escape is worse than either: a
        // 500 logged as a fault, describing a removal that in fact succeeded.
        await Assert.That(escaped).IsTypeOf<NotFoundException>();
    }

    /// <summary>
    /// A conflict over somebody else's entity is not dressed up as a passkey that is already gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The narrowing half of the delete, and the reason its <c>catch</c> carries a <c>when</c> at
    /// all.</b> A concurrency conflict carries no SQLSTATE and no constraint name, so the
    /// <i>entries</i> are the only thing there is to narrow on: every conflicting row must be a
    /// <see cref="Credential" /> this call itself marked <c>Deleted</c>. Drop the <c>when</c> clause and
    /// a stranger's conflict comes back as <see cref="NotFoundException" /> — a 404 telling somebody
    /// their passkey is already gone, on a request that rolled back and removed nothing, which is a
    /// client's cue to stop retrying at exactly the moment retrying is the right thing to do.
    /// </para>
    /// <para>
    /// <b>The intruder is a second passkey's signature counter</b>, loaded first, removed out of band
    /// second, and marked <c>Deleted</c> third — so the context is tracking a delete the database will
    /// match no row for. It belongs to a <em>different</em> credential on purpose: the counter of the
    /// passkey under test leaves by the database's own cascade from the row this method removes, so
    /// tracking that one would be arranging a conflict the delete itself causes rather than one riding
    /// along beside it. <c>RecoveryCodeRepositoryTests</c> stages the identical intruder against its own
    /// delete, and this is written to that shape rather than to a second one.
    /// </para>
    /// <para>
    /// <b>The passkey being revoked is deliberately still present</b>, so exactly one entry can be in
    /// the exception and the test cannot pass or fail on how EF happened to batch two failures.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DeletePasskeyAsync_WhenAnUnrelatedEntityConflicts_LetsTheConflictEscape()
    {
        // Arrange — two passkeys: the one being revoked, which is entirely healthy, and a bystander
        // whose signature counter is not.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId);
        Guid bystanderId = await host.SeedPasskeyAsync(userId, UnregisteredWebAuthnCredentialId);

        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new PasskeyRepository(db);
        Credential credential = await repository.FindPasskeyCredentialAsync(credentialId, userId)
            ?? throw new InvalidOperationException(
                "The seeded passkey was not readable through the repository before the act.");

        PasskeySignatureCounter counter = await db.PasskeySignatureCounters
            .SingleAsync(tracked => tracked.CredentialId == bystanderId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        int removedOutOfBand = await DeleteSignatureCounterAsync(admin, bystanderId);
        db.PasskeySignatureCounters.Remove(counter);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.DeletePasskeyAsync(credential));

        // Assert — the premise first, or the exception below was raised by something this test did not
        // arrange.
        await Assert.That(removedOutOfBand).IsEqualTo(1);

        // A 500 naming a conflict this method does not model beats a 404 claiming a revocation that a
        // rolled-back transaction did not perform.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateConcurrencyException>();
    }

    /// <summary>
    /// A registration of an authenticator this account already holds is refused, and leaves nothing
    /// behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The translation half, without which the control below could be satisfied by deleting the
    /// <c>catch</c> outright.</b> <c>PasskeyCeremonyTests</c> takes the same refusal as a 409 over HTTP,
    /// which is the answer a client sees; this is the same rule read at the layer that decides it, and
    /// the two are not redundant in the direction that matters here — a widened <c>when</c> clause is
    /// invisible from the route, because a route only ever stages the violation the repository does
    /// model.
    /// </para>
    /// <para>
    /// <b>The winner is committed by a context of its own.</b> Written through the context under test it
    /// would travel inside the same unit of work, where this insert could not fail to see it, and the
    /// collision would be EF's rather than the database's — the reason
    /// <c>RecoveryCodeRepositoryTests</c> gives for the same arrangement.
    /// </para>
    /// <para>
    /// The registration is aimed at the <b>same</b> account, which is the shape a person re-registering
    /// an authenticator they already enrolled produces, and it is also the sharper of the two: the
    /// account is allowed to hold a second passkey — <c>UserRepositoryTests</c> pins that — so nothing
    /// but the handle index can refuse this row. The rows are counted afterwards because the promise is
    /// one save: a refusal that left the credential behind without its key would be a passkey nothing
    /// can verify a signature against, and it would satisfy a bare <c>IsFalse</c> perfectly.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryAddAsync_WhenTheHandleIsAlreadyRegistered_ReturnsFalse()
    {
        // Arrange — the winner's passkey, committed by another session.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid winnerId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId);

        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new PasskeyRepository(db);
        (Credential credential, PasskeyPublicKey publicKey, PasskeySignatureCounter counter) =
            NewPasskeyFor(userId, WebAuthnCredentialId);

        // Act
        bool added = await repository.TryAddAsync(credential, publicKey, counter);

        // Assert — refused, and the winner is still the account's.
        await Assert.That(added).IsFalse();

        // Nothing of the loser's survives, and each row is looked for by the loser's own id rather
        // than by a count: the winner's three rows are still there, so a count would be answering a
        // question about the seed.
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.Credentials.AnyAsync(row => row.Id == credential.Id)).IsFalse();
        await Assert.That(await verify.PasskeyPublicKeys.AnyAsync(row => row.CredentialId == credential.Id))
            .IsFalse();
        await Assert.That(await verify.PasskeySignatureCounters.AnyAsync(row => row.CredentialId == credential.Id))
            .IsFalse();
        await Assert.That(await verify.Credentials.AnyAsync(row => row.Id == winnerId)).IsTrue();
    }

    /// <summary>
    /// A unique violation this repository does not model is not dressed up as an authenticator that is
    /// already registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The narrowing half, and the reason the <c>catch</c> carries a constraint name at all.</b>
    /// <c>TryAddAsync</c> writes three rows and <c>SaveChangesAsync</c> flushes every other tracked one
    /// beside them, so a <c>23505</c> reaching that <c>catch</c> says only that <i>some</i> unique rule
    /// broke. Widen the <c>when</c> clause to the bare SQLSTATE and this test's arrangement — a
    /// stranger's email collision — comes back as <see langword="false" />, which the ceremony above
    /// reports as "this authenticator is already registered": a confident, specific, false 409 about a
    /// handle no row in the table holds.
    /// </para>
    /// <para>
    /// <b>The intruder is a second <c>users</c> row reusing an address the seeded account already
    /// holds</b>, breaking <c>IX_users_email</c> with the same <c>23505</c> the handle index would
    /// raise. Not even a row this repository has a port for, which is the point:
    /// <c>RepositoryConstraintAttributionTests</c> stages the identical intruder against
    /// <c>BudgetRepository</c> for the same reason — the tracked graph is wider than any one
    /// repository's subject. A users row with no credential is legal at the schema level, so exactly
    /// one rule is broken and the test cannot pass or fail on which of two violations PostgreSQL
    /// reported first.
    /// </para>
    /// <para>
    /// <b>The passkey being registered carries a handle nothing holds</b>, so the index this method
    /// does model is untouched and the refusal is attributable to the intruder alone.
    /// </para>
    /// <para>
    /// The SQLSTATE is asserted beside the constraint name, which is what keeps this a narrowing test
    /// rather than a test that any failure escapes: a violation of some entirely different kind would
    /// satisfy "not the handle index" without ever exercising the filter. The expected behaviour is
    /// that an unmodelled violation <b>propagates</b> — a 500 naming the real constraint beats a 409
    /// that lies.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryAddAsync_WhenATrackedRowBreaksAnotherUniqueIndex_LetsTheViolationEscape()
    {
        // Arrange — an account, and a second users row arriving with that account's address.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", TakenEmail);

        await using BudgetoidDbContext db = CreateDb(host);
        db.Users.Add(User.Create(TakenEmail, SeedInstant));
        var repository = new PasskeyRepository(db);

        // The registration itself is flawless: a handle no row in the table carries.
        (Credential credential, PasskeyPublicKey publicKey, PasskeySignatureCounter counter) =
            NewPasskeyFor(userId, UnregisteredWebAuthnCredentialId);

        // Act
        Exception? escaped = await CaptureAsync(
            () => repository.TryAddAsync(credential, publicKey, counter));

        // Assert — something escaped, which is already the claim: a swallowed violation would have
        // returned false and left this null.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateException>();

        // And it really was a unique violation — on a rule that is not this repository's to speak for.
        await Assert.That(SqlStateOf(escaped)).IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That(ConstraintNameOf(escaped)).IsEqualTo(UserEmailIndex);
        await Assert.That(ConstraintNameOf(escaped))
            .IsNotEqualTo(PasskeyPublicKeyConfiguration.WebAuthnCredentialIdIndexName);
    }

    /// <summary>
    /// The address both the seeded account and the intruding row carry. A constant because the
    /// collision is the arrangement: two literals that happened to match would be a coincidence a
    /// reader has to verify.
    /// </summary>
    private const string TakenEmail = "person@example.com";

    /// <summary>
    /// The unique index <c>users.email</c> carries, spelled out rather than read off
    /// <c>UserConfiguration</c>. A test that took its expectation from the configuration the schema was
    /// rendered from would agree with it by construction; the habit is
    /// <c>RepositoryConstraintAttributionTests</c>', which spells the same name as a literal for the
    /// same reason.
    /// </summary>
    private const string UserEmailIndex = "IX_users_email";

    /// <summary>
    /// Fixed UTC instant for the rows these tests write themselves. PostgreSQL <c>timestamptz</c>
    /// rejects a non-UTC <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The WebAuthn handle the seeded passkey carries. Nothing here verifies a signature, so the
    /// bytes are arbitrary — but there are
    /// <see cref="PasskeyPublicKey.MinWebAuthnCredentialIdLength" /> of them, because a shorter handle
    /// is refused by the domain factory and the seed would fail before the act.
    /// </summary>
    private static readonly byte[] WebAuthnCredentialId =
        [.. Enumerable.Range(1, PasskeyPublicKey.MinWebAuthnCredentialIdLength).Select(value => (byte)value)];

    /// <summary>
    /// A second handle, differing from <see cref="WebAuthnCredentialId" /> in every byte, for the
    /// registration that must be refused by somebody else's rule rather than by its own.
    /// </summary>
    private static readonly byte[] UnregisteredWebAuthnCredentialId =
        [.. Enumerable.Range(1, PasskeyPublicKey.MinWebAuthnCredentialIdLength).Select(value => (byte)(value + 128))];

    /// <summary>
    /// The COSE key registered passkeys carry here. Four bytes: nothing in this file verifies a
    /// signature, and the only rule the column holds is that the key is between one byte and
    /// <see cref="PasskeyPublicKey.MaxCoseKeyLength" />.
    /// </summary>
    private static readonly byte[] CoseKey = [0xA5, 0x01, 0x02, 0x03];

    /// <summary>
    /// Builds the three rows a registration writes — the credential the key hangs off, the key itself,
    /// and the counter a clone gives itself away against — without writing any of them.
    /// </summary>
    /// <remarks>
    /// Through the domain factories rather than assembled from loose ids, for the reason
    /// <c>RepositoryTestHost.SeedPasskeyAsync</c> gives: <see cref="PasskeyPublicKey.Register" /> and
    /// <see cref="PasskeySignatureCounter.Start" /> copy the owner and the type off the credential
    /// itself, which are the columns the composite foreign keys compare against
    /// <c>credentials(id, user_id, type)</c>. A test that built them from ids of its own could arrange a
    /// shape no ceremony can produce, and would then be measuring a schema nobody ships.
    /// </remarks>
    private static (Credential Credential, PasskeyPublicKey PublicKey, PasskeySignatureCounter Counter)
        NewPasskeyFor(Guid userId, byte[] webAuthnCredentialId)
    {
        Credential credential = Credential.CreatePasskey(userId, SeedInstant);

        return (
            credential,
            PasskeyPublicKey.Register(credential, webAuthnCredentialId, CoseKey, CoseAlgorithm.Es256),
            PasskeySignatureCounter.Start(credential, value: 0));
    }

    /// <summary>
    /// Names the constraint PostgreSQL actually refused on, or <see langword="null" /> when the
    /// escaping exception never reached the database at all.
    /// <c>RepositoryConstraintAttributionTests</c>' helper, spelled out here because each file in this
    /// folder owns the readers its own assertions need.
    /// </summary>
    private static string? ConstraintNameOf(Exception? exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
            ? postgresException.ConstraintName
            : null;

    /// <summary>
    /// The SQLSTATE PostgreSQL refused with, or <see langword="null" /> when nothing did. Read beside
    /// the constraint name so a narrowing test can say the violation it staged really is the kind the
    /// filter has to tell apart.
    /// </summary>
    private static string? SqlStateOf(Exception? exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
            ? postgresException.SqlState
            : null;

    /// <summary>
    /// Removes one <c>credentials</c> row, standing in for the request that won the race.
    /// </summary>
    /// <remarks>
    /// On its own connection, and that is the whole point rather than tidiness: issued through the
    /// repository's own context it would travel inside that context's unit of work, where the DELETE
    /// under test could not fail to see it. Committed by another session is the only version of this
    /// state a real race produces. The dependent <c>passkey_public_keys</c> and
    /// <c>passkey_signature_counters</c> rows leave with it by the database's own cascade, exactly as
    /// they would have under the winner.
    /// </remarks>
    private static async Task<int> DeleteCredentialAsync(NpgsqlConnection admin, Guid credentialId)
    {
        await using NpgsqlCommand command = new("delete from credentials where id = @id", admin);
        command.Parameters.AddWithValue("id", credentialId);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Removes one signature counter out of band, which is what makes the tracked copy of it a delete
    /// nothing will match. On its own connection for the reason
    /// <see cref="DeleteCredentialAsync" /> gives.
    /// </summary>
    private static async Task<int> DeleteSignatureCounterAsync(NpgsqlConnection admin, Guid credentialId)
    {
        await using NpgsqlCommand command = new(
            "delete from passkey_signature_counters where credential_id = @id",
            admin);
        command.Parameters.AddWithValue("id", credentialId);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Runs <paramref name="action" /> and hands back whatever escaped, or <see langword="null" />
    /// when nothing did. Deliberately untyped, as in <see cref="UserRepositoryTests" />: the question
    /// this file asks is <i>which</i> exception surfaces, so catching a specific one in the helper
    /// would decide the answer before the assertion reads it — and the failure this test is written
    /// to catch is a <c>DbUpdateConcurrencyException</c> arriving where a
    /// <see cref="NotFoundException" /> was promised.
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
    /// Builds a context with no ambient budget, which is safe here because <c>Credential</c> carries
    /// no budget query filter — a credential belongs to a person, not to a tenant.
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
