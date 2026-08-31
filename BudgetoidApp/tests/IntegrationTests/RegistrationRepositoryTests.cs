using System.Security.Cryptography;
using Application.Sessions;
using Domain.Budgets;
using Domain.Sessions;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// What <see cref="RegistrationRepository" /> does when a registration is refused: to the context it was
/// handed, and to a violation that is none of the four it speaks for.
/// </summary>
/// <remarks>
/// <para>
/// <b>The route's own tests cannot reach this, and the reason is worth stating.</b>
/// <see cref="AccountRegistrationTests" /> proves that a refused registration writes no row on
/// <em>that</em> save; nothing there says anything about the save after it, and nothing on the live route
/// makes one — the handler's only move after a refusal is a read, and EF Core does not flush pending
/// changes to run a query. So the four <c>Detach</c> calls in the repository's catch blocks are
/// unobservable over HTTP today and every one of them can be deleted with the whole suite green.
/// </para>
/// <para>
/// <b>Which is a statement about today's caller rather than about the rule.</b> The context is
/// request-scoped and <c>SaveChangesAsync</c> flushes everything it is tracking rather than only what
/// the later call handed it, so the first caller that writes after a refusal — a retry, a second
/// registration attempt, an audit row, anything added to the handler below the conflict — issues thirty
/// rows nobody asked for. What they hit is the loser's own collision, on a <c>when</c> clause the later
/// call wrote about its own account, and a second registration beyond reproach is refused for somebody
/// else's rows.
/// </para>
/// <para>
/// <b>This test was written once before and lost.</b> It shipped as
/// <c>UserRepositoryTests.TryAddAsync_AfterARefusedInsert_LeavesTheContextUsable</c>, against the
/// three-row <c>TryAddAsync</c> that preceded this repository, and its commit body said it would move
/// here when that method was replaced. It did not; it was deleted with the method. This is the move.
/// </para>
/// <para>
/// <b>The assertions are outcomes rather than change-tracker state.</b> Reading
/// <c>ChangeTracker.Entries()</c> would restate the implementation line for line and would stay green if
/// the detach moved somewhere it no longer helps. A later write that succeeds and lands exactly its own
/// rows is the property the caller needs. The rows are read back through a <em>fresh</em> context, so
/// this cannot pass by the second save quietly re-inserting the loser and the assertions reading them
/// out of the tracker that put them there.
/// </para>
/// <para>
/// Driven on the container superuser connection, which both isolation policies are <c>FOR ALL</c>
/// against: half of what is asserted here is that rows are <b>absent</b>, and a policed connection
/// reports a row that is there exactly as it reports one that is not.
/// </para>
/// </remarks>
public sealed class RegistrationRepositoryTests
{
    private const string WinnerSubject = "google-registration-winner";

    private const string WinnerEmail = "registration-winner@example.com";

    /// <summary>
    /// The address the losing attempt asserts, and it is deliberately <b>free</b>.
    /// </summary>
    /// <remarks>
    /// With a colliding address the leftover <c>users</c> row would refuse itself on the way out of the
    /// later save, and the test would report the wrong disease: a red naming the email index rather than
    /// the credential the loser is really carrying. Free, the only thing that can refuse the loser is the
    /// provider subject — which is exactly what the later call's own filter names.
    /// </remarks>
    private const string LoserEmail = "registration-loser@example.com";

    private const string NewcomerSubject = "google-registration-newcomer";

    private const string NewcomerEmail = "registration-newcomer@example.com";

    /// <summary>
    /// A refusal leaves the context usable: the next registration through the same context saves what it
    /// was handed, and nothing of the refused account rides along with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it holds: the four <c>Detach</c> calls in <see cref="RegistrationRepository" />'s catch
    /// blocks.</b> Remove all four and this test reds on one line — the third call answers
    /// <see cref="RegistrationOutcome.SubjectTaken" /> instead of
    /// <see cref="RegistrationOutcome.Registered" />, because the flush carries the loser's federated
    /// credential and it still holds the winner's provider identity.
    /// </para>
    /// <para>
    /// <b>What it costs when it fires: a person who has never registered anything is told their Google
    /// account is already registered, and told to sign in with a passkey that does not exist.</b> There is
    /// no state they can reach from which the message becomes true and nothing in the response names the
    /// real cause.
    /// </para>
    /// <para>
    /// <b>A second registration rather than an arbitrary write</b>, because that is the shape the caller
    /// would take: a request loses, the handler answers it, and the scoped context lives on. Three calls
    /// on one context — the winner, the loser, the newcomer — so the refusal sits between two saves that
    /// must both behave.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RegisterAsync_AfterARefusedRegistration_LeavesTheContextUsable()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using BudgetoidDbContext db = CreateDb(host);
        RegistrationRepository repository = new(db);

        Registration winner = NewRegistration(WinnerSubject, WinnerEmail, out Guid winnerId);
        Registration loser = NewRegistration(WinnerSubject, LoserEmail, out Guid loserId);
        Registration newcomer = NewRegistration(NewcomerSubject, NewcomerEmail, out Guid newcomerId);

        // Act — the winner, the refusal, and then the same context doing what a caller does next.
        RegistrationOutcome first = await repository.RegisterAsync(winner);
        RegistrationOutcome refused = await repository.RegisterAsync(loser);
        RegistrationOutcome third = await repository.RegisterAsync(newcomer);

        // Assert — the middle call lost to the subject it really collided on, and the third did not lose
        // at all. Nobody holds the newcomer's subject and nobody holds its address, so a SubjectTaken
        // here is the refused account's leftovers being read as the newcomer's own collision.
        await Assert.That(first).IsEqualTo(RegistrationOutcome.Registered);
        await Assert.That(refused).IsEqualTo(RegistrationOutcome.SubjectTaken);
        await Assert.That(third).IsEqualTo(RegistrationOutcome.Registered);

        // The newcomer really landed. Without this, a save that wrote nothing at all would satisfy every
        // remaining assertion, since each of them is about a row being absent.
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.Users.AnyAsync(user => user.Id == newcomerId)).IsTrue();
        await Assert.That(await verify.Users.AnyAsync(user => user.Id == winnerId)).IsTrue();

        // And the refused account is still nowhere, one save later. Looked for by the loser's own handle
        // rather than by a count: the winner's rows and the newcomer's are both there, so a count would
        // be answering a question about them instead. Every relation the registration writes is asked,
        // because which entity was left behind decides which of them the leftovers land in.
        await Assert.That(await verify.Users.AnyAsync(user => user.Id == loserId)).IsFalse();
        await Assert.That(await verify.Budgets.AnyAsync(budget => budget.UserId == loserId)).IsFalse();
        await Assert.That(await verify.Credentials.AnyAsync(credential => credential.UserId == loserId))
            .IsFalse();
        await Assert.That(await verify.PasskeyPublicKeys.AnyAsync(key => key.UserId == loserId)).IsFalse();
        await Assert.That(await verify.PasskeySignatureCounters
                .AnyAsync(counter => counter.UserId == loserId))
            .IsFalse();
        await Assert.That(await verify.RecoveryCodeHashes.AnyAsync(hash => hash.UserId == loserId))
            .IsFalse();
        await Assert.That(await verify.WrappedAccountKeys.AnyAsync(keys => keys.UserId == loserId))
            .IsFalse();
        await Assert.That(await verify.Sessions.AnyAsync(session => session.UserId == loserId)).IsFalse();
        await Assert.That(await verify.SessionTokens.AnyAsync(token => token.UserId == loserId)).IsFalse();
    }

    private const string BystanderSubject = "google-registration-bystander";

    private const string BystanderEmail = "registration-bystander@example.com";

    private const string CollidingSubject = "google-registration-colliding";

    private const string CollidingEmail = "registration-colliding@example.com";

    /// <summary>
    /// The primary key over <c>recovery_code_hashes.verifier_hash</c>, spelled out rather than read off
    /// <see cref="RecoveryCodeHashConfiguration" />.
    /// </summary>
    /// <remarks>
    /// <see cref="PasskeyRepositoryTests" /> spells <c>IX_users_email</c> out for the same reason: a
    /// test that took its expectation from the configuration the schema was generated from would agree
    /// with a renamed constraint the moment it was renamed, and this file's whole subject is what
    /// PostgreSQL <em>reports</em>. The configuration declares no constant for it — EF's convention
    /// names it — so there is nothing to read off in any case.
    /// </remarks>
    private const string RecoveryCodeHashPrimaryKeyName = "PK_recovery_code_hashes";

    /// <summary>
    /// A unique violation this repository does not model escapes, instead of being answered as one of
    /// the four refusals it does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it holds: the <c>ConstraintName</c> comparison inside <c>IsUniqueViolationOf</c>, which
    /// is the whole reason those four <c>catch</c> clauses carry an index name at all.</b> The
    /// translation half — that each named index becomes its own
    /// <see cref="RegistrationOutcome" /> — is pinned over HTTP by
    /// <see cref="AccountRegistrationTests" />. The mis-attribution half was pinned nowhere: widen any
    /// one of the four <c>when</c> clauses to the bare SQLSTATE and every test in the suite stays
    /// green, because nothing else stages a <c>23505</c> this method has no sentence for. Widen the
    /// email clause and this test reds, reporting
    /// <see cref="RegistrationOutcome.EmailTaken" /> where an exception should have escaped.
    /// </para>
    /// <para>
    /// <b>What it costs when it fires: somebody registering under an address nobody holds is told the
    /// address is taken</b>, and sent to a sign-in or a password reset for an account that does not
    /// exist. It is worse than the 500 the escape produces, because a 500 names the real constraint in
    /// a log and a confident, specific, false 409 names a rule that was never broken — the loop it
    /// puts a person in has no exit, since no address they can choose is the one really refusing them.
    /// </para>
    /// <para>
    /// <b>Staged on <c>PK_recovery_code_hashes</c> because it is keyed on a value a <em>stranger</em>
    /// holds.</b> The primary key is <c>verifier_hash</c> and it is unique table-wide regardless of
    /// owner, so one account's code can refuse another account's registration — which is exactly the
    /// property that makes the missing control matter for <c>IX_users_email</c> and
    /// <c>IX_credentials_provider_subject</c>. All three are rules about the whole table, so the row
    /// that breaks one is routinely a row the refused caller has never seen and could not have
    /// chosen. A per-account rule could not stand in here: the account id is derived from a spent
    /// nonce, so nothing this save writes can collide with anything filed under another <c>user_id</c>
    /// — the argument the repository itself makes about the three indexes it deliberately does not
    /// narrow on.
    /// </para>
    /// <para>
    /// <b>One of the ten hashes is replaced, never all ten.</b> A uniformly borrowed set would be
    /// refused by the first row PostgreSQL reached whatever the rule, and the test could not say the
    /// refusal was the collision rather than a set that was wrong in some other way. Everything else
    /// on the second registration — the subject, the address, the WebAuthn handle, the eleven factor
    /// identifiers, the account id — is freshly minted, so exactly one unique rule is breakable and
    /// the answer cannot turn on which of two violations the server happened to report first.
    /// </para>
    /// <para>
    /// The SQLSTATE is asserted beside the constraint name, which is what keeps this a narrowing test
    /// rather than a test that any failure escapes: a violation of some entirely different kind would
    /// satisfy "not one of the four" without ever exercising the filter. And the bystander's rows are
    /// read back at the end, because every other assertion here is about a row being <em>absent</em> —
    /// a save that wiped the database would satisfy all of them.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RegisterAsync_WhenAnotherUniqueRuleIsBroken_LetsTheViolationEscape()
    {
        // Arrange — an account that registered normally, and one of the ten verifiers it minted. On its
        // own context, exactly as its own request would have had: shared with the call under test, the
        // two sets would meet in the change tracker rather than in the database, and EF refuses a second
        // entity carrying a tracked primary key with an InvalidOperationException before a statement is
        // sent. What this test is about is the sentence PostgreSQL sends back.
        await using RepositoryTestHost host = await StartHostAsync();

        List<byte[]> bystandersVerifiers = [];
        Guid bystanderId;

        await using (BudgetoidDbContext seedDb = CreateDb(host))
        {
            Registration bystander = NewRegistration(
                BystanderSubject, BystanderEmail, out bystanderId, bystandersVerifiers);
            RegistrationOutcome seeded = await new RegistrationRepository(seedDb).RegisterAsync(bystander);

            await Assert.That(seeded).IsEqualTo(RegistrationOutcome.Registered);
        }

        await using BudgetoidDbContext db = CreateDb(host);
        RegistrationRepository repository = new(db);

        // A second registration that is beyond reproach everywhere the four filters look, carrying one
        // code the bystander already holds. Built by replacing a single hash rather than by minting the
        // set around a borrowed verifier, so the other nine stay exactly what NewRegistration produced.
        Registration fresh = NewRegistration(CollidingSubject, CollidingEmail, out Guid collidingId);
        RecoveryCodeHash borrowed = RecoveryCodeHash.From(
            fresh.RecoveryCodesCredential,
            bystandersVerifiers[0],
            DateTime.UtcNow);
        Registration colliding = fresh with
        {
            RecoveryCodeHashes = [borrowed, .. fresh.RecoveryCodeHashes.Skip(1)],
        };

        // Act
        Exception? escaped = await CaptureAsync(() => repository.RegisterAsync(colliding));

        // Assert — something escaped, which is already the claim: a swallowed violation would have
        // returned one of the four outcomes and left this null.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateException>();

        // And it really was a unique violation, on the rule the arrangement staged.
        await Assert.That(SqlStateOf(escaped)).IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That(ConstraintNameOf(escaped)).IsEqualTo(RecoveryCodeHashPrimaryKeyName);

        // All four, because the claim is that a stranger's violation is dressed up as none of this
        // repository's four sentences. Three of them named would stay green on a fourth widened to the
        // bare SQLSTATE, and each of the four answers with a different, equally confident lie.
        await Assert.That(ConstraintNameOf(escaped))
            .IsNotEqualTo(CredentialConfiguration.ProviderSubjectIndexName);
        await Assert.That(ConstraintNameOf(escaped)).IsNotEqualTo(UserConfiguration.EmailIndexName);
        await Assert.That(ConstraintNameOf(escaped))
            .IsNotEqualTo(PasskeyPublicKeyConfiguration.WebAuthnCredentialIdIndexName);
        await Assert.That(ConstraintNameOf(escaped))
            .IsNotEqualTo(WrappedAccountKeysConfiguration.PrimaryKeyName);

        // Nothing of the refused account landed. Through a fresh context, so this cannot pass by reading
        // the rows back out of the tracker that queued them, and by that account's own id rather than by
        // a count — the bystander's rows are still there, so a count would be answering about those.
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.Users.AnyAsync(user => user.Id == collidingId)).IsFalse();
        await Assert.That(await verify.Credentials.AnyAsync(credential => credential.UserId == collidingId))
            .IsFalse();
        await Assert.That(await verify.Budgets.AnyAsync(budget => budget.UserId == collidingId)).IsFalse();
        await Assert.That(await verify.RecoveryCodeHashes.AnyAsync(hash => hash.UserId == collidingId))
            .IsFalse();
        await Assert.That(await verify.WrappedAccountKeys.AnyAsync(keys => keys.UserId == collidingId))
            .IsFalse();
        await Assert.That(await verify.Sessions.AnyAsync(session => session.UserId == collidingId))
            .IsFalse();
        await Assert.That(await verify.SessionTokens.AnyAsync(token => token.UserId == collidingId))
            .IsFalse();

        // And the bystander is untouched, all ten codes of it. Without this, a save that rolled the
        // whole database back would satisfy every absence assertion above.
        await Assert.That(await verify.Users.AnyAsync(user => user.Id == bystanderId)).IsTrue();
        await Assert.That(await verify.RecoveryCodeHashes.CountAsync(hash => hash.UserId == bystanderId))
            .IsEqualTo(RecoveryCodeSetSize);
    }

    /// <summary>
    /// One whole account, built the way <c>RegisterAccountHandler</c> builds one: the user, its budget,
    /// three credentials, the passkey's material and counter, ten hashes, eleven shares of the account
    /// keys, and the session with its handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every value a unique rule is keyed on is minted fresh per call — the account id, the WebAuthn
    /// handle, the ten verifiers, the eleven factor identifiers — so the only things two registrations
    /// built here can collide on are the two the caller chooses. That is what keeps
    /// <see cref="RegisterAsync_AfterARefusedRegistration_LeavesTheContextUsable" /> a statement about the
    /// subject rather than about whichever rule happened to be breached first.
    /// </para>
    /// <para>
    /// <c>factor_id</c> is <c>PK_wrapped_account_keys</c> and therefore unique table-wide rather than per
    /// account, so a shared value here would make the second registration anywhere in one database a
    /// <c>23505</c> — the same reason <see cref="WrappedKeyFixture" /> is minted per call.
    /// </para>
    /// <para>
    /// <paramref name="mintedVerifiers" /> is how a caller gets one of those minted values back, and it
    /// is an optional collection rather than a second <c>out</c> so that the calls which do not want one
    /// keep reading exactly as they did. The verifiers are otherwise unreachable: they are hashed inside
    /// <see cref="RecoveryCodeHash.From" /> and the row hands back only the digest, which is the whole
    /// design — see that factory. <see cref="RegisterAsync_WhenAnotherUniqueRuleIsBroken_LetsTheViolationEscape" />
    /// needs one because a stranger's code is what it stages the collision on.
    /// </para>
    /// </remarks>
    private static Registration NewRegistration(
        string googleSubject,
        string email,
        out Guid accountId,
        ICollection<byte[]>? mintedVerifiers = null)
    {
        accountId = Guid.CreateVersion7();
        DateTime now = DateTime.UtcNow;

        User user = User.CreateWithId(accountId, email, now);
        Budget defaultBudget = Budget.CreateDefault(Guid.CreateVersion7(), user.Id, now);
        Credential federated = Credential.CreateFederated(
            user.Id,
            Credential.GoogleProvider,
            googleSubject,
            now);
        Credential passkey = Credential.CreatePasskey(user.Id, now);
        Credential recoveryCodes = Credential.CreateRecoveryCodes(user.Id, now);

        PasskeyPublicKey publicKey = PasskeyPublicKey.Register(
            passkey,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(77),
            CoseAlgorithm.Es256);
        PasskeySignatureCounter counter = PasskeySignatureCounter.Start(passkey, value: 0);

        IReadOnlyList<RecoveryCodeHash> hashes =
        [
            .. Enumerable.Range(0, RecoveryCodeSetSize)
                .Select(_ => NewCode(recoveryCodes, now, mintedVerifiers)),
        ];

        IReadOnlyList<WrappedAccountKeys> wrappedAccountKeys =
        [
            SharedFor(passkey, now),
            .. Enumerable.Range(0, RecoveryCodeSetSize).Select(_ => SharedFor(recoveryCodes, now)),
        ];

        Session session = Session.Establish(passkey, now, now + SessionPolicy.Lifetime);
        SessionHandle handle = SessionHandle.Mint();

        return new Registration(
            user,
            defaultBudget,
            federated,
            passkey,
            recoveryCodes,
            publicKey,
            counter,
            hashes,
            wrappedAccountKeys,
            session,
            handle.TokenFor(session));
    }

    /// <summary>How many codes a card holds. Restated rather than read off the handler.</summary>
    private const int RecoveryCodeSetSize = 10;

    /// <summary>
    /// One unredeemed code, over a verifier minted here and handed to <paramref name="mintedVerifiers" />
    /// on the way past when a caller asked for it.
    /// </summary>
    /// <remarks>
    /// A named method rather than a statement lambda inside the collection expression, because the
    /// verifier has to be named twice — once to record it and once to hash it — and a call that hashed
    /// a second draw would file a code no caller of this helper holds.
    /// </remarks>
    private static RecoveryCodeHash NewCode(
        Credential recoveryCodes,
        DateTime now,
        ICollection<byte[]>? mintedVerifiers)
    {
        byte[] verifier = RandomNumberGenerator.GetBytes(RecoveryCodeHash.VerifierLength);
        mintedVerifiers?.Add(verifier);

        return RecoveryCodeHash.From(recoveryCodes, verifier, now);
    }

    /// <summary>
    /// One factor's share of the account keys, over a fresh identifier and a well-formed envelope pair.
    /// </summary>
    /// <remarks>
    /// The envelopes are <see cref="WrappedKeyFixture" />'s, which is the suite's one definition of the
    /// width and version the columns accept. Nothing here opens one — this file's subject is the change
    /// tracker, not key custody — so random bytes of the right shape are the whole requirement.
    /// </remarks>
    private static WrappedAccountKeys SharedFor(Credential credential, DateTime now)
    {
        WrappedKeyFixture envelopes = WrappedKeyFixture.Mint();

        return WrappedAccountKeys.For(
            credential,
            envelopes.Factor,
            envelopes.ContentEnvelope,
            envelopes.IndexEnvelope,
            now);
    }

    /// <summary>
    /// Names the constraint PostgreSQL actually refused on, or <see langword="null" /> when the escaping
    /// exception never reached the database at all.
    /// </summary>
    /// <remarks>
    /// <see cref="PasskeyRepositoryTests" />' helper, spelled out here because each file in this folder
    /// owns the readers its own assertions need.
    /// </remarks>
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
    /// Runs <paramref name="action" /> and hands back whatever escaped, or <see langword="null" /> when
    /// nothing did.
    /// </summary>
    /// <remarks>
    /// Deliberately untyped, as in <see cref="PasskeyRepositoryTests" />: the question is <i>which</i>
    /// exception surfaces, so catching a specific one in the helper would decide the answer before the
    /// assertion reads it — and the failure being watched for here is no exception at all.
    /// </remarks>
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
    /// A context with no ambient budget, which is safe because nothing a registration writes carries a
    /// budget query filter — see <see cref="BudgetoidDbContext" />, which says so relation by relation.
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
