using System.Security.Cryptography;
using Application.Sessions;
using Domain.Budgets;
using Domain.Sessions;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace IntegrationTests;

/// <summary>
/// What <see cref="RegistrationRepository" /> does to the context it was handed when a registration is
/// refused.
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
    /// </remarks>
    private static Registration NewRegistration(string googleSubject, string email, out Guid accountId)
    {
        accountId = Guid.CreateVersion7();
        DateTime now = DateTime.UtcNow;

        User user = User.CreateWithId(accountId, email, now);
        Budget defaultBudget = Budget.CreateDefault(user.Id, now);
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
                .Select(_ => RecoveryCodeHash.From(
                    recoveryCodes,
                    RandomNumberGenerator.GetBytes(RecoveryCodeHash.VerifierLength),
                    now)),
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
