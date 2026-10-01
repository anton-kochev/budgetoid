using Application.Abstractions;
using Application.KeyRotations.ResealRows;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Common;
using Domain.Payees;
using Domain.Security;
using Domain.Transactions;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The chunk that re-seals rows, driven against a real PostgreSQL over the least-privilege
/// application role: whether the ciphertext and the stamp reach the database together, whether an
/// abandoned unit of work takes both back, and what happens to a chunk naming a row of another budget.
/// </summary>
/// <remarks>
/// <para>
/// <b>ON <see cref="RepositoryTestHost.AppConnectionString" /> AND NOT ON THE CONTAINER SUPERUSER, AND
/// THAT IS THE POINT OF THE FILE.</b> PostgreSQL skips every privilege check for a superuser, so a
/// chunk driven on the host's own connection would pass whatever the grant matrix says — including
/// with no grants script at all. The six <c>rotation_id</c> columns carry no <c>GRANT UPDATE</c>
/// today, and until five column lists in <c>app-role-grants.sql</c> are widened the save here meets
/// <c>42501</c>. <b>That is the fail-closed direction working rather than an obstacle</b>, and it is
/// the reason this suite is worth more than one driven where privileges cannot be observed.
/// </para>
/// <para>
/// <b>There is no grant-absence test here and there must not be one.</b> "The role cannot write
/// <c>rotation_id</c>" is true for as long as it takes to widen the lists and wrong for ever
/// afterwards; <c>AppRoleGrantMatrixTests</c> is where the widened grant is pinned once it lands.
/// What this file asserts is the behaviour that has to be true on both sides of that change.
/// </para>
/// <para>
/// <b>Every arrangement and every read-back is made on the container superuser connection; only the
/// act runs on the app role.</b> That is <c>AppRoleGrantsTests</c>' idiom and here it is load-bearing
/// twice over: half of what is asserted is that a column did <em>not</em> move, and a policed
/// connection reports a row it cannot see exactly as it reports one that did not change — and the
/// seeding writes columns the app role is deliberately never granted.
/// </para>
/// <para>
/// <b>FIVE ARMS AND NO BUDGET ARM. THE REASON IS AN ACCEPTANCE CRITERION AND NOT AN ACCESS
/// MODIFIER.</b> A budget arm would need <c>rotation_id</c> in the <c>budgets</c> <c>GRANT UPDATE</c>
/// column list, and this story's own criterion is that the application role holds <c>UPDATE</c> on
/// <c>budgets.name</c> <em>and no other column</em> — an omission from a column list being the only
/// way this schema makes a column immutable. The sixth arm is therefore refused rather than missing,
/// and adding it means arguing against that criterion first. <b><c>Budget.ResealName</c> being
/// <see langword="internal" /> is the mechanism that makes the refusal a compile error</b>:
/// <c>Domain.csproj</c> grants its internals to <c>Infrastructure</c> alone, so this project could not
/// call it whatever the grant said. Nothing is lost either way — every budget row is nameless today
/// and the completeness gate is presence-aware, so none is ever outstanding.
/// <c>docs/business-logic/key-rotation.md</c> argues all of it; the absent sixth arm is a decision and
/// not a gap.
/// </para>
/// <para>
/// <b>What "in one transaction" is measured as, and — read this before trusting the word in a test
/// name — what is NOT measured here.</b> The guarantee the doc states is transactional and not
/// statement-level: if EF split a row's ciphertext and its stamp into two statements they would still
/// commit or roll back together, so a statement census could say which columns one <c>UPDATE</c> named
/// and would say nothing about the guarantee. What this file holds instead is two claims that are
/// weaker than that guarantee and are worth having on their own terms. The happy path reads every
/// column back after the handler returned, so the ciphertext and the stamp <em>arrived</em> together;
/// <see cref="ResealChunk_WhenTheUnitOfWorkIsAbandoned_LeavesEveryRowAsItWas" /> fails the unit of work
/// after the save and reads the same columns again, so <em>nothing is written when the unit of work
/// fails</em>.
/// </para>
/// <para>
/// <b>NEITHER OF THEM SAYS THE SAVE IS INSIDE THE TRANSACTION, AND THIS PARAGRAPH USED TO SAY THEY
/// DID.</b> It read "the claim is made twice from two directions", which is a completeness claim about
/// a pair, and measured it is false: a handler that entered the executor, did nothing in the delegate,
/// and saved <em>after</em> <c>ExecuteAsync</c> returned — skipping the save when it faulted — leaves
/// every case in this file green. The happy path still finds the rows rewritten, and the abandoned case
/// still finds them untouched, because that handler never saved at all. Where the save landed relative
/// to the delegate is control flow, it is visible without a database, and
/// <c>ResealRowsHandlerTests.ResealChunk_SavesInsideTheUnitOfWork</c> holds it <b>alone</b>. Do not
/// read this file as a second opinion on it.
/// </para>
/// </remarks>
public sealed class ResealChunkTests
{
    /// <summary>
    /// The five tables a chunk rewrites, in the order <c>docs/business-logic/key-rotation.md</c> lists
    /// them — <b>minus <c>budgets</c></b>, for the reason this file's summary gives.
    /// </summary>
    /// <remarks>
    /// Written out rather than derived from the model, following <c>RotationCompletenessTests</c>: a
    /// list built from the mapping agrees with whatever the mapping later decides, and the point of
    /// this one is to be the independent statement of how many arms there are.
    /// </remarks>
    private static readonly string[] ResealedTables =
        ["accounts", "payees", "category_groups", "categories", "transactions"];

    /// <summary>
    /// The happy path: every row the chunk names comes back carrying the new ciphertext, the new blind
    /// index and the run's identifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The stamp is asserted beside the ciphertext on every arm, and neither half stands alone.</b> A
    /// chunk that wrote the ciphertext and forgot the stamp leaves the completeness gate refusing for
    /// ever with nothing anywhere saying why — the non-converging rotation the doc calls harder to
    /// diagnose than a crash. A chunk that wrote the stamp and dropped the ciphertext is the opposite
    /// and worse: the gate answers <b>complete</b>, the promotion destroys the only wrapped copies of
    /// the generation those rows are still sealed under, and the discovery is a person reloading the
    /// page to find their budget unreadable.
    /// </para>
    /// <para>
    /// <b>The blind index is read too, and on a payee it is the half that bites.</b> A rotation replaces
    /// the index key as well as the content key, so a member that re-sealed the envelope alone would
    /// leave every payee keyed under the previous generation: find-or-create stops finding anything and
    /// the next transaction against each existing counterparty mints a duplicate.
    /// </para>
    /// <para>
    /// <b>Offenders are collected and asserted as an empty list rather than table by table</b>, so a
    /// failure names every arm that stood still and not merely the first one.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_StampsEveryRowItRewrites_InOneTransaction()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        SeededAccount account = await SeedAccountAsync(host, "google-owner", "owner@example.com");
        Guid rotationId = await StageRotationAsync(host, account);
        IReadOnlyDictionary<string, RowState> before = await SnapshotAsync(admin, account);

        // The arrangement, asserted before the subject is touched: every row exists and not one of them
        // carries a stamp of any kind, so every claim below is about what the act did.
        await Assert.That(before.Count).IsEqualTo(ResealedTables.Length);
        await Assert.That(before.Where(row => row.Value.RotationId is not null).Select(row => row.Key).ToArray())
            .IsEmpty();

        // Act — through the real adapter, on the least-privilege role, inside the real unit of work.
        await using BudgetoidDbContext db = AppDb(host, account);
        await HandlerOver(db, account).HandleAsync(ChunkFor(account, rotationId));

        // Assert — read back on the superuser connection, so nothing the act did can be hidden from the
        // reader by a policy.
        IReadOnlyDictionary<string, RowState> after = await SnapshotAsync(admin, account);

        await Assert.That(after
                .Where(row => row.Value.RotationId != rotationId)
                .Select(row => $"{row.Key}: {row.Value.RotationId?.ToString() ?? "no stamp"}")
                .ToArray())
            .IsEmpty();

        // And the values really moved. Every arm carries a sealed narrative value; the four that carry a
        // name carry a blind index beside it, and the three that carry a description carry one that was
        // present before and must be present after.
        await Assert.That(after
                .Where(row => !row.Value.NarrativeMoved(before[row.Key]))
                .Select(row => row.Key)
                .ToArray())
            .IsEmpty();
    }

    /// <summary>
    /// A unit of work that fails leaves every row exactly as it was — ciphertext and stamp alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>WHAT THIS HOLDS IS "NOTHING IS WRITTEN WHEN THE UNIT OF WORK FAILS", AND IT IS NOT "THE SAVE
    /// IS INSIDE THE TRANSACTION".</b> The two read alike and the difference is the whole of why this
    /// note is here. Measured: a handler that entered the executor, did nothing in the delegate and
    /// saved after <c>ExecuteAsync</c> returned — skipping the save when it faulted — passes this case,
    /// because it never saved, and passes every other case in this file too. So this one cannot tell a
    /// rollback from a save that never happened, and it does not claim to.
    /// <c>ResealRowsHandlerTests.ResealChunk_SavesInsideTheUnitOfWork</c> is what holds the side of the
    /// delegate the write landed on, and it holds it alone.
    /// </para>
    /// <para>
    /// <b>That leaves a claim worth having rather than a decoration.</b> Every other assertion here is
    /// satisfied by a handler that wrote and then failed — the rows moved, the columns agree, the
    /// account is stamped — and this is the only case that says a failure anywhere in the unit of work
    /// leaves nothing behind. On a rotation what it would otherwise leave behind is an account half
    /// under each content key, every half stamped as though the whole run had rewritten it, which is
    /// the state the staging design exists to keep recoverable.
    /// </para>
    /// <para>
    /// <b>Strengthening it to the stronger claim was considered and is not free.</b> The wrong handler
    /// above is distinguished only by a <em>second</em> fault — something that fails after
    /// <c>ExecuteAsync</c> has returned successfully, or an observation of the write's position — and
    /// the first is a second failure injected into a case already named for one, which would make a
    /// green run ambiguous about which fault it survived. The observation is the cheaper instrument and
    /// it needs no database, which is why it lives one tier down.
    /// </para>
    /// <para>
    /// <b>The failure is injected after the operation and before the commit</b>, by a decorator wrapping
    /// the real <c>DbContextTransactionalExecutor</c>: the inner executor owns the begin and the commit,
    /// so a throw between the two abandons exactly what a real failure on any later statement would.
    /// Failing <em>inside</em> the save instead would measure PostgreSQL's statement atomicity, which is
    /// not in question.
    /// </para>
    /// <para>
    /// <b>The read-back is the whole snapshot, not one row.</b> An arm that committed on its own is the
    /// defect, so the assertion has to be able to name which one — and a chunk touching five tables has
    /// five chances to be the one that escaped.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_WhenTheUnitOfWorkIsAbandoned_LeavesEveryRowAsItWas()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        SeededAccount account = await SeedAccountAsync(host, "google-owner", "owner@example.com");
        Guid rotationId = await StageRotationAsync(host, account);
        IReadOnlyDictionary<string, RowState> before = await SnapshotAsync(admin, account);

        await using BudgetoidDbContext db = AppDb(host, account);
        AbandonedUnitOfWork abandoned = new(new DbContextTransactionalExecutor(db));
        ResealRowsHandler handler = new(
            new NarrativeResealRepository(db),
            new KeyRotationRepository(db),
            new TestUserContext(account.UserId),
            abandoned);

        // Act
        Exception? escaped = await CaptureAsync(
            () => handler.HandleAsync(ChunkFor(account, rotationId)));

        // The act really did fail the way this case arranges, rather than for some other reason — a
        // 42501 out of the save would satisfy "something threw" while proving nothing about a rollback.
        await Assert.That(escaped).IsTypeOf<AbandonedUnitOfWorkException>();

        // Assert — every column of every arm is exactly what it was.
        IReadOnlyDictionary<string, RowState> after = await SnapshotAsync(admin, account);

        await Assert.That(after
                .Where(row => !row.Value.Equals(before[row.Key]))
                .Select(row => row.Key)
                .ToArray())
            .IsEmpty();
    }

    /// <summary>
    /// A chunk naming a row of another budget is refused, and rewrites nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The five sets carry the <c>BudgetIsolation</c> query filter, so a foreign row is
    /// <em>invisible</em> rather than forbidden.</b> The port's read simply does not answer for that
    /// identifier, which is why the refusal has to be the handler's: a caller that treated a missing key
    /// as "nothing to do here" would answer <c>200</c> to a chunk that re-sealed nothing, the client
    /// would count the rows as done, and the completeness gate would refuse a run the client believes it
    /// finished. <c>NotFoundException</c> is the house answer for a row a request cannot see, and it is
    /// what turns the invisibility into the API's own 404 rather than into silence.
    /// </para>
    /// <para>
    /// <b>The foreign payee belongs to a second account and not to a second budget of this one.</b> An
    /// account owning two budgets is refused a rotation outright — <c>RotationScopeException</c>, at the
    /// begin and again at the completeness gate — so arranging one here would be arranging a state no
    /// rotation reaches, and the refusal under test could be read as that one.
    /// </para>
    /// <para>
    /// <b>The chunk names this account's own rows beside the stranger's</b>, so the assertion that
    /// nothing moved is about a refusal that preceded the write rather than about a chunk with nothing
    /// in it. A handler that re-sealed the four legible arms and then refused for the fifth would leave
    /// this account's rows carrying a stamp the rest of the run never earned.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_WhenARowBelongsToAnotherBudget_IsRefused()
    {
        // Arrange — two accounts, each with its own budget, and a chunk that names one payee from each.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        SeededAccount mine = await SeedAccountAsync(host, "google-owner", "owner@example.com");
        SeededAccount theirs = await SeedAccountAsync(host, "google-other", "other@example.com");
        Guid rotationId = await StageRotationAsync(host, mine);
        IReadOnlyDictionary<string, RowState> before = await SnapshotAsync(admin, mine);

        ResealRowsCommand chunk = ChunkFor(mine, rotationId) with
        {
            Payees =
            [
                new ResealedPayee(mine.PayeeId, SealedNarrative.Indexed("grocer rotated")),
                new ResealedPayee(theirs.PayeeId, SealedNarrative.Indexed("stranger rotated")),
            ],
        };

        // The premise: the stranger's payee really is in another budget, and it really is in the chunk.
        await Assert.That(theirs.BudgetId).IsNotEqualTo(mine.BudgetId);
        await Assert.That(chunk.Payees.Select(payee => payee.Id).ToArray()).Contains(theirs.PayeeId);

        await using BudgetoidDbContext db = AppDb(host, mine);

        // Act
        NotFoundException refusal = await ThrowsAsync<NotFoundException>(
            () => HandlerOver(db, mine).HandleAsync(chunk));

        // Assert — that it refused at all is the first claim. The message is not pinned: it reaches a
        // caller as a 404 body, and asserting its wording here would make that a phrasing test.
        await Assert.That(refusal).IsNotNull();

        // And this account's own rows are exactly as they were — no arm was rewritten on the way to the
        // one that could not be read.
        IReadOnlyDictionary<string, RowState> after = await SnapshotAsync(admin, mine);
        await Assert.That(after
                .Where(row => !row.Value.Equals(before[row.Key]))
                .Select(row => row.Key)
                .ToArray())
            .IsEmpty();

        // And the stranger's row is untouched too, which is a different claim: the one above is about
        // this account, and this one is about the budget the chunk reached into.
        await Assert.That(await StampOfAsync(admin, "payees", theirs.PayeeId)).IsNull();
    }

    /// <summary>
    /// The adapter answers the rows the chunk named and no others, on a budget that holds siblings
    /// beside them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THIS ASKS THE PORT DIRECTLY RATHER THAN READING THE CHUNK'S SIDE EFFECTS, AND THE EARLIER
    /// SHAPE OF THIS CASE IS WHY.</b> It was written as "run a chunk, then read the siblings off the
    /// database", and measured, that cannot fail on its own: a query that loses its identifier
    /// predicate pulls the whole budget into the change tracker and a correct handler writes to none of
    /// it, so every row on disk is untouched and the case is green. It reddened only when the handler
    /// <em>also</em> drove from what it loaded — a conjunction, and the handler half of it is held by
    /// <c>ResealRowsHandlerTests.ResealChunk_RewritesNoRowItWasNotGiven</c> on its own. A case that
    /// needs somebody else's defect to bite is not holding its own claim.
    /// </para>
    /// <para>
    /// <b>So the claim is made where it is observable: the answer itself.</b> Asked for one payee and
    /// one transaction out of a budget holding two of each, the adapter comes back with one of each —
    /// which reddens the moment the <c>Where</c> goes, with no second fault required.
    /// </para>
    /// <para>
    /// <b>What an over-fetch costs even when nothing is written to it</b>, so the claim is not read as
    /// tidiness: every chunk of a rotation would drag the account's whole <c>transactions</c> table
    /// through the change tracker — the largest table in the product on any account that has been used —
    /// and a rotation is chunked precisely because it does not fit in one request. It also widens the
    /// blast radius of every later defect on this path from the rows a client named to the whole budget.
    /// </para>
    /// <para>
    /// <b>It writes nothing, so it is the one case in this file the missing <c>rotation_id</c> grant
    /// does not reach.</b> A read needs no <c>UPDATE</c> privilege; this case is green the day the port
    /// exists, whatever the column lists say.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunk_LoadsOnlyTheRowsTheChunkNames()
    {
        // Arrange — one account, and one extra payee and one extra transaction inside its own budget.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        SeededAccount account = await SeedAccountAsync(host, "google-owner", "owner@example.com");
        (Guid siblingPayeeId, Guid siblingTransactionId) = await AddSiblingsAsync(host, account);

        // The premise: both siblings live in the budget the read runs inside, so they are visible to the
        // adapter and only its own predicate keeps them out. Without this the case would pass over a
        // query scoped by nothing, because the query filter would have excluded them anyway.
        await Assert.That(await BudgetOfAsync(admin, "payees", siblingPayeeId)).IsEqualTo(account.BudgetId);
        await Assert.That(await BudgetOfAsync(admin, "transactions", siblingTransactionId))
            .IsEqualTo(account.BudgetId);

        await using BudgetoidDbContext db = AppDb(host, account);
        NarrativeResealRepository rows = new(db);

        // Act — ask for one row of each arm that has a sibling.
        IReadOnlyDictionary<Guid, Payee> payees = await rows.ListPayeesAsync([account.PayeeId]);
        IReadOnlyDictionary<Guid, Transaction> transactions =
            await rows.ListTransactionsAsync([account.TransactionId]);

        // Assert — exactly what was asked for, named as sets so a failure says which extra row arrived
        // rather than only that the count was wrong.
        await Assert.That(payees.Keys.ToArray()).IsEquivalentTo(new[] { account.PayeeId });
        await Assert.That(transactions.Keys.ToArray()).IsEquivalentTo(new[] { account.TransactionId });
    }

    /// <summary>
    /// The chunk a correct client sends for <paramref name="account" />: one entry per arm, every value
    /// re-sealed under a label that differs from the seeded one.
    /// </summary>
    /// <remarks>
    /// <b>Every description is present, because every seeded row holds one.</b> The presence rule
    /// refuses a reseal that changes whether a column holds a value, and a chunk leaving a note out
    /// would be refused for that rather than measuring anything this file is about — the two arms of
    /// that rule are the unit tier's, which can tell the two sentences apart.
    /// </remarks>
    private static ResealRowsCommand ChunkFor(SeededAccount account, Guid rotationId) => new(
        rotationId,
        [new ResealedAccount(account.AccountId, SealedNarrative.Indexed("checking rotated"))],
        [new ResealedPayee(account.PayeeId, SealedNarrative.Indexed("grocer rotated"))],
        [
            new ResealedCategoryGroup(
                account.CategoryGroupId,
                SealedNarrative.Indexed("everyday rotated"),
                SealedNarrative.Description("group note rotated")),
        ],
        [
            new ResealedCategory(
                account.CategoryId,
                SealedNarrative.Indexed("groceries rotated"),
                SealedNarrative.Description("category note rotated")),
        ],
        [
            new ResealedTransaction(
                account.TransactionId,
                SealedNarrative.Description("transaction note rotated")),
        ]);

    /// <summary>
    /// The handler as production assembles it, over one context.
    /// </summary>
    /// <remarks>
    /// The two ports are resolved as their concrete adapters because that is what is under test here;
    /// the executor is the real one, so the unit of work this handler opens is a real PostgreSQL
    /// transaction on the same connection the repositories write through.
    /// </remarks>
    private static ResealRowsHandler HandlerOver(BudgetoidDbContext db, SeededAccount account) => new(
        new NarrativeResealRepository(db),
        new KeyRotationRepository(db),
        new TestUserContext(account.UserId),
        new DbContextTransactionalExecutor(db));

    /// <summary>
    /// A context on the <b>least-privilege</b> connection, carrying the session settings both isolation
    /// policies read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>SessionContextInterceptor</c> is wired in rather than the settings being written by
    /// hand.</b> It is a connection-opened interceptor, so it is what survives EF closing and re-opening
    /// the connection between operations and what survives a retrying execution strategy replaying the
    /// unit of work — and a test that issued one <c>set_config</c> of its own would be measuring a
    /// session production never produces.
    /// </para>
    /// <para>
    /// <b>Both settings, because both policies are in play.</b> <c>budget_isolation</c> scopes the five
    /// arms on <c>app.current_budget_id</c>, and <c>user_isolation</c> scopes the <c>key_rotations</c>
    /// read on <c>app.current_user_id</c>; an unset setting reaches a policy as <c>''::uuid</c> and
    /// raises <c>22P02</c> rather than reading the wrong rows.
    /// </para>
    /// </remarks>
    private static BudgetoidDbContext AppDb(RepositoryTestHost host, SeededAccount account)
    {
        TestBudgetContext budgetContext = new(account.BudgetId);
        TestUserContext userContext = new(account.UserId);

        return new BudgetoidDbContext(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.AppConnectionString)
                .AddInterceptors(new SessionContextInterceptor(budgetContext, userContext))
                .Options,
            budgetContext);
    }

    /// <summary>
    /// One furnished account: a user, its budget, a passkey, and one row in each of the five arms.
    /// </summary>
    /// <remarks>
    /// <b>Every row carries a narrative value, including the two optional notes.</b> A row bearing none
    /// is a row a chunk never visits, so a fixture that left a note out by accident would be arranging
    /// the presence rule's case inside every test here.
    /// </remarks>
    private static async Task<SeededAccount> SeedAccountAsync(
        RepositoryTestHost host,
        string googleSubject,
        string email)
    {
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(googleSubject, email);
        Guid credentialId = await host.SeedPasskeyAsync(owner.UserId, Guid.NewGuid().ToByteArray());

        await using BudgetoidDbContext db = AdminDb(host, owner.BudgetId);

        Account account = Account.Create(
            Guid.CreateVersion7(),
            owner.BudgetId,
            SealedNarrative.Indexed($"{googleSubject} checking"),
            AccountType.Checking,
            0m,
            "USD",
            UsdMinorUnit,
            SeedInstant);
        Payee payee = Payee.Create(
            Guid.CreateVersion7(),
            owner.BudgetId,
            SealedNarrative.Indexed($"{googleSubject} grocer"),
            SeedInstant);
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(),
            owner.BudgetId,
            SealedNarrative.Indexed($"{googleSubject} everyday"),
            SealedNarrative.Description($"{googleSubject} group note"),
            0,
            SeedInstant);
        db.Accounts.Add(account);
        db.Payees.Add(payee);
        db.CategoryGroups.Add(group);
        await db.SaveChangesAsync();

        // Second save: the category names the group by foreign key, so the group has to be on the
        // database before it can be filed under.
        Category category = Category.Create(
            Guid.CreateVersion7(),
            owner.BudgetId,
            group.Id,
            SealedNarrative.Indexed($"{googleSubject} groceries"),
            SealedNarrative.Description($"{googleSubject} category note"),
            0,
            SeedInstant);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        Transaction transaction = Transaction.Create(
            Guid.CreateVersion7(),
            owner.BudgetId,
            account.Id,
            -10m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            SealedNarrative.Description($"{googleSubject} transaction note"),
            SeedInstant);
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        return new SeededAccount(
            owner.UserId,
            owner.BudgetId,
            credentialId,
            account.Id,
            payee.Id,
            group.Id,
            category.Id,
            transaction.Id);
    }

    /// <summary>
    /// Stages one rotation for <paramref name="account" /> and returns the identifier a chunk has to
    /// quote.
    /// </summary>
    /// <remarks>
    /// <b>Through <c>KeyRotation.Begin</c> over the loaded passkey</b> rather than an <c>insert</c>, the
    /// idiom <c>KeyRotationRepositoryTests</c> keeps: the factory refuses an absent or over-wide
    /// manifest, an epoch below the floor, an empty identifier and a credential that is not a passkey,
    /// so a seeded row is one a begin could really have written. The seals a begin writes beside it are
    /// deliberately absent — nothing a chunk does reads one, and seeding them would suggest otherwise.
    /// </remarks>
    private static async Task<Guid> StageRotationAsync(RepositoryTestHost host, SeededAccount account)
    {
        await using BudgetoidDbContext db = AdminDb(host, account.BudgetId);
        Credential passkey = await db.Credentials
            .SingleAsync(credential => credential.Id == account.CredentialId);
        KeyRotation rotation = KeyRotation.Begin(
            passkey,
            Guid.CreateVersion7(),
            ManifestFixture.Mint().Manifest,
            StagedRotationEpoch,
            SeedInstant);
        db.KeyRotations.Add(rotation);
        await db.SaveChangesAsync();

        return rotation.RotationId;
    }

    /// <summary>
    /// Every column a reseal may touch, on every arm, read on the superuser connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read as columns rather than through entities.</b> The three nullable narrative columns
    /// round-trip through a value converter, and a converter that materialised an empty envelope for a
    /// <c>NULL</c> would make an entity disagree with the column a completeness gate's SQL tests —
    /// <c>RotationCompletenessTests</c> makes the same choice for the same reason. Reading raw also
    /// keeps this guard independent of the mapping the act went through.
    /// </para>
    /// <para>
    /// The absent columns are absent rather than empty: <c>accounts</c> and <c>payees</c> carry no
    /// description and <c>transactions</c> carries no name, so those members stay
    /// <see langword="null" /> and a comparison over the record compares what each row actually holds.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, RowState>> SnapshotAsync(
        NpgsqlConnection admin,
        SeededAccount account)
    {
        Dictionary<string, RowState> snapshot = [];

        foreach (string table in ResealedTables)
        {
            snapshot[table] = await RowOfAsync(admin, table, account.RowIn(table));
        }

        return snapshot;
    }

    /// <summary>
    /// The same four columns for one named row, so a case can read a sibling the account record does
    /// not carry.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="SnapshotAsync" /> rather than written twice: a second column list is a
    /// second chance to read <c>description</c> where <c>name</c> belongs, which would report an
    /// untouched row as changed and a changed one as untouched on different arms.
    /// </remarks>
    private static async Task<RowState> RowOfAsync(NpgsqlConnection admin, string table, Guid rowId)
    {
        string columns = table switch
        {
            "accounts" or "payees" => "name, name_key, null::bytea, rotation_id",
            "category_groups" or "categories" => "name, name_key, description, rotation_id",
            "transactions" => "null::bytea, null::bytea, description, rotation_id",
            _ => throw new ArgumentOutOfRangeException(
                nameof(table), table, "No arm of a chunk writes that table."),
        };

        // The identifier is interpolated because an identifier cannot be a parameter, and it is never
        // caller-supplied text: every value reaching here comes from ResealedTables or from a literal
        // in this file.
        await using NpgsqlCommand command = new(
            $"select {columns} from {table} where id = @id", admin);
        command.Parameters.AddWithValue("id", rowId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"No row of '{table}' is filed under {rowId}.");
        }

        return new RowState(
            Bytes(reader, 0),
            Bytes(reader, 1),
            Bytes(reader, 2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3));
    }

    /// <summary>
    /// Which budget one row belongs to, read on the superuser connection.
    /// </summary>
    /// <remarks>
    /// The over-reach case turns on this and on nothing else visible in its arrangement: a sibling
    /// seeded into the wrong budget would be kept out by the query filter rather than by the predicate
    /// under test, and the case would then pass while measuring the opposite thing.
    /// </remarks>
    private static async Task<Guid> BudgetOfAsync(NpgsqlConnection admin, string table, Guid rowId)
    {
        await using NpgsqlCommand command = new($"select budget_id from {table} where id = @id", admin);
        command.Parameters.AddWithValue("id", rowId);

        return await command.ExecuteScalarAsync() is Guid budgetId
            ? budgetId
            : throw new InvalidOperationException($"No row of '{table}' is filed under {rowId}.");
    }

    /// <summary>
    /// Adds one payee and one noted transaction to the account's own budget, which no chunk in this
    /// file names.
    /// </summary>
    /// <remarks>
    /// <b>Two arms rather than one, and <c>transactions</c> is the second on purpose</b> — it is the
    /// only arm where a real account holds thousands of rows, so it is where an adapter that answered
    /// the whole budget costs the most. Both rows carry a narrative value: one bearing none is a row a
    /// chunk is right never to visit, which would make "left alone" and "nothing to do" the same
    /// observation.
    /// </remarks>
    private static async Task<(Guid PayeeId, Guid TransactionId)> AddSiblingsAsync(
        RepositoryTestHost host,
        SeededAccount account)
    {
        await using BudgetoidDbContext db = AdminDb(host, account.BudgetId);

        Payee payee = Payee.Create(
            Guid.CreateVersion7(),
            account.BudgetId,
            SealedNarrative.Indexed("household baker"),
            SeedInstant);
        Transaction transaction = Transaction.Create(
            Guid.CreateVersion7(),
            account.BudgetId,
            account.AccountId,
            -47m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 14),
            SealedNarrative.Description("household hardware"),
            SeedInstant);
        db.Payees.Add(payee);
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        return (payee.Id, transaction.Id);
    }

    private static byte[]? Bytes(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (byte[])reader.GetValue(ordinal);

    /// <summary>Reads one row's stamp back, as the database holds it.</summary>
    private static async Task<Guid?> StampOfAsync(NpgsqlConnection admin, string table, Guid rowId)
    {
        await using NpgsqlCommand command = new($"select rotation_id from {table} where id = @id", admin);
        command.Parameters.AddWithValue("id", rowId);

        return await command.ExecuteScalarAsync() switch
        {
            Guid stamp => stamp,
            null or DBNull => null,
            var unexpected => throw new InvalidOperationException(
                $"'{table}'.rotation_id came back as '{unexpected}'."),
        };
    }

    private static BudgetoidDbContext AdminDb(RepositoryTestHost host, Guid budgetId) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options,
        new TestBudgetContext(budgetId));

    private static async Task<NpgsqlConnection> OpenAdminAsync(RepositoryTestHost host)
    {
        NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// Runs an action expected to throw and hands back the exception it threw.
    /// </summary>
    /// <remarks>
    /// The catch names <typeparamref name="TException" /> exactly, so a <c>42501</c> out of the save, a
    /// mis-seeded fixture or a disposed context escapes and fails the test as itself rather than
    /// passing as the refusal under test.
    /// </remarks>
    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name}, and the chunk answered instead of refusing.");
    }

    /// <summary>Runs an action and hands back whatever escaped it, or null.</summary>
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception escaped)
        {
            return escaped;
        }
    }

    /// <summary>
    /// The columns of one row a reseal may write, as the database holds them.
    /// </summary>
    /// <remarks>
    /// A <see langword="record" /> so that "nothing moved" is one comparison per arm rather than four,
    /// and the byte arrays are compared by content through <see cref="Equals(RowState)" /> below —
    /// a record's generated equality over <see cref="byte" /><c>[]</c> is reference equality, which
    /// would report two reads of an unchanged row as different and make every rollback assertion pass
    /// for the wrong reason.
    /// </remarks>
    private sealed record RowState(byte[]? Name, byte[]? NameKey, byte[]? Description, Guid? RotationId)
    {
        public bool Equals(RowState? other) =>
            other is not null
            && Same(Name, other.Name)
            && Same(NameKey, other.NameKey)
            && Same(Description, other.Description)
            && RotationId == other.RotationId;

        public override int GetHashCode() => RotationId?.GetHashCode() ?? 0;

        /// <summary>
        /// Whether every narrative column this row carries holds something other than what
        /// <paramref name="before" /> held — the half of "the stamp and the ciphertext move together"
        /// that is about the ciphertext.
        /// </summary>
        /// <remarks>
        /// <b>Columns the table does not have are skipped rather than counted as moved.</b>
        /// <c>transactions</c> carries no name and <c>accounts</c> and <c>payees</c> carry no
        /// description, so a rule demanding all three would be unsatisfiable on every arm — and one
        /// demanding "at least one moved" would pass on a payee whose blind index was left behind,
        /// which is the failure that silently duplicates counterparties.
        /// </remarks>
        public bool NarrativeMoved(RowState before) =>
            Moved(Name, before.Name)
            && Moved(NameKey, before.NameKey)
            && Moved(Description, before.Description);

        private static bool Moved(byte[]? now, byte[]? before) => before is null || !Same(now, before);

        private static bool Same(byte[]? left, byte[]? right) =>
            (left, right) switch
            {
                (null, null) => true,
                (null, _) or (_, null) => false,
                _ => left.AsSpan().SequenceEqual(right),
            };
    }

    /// <summary>
    /// One seeded account: its owner, its budget, its passkey, and the row it holds in each of the five
    /// arms.
    /// </summary>
    private readonly record struct SeededAccount(
        Guid UserId,
        Guid BudgetId,
        Guid CredentialId,
        Guid AccountId,
        Guid PayeeId,
        Guid CategoryGroupId,
        Guid CategoryId,
        Guid TransactionId)
    {
        /// <summary>The identifier of this account's row in <paramref name="table" />.</summary>
        /// <remarks>
        /// It throws on an unknown name rather than returning a default, because <see cref="Guid.Empty" />
        /// would match no row and the miss would be reported one layer away from its cause.
        /// </remarks>
        public Guid RowIn(string table) => table switch
        {
            "accounts" => AccountId,
            "payees" => PayeeId,
            "category_groups" => CategoryGroupId,
            "categories" => CategoryId,
            "transactions" => TransactionId,
            _ => throw new ArgumentOutOfRangeException(
                nameof(table), table, "No seeded row is filed under that table."),
        };
    }

    /// <summary>
    /// Wraps the real executor and throws after the unit of work has returned, so the inner executor's
    /// commit never runs and its disposal rolls the transaction back.
    /// </summary>
    /// <remarks>
    /// <b>It models a failure on a later statement and not a failure of the save.</b> The operation is
    /// allowed to complete, so every statement the chunk issued really reached PostgreSQL inside the
    /// transaction — which is the only arrangement under which "was it rolled back?" is a question with
    /// a meaningful answer.
    /// </remarks>
    private sealed class AbandonedUnitOfWork(ITransactionalExecutor inner) : ITransactionalExecutor
    {
        public Task<TResult> ExecuteAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);

            return inner.ExecuteAsync<TResult>(
                async token =>
                {
                    await operation(token);
                    throw new AbandonedUnitOfWorkException();
                },
                cancellationToken);
        }
    }

    /// <summary>
    /// The failure <see cref="AbandonedUnitOfWork" /> injects, named so that a rollback case cannot pass
    /// on any other exception.
    /// </summary>
    private sealed class AbandonedUnitOfWorkException()
        : Exception("The unit of work was abandoned after the chunk saved.");

    /// <summary>
    /// The generation the seeded staged manifest is filed at. Above the floor, so the row is one a
    /// begin could really have written.
    /// </summary>
    private const int StagedRotationEpoch = 4;

    /// <summary>Minor unit of the USD rows seeded here; precision is what no case is about.</summary>
    private const int UsdMinorUnit = 2;

    /// <summary>
    /// Fixed UTC instant for every seeded row, matching <c>RepositoryTestHost</c>'s own. PostgreSQL
    /// <c>timestamptz</c> rejects a non-UTC <see cref="DateTime" />, so the kind is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);
}
