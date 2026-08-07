using Application.Users.EnsureUser;
using Domain.Budgets;
using Domain.Common;
using Domain.Users;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class EnsureUserHandlerTests
{
    [Test]
    public async Task EnsureUser_ReturningUserWhoseProviderEmailChanged_KeepsTheRegisteredEmail()
    {
        // Arrange — the provider now reports a different email than the one registration captured.
        // It gates registration and is never consulted again, so what it reports later is not
        // authority to change anything: an email change is a separate exchange the user deliberately
        // initiates. The budget is seeded alongside the account because an account without one is no
        // longer a state anything can produce — the three rows go in together or not at all.
        User user = User.Create("old@example.com", UtcNow());
        (InMemoryUserRepository users, _, _, EnsureUserHandler handler) = CreateHandler(
            user,
            GoogleCredentialFor(user, "google-1"),
            Budget.CreateDefault(user.Id, UtcNow()));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "new@example.com"));

        // Assert — the sign-in resolves to the same account, and that account still holds the email
        // it registered with. Reintroducing a silent per-request refresh fails this line.
        // The stored row is read off the fake rather than through the repository, because discovery
        // now answers with an id and nothing else — by design, since that lookup runs on a session
        // that has no identity yet and must touch no policed table. The claim under test is unchanged:
        // what the users table holds is still what registration wrote.
        await Assert.That(provisioned.UserId).IsEqualTo(user.Id);
        User stored = users.StoredUsers[provisioned.UserId];
        await Assert.That(stored.Email.Value).IsEqualTo("old@example.com");
    }

    [Test]
    public async Task EnsureUser_NewSubject_CreatesExactlyOneDefaultBudgetOwnedByTheUser()
    {
        // Arrange
        (_, InMemoryBudgetRepository budgets, _, EnsureUserHandler handler) = CreateHandler();

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-new", "new@example.com"));

        // Assert
        await Assert.That(budgets.Budgets.Count).IsEqualTo(1);
        Budget budget = budgets.Budgets[0];
        await Assert.That(budget.UserId).IsEqualTo(provisioned.UserId);
        await Assert.That(budget.Name).IsNull();
        await Assert.That(budget.BaseCurrencyCode).IsNull();
        await Assert.That(budget.CreatedAtUtc).IsEqualTo(UtcNow());
        await Assert.That(provisioned.BudgetId).IsEqualTo(budget.Id);
    }

    [Test]
    public async Task EnsureUser_NewSubject_StoresExactlyOneFederatedGoogleCredential()
    {
        // Arrange
        (InMemoryUserRepository users, _, _, EnsureUserHandler handler) = CreateHandler();

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-new", "new@example.com"));

        // Assert — the subject reaches the credential row and nothing else, which is what keeps a
        // second sign-in method addable later without touching the user row.
        await Assert.That(users.AddedCredentials.Count).IsEqualTo(1);
        Credential credential = users.AddedCredentials[0];
        await Assert.That(credential.UserId).IsEqualTo(provisioned.UserId);
        await Assert.That(credential.Type).IsEqualTo(CredentialType.Federated);
        await Assert.That(credential.Provider).IsEqualTo(Credential.GoogleProvider);
        await Assert.That(credential.Subject).IsEqualTo("google-new");
    }

    /// <summary>
    /// The atomicity the whole change turns on: the user, the credential that resolves to it and the
    /// budget it owns are one write or none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The argument was already made for the credential — a <c>users</c> row persisted without one
    /// holds the unique email forever while no sign-in resolves to it, and nothing can heal that. It
    /// applies to the budget verbatim: a user whose budget insert was lost is a signed-in person whose
    /// every budget-scoped query comes back empty, and until now the answer was a heal that ran on
    /// every authenticated request. One save deletes the state instead of repairing it.
    /// </para>
    /// <para>
    /// It also buys the conflict path its soundness. A reported unique violation means the winner's
    /// transaction committed, and with one save that transaction <b>contained its budget row</b> — so
    /// the loser can adopt a budget it knows is there. Under the split save the winner could commit its
    /// user and still lose its budget, which is exactly why the adoption used to be a find-or-create.
    /// </para>
    /// <para>
    /// Split the save — the user and its credential through the repository, the budget through
    /// <see cref="IBudgetRepository.TryAddAsync" /> afterwards — and two of these lines go red: the one
    /// save would no longer carry the budget, and the budget repository's write count would no longer
    /// be zero.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EnsureUser_NewSubject_WritesTheUserTheCredentialAndTheBudgetInOneSave()
    {
        // Arrange
        (InMemoryUserRepository users, InMemoryBudgetRepository budgets, _, EnsureUserHandler handler) =
            CreateHandler();

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-new", "new@example.com"));

        // Assert — one call to the one method that writes, and all three rows were handed to it
        // together. Asserted row by row rather than as a count of three, because three rows naming
        // three different accounts would also be three rows.
        await Assert.That(users.Saves.Count).IsEqualTo(1);
        InMemoryUserRepository.AttemptedSave save = users.Saves[0];
        await Assert.That(save.User.Id).IsEqualTo(provisioned.UserId);
        await Assert.That(save.Credential.UserId).IsEqualTo(provisioned.UserId);
        await Assert.That(save.DefaultBudget.UserId).IsEqualTo(provisioned.UserId);
        await Assert.That(save.DefaultBudget.Id).IsEqualTo(provisioned.BudgetId);

        // And nothing reached the budget repository's own insert, which is the second save this change
        // removes. The row is nonetheless there to be read, because the one save put it there.
        await Assert.That(budgets.AddCallCount).IsEqualTo(0);
        await Assert.That(budgets.Budgets.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureUser_ExistingCredentialWithABudget_DoesNotCreateAnother()
    {
        // Arrange
        User user = User.Create("person@example.com", UtcNow());
        Budget existingBudget = Budget.CreateDefault(user.Id, UtcNow());
        (InMemoryUserRepository users, InMemoryBudgetRepository budgets, _, EnsureUserHandler handler) =
            CreateHandler(user, GoogleCredentialFor(user, "google-1"), existingBudget);

        // Act
        ProvisionedUser first = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com"));
        ProvisionedUser second = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com"));

        // Assert — a returning request writes nothing at all now: not through the budget repository,
        // which provisioning no longer inserts through, and not through the user repository either.
        await Assert.That(budgets.Budgets.Count).IsEqualTo(1);
        await Assert.That(budgets.AddCallCount).IsEqualTo(0);
        await Assert.That(users.AddCallCount).IsEqualTo(0);
        await Assert.That(first.BudgetId).IsEqualTo(existingBudget.Id);
        await Assert.That(second.BudgetId).IsEqualTo(existingBudget.Id);
    }

    [Test]
    public async Task EnsureUser_WhenUserInsertLosesTheCredentialRace_ReturnsTheConcurrentlyCreatedUser()
    {
        // Arrange — a concurrent request inserts a credential on the same provider and subject
        // between our read and our write, which the repository reports as false rather than as a
        // throw. The winner's save carried its budget too, because that is what one save means.
        User concurrentUser = User.Create("person@example.com", UtcNow());
        (InMemoryUserRepository users, _, _, EnsureUserHandler handler) = CreateHandler();
        users.FailNextAddWithCredentialRace(
            concurrentUser,
            GoogleCredentialFor(concurrentUser, "google-1"),
            Budget.CreateDefault(concurrentUser.Id, UtcNow()));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com"));

        // Assert
        await Assert.That(provisioned.UserId).IsEqualTo(concurrentUser.Id);
        await Assert.That(users.AddCallCount).IsEqualTo(1);
    }

    /// <summary>
    /// The loser of a credential race reports the winner's budget, and inserts none of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the riskiest requirement in the change. With one save the loser wrote <b>nothing</b> —
    /// no user, no credential, no budget — so the only budget it can honestly report is the winner's,
    /// and it has to read it rather than mint it. What makes that sound is new: a reported unique
    /// violation means the winner's transaction committed, and under one save that transaction
    /// contained its budget row. Under the split save it did not, which is why the old code
    /// find-or-created here.
    /// </para>
    /// <para>
    /// The control is what the handler does today: it find-or-creates, so a loser that reached this
    /// point with no budget visible would insert a second one. Both halves are asserted — the id
    /// returned is the winner's, and the budget repository's insert was never called — because either
    /// alone can be satisfied by the wrong handler. Returning the winner's id while also writing a
    /// stray row leaves an orphan budget nobody will ever open; writing nothing while returning an id
    /// the caller minted scopes the rest of the request to a tenant that does not exist.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EnsureUser_WhenTheInsertLosesTheCredentialRace_AdoptsTheWinnersBudget()
    {
        // Arrange — the winner's whole account, landed in one save between our read and our write.
        User winner = User.Create("person@example.com", UtcNow());
        Budget winnersBudget = Budget.CreateDefault(winner.Id, UtcNow());
        (InMemoryUserRepository users, InMemoryBudgetRepository budgets, _, EnsureUserHandler handler) =
            CreateHandler();
        users.FailNextAddWithCredentialRace(
            winner,
            GoogleCredentialFor(winner, "google-1"),
            winnersBudget);

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com"));

        // Assert — the loser adopted both of the winner's ids.
        await Assert.That(provisioned.UserId).IsEqualTo(winner.Id);
        await Assert.That(provisioned.BudgetId).IsEqualTo(winnersBudget.Id);

        // And created nothing of its own: one budget in the store, and the insert never called. A
        // find-or-create surviving on this path fails the second line even when it happens to find
        // the winner's row, because the call itself is what must be gone.
        await Assert.That(budgets.Budgets.Count).IsEqualTo(1);
        await Assert.That(budgets.AddCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// The winner's identity reaches the session <b>before</b> the budget read that depends on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Newly load-bearing, and invisible to every other test in this suite. <c>budgets</c> is policed
    /// by <c>user_isolation</c>, which reads <c>app.current_user_id</c> — so a read issued while the
    /// session still names the loser's phantom id returns zero rows, and the handler then throws the
    /// exception that means "this account has no budget" about an account that has one. The caller
    /// gets a 500 on a race the design handles.
    /// </para>
    /// <para>
    /// The same ordering exists today, but it is not load-bearing today: a read under the wrong
    /// identity merely came back empty and triggered the heal, which silently created a second budget
    /// for nobody. Nothing went red, which is precisely why this test has to exist now.
    /// </para>
    /// <para>
    /// The instant is snapshotted from inside <see cref="IBudgetRepository.FindFirstForUserAsync" />
    /// rather than read afterwards, because ordering is the entire claim: a handler that publishes the
    /// winner before the read and one that publishes it after end with the same list of published ids.
    /// Swap the two statements locally and this is the only test that goes red.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EnsureUser_WhenTheInsertLosesTheCredentialRace_PublishesTheWinnerBeforeReadingItsBudget()
    {
        // Arrange — the same lost race, watched from the budget read's side.
        User winner = User.Create("person@example.com", UtcNow());
        (InMemoryUserRepository users, InMemoryBudgetRepository budgets, RecordingUserContextWriter writer,
            EnsureUserHandler handler) = CreateHandler();
        users.FailNextAddWithCredentialRace(
            winner,
            GoogleCredentialFor(winner, "google-1"),
            Budget.CreateDefault(winner.Id, UtcNow()));
        budgets.ObservePublicationsDuring(writer);

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com"));

        // Assert — one budget read, and the session named the winner when it was issued.
        await Assert.That(budgets.IdentityWhenFindFirstWasEntered.Count).IsEqualTo(1);
        await Assert.That(budgets.IdentityWhenFindFirstWasEntered[0]).IsEqualTo(winner.Id);
        await Assert.That(provisioned.UserId).IsEqualTo(winner.Id);

        // And specifically not the loser's own phantom id — the one it published before its doomed
        // insert, because the users INSERT is checked against app.current_user_id. Comparing against
        // that value rather than against "not empty" is what makes the assertion name the failure:
        // reading under the phantom is the exact mistake, not merely reading under something wrong.
        await Assert.That(budgets.IdentityWhenFindFirstWasEntered[0]).IsNotEqualTo(writer.Published[0]);
    }

    [Test]
    public async Task EnsureUser_NewSubjectWithAnEmailAnotherAccountHolds_ThrowsConflictException()
    {
        // Arrange — a brand-new subject whose email is already linked to a different account.
        (InMemoryUserRepository users, InMemoryBudgetRepository budgets, _, EnsureUserHandler handler) =
            CreateHandler();
        users.FailNextAddWithEmailConflict();

        // Act — anything other than ConflictException escapes this helper and fails the test. The
        // insert is refused and the re-read by credential finds nothing, so this is not a race that
        // path can resolve — and that empty re-read is precisely where the exception now comes from,
        // in place of the InvalidOperationException the branch used to raise.
        ConflictException exception = await ThrowsConflictExceptionAsync(() =>
            handler.HandleAsync(new EnsureUserCommand("google-new", "taken@example.com")));

        // Assert
        await Assert.That(exception.Message).IsNotEmpty();
        await Assert.That(users.AddCallCount).IsEqualTo(1);
        await Assert.That(budgets.Budgets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EnsureUser_ExistingSubject_PublishesTheResolvedUserId()
    {
        // Arrange — a returning sign-in, where the credential row already names an account that owns
        // its budget.
        User user = User.Create("person@example.com", UtcNow());
        (_, _, RecordingUserContextWriter writer, EnsureUserHandler handler) = CreateHandler(
            user,
            GoogleCredentialFor(user, "google-1"),
            Budget.CreateDefault(user.Id, UtcNow()));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com"));

        // Assert — exactly one publication, carrying the id the credential resolved. The budgets read
        // that follows is policed on app.current_user_id, so a handler that resolved the id and kept
        // it to itself would find no budget for an account that has one and fail the request outright.
        await Assert.That(writer.Published.Count).IsEqualTo(1);
        await Assert.That(writer.Published[0]).IsEqualTo(user.Id);
        await Assert.That(writer.Published[0]).IsEqualTo(provisioned.UserId);
    }

    [Test]
    public async Task EnsureUser_NewSubject_PublishesTheNewUserIdBeforeTheUserIsWritten()
    {
        // Arrange — the repository is armed to snapshot the writer as TryAddAsync is entered. Ordering
        // is the entire claim, and User.Create mints the id with Guid.CreateVersion7 client-side, so
        // the id genuinely exists before the row does and nothing stops the publication preceding the
        // insert.
        (InMemoryUserRepository users, _, RecordingUserContextWriter writer, EnsureUserHandler handler) =
            CreateHandler();
        users.ObservePublicationsDuring(writer);

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-new", "new@example.com"));

        // Assert — the id was already published when the insert began, and it is the id being
        // inserted. Publish it afterwards and the users INSERT runs on a session with no
        // app.current_user_id, so WITH CHECK (id = current_user_id) refuses the row and registration
        // is broken for every new account — while a test that read the writer only at the end would
        // still be green, which is exactly why the snapshot is taken from inside the call.
        await Assert.That(users.PublishedWhenAddWasEntered.Count).IsEqualTo(1);
        await Assert.That(users.PublishedWhenAddWasEntered[0]).IsEqualTo(provisioned.UserId);
        await Assert.That(users.AddedCredentials[0].UserId).IsEqualTo(provisioned.UserId);
    }

    [Test]
    public async Task EnsureUser_LostTheRace_PublishesTheWinningUserId()
    {
        // Arrange — the same lost credential race the returns-the-concurrent-user test above drives,
        // read from the publication side rather than the return value.
        User concurrentUser = User.Create("person@example.com", UtcNow());
        (InMemoryUserRepository users, _, RecordingUserContextWriter writer, EnsureUserHandler handler) =
            CreateHandler();
        users.FailNextAddWithCredentialRace(
            concurrentUser,
            GoogleCredentialFor(concurrentUser, "google-1"),
            Budget.CreateDefault(concurrentUser.Id, UtcNow()));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com"));

        // Assert — the loser published its own id before its doomed insert, so the count is not the
        // subject; what matters is who gets the last word, because the session carries that id into
        // the budgets read and on into the rest of the request. A stale loser id there polices every
        // later query against a user row that was never written. Pinning the last publication to
        // ProvisionedUser.UserId as well as to the winner states the rule that outlives this
        // particular race: the caller is never told one account while the session holds another.
        await Assert.That(writer.Published[^1]).IsEqualTo(concurrentUser.Id);
        await Assert.That(writer.Published[^1]).IsEqualTo(provisioned.UserId);
    }

    private static DateTime UtcNow() => new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private static Credential GoogleCredentialFor(User user, string subject) =>
        Credential.CreateFederated(user.Id, Credential.GoogleProvider, subject, UtcNow());

    /// <summary>
    /// The handler and the three fakes a test asserts against, together, because none of the four is
    /// useful without the others.
    /// </summary>
    /// <remarks>
    /// A positional record so a test can destructure the parts it cares about and discard the rest,
    /// which is what keeps the Arrange block down to the one or two lines that are actually about the
    /// case under test.
    /// </remarks>
    private sealed record Harness(
        InMemoryUserRepository Users,
        InMemoryBudgetRepository Budgets,
        RecordingUserContextWriter Writer,
        EnsureUserHandler Handler);

    /// <summary>
    /// Builds provisioning over fresh fakes, optionally seeded with an account that already exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The writer and the clock are built once and shared with the resolve half, because in the
    /// composition root they are the same request-scoped writer and the same clock. Handing the two
    /// halves separate instances would let a handler that published the identity only on its own
    /// writer still pass.
    /// </para>
    /// <para>
    /// <paramref name="existingBudget" /> is a separate argument rather than something this factory
    /// derives from <paramref name="existingUser" />: a seeded account without its budget is a state
    /// production can no longer reach, and a test that wants to describe one should have to say so out
    /// loud rather than get it by omission.
    /// </para>
    /// </remarks>
    private static Harness CreateHandler(
        User? existingUser = null,
        Credential? existingCredential = null,
        Budget? existingBudget = null)
    {
        InMemoryBudgetRepository budgets = new();
        InMemoryUserRepository users = new(budgets, existingUser, existingCredential);
        RecordingUserContextWriter writer = new();
        FakeTimeProvider time = new(new DateTimeOffset(UtcNow()));

        if (existingBudget is not null)
        {
            budgets.Seed(existingBudget);
        }

        return new Harness(
            users,
            budgets,
            writer,
            new EnsureUserHandler(
                users,
                budgets,
                writer,
                time,
                new ResolveUserHandler(users, budgets, writer)));
    }

    private static async Task<ConflictException> ThrowsConflictExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ConflictException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ConflictException.");
    }

    /// <summary>
    /// In-memory <see cref="IUserRepository"/> that reproduces the two ways the write can be refused,
    /// both of them reported as <see langword="false"/>: a lost race on the unique
    /// <c>(provider, subject)</c> credential rule, and an email already linked to another account.
    /// The two are deliberately indistinguishable from the outside — what separates them is whether a
    /// row is then there to be re-read, which is the caller's question to ask.
    /// </summary>
    /// <remarks>
    /// It is handed the <see cref="InMemoryBudgetRepository"/> the handler reads through, and writes
    /// the accepted budget straight into it. That sharing is the whole point rather than plumbing: one
    /// save means the budget row is visible to the next reader the instant the user row is, so a fake
    /// that kept its own budgets would let a handler pass while modelling nothing. It writes through
    /// <see cref="InMemoryBudgetRepository.Seed"/> and not through that repository's own
    /// <c>TryAddAsync</c>, because the row does not go in through <see cref="IBudgetRepository"/> at
    /// all any more — which is what leaves the insert count at zero for a test to assert.
    /// </remarks>
    private sealed class InMemoryUserRepository : IUserRepository
    {
        private readonly InMemoryBudgetRepository _budgets;
        private readonly List<AttemptedSave> _saves = [];
        private readonly Dictionary<Guid, User> _storedUsers = [];
        private List<Guid> _publishedWhenAddWasEntered = [];
        private RecordingUserContextWriter? _observedWriter;
        private User? _existingUser;
        private Credential? _existingCredential;
        private bool _failNextAddWithCredentialRace;
        private User? _raceWinner;
        private Credential? _raceWinnerCredential;
        private Budget? _raceWinnerBudget;
        private bool _failNextAddWithEmailConflict;

        public InMemoryUserRepository(
            InMemoryBudgetRepository budgets,
            User? existingUser = null,
            Credential? existingCredential = null)
        {
            _budgets = budgets;
            _existingUser = existingUser;
            _existingCredential = existingCredential;

            if (existingUser is not null)
            {
                _storedUsers[existingUser.Id] = existingUser;
            }
        }

        /// <summary>
        /// Everything one call to <see cref="TryAddAsync"/> was handed, kept together because
        /// "together" is the claim: the three rows are one write or none.
        /// </summary>
        public readonly record struct AttemptedSave(User User, Credential Credential, Budget DefaultBudget);

        /// <summary>Every call to <see cref="TryAddAsync"/>, refused ones included.</summary>
        public IReadOnlyList<AttemptedSave> Saves => _saves;

        public int AddCallCount => _saves.Count;

        /// <summary>Every credential handed to <see cref="TryAddAsync"/>, refused calls included.</summary>
        public IReadOnlyList<Credential> AddedCredentials => [.. _saves.Select(save => save.Credential)];

        /// <summary>
        /// Every user row this repository holds, by id. Discovery no longer hands back a user, so this
        /// is the only way left to ask what was actually stored — and it is a fair question for a test
        /// to ask, because the row is what a later request reads.
        /// </summary>
        public IReadOnlyDictionary<Guid, User> StoredUsers => _storedUsers;

        /// <summary>
        /// What <paramref name="writer"/> had published at the instant <see cref="TryAddAsync"/> was
        /// entered.
        /// </summary>
        /// <remarks>
        /// The snapshot has to be taken from inside the call because the ordering is the claim. Read
        /// once the handler has returned, a handler that publishes before its insert and one that
        /// publishes after are indistinguishable — both end with the same list — yet only the first
        /// survives the <c>WITH CHECK</c> on the users INSERT.
        /// </remarks>
        public void ObservePublicationsDuring(RecordingUserContextWriter writer) => _observedWriter = writer;

        /// <summary>
        /// The snapshot <see cref="ObservePublicationsDuring"/> arms. Empty when nothing was observed.
        /// </summary>
        public IReadOnlyList<Guid> PublishedWhenAddWasEntered => _publishedWhenAddWasEntered;

        /// <summary>
        /// Makes the next <see cref="TryAddAsync"/> call report a lost race on the unique
        /// <c>(provider, subject)</c> rule. The winning account is stored instead — all three of its
        /// rows, because that is what the winner's one save committed, and it is what makes the
        /// caller's re-read path observable.
        /// </summary>
        public void FailNextAddWithCredentialRace(User winner, Credential winnerCredential, Budget winnerBudget)
        {
            _failNextAddWithCredentialRace = true;
            _raceWinner = winner;
            _raceWinnerCredential = winnerCredential;
            _raceWinnerBudget = winnerBudget;
        }

        /// <summary>
        /// Makes the next <see cref="TryAddAsync"/> call report a refused insert because another
        /// account already holds that email. It reports <see langword="false"/> like any other
        /// refusal, and stores nothing — the absent row is the only difference from a lost race, and
        /// the caller's re-read is what finds it.
        /// </summary>
        public void FailNextAddWithEmailConflict() => _failNextAddWithEmailConflict = true;

        // Answers from the credential alone, which is not a simplification of the fake but the
        // contract: the real query may not join users, because discovery runs before the session
        // carries an identity and users is policed. Returning _existingUser?.Id here would let a
        // production query that still joined pass this suite.
        public Task<Guid?> FindUserIdByFederatedCredentialAsync(
            string provider,
            string subject,
            CancellationToken cancellationToken = default)
        {
            bool matches = _existingCredential is not null
                           && _existingCredential.Provider == provider.Trim()
                           && _existingCredential.Subject == subject.Trim();

            return Task.FromResult<Guid?>(matches ? _existingCredential!.UserId : null);
        }

        public Task<bool> TryAddAsync(
            User user,
            Credential credential,
            Budget defaultBudget,
            CancellationToken cancellationToken = default)
        {
            _saves.Add(new AttemptedSave(user, credential, defaultBudget));

            // Before anything else in this method: a snapshot taken after the store mutated would be
            // measuring the fake, not the handler.
            if (_observedWriter is not null)
            {
                _publishedWhenAddWasEntered = [.. _observedWriter.Published];
            }

            if (_failNextAddWithEmailConflict)
            {
                _failNextAddWithEmailConflict = false;

                // Nothing is stored, on purpose: the handler's follow-up
                // FindUserIdByFederatedCredentialAsync has to come back with no id, because that
                // empty re-read is the signal it keys on.
                return Task.FromResult(false);
            }

            if (_failNextAddWithCredentialRace)
            {
                _failNextAddWithCredentialRace = false;
                _existingUser = _raceWinner;
                _existingCredential = _raceWinnerCredential;

                if (_raceWinner is not null)
                {
                    _storedUsers[_raceWinner.Id] = _raceWinner;
                }

                // The winner's budget lands with the rest of its account, which is the property the
                // conflict path now rests on: a reported unique violation means the winning
                // transaction committed, and that transaction contained this row.
                if (_raceWinnerBudget is not null)
                {
                    _budgets.Seed(_raceWinnerBudget);
                }

                _raceWinner = null;
                _raceWinnerCredential = null;
                _raceWinnerBudget = null;
                return Task.FromResult(false);
            }

            _existingUser = user;
            _existingCredential = credential;
            _storedUsers[user.Id] = user;
            _budgets.Seed(defaultBudget);
            return Task.FromResult(true);
        }

        /// <summary>
        /// Drops the row and, with it, the credential that resolved to it — the cascade the real
        /// delete relies on, in the only shape this fake can hold. A row that is already gone is not
        /// an error, as the interface documents: the call states a post-condition rather than acting
        /// on a row.
        /// </summary>
        /// <remarks>
        /// No erasure test drives this fake — erasure has its own in <c>UnitTests.Fakes</c>, which
        /// carries the RESTRICT rule this one has no collaborator to model. It is implemented in
        /// state rather than as a throw so that the fake stays internally consistent: leaving
        /// <see cref="FindUserIdByFederatedCredentialAsync"/> resolving an id whose row was deleted
        /// would be a behaviour the real repository cannot produce.
        /// </remarks>
        public Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            _storedUsers.Remove(userId);

            if (_existingUser?.Id == userId)
            {
                _existingUser = null;
                _existingCredential = null;
            }

            return Task.CompletedTask;
        }
    }
}
