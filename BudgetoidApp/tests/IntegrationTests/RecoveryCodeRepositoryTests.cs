using System.Security.Cryptography;
using Domain.Common;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// What <see cref="RecoveryCodeRepository" /> answers to the loser of two concurrent generations for
/// one account — on the delete when the account already held a set, and on the insert when it did not.
/// </summary>
/// <remarks>
/// <para>
/// <b>The race is real and reachable today.</b> Two requests issuing codes for one account — a
/// double-clicked button, a client retry, two open tabs — both pass the gate on nonces of their own,
/// both resolve the same previous set, and both try to delete it. The winner commits; the loser's
/// <c>DELETE</c> matches zero rows where EF expected one, and <c>SaveChangesAsync</c> raises
/// <see cref="DbUpdateConcurrencyException" />. Nothing above translates it, so the person is answered
/// 500 and told nothing, having just been shown ten codes that will never redeem.
/// </para>
/// <para>
/// <b>It has a second half, and that one is the likelier of the two.</b> On an account that holds no set
/// yet — the double-clicked button on a fresh account, which is where the button is clicked — neither
/// request reaches <see cref="RecoveryCodeRepository.DeleteSetAsync" /> at all, because there is nothing
/// to delete. Both insert, and the loser collides on <c>IX_credentials_user_id_recovery_codes</c> with a
/// <c>23505</c>. The caller's situation is identical to the delete half's: this attempt wrote nothing,
/// somebody else's set is the account's, and the ten codes already shown to a person will never redeem.
/// </para>
/// <para>
/// <b>So both halves answer with the same sentence, and
/// <see cref="BothHalvesOfTheRace_RefuseWithTheIdenticalSentence" /> is what holds them to it.</b> Two
/// different sentences would let a losing caller tell "you already had a set" from "you had none" — a
/// fact about the account's previous state that a request which wrote nothing has no business learning
/// and no use for. The sameness is a decision, not reuse, so it is asserted rather than left to whoever
/// next edits one of the two messages.
/// </para>
/// <para>
/// <b>409 is the answer this file pins, and the decision is worth reading before it is changed.</b>
/// <list type="bullet">
/// <item>
/// It cannot be a <b>200</b>. The loser wrote no set: the winner's ten codes are the account's, and the
/// loser's client is holding ten it has already shown a person. A success here is the one outcome that
/// leaves somebody with a printed card that unlocks nothing, and no way to find out.
/// </item>
/// <item>
/// It cannot be a <b>404</b>, which is what the sibling path answers —
/// <c>PasskeyRepository.DeletePasskeyAsync</c> turns the identical exception into
/// <see cref="NotFoundException" />. That is right there and wrong here, and the difference is what the
/// caller named: a revocation names a credential in its route, so "that row is gone" is an answer about
/// the thing asked for, and the two orderings of the pair become indistinguishable, which is what makes
/// a client's retry safe. A generation names nothing. The resource it addresses —
/// <c>/api/me/recovery-codes</c> — exists, and telling a caller it does not is a false statement about
/// its own account.
/// </item>
/// <item>
/// It cannot be the gate's <b>401</b>. The caller proved possession of an authenticator registered to
/// this account; reporting a lost race as a failed proof sends a person to debug an authenticator that
/// is working perfectly.
/// </item>
/// <item>
/// It cannot be <b>swallowed and continued</b>, in the shape <c>UserRepository.DeleteAsync</c> uses. The
/// row is already gone, so carrying on means inserting this request's set beside the winner's —
/// <c>IX_credentials_user_id_recovery_codes</c> permits one per account, so the insert is refused with
/// <c>23505</c> and the 500 comes back anyway, one statement later and harder to read.
/// </item>
/// </list>
/// So: <b>409 with a real sentence.</b> The request conflicts with the state of the resource, it would
/// succeed unchanged if made again, and a retry is exactly what the client should do — with a fresh
/// assertion, since the nonce this attempt spent is spent. A sentence is legitimate here for the reason
/// the whole validation family is: this is past the gate.
/// </para>
/// <para>
/// <b>Turning a persistence failure into a domain answer is Infrastructure's job, and this is the lowest
/// layer that can be measured doing it.</b> Naming <see cref="DbUpdateConcurrencyException" /> in a
/// handler would put the EF assembly on <c>Application.csproj</c> — a reference pointing the wrong way
/// down a dependency direction that runs <c>Infrastructure → Application</c> — and a fake standing in for
/// <see cref="IRecoveryCodeRepository" /> could only raise that type by modelling something the port
/// never surfaces. <see cref="PasskeyRepositoryTests" /> is the same file for the passkey delete, and
/// this is written to its shape rather than to a second one.
/// </para>
/// <para>
/// Every row is written and removed on <see cref="RepositoryTestHost.ConnectionString" /> — the container
/// superuser. What is measured here is the repository's own translation, not the isolation policies;
/// <c>RlsIsolationTests</c> owns those.
/// </para>
/// </remarks>
public sealed class RecoveryCodeRepositoryTests
{
    /// <summary>How many codes a seeded set holds. Only the plurality matters to anything here.</summary>
    private const int SeededCodeCount = 10;

    /// <summary>The exact width of a verifier, which <c>RecoveryCodeHash.From</c> refuses either side of.</summary>
    private const int VerifierLength = 32;

    /// <summary>
    /// Fixed UTC instant for every seeded row. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing, not decoration.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 8, 11, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// A set arrives for an account that already holds one, and the insert answers
    /// <see cref="ConflictException" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The half of the race no delete is involved in.</b> Both requests found no previous set, so
    /// neither called <c>DeleteSetAsync</c>; the winner's credential is in the table by the time this
    /// one's insert reaches it, and <c>IX_credentials_user_id_recovery_codes</c> — one issued set per
    /// account — refuses it with a <c>23505</c>. Left untranslated that is a 500 on the request of
    /// somebody looking at ten codes that will never redeem.
    /// </para>
    /// <para>
    /// <b>The winner's set is committed by a context of its own</b>, for the reason
    /// <see cref="DeleteCredentialAsync" /> gives about the other half: written through the context under
    /// test it would travel inside the same unit of work, where this insert could not fail to see it, and
    /// the collision would be EF's rather than the database's.
    /// </para>
    /// <para>
    /// No race is arranged and none should be — interleaving two transactions at a chosen statement buys
    /// a timing-dependent test for a branch whose entire input is this state.
    /// </para>
    /// <para>
    /// The winner's set is counted before the act, or "the account already holds one" is a claim about a
    /// row the arrangement never wrote and the exception below was raised by something else entirely. The
    /// rows are counted again afterwards, because the promise is one save: a loser that had left its ten
    /// hashes behind — codes filed against a credential that was rolled out from under them — would throw
    /// exactly as this test demands.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AddSetAsync_WhenTheAccountAlreadyHoldsASet_ThrowsConflict()
    {
        // Arrange — the winner's set, committed by another session.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await SeedRecoveryCodeSetAsync(host, userId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        long setsTheWinnerLeft = await CountSetsOfUserAsync(admin, userId);

        await using BudgetoidDbContext db = CreateDb(host);
        RecoveryCodeRepository repository = new(db);
        (Credential set, RecoveryCodeHash[] hashes) = NewSetFor(userId, Verifiers());

        // Act
        Exception? escaped = await CaptureAsync(() => repository.AddSetAsync(set, hashes));

        // Assert — the premise first: the winner really did leave a set behind.
        await Assert.That(setsTheWinnerLeft).IsEqualTo(1L);

        // 409, for the reasons written out on the class. Letting the DbUpdateException escape is a 500
        // logged as a fault, describing a database rule the caller broke by asking twice.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<ConflictException>();

        // And it says something. A conflict with an empty message reaches the wire as a 409 with no
        // detail, which tells a client nothing about whether to retry.
        await Assert.That(string.IsNullOrWhiteSpace(escaped!.Message)).IsFalse();

        // The loser wrote nothing at all: one set, and the winner's ten codes are still the account's.
        await Assert.That(await CountSetsOfUserAsync(admin, userId)).IsEqualTo(1L);
        await Assert.That(await CountCodesOfUserAsync(admin, userId)).IsEqualTo((long)SeededCodeCount);
    }

    /// <summary>
    /// The control: an account holding no set is written one, credential and codes together.
    /// </summary>
    /// <remarks>
    /// Without this, the pin above is satisfied by a method that throws <see cref="ConflictException" />
    /// unconditionally — which would mean no account in the product could ever hold a set. Both counts
    /// are read after one call, because the promise is that they land together: a credential with no
    /// codes is a set that counts as issued and can never be redeemed, and it is what a save split in two
    /// leaves behind when the second half fails.
    /// </remarks>
    [Test]
    public async Task AddSetAsync_ForAnAccountHoldingNoSet_WritesTheCredentialAndItsCodes()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");

        await using BudgetoidDbContext db = CreateDb(host);
        RecoveryCodeRepository repository = new(db);
        (Credential set, RecoveryCodeHash[] hashes) = NewSetFor(userId, Verifiers());

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // No set before the act, or every row counted afterwards is one the arrangement produced.
        await Assert.That(await CountSetsOfUserAsync(admin, userId)).IsEqualTo(0L);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.AddSetAsync(set, hashes));

        // Assert
        await Assert.That(escaped).IsNull();
        await Assert.That(await CountCredentialAsync(admin, set.Id)).IsEqualTo(1L);
        await Assert.That(await CountCodesAsync(admin, set.Id)).IsEqualTo((long)SeededCodeCount);
    }

    /// <summary>
    /// A unique violation this repository does not model is not dressed up as a lost race.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The narrowing control for the insert, and the collision it stages is the one the catch's own
    /// comment names.</b> This call adds the credential <em>and</em> one row per code, and
    /// <c>recovery_code_hashes</c> is keyed on the verifier hash — so a <c>23505</c> raised inside a
    /// single <c>AddSetAsync</c> can just as easily be two codes hashing alike as a second set. Here the
    /// colliding hash belongs to a <b>bystander's</b> set, which is the sharper version of the same
    /// thing: nothing about this account's set is wrong, and a <c>catch</c> matching on the SQLSTATE
    /// alone would tell this caller their codes had been replaced by a request that never existed.
    /// </para>
    /// <para>
    /// The expected behaviour is that an unmodelled violation <b>propagates</b>: a 500 naming the real
    /// constraint beats a 409 that lies, which is the reasoning
    /// <c>RepositoryConstraintAttributionTests</c> writes down for every repository in this folder that
    /// translates anything.
    /// </para>
    /// <para>
    /// <b>The account under test deliberately holds no set of its own</b>, so exactly one rule is broken
    /// and the test cannot pass or fail on which of two violations PostgreSQL reported first.
    /// </para>
    /// <para>
    /// The SQLSTATE is asserted beside the constraint name, and that is what keeps this a narrowing test
    /// rather than a test that any failure escapes: a violation of some entirely different kind would
    /// satisfy "not the recovery-codes index" without ever exercising the filter.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AddSetAsync_WhenAnotherUniqueRuleIsBroken_LetsTheViolationEscape()
    {
        // Arrange — a bystander's set, and one of its verifiers copied into the set being written.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid bystanderId = await host.SeedUserAsync("google-2", "bystander@example.com");
        byte[][] bystandersVerifiers = await SeedRecoveryCodeSetAsync(host, bystanderId);

        // One member out of ten, never all of them, so the refusal is attributable to the collision and
        // not to a set that is uniformly wrong.
        byte[][] verifiers = Verifiers();
        verifiers[^1] = bystandersVerifiers[0];

        await using BudgetoidDbContext db = CreateDb(host);
        RecoveryCodeRepository repository = new(db);
        (Credential set, RecoveryCodeHash[] hashes) = NewSetFor(userId, verifiers);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // This account holds no set, so the one rule this insert can break is the bystander's.
        await Assert.That(await CountSetsOfUserAsync(admin, userId)).IsEqualTo(0L);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.AddSetAsync(set, hashes));

        // Assert
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateException>();

        // And it really was a unique violation — on a rule that is not this repository's to speak for.
        await Assert.That(SqlStateOf(escaped)).IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That(ConstraintNameOf(escaped))
            .IsNotEqualTo(CredentialConfiguration.RecoveryCodesPerUserIndexName);
    }

    /// <summary>
    /// The set is read back through the owner-scoped lookup, removed out of band, and the delete that
    /// follows answers <see cref="ConflictException" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The arrangement is the losing request's state made concrete: the scoped lookup yields a credential
    /// naming a real row, another session commits the same delete, and what is left is a tracked entity
    /// naming a row that no longer exists. No race is arranged and none should be — interleaving two
    /// transactions at a chosen statement buys a timing-dependent test for a branch whose entire input is
    /// this state, and a flaky assertion about a rare path is worse than none.
    /// </para>
    /// <para>
    /// <b>Read through <c>FindRecoveryCodeCredentialAsync</c> rather than fabricated</b>, because that is
    /// the one query producing a <see cref="Credential" /> the table actually holds, and its predicate
    /// names the owner and the type. <c>Credential.CreateRecoveryCodes</c> is public, so an instance can
    /// certainly be made elsewhere — but it mints its own <c>Guid.CreateVersion7()</c>, so the row it
    /// names does not exist and the delete would raise this same exception for a reason that has nothing
    /// to do with a race. ADR 0014 names exactly that distinction as the thing review has to catch, since
    /// <c>credentials</c> is exempt from row-level security and this predicate is the entire scope of the
    /// delete.
    /// </para>
    /// <para>
    /// The out-of-band removal is asserted to have removed a row before the act. A run in which it
    /// matched nothing would be arranging the opposite of this test and could still go green against a
    /// repository that threw for an entirely unrelated reason.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DeleteSetAsync_WhenTheSetIsAlreadyGone_ThrowsConflict()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await SeedRecoveryCodeSetAsync(host, userId);

        await using BudgetoidDbContext db = CreateDb(host);
        RecoveryCodeRepository repository = new(db);
        Credential set = await repository.FindRecoveryCodeCredentialAsync(userId)
            ?? throw new InvalidOperationException(
                "The seeded set was not readable through the repository before the act.");

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        int removedByTheWinner = await DeleteCredentialAsync(admin, set.Id);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.DeleteSetAsync(set));

        // Assert — the premise first: the winner really did take the row.
        await Assert.That(removedByTheWinner).IsEqualTo(1);

        // 409, for the reasons written out on the class. Letting the DbUpdateConcurrencyException escape
        // is a 500 logged as a fault, describing a removal that in fact succeeded — on the request of
        // somebody who is at that moment looking at ten codes that will never work.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<ConflictException>();

        // And it says something. A conflict with an empty message reaches the wire as a 409 with no
        // detail, which tells a client nothing about whether to retry.
        await Assert.That(string.IsNullOrWhiteSpace(escaped!.Message)).IsFalse();
    }

    /// <summary>
    /// The control: a set that is still there is removed, and its codes leave with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, the pin above is satisfied by a method that throws <see cref="ConflictException" />
    /// unconditionally — which would make every generation after the first one fail. It is also where the
    /// cascade is observed: the delete names the set's <c>credentials</c> row and nothing else, and the
    /// <c>recovery_code_hashes</c> rows go by the database's own <c>ON DELETE CASCADE</c>, which runs with
    /// the referencing table owner's privileges rather than this role's.
    /// </para>
    /// <para>
    /// Both counts are asserted before the act, because "the rows are gone" is a claim about rows the
    /// arrangement has to have written — and a seeding path that silently wrote nothing would satisfy the
    /// zeros afterwards perfectly.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DeleteSetAsync_WhenTheSetIsStillThere_RemovesItAndItsCodes()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await SeedRecoveryCodeSetAsync(host, userId);

        await using BudgetoidDbContext db = CreateDb(host);
        RecoveryCodeRepository repository = new(db);
        Credential set = await repository.FindRecoveryCodeCredentialAsync(userId)
            ?? throw new InvalidOperationException(
                "The seeded set was not readable through the repository before the act.");

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await CountCredentialAsync(admin, set.Id)).IsEqualTo(1L);
        await Assert.That(await CountCodesAsync(admin, set.Id)).IsEqualTo((long)SeededCodeCount);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.DeleteSetAsync(set));

        // Assert
        await Assert.That(escaped).IsNull();
        await Assert.That(await CountCredentialAsync(admin, set.Id)).IsEqualTo(0L);
        await Assert.That(await CountCodesAsync(admin, set.Id)).IsEqualTo(0L);
    }

    /// <summary>
    /// A conflict over somebody else's entity is not dressed up as this repository's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mechanism, and it is this folder's own.</b> <c>SaveChangesAsync</c> flushes everything the
    /// context is tracking, not only the entity the repository was handed. So this test tracks one
    /// unrelated row — a passkey's signature counter — as <c>Deleted</c> after removing it out of band,
    /// then asks the repository to delete a set that is perfectly present. The counter's <c>DELETE</c>
    /// matches zero rows, EF raises the same <see cref="DbUpdateConcurrencyException" /> the race raises,
    /// and a <c>catch</c> that looked only at the exception's type would report a stranger's conflict as
    /// "your recovery codes were replaced by another request" — a confident, specific, false 409.
    /// </para>
    /// <para>
    /// The expected behaviour is that an unattributable conflict <b>propagates</b>: a 500 naming the real
    /// failure beats a 409 that lies, which is the reasoning
    /// <c>RepositoryConstraintAttributionTests</c> already writes down for every repository that
    /// translates anything, and which <c>PasskeyRepository.DeletePasskeyAsync</c> implements by narrowing
    /// its catch to entries that are <see cref="Credential" /> rows it marked <c>Deleted</c> itself. A
    /// concurrency conflict carries no SQLSTATE and no constraint name, so the entries are the only thing
    /// there is to narrow on.
    /// </para>
    /// <para>
    /// <b>The set itself is deliberately still present</b>, so exactly one entry can be in the exception
    /// and the test cannot pass or fail on how EF happened to batch two failures. The credential's own
    /// delete succeeds; the conflict is entirely the counter's.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DeleteSetAsync_WhenAnUnrelatedEntityConflicts_LetsTheConflictEscape()
    {
        // Arrange — a set that is entirely healthy, and a signature counter that is not.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await SeedRecoveryCodeSetAsync(host, userId);
        Guid passkeyId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId);

        await using BudgetoidDbContext db = CreateDb(host);
        RecoveryCodeRepository repository = new(db);
        Credential set = await repository.FindRecoveryCodeCredentialAsync(userId)
            ?? throw new InvalidOperationException(
                "The seeded set was not readable through the repository before the act.");

        // The intruder: loaded first, removed out of band second, and marked Deleted third — so the
        // context is tracking a delete the database will match no row for.
        PasskeySignatureCounter counter = await db.PasskeySignatureCounters
            .SingleAsync(tracked => tracked.CredentialId == passkeyId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        int removedOutOfBand = await DeleteSignatureCounterAsync(admin, passkeyId);
        db.PasskeySignatureCounters.Remove(counter);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.DeleteSetAsync(set));

        // Assert — the premise first, or the exception below was raised by something this test did not
        // arrange.
        await Assert.That(removedOutOfBand).IsEqualTo(1);

        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateConcurrencyException>();
    }

    /// <summary>
    /// Both halves of the race refuse with the <b>same sentence</b>, and the sameness is the assertion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two different sentences would be a disclosure, which is why this is pinned rather than left to
    /// whoever next edits one of the messages.</b> The two halves are told apart by exactly one thing:
    /// whether the account held a set before the losing request arrived. A caller that could read that
    /// difference off the refusal would learn a fact about its own account's previous state that it did
    /// not have — and, on a request that wrote nothing, has no use for. The messages are also the one
    /// part of this behaviour that is easy to "improve" one half of: a reader making the insert's
    /// sentence more helpful would split them without noticing that the split is the leak.
    /// </para>
    /// <para>
    /// <b>Both are driven for real, on two accounts, each arranged in the state its own half loses
    /// in.</b> Neither arrangement can produce the other's failure — one account holds a set that has
    /// gone out from under a loaded entity, the other holds one the insert collides with — so a green
    /// run means two genuinely different code paths agreed, rather than one path being measured twice.
    /// Asserting the constant directly would prove less: two catches can name one constant today and be
    /// split into two tomorrow, and it is the observable answer, not the field, that a caller reads.
    /// </para>
    /// <para>
    /// Each half also has its own test above, so this one is left to say nothing about which exception
    /// type either produces beyond what the comparison needs.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BothHalvesOfTheRace_RefuseWithTheIdenticalSentence()
    {
        // Arrange — two accounts, so each half is arranged in the state it actually loses in. Separate
        // contexts as well: a failed save leaves its own unit of work half-unwound, and the second half
        // must not be measured through a context the first one broke.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid loserOnTheInsert = await host.SeedUserAsync("google-1", "first@example.com");
        Guid loserOnTheDelete = await host.SeedUserAsync("google-2", "second@example.com");
        await SeedRecoveryCodeSetAsync(host, loserOnTheInsert);
        await SeedRecoveryCodeSetAsync(host, loserOnTheDelete);

        await using BudgetoidDbContext insertingDb = CreateDb(host);
        RecoveryCodeRepository inserting = new(insertingDb);
        (Credential set, RecoveryCodeHash[] hashes) = NewSetFor(loserOnTheInsert, Verifiers());

        await using BudgetoidDbContext deletingDb = CreateDb(host);
        RecoveryCodeRepository deleting = new(deletingDb);
        Credential doomed = await deleting.FindRecoveryCodeCredentialAsync(loserOnTheDelete)
            ?? throw new InvalidOperationException(
                "The seeded set was not readable through the repository before the act.");

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        int removedByTheWinner = await DeleteCredentialAsync(admin, doomed.Id);

        // Act
        Exception? onTheInsert = await CaptureAsync(() => inserting.AddSetAsync(set, hashes));
        Exception? onTheDelete = await CaptureAsync(() => deleting.DeleteSetAsync(doomed));

        // Assert — the premise first: the delete half really did lose its row.
        await Assert.That(removedByTheWinner).IsEqualTo(1);

        // Both are the conflict, or the comparison below is between two messages that mean nothing.
        await Assert.That(onTheInsert).IsTypeOf<ConflictException>();
        await Assert.That(onTheDelete).IsTypeOf<ConflictException>();

        // The claim this test exists for.
        await Assert.That(onTheInsert!.Message).IsEqualTo(onTheDelete!.Message);

        // And they agree on a sentence rather than on emptiness, which two silent conflicts would also
        // satisfy.
        await Assert.That(string.IsNullOrWhiteSpace(onTheInsert.Message)).IsFalse();
    }

    /// <summary>
    /// The WebAuthn handle the seeded passkey carries. Nothing here verifies a signature, so the bytes
    /// are arbitrary — but there are <see cref="PasskeyPublicKey.MinWebAuthnCredentialIdLength" /> of
    /// them, because a shorter handle is refused by the domain factory and the seed would fail before the
    /// act.
    /// </summary>
    private static readonly byte[] WebAuthnCredentialId =
        [.. Enumerable.Range(1, PasskeyPublicKey.MinWebAuthnCredentialIdLength).Select(value => (byte)value)];

    /// <summary>
    /// Files one whole set onto an existing account: the <c>credentials</c> row standing for it and one
    /// <c>recovery_code_hashes</c> row per code, in one save. Hands back the verifiers it stored the
    /// hashes of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Through the domain factories and a context of its own rather than through
    /// <c>RecoveryCodeRepository.AddSetAsync</c>, so the arrangement does not run the class under test —
    /// a seeded row here is one the application could really have written without the seeding being
    /// evidence about the repository. One save, mirroring the shape a generation writes them in: a
    /// credential with no codes is a set that counts as issued and can never be redeemed, so a test
    /// arranged that way would be measuring a state the product cannot produce.
    /// </para>
    /// <para>
    /// The verifiers are returned for the one caller that needs a value this table already holds —
    /// <see cref="AddSetAsync_WhenAnotherUniqueRuleIsBroken_LetsTheViolationEscape" />, which stages its
    /// collision against a seeded row rather than against a hash it computed itself.
    /// </para>
    /// </remarks>
    private static async Task<byte[][]> SeedRecoveryCodeSetAsync(RepositoryTestHost host, Guid userId)
    {
        byte[][] verifiers = Verifiers();

        await using BudgetoidDbContext db = CreateDb(host);
        (Credential set, RecoveryCodeHash[] hashes) = NewSetFor(userId, verifiers);
        db.Credentials.Add(set);
        db.RecoveryCodeHashes.AddRange(hashes);

        await db.SaveChangesAsync();

        return verifiers;
    }

    /// <summary>
    /// A whole set for one account, built the way a generation builds it: one credential standing for
    /// the set, and one hash per verifier filed against it.
    /// </summary>
    /// <remarks>
    /// Every hash goes through <see cref="RecoveryCodeHash.From" /> against the credential itself, which
    /// is what copies the owner and the type onto the row — the three columns the composite foreign key
    /// compares against <c>credentials(id, user_id, type)</c>. Assembling them from loose ids would let a
    /// test arrange a shape the product cannot write.
    /// </remarks>
    private static (Credential Set, RecoveryCodeHash[] Hashes) NewSetFor(
        Guid userId,
        IReadOnlyList<byte[]> verifiers)
    {
        Credential set = Credential.CreateRecoveryCodes(userId, SeedInstant);

        return (set, [.. verifiers.Select(verifier => RecoveryCodeHash.From(set, verifier, SeedInstant))]);
    }

    /// <summary>
    /// <see cref="SeededCodeCount" /> distinct verifiers of the width <c>RecoveryCodeHash.From</c>
    /// demands.
    /// </summary>
    /// <remarks>
    /// Random rather than fixed vectors, which costs nothing: no assertion depends on the value of a
    /// verifier, so repeatability is not at stake — and randomness is what makes the one deliberate
    /// collision in this file the only one in its set.
    /// </remarks>
    private static byte[][] Verifiers() =>
    [
        .. Enumerable.Range(0, SeededCodeCount).Select(_ => RandomNumberGenerator.GetBytes(VerifierLength)),
    ];

    /// <summary>
    /// Removes one <c>credentials</c> row, standing in for the request that won the race.
    /// </summary>
    /// <remarks>
    /// On its own connection, and that is the whole point rather than tidiness: issued through the
    /// repository's own context it would travel inside that context's unit of work, where the
    /// <c>DELETE</c> under test could not fail to see it. Committed by another session is the only
    /// version of this state a real race produces. The dependent <c>recovery_code_hashes</c> rows leave
    /// with it by the database's own cascade, exactly as they would have under the winner.
    /// </remarks>
    private static async Task<int> DeleteCredentialAsync(NpgsqlConnection admin, Guid credentialId)
    {
        await using NpgsqlCommand command = new("delete from credentials where id = @id", admin);
        command.Parameters.AddWithValue("id", credentialId);

        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Removes one signature counter out of band, which is what makes the tracked copy of it a delete
    /// nothing will match.
    /// </summary>
    private static async Task<int> DeleteSignatureCounterAsync(NpgsqlConnection admin, Guid credentialId)
    {
        await using NpgsqlCommand command = new(
            "delete from passkey_signature_counters where credential_id = @id",
            admin);
        command.Parameters.AddWithValue("id", credentialId);

        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountCredentialAsync(NpgsqlConnection admin, Guid credentialId) =>
        await CountAsync(admin, "select count(*) from credentials where id = @id", credentialId);

    private static async Task<long> CountCodesAsync(NpgsqlConnection admin, Guid credentialId) =>
        await CountAsync(
            admin,
            "select count(*) from recovery_code_hashes where credential_id = @id",
            credentialId);

    /// <summary>
    /// How many sets the account holds, which the product's own partial index bounds at one — so this is
    /// how a test says "the loser's credential is not there beside the winner's".
    /// </summary>
    private static async Task<long> CountSetsOfUserAsync(NpgsqlConnection admin, Guid userId) =>
        await CountAsync(
            admin,
            "select count(*) from credentials where user_id = @id and type = 'recovery_codes'",
            userId);

    /// <summary>
    /// Every unredeemed code of one account, counted across whatever sets it holds — so a loser's ten
    /// hashes landing beside the winner's is a number this reports and a per-credential count is not.
    /// </summary>
    private static async Task<long> CountCodesOfUserAsync(NpgsqlConnection admin, Guid userId) =>
        await CountAsync(admin, "select count(*) from recovery_code_hashes where user_id = @id", userId);

    private static async Task<long> CountAsync(NpgsqlConnection admin, string sql, Guid id)
    {
        await using NpgsqlCommand command = new(sql, admin);
        command.Parameters.AddWithValue("id", id);

        // Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the query
        // changed shape, and that should fail loudly here instead of at the assertion.
        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from '{sql}', got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Runs <paramref name="action" /> and hands back whatever escaped, or <see langword="null" /> when
    /// nothing did. Deliberately untyped, as in <see cref="PasskeyRepositoryTests" />: the question this
    /// file asks is <i>which</i> exception surfaces, so catching a specific one in the helper would
    /// decide the answer before the assertion reads it — and the failure this file is written to catch is
    /// a <see cref="DbUpdateConcurrencyException" /> arriving where a <see cref="ConflictException" /> was
    /// promised.
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
    /// Names the constraint PostgreSQL actually refused on, or <see langword="null" /> when the escaping
    /// exception never reached the database at all. <c>RepositoryConstraintAttributionTests</c>' helper,
    /// spelled out here for the reason the seeding above is: each file in this folder owns the readers
    /// its own assertions need.
    /// </summary>
    private static string? ConstraintNameOf(Exception? exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
            ? postgresException.ConstraintName
            : null;

    /// <summary>
    /// The SQLSTATE PostgreSQL refused with, or <see langword="null" /> when nothing did. Read beside the
    /// constraint name so a narrowing test can say the violation it staged really is the kind the filter
    /// has to tell apart.
    /// </summary>
    private static string? SqlStateOf(Exception? exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
            ? postgresException.SqlState
            : null;

    /// <summary>
    /// Builds a context with no ambient budget, which is safe here because neither <c>Credential</c> nor
    /// <c>RecoveryCodeHash</c> carries a budget query filter — both belong to a person, not to a tenant.
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
