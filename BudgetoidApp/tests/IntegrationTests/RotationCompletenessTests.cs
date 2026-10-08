using Application.KeyRotations;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Transactions;
using Infrastructure.Persistence;
using Infrastructure.ReadServices;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The completeness gate: the read a content-key rotation's final step consults before it overwrites
/// the live wrapped keys in place and destroys the only copies of the generation still in force.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is being measured is a refusal, and the cost of getting it wrong is total.</b> Promotion is
/// destructive by construction — <c>docs/business-logic/key-rotation.md</c> argues under "Why staging"
/// that there is no ordering in which the old keys survive it — so a gate that answers "complete" one
/// row early leaves that row sealed under a key no wrapped copy exists for anywhere on earth. There is
/// no repair, no migration and no support path. Most of this file is about the gate saying <b>no</b>;
/// the cases that reach <see langword="true" /> are here so that a gate hard-wired to refuse could not
/// satisfy the rest, and they are the reason the mirror failure — a rotation that can never finish — is
/// also covered.
/// </para>
/// <para>
/// <b>The gate has a third answer, and two cases are about it.</b> Where the question is one the read
/// cannot see the whole of — an account owning a budget this request is not inside, or a request inside
/// a budget this account does not own — it throws <see cref="RotationScopeException" /> rather than
/// answering either way. <c>docs/business-logic/export.md</c> is the authority for that rule and
/// <c>ExportDataHandler</c> makes the same refusal in the same spelling: set equality between the owned
/// budgets and the ambient one, in both directions, deliberately not a count.
/// </para>
/// <para>
/// <b>The server cannot tell rotated ciphertext from un-rotated by looking</b>, which is the whole
/// reason a stamp exists. Every seal draws a fresh nonce so the bytes differ either way, and the
/// associated data binding an envelope to its row is rebuilt from context rather than carried inside
/// it. So a chunk <em>tells</em> the server, by writing the in-flight rotation's identifier into the
/// row's <c>rotation_id</c> in the same transaction as the ciphertext, and this read asks whether any
/// row is missing that value.
/// </para>
/// <para>
/// <b>Six tables carry the stamp and all six are probed one at a time</b>, because a gate that reaches
/// five of them is green on every test that seeds the sixth alongside the others. The parameterized
/// case below is therefore six cases, and its arrangement assertions say which table was disturbed
/// rather than merely that something was — a clear that landed on the wrong table would otherwise read
/// as the one under test.
/// </para>
/// <para>
/// <b>Every other rule here is written six times over, and none of them can be censused.</b> The
/// unstamped rule has the parameterized case; the presence rule and the staleness rule do not, and
/// cannot in the way this repository usually closes a per-table gap. <c>NarrativeResealSurfaceTests</c>
/// can read the compiled body of each reseal member and count calls to <c>NarrativeReseal.Resealed</c>
/// because the rule it holds is <em>"call this member"</em> — a fact about a call site, which IL makes
/// enumerable. There is no equivalent handle on <em>"this <c>Where</c> clause has these three
/// conjuncts"</em>: the six arms are one compiled expression tree inside one method body, not six
/// members and not data, so a census would have to recognise a predicate's shape rather than count a
/// symbol. <b>Which rules are census-able and which are not is the useful distinction</b>, and it is
/// what decides that the per-table coverage below is a set of chosen samples rather than an
/// enumeration. Each of those cases says in its own remark which tables it leaves out and on what
/// argument — and every one of those arguments is about how the arms are spelled today, so each stops
/// being true the day an arm is edited on its own.
/// </para>
/// <para>
/// <b>These tests run against a real PostgreSQL and not a double, and the reason is the predicate.</b>
/// The mistake this file exists to catch — <c>rotation_id &lt;&gt; @current</c> over a nullable column —
/// is a three-valued-logic mistake that only SQL makes. Written in LINQ-to-objects against an in-memory
/// list, <c>row.RotationId != current</c> is <see langword="true" /> for a <see langword="null" /> and
/// the test passes; translated to SQL by the same expression tree, <c>NULL &lt;&gt; anything</c> is
/// <c>NULL</c> and the row silently drops out. A unit test of this gate would assert the opposite of
/// what ships.
/// </para>
/// <para>
/// <b>Every arrangement is made on the container superuser connection, and the measurement is made
/// through <see cref="BudgetoidDbContext" /> on the same connection with an ambient budget.</b> That is
/// the idiom <c>BudgetIsolationTests</c> and <c>AppRoleGrantsTests</c> both use, and here it is load
/// bearing rather than convenient: on a policed application connection the two isolation policies would
/// scope every statement underneath whatever the gate asked for, so a gate that scoped <em>nothing</em>
/// would answer correctly and the tenancy case at the bottom of this file would pass over it. Read
/// through the superuser, the only scoping in play is the one the implementation writes — the
/// <c>BudgetIsolation</c> query filter on the five budget-owned sets, and an explicit owner predicate on
/// <c>budgets</c>, which carries no filter at all. In production both policies sit under that and
/// neither replaces it, for the reason <c>ExportReadService</c> records where it makes the same split.
/// </para>
/// <para>
/// <b>The stamps are written with SQL rather than through the six reseal members</b>, and that is a
/// decision about what is under test rather than a shortcut. <c>Budget.ResealName</c> is
/// <see langword="internal" /> and <c>Domain.csproj</c> grants its internals to <c>Infrastructure</c>
/// alone, so this project cannot call it at all and a budget could not be stamped through the domain
/// here whatever the other five did. Splitting the arrangement — five through entities, one through
/// SQL — would put the table most likely to be dropped from the gate on the least uniform seeding path.
/// What is under test is the read, and the read cannot see how a value arrived in the column.
/// </para>
/// </remarks>
public sealed class RotationCompletenessTests
{
    /// <summary>
    /// The six tables carrying a <c>rotation_id</c>, in the order
    /// <c>docs/business-logic/key-rotation.md</c> lists them.
    /// </summary>
    /// <remarks>
    /// Written out rather than derived from the model, following <c>KeyRotationSchemaTests</c>: a list
    /// built from the mapping agrees with whatever the mapping later decides, and the point of this one
    /// is to be the independent statement of how many tables there are.
    /// </remarks>
    private static readonly string[] StampedTables =
        ["budgets", "accounts", "payees", "category_groups", "categories", "transactions"];

    /// <summary>
    /// A fully furnished, fully rewritten account. The gate's only "yes".
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is here for the reason <c>ErasureAtomicityTests</c> keeps its counterweight: without a case
    /// that reaches <see langword="true" />, a gate hard-wired to <see langword="false" /> would satisfy
    /// every other case in this file, and a rotation that can never be completed is a feature that
    /// silently does not work rather than a crash anybody reports.
    /// </para>
    /// <para>
    /// <b>The budget is seeded with a name, which no production budget has.</b> Registration writes the
    /// nameless budget and naming is unbuilt, so <c>budgets.name</c> is <c>NULL</c> on every row in
    /// every database today — which makes "a budget is a narrative-bearing row" read as vacuous exactly
    /// where it is most expensive to be wrong. Seeding a named budget is what the doc anticipates when
    /// it says <c>Budget.ResealName</c> is exercised only by tests that seed one.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Completeness_WhenEveryNarrativeRowCarriesTheRotation_IsTrue()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        SeededAccount account = await SeedAccountAsync(host, "google-owner", "owner@example.com", "Household");
        Guid rotationId = Guid.CreateVersion7();
        await StampEveryRowAsync(admin, account, rotationId);

        // The arrangement, asserted before the subject is touched. Both halves are needed and they fail
        // for different reasons: the first says the seeding put rows in all six tables — an empty table
        // has no unstamped row and would make this case pass against a gate that never reads it — and
        // the second says every one of those rows really carries this rotation's identifier.
        await Assert.That(await TablesWithNoRowsAsync(admin, account)).IsEmpty();
        await Assert.That(await TablesHoldingAnUnstampedRowAsync(admin, account, rotationId)).IsEmpty();

        // Act
        bool complete = await AskAsync(host, account, rotationId);

        // Assert
        await Assert.That(complete).IsTrue();
    }

    /// <summary>
    /// One row in one table left without the stamp, six times over — once per table that carries one.
    /// </summary>
    /// <param name="table">The table whose row is un-stamped, and the only thing that varies.</param>
    /// <remarks>
    /// <para>
    /// <b>Six cases rather than one, and the sixth is the reason.</b> A gate that probes five tables
    /// answers every question the other five cases ask; only the case naming the missing table catches
    /// it. <c>budgets</c> is the one to expect to be dropped — it is the only table of the six that is
    /// not budget-owned, so it needs its own scoping rather than riding the query filter, and its
    /// narrative column is <c>NULL</c> on every production row, which makes the table look like it has
    /// nothing to contribute.
    /// </para>
    /// <para>
    /// <b>The stamp is written and then cleared, rather than never written</b>, which is what a second
    /// browser tab holding the previous content key does to a row this rotation already rewrote: it
    /// writes old-key ciphertext through an ordinary mutator, and every ordinary narrative write sets
    /// <c>rotation_id</c> back to <c>NULL</c>. That is the hazard the stamp exists for, so it is the
    /// shape the arrangement takes.
    /// </para>
    /// <para>
    /// <b>The arrangement assertion names the table.</b> "Some table holds an unstamped row" is
    /// satisfied by a clear that landed on the wrong one, and the answer would then be right for a
    /// reason this case is not about — which is how a parameterized case silently degenerates into one
    /// case repeated six times.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("budgets")]
    [Arguments("accounts")]
    [Arguments("payees")]
    [Arguments("category_groups")]
    [Arguments("categories")]
    [Arguments("transactions")]
    public async Task Completeness_WhenOneRowInOneTableIsUnstamped_IsFalse(string table)
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        SeededAccount account = await SeedAccountAsync(host, "google-owner", "owner@example.com", "Household");
        Guid rotationId = Guid.CreateVersion7();
        await StampEveryRowAsync(admin, account, rotationId);
        await SetStampAsync(admin, table, account.RowIn(table), stamp: null);

        // The arrangement — exactly this table holds an unstamped row, and the other five do not.
        await Assert.That(await TablesWithNoRowsAsync(admin, account)).IsEmpty();
        await Assert.That(await TablesHoldingAnUnstampedRowAsync(admin, account, rotationId))
            .IsEquivalentTo(new[] { table });

        // Act
        bool complete = await AskAsync(host, account, rotationId);

        // Assert
        await Assert.That(complete).IsFalse();
    }

    /// <summary>
    /// An account rotating for the <b>first</b> time, where every <c>rotation_id</c> in it is
    /// <c>NULL</c>. <b>The most important case in this file.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A green here is not "wrong", it is the loss of the account.</b> The predicate everybody writes
    /// first is <c>rotation_id &lt;&gt; @current</c>, and over a nullable column it is silently wrong:
    /// <c>NULL &lt;&gt; anything</c> is <c>NULL</c>, never <c>true</c>, so every row no rotation has
    /// ever touched drops out of "rows still to do". On a first rotation that is <em>every row in the
    /// account</em> — so the set of rows still to do is empty, the gate answers "complete" before a
    /// single chunk has run, the promotion overwrites the live wrapped keys with the staged pair, and
    /// the whole budget is left sealed under a content key that now exists in no wrapped copy anywhere.
    /// Nothing throws, nothing is logged, and the discovery is a person who reloads the page and finds
    /// every name and note in their budget permanently unreadable.
    /// </para>
    /// <para>
    /// The correct predicate is <c>rotation_id IS DISTINCT FROM @current</c>, or an explicit
    /// <c>IS NULL OR &lt;&gt;</c>. <c>BudgetConfiguration</c> records the same trade where the column is
    /// declared, and records why the alternative — a <c>NOT NULL</c> column with a sentinel default,
    /// which would have made the naive predicate correct — was refused.
    /// </para>
    /// <para>
    /// <b>It is arranged as a first rotation explicitly rather than by leaving out a step.</b> Nothing
    /// is stamped and nothing is un-stamped: the rows are exactly as the account has held them since
    /// registration, which is the state every account in the product is in today, because no route can
    /// reach the staged state at all.
    /// </para>
    /// <para>
    /// <b>It does not overlap the six cases above.</b> Each of those leaves five tables fully stamped,
    /// so a gate with the naive predicate still finds the one disturbed row through one of the other
    /// five reads and answers <see langword="false" /> — correctly, by luck. Only when <em>every</em>
    /// row is <c>NULL</c> does the whole predicate evaporate, and only this case arranges that.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Completeness_ForAnAccountThatHasNeverBeenRotated_IsFalse()
    {
        // Arrange — a furnished account and a rotation identifier that has never been written anywhere.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        SeededAccount account = await SeedAccountAsync(host, "google-owner", "owner@example.com", "Household");
        Guid firstEverRotationId = Guid.CreateVersion7();

        // The arrangement, and here it is the test's whole premise rather than a guard beside it: every
        // one of the six tables holds rows, and not one row in any of them carries a stamp of any kind.
        await Assert.That(await TablesWithNoRowsAsync(admin, account)).IsEmpty();
        await Assert.That(await TablesHoldingAStampedRowAsync(admin, account)).IsEmpty();

        // Act
        bool complete = await AskAsync(host, account, firstEverRotationId);

        // Assert — false, because nothing has been rewritten yet. A true here would mean the promotion
        // runs on an untouched account.
        await Assert.That(complete).IsFalse();
    }

    /// <summary>
    /// A row carrying no narrative value at all has nothing to re-seal, so it does not have to be
    /// stamped for the rotation to finish.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A transaction with no note is the honest example, and very nearly the only one.</b> Four of
    /// the eight narrative columns in the schema are nullable —<c>budgets.name</c>,
    /// <c>category_groups.description</c>, <c>categories.description</c> and
    /// <c>transactions.description</c> — but on <c>category_groups</c> and <c>categories</c> the
    /// description sits beside a <b>required</b> <c>IndexedName</c>, so those rows always carry a
    /// narrative value whatever the note does. <c>accounts</c> and <c>payees</c> carry a required name
    /// and nothing else. <c>transactions.description</c> is the only nullable narrative column that is
    /// the <em>whole</em> of its row's narrative, which makes a note-less transaction the only row in
    /// five of the six tables that can be arranged to bear nothing. <c>budgets</c> is the sixth and is
    /// deliberately not asserted here; see the remark on <see cref="StampedTables" />' sibling case
    /// above and the note in this file's summary.
    /// </para>
    /// <para>
    /// <b>Without this, the gate blocks a rotation that genuinely finished</b>, and it blocks it in the
    /// way that is hardest to diagnose: the client re-seals everything it can see, the gate goes on
    /// refusing, and the rotation never converges. That is the mirror of the catastrophe above — not
    /// data loss, a feature that silently cannot finish.
    /// </para>
    /// <para>
    /// The note-less transaction is added <em>after</em> the stamping so that it is unstamped by
    /// construction rather than by a second statement, which is also what the real path produces: a
    /// chunk that has nothing to rewrite on a row does not visit it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Completeness_WhenATransactionCarriesNoNote_IgnoresThatRow()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        SeededAccount account = await SeedAccountAsync(host, "google-owner", "owner@example.com", "Household");
        Guid rotationId = Guid.CreateVersion7();
        await StampEveryRowAsync(admin, account, rotationId);
        Guid silentTransactionId = await AddNoteLessTransactionAsync(host, account);

        // The arrangement. All three halves matter: the row exists, it really holds no note, and it
        // really carries no stamp — and the transactions table still holds the stamped row seeded with
        // it, so this case cannot be satisfied by a gate that skips the table outright.
        await Assert.That(await ColumnIsNullAsync(admin, "transactions", "description", silentTransactionId))
            .IsTrue();
        await Assert.That(await StampOfAsync(admin, "transactions", silentTransactionId)).IsNull();
        await Assert.That(await StampOfAsync(admin, "transactions", account.TransactionId))
            .IsEqualTo(rotationId);

        // Act
        bool complete = await AskAsync(host, account, rotationId);

        // Assert — the unstamped row does not block, because it has nothing to re-encrypt.
        await Assert.That(complete).IsTrue();
    }

    /// <summary>
    /// A budget with no name has nothing to re-seal either, and <b>every budget in the product is one</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This file's fixture is the inverse of production, and that is what makes this case necessary
    /// rather than thorough.</b> Registration writes the nameless budget and naming is unbuilt, so
    /// <c>budgets.name</c> is <c>NULL</c> on every row in every database today. Every other case here
    /// seeds a <em>named</em> budget on purpose, because the claims they make about the <c>budgets</c>
    /// arm would otherwise be vacuous — and the price of that choice is that the state 100% of real
    /// accounts are in is the one state none of them arranges.
    /// </para>
    /// <para>
    /// <b>What it costs to lose is every rotation in the product, permanently.</b> An implementation
    /// that dropped the <c>name is not null</c> arm from the <c>budgets</c> predicate would report one
    /// outstanding row on every account there is. The client cannot clear it: the presence rule refuses
    /// to create a narrative value where the column held none, so <c>Budget.ResealName</c> cannot put a
    /// name there, and a row that cannot be re-sealed cannot be stamped by re-sealing it. The gate would
    /// refuse for ever, the client would re-seal everything it can see for ever, and the feature would
    /// be one that silently does not work — with all eleven of the cases beside this one green.
    /// </para>
    /// <para>
    /// <b>The budget is deliberately left unstamped rather than stamped and skipped.</b> Stamping it
    /// would make this case pass against a gate with no presence test at all, which is the whole of what
    /// it is here to catch. Leaving it out models the real path too: a chunk with nothing to rewrite on
    /// a row does not visit it — though the doc records that the client stamps it anyway for uniformity,
    /// which is why the stamp on a nameless budget is one the gate must not <em>need</em>.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Completeness_WhenTheBudgetCarriesNoName_IgnoresThatRow()
    {
        // Arrange — the budget registration itself writes, through the seeder registration itself uses.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync("google-owner", "owner@example.com");
        SeededAccount account = await FurnishBudgetAsync(host, owner.UserId, owner.BudgetId, "Household");
        Guid rotationId = Guid.CreateVersion7();
        await StampEveryRowAsync(admin, account, rotationId);
        await SetStampAsync(admin, "budgets", account.BudgetId, stamp: null);

        // The arrangement. The budget really holds no name and really carries no stamp, the five
        // budget-owned tables are furnished and fully stamped, and the one table the database reckons
        // outstanding is exactly the one this case says must not count.
        await Assert.That(await TablesWithNoRowsAsync(admin, account)).IsEmpty();
        await Assert.That(await ColumnIsNullAsync(admin, "budgets", "name", account.BudgetId)).IsTrue();
        await Assert.That(await StampOfAsync(admin, "budgets", account.BudgetId)).IsNull();
        await Assert.That(await TablesHoldingAnUnstampedRowAsync(admin, account, rotationId))
            .IsEquivalentTo(new[] { "budgets" });

        // Act
        bool complete = await AskAsync(host, account, rotationId);

        // Assert — complete, because the one row still bare of a stamp has no narrative to re-seal.
        await Assert.That(complete).IsTrue();
    }

    /// <summary>
    /// A category group carrying a name and no note is still a narrative row, and still owes a stamp.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The presence rule for <c>category_groups</c> and <c>categories</c> keys on the required name,
    /// and those two arms therefore carry no presence test at all.</b> A nullable description sits
    /// beside a non-nullable <c>IndexedName</c> on both, so the row bears narrative whatever the note
    /// does. The mistake this case exists for is the one that looks most like care: seeing four nullable
    /// narrative columns in the schema and writing <c>description is not null</c> into all four arms,
    /// by symmetry with <c>transactions</c> where it is right.
    /// </para>
    /// <para>
    /// <b>Nothing else in this file arranges the row that would expose it.</b> Both groups and both
    /// categories the fixture seeds carry a description, so a gate skipping every note-less group agrees
    /// with all eleven cases beside this one — while the skipped row's <em>name</em> still holds
    /// ciphertext under the key the promotion is about to destroy.
    /// </para>
    /// <para>
    /// <c>category_groups</c> rather than <c>categories</c> for one reason only: the two arms are
    /// written identically and a group needs no parent row, so the case that catches the mistake on one
    /// is the case that would catch it on the other, arranged in half the statements. If the two arms
    /// ever stop being identical, this remark is the thing that stopped being true.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Completeness_WhenACategoryGroupCarriesNoDescription_IsFalse()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        SeededAccount account = await SeedAccountAsync(host, "google-owner", "owner@example.com", "Household");
        Guid rotationId = Guid.CreateVersion7();
        await StampEveryRowAsync(admin, account, rotationId);
        Guid unnotedGroupId = await AddDescriptionLessCategoryGroupAsync(host, account);

        // The arrangement, and the second line is the half that is easy to leave out: a group with no
        // description proves nothing unless its name is present, because a row bearing no narrative at
        // all is one the gate is right to skip. The fourth line keeps the seeded, fully stamped group
        // beside it, so the answer cannot come from a gate that skips the table outright.
        await Assert.That(await ColumnIsNullAsync(admin, "category_groups", "description", unnotedGroupId))
            .IsTrue();
        await Assert.That(await ColumnIsNullAsync(admin, "category_groups", "name", unnotedGroupId))
            .IsFalse();
        await Assert.That(await StampOfAsync(admin, "category_groups", unnotedGroupId)).IsNull();
        await Assert.That(await StampOfAsync(admin, "category_groups", account.CategoryGroupId))
            .IsEqualTo(rotationId);

        // Act
        bool complete = await AskAsync(host, account, rotationId);

        // Assert — outstanding, because the name is narrative and this rotation never rewrote it.
        await Assert.That(complete).IsFalse();
    }

    /// <summary>
    /// A stamp left behind by an earlier, abandoned run does not count as this run's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is a different failure from an unstamped row and a gate can pass one while failing the
    /// other.</b> A predicate written as <c>rotation_id IS NULL</c> — "have all the rows been touched
    /// yet?" — answers every one of the six cases above correctly and answers this one wrong, because
    /// the row has been touched, just not by the run about to promote. A rotation begins by minting a
    /// new identifier and an abandoned run's rows keep the old one, so on any account where somebody
    /// started a rotation, closed the tab, and started another, this is not a corner case.
    /// </para>
    /// <para>
    /// <c>payees</c> carries the stale stamp because its arm is the plainest of the six — a bare
    /// <c>rotation_id != @current</c> with nothing else in the predicate — so this is the stale case in
    /// its simplest possible surroundings. It is not the only one: the two siblings below arrange the
    /// same staleness on <c>budgets</c> and on <c>transactions</c>, the only two arms that are not
    /// copies of this one, and the first of those remarks says which tables the three still leave
    /// unexercised and why.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Completeness_WhenARowCarriesAnEarlierRotationsStamp_IsFalse()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        SeededAccount account = await SeedAccountAsync(host, "google-owner", "owner@example.com", "Household");
        Guid abandonedRotationId = Guid.CreateVersion7();
        Guid rotationId = Guid.CreateVersion7();
        await StampEveryRowAsync(admin, account, rotationId);
        await SetStampAsync(admin, "payees", account.PayeeId, abandonedRotationId);

        // The arrangement. The payee is the only row in the account not carrying this run's stamp —
        // and it is not merely un-stamped: it holds the abandoned run's identifier, which is the
        // difference between this case and its sibling above. Asserting "no table holds an unstamped
        // row" here would be wrong rather than weak: a stale stamp IS distinct from the current one, so
        // payees belongs on that list and its presence is the arrangement working.
        await Assert.That(abandonedRotationId).IsNotEqualTo(rotationId);
        await Assert.That(await StampOfAsync(admin, "payees", account.PayeeId))
            .IsEqualTo(abandonedRotationId);
        await Assert.That(await TablesHoldingAnUnstampedRowAsync(admin, account, rotationId))
            .IsEquivalentTo(new[] { "payees" });

        // Act
        bool complete = await AskAsync(host, account, rotationId);

        // Assert
        await Assert.That(complete).IsFalse();
    }

    /// <summary>
    /// The same abandoned run's stamp, on the one arm that is not a bare comparison.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A per-table rule exercised on one table is a rule untested on five.</b> The staleness rule —
    /// that <c>rotation_id</c> must be compared with <c>IS DISTINCT FROM</c> semantics rather than asked
    /// <c>IS NULL</c> — is written six times over, once per arm, and its sibling above arranges it only
    /// on <c>payees</c>. An arm spelled <c>RotationId == null</c> anywhere else answers every case in
    /// this file correctly, reports an abandoned run's rows as this run's progress, and the promotion
    /// destroys the keys the ciphertext under those stale stamps is sealed under.
    /// </para>
    /// <para>
    /// <b><c>budgets</c> is the second table, chosen because its arm is the least like the one already
    /// covered.</b> It is the only one of the six that is not budget-owned, so it carries a hand-written
    /// owner predicate where the other five ride the query filter; and it is one of only two carrying a
    /// presence test. The stamp comparison there sits between two other conditions, which is where a
    /// re-spelling is most likely to be made and least likely to look wrong — and its narrative column
    /// is <c>NULL</c> on every production row, which makes the whole arm read as contributing nothing.
    /// </para>
    /// <para>
    /// <b>What the three cases still do not exercise: <c>accounts</c>, <c>category_groups</c> and
    /// <c>categories</c>.</b> All three are spelled identically to <c>payees</c> — one bare comparison,
    /// no presence test, no owner predicate — so a case on any of them would arrange what the sibling
    /// above already arranges. <b>That is a judgement about the present spelling, not a claim of
    /// coverage</b>, and the day those three arms stop being copies of one another it is the wrong
    /// judgement: an arm edited on its own reddens nothing here. The two arms that are <em>not</em>
    /// copies of anything — <c>budgets</c> here and <c>transactions</c> below — are covered for exactly
    /// that reason.
    /// </para>
    /// <para>
    /// <b>The budget is named, and the arrangement says so.</b> A nameless budget is skipped on presence
    /// before its stamp is ever compared, so on the fixture the sibling case beside this one seeds, this
    /// case would pass against a gate with no staleness rule at all.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Completeness_WhenTheBudgetRowCarriesAnEarlierRotationsStamp_IsFalse()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        SeededAccount account = await SeedAccountAsync(host, "google-owner", "owner@example.com", "Household");
        Guid abandonedRotationId = Guid.CreateVersion7();
        Guid rotationId = Guid.CreateVersion7();
        await StampEveryRowAsync(admin, account, rotationId);
        await SetStampAsync(admin, "budgets", account.BudgetId, abandonedRotationId);

        // The arrangement. The budget bears a name — without which it is skipped on presence and this
        // case measures nothing — it holds the abandoned run's identifier rather than no identifier, and
        // it is the only row in the account the database reckons outstanding.
        await Assert.That(abandonedRotationId).IsNotEqualTo(rotationId);
        await Assert.That(await ColumnIsNullAsync(admin, "budgets", "name", account.BudgetId)).IsFalse();
        await Assert.That(await StampOfAsync(admin, "budgets", account.BudgetId))
            .IsEqualTo(abandonedRotationId);
        await Assert.That(await TablesHoldingAnUnstampedRowAsync(admin, account, rotationId))
            .IsEquivalentTo(new[] { "budgets" });

        // Act
        bool complete = await AskAsync(host, account, rotationId);

        // Assert
        await Assert.That(complete).IsFalse();
    }

    /// <summary>
    /// The abandoned run's stamp on the other presence-bearing arm, and on the table that holds the most
    /// rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The third and last arm that is not a copy of another.</b> <c>transactions</c> is the only set
    /// besides <c>budgets</c> whose predicate carries a presence test, so its stamp comparison also sits
    /// beside another condition rather than alone — and the two are not interchangeable: the presence
    /// test there is <c>description is not null</c> on a budget-owned table riding the query filter,
    /// where the one on <c>budgets</c> is <c>name is not null</c> beside a hand-written owner predicate.
    /// An arm re-spelled <c>Description != null &amp;&amp; RotationId == null</c> reads as the careful
    /// version of the right predicate and is the silent wrong one.
    /// </para>
    /// <para>
    /// <b>It is the table the mistake costs the most on.</b> Every other stamped table holds one row per
    /// thing a person names; this one holds a row per movement of money, so it is the largest table in
    /// the product by a wide margin on any account that has been used. A staleness rule lost here reads
    /// an abandoned run's stamps as this run's progress across more rows than the other five put
    /// together, and the promotion then destroys the keys those rows' notes are sealed under.
    /// </para>
    /// <para>
    /// <b>The transaction carries a note, and the arrangement says so.</b> The fixture's does, but the
    /// file also seeds a note-less one elsewhere, and a transaction bearing no narrative is skipped on
    /// presence before its stamp is ever compared — on such a row this case would pass against a gate
    /// with no staleness rule at all, exactly as it would on a nameless budget.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Completeness_WhenATransactionCarriesAnEarlierRotationsStamp_IsFalse()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        SeededAccount account = await SeedAccountAsync(host, "google-owner", "owner@example.com", "Household");
        Guid abandonedRotationId = Guid.CreateVersion7();
        Guid rotationId = Guid.CreateVersion7();
        await StampEveryRowAsync(admin, account, rotationId);
        await SetStampAsync(admin, "transactions", account.TransactionId, abandonedRotationId);

        // The arrangement. The transaction bears a note — without which it is skipped on presence and
        // this case measures nothing — it holds the abandoned run's identifier rather than no
        // identifier, and it is the only row in the account the database reckons outstanding.
        await Assert.That(abandonedRotationId).IsNotEqualTo(rotationId);
        await Assert.That(await ColumnIsNullAsync(admin, "transactions", "description", account.TransactionId))
            .IsFalse();
        await Assert.That(await StampOfAsync(admin, "transactions", account.TransactionId))
            .IsEqualTo(abandonedRotationId);
        await Assert.That(await TablesHoldingAnUnstampedRowAsync(admin, account, rotationId))
            .IsEquivalentTo(new[] { "transactions" });

        // Act
        bool complete = await AskAsync(host, account, rotationId);

        // Assert
        await Assert.That(complete).IsFalse();
    }

    /// <summary>
    /// Another account's rows are not this rotation's business, however unfinished they look.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read on the superuser connection, so nothing but the implementation's own scoping answers
    /// this.</b> Under the two isolation policies a gate that scoped nothing at all would still return
    /// only the ambient account's rows, so this case would pass over a read with no owner predicate
    /// anywhere — a policy makes a wrong query answer empty rather than correct, which is the argument
    /// <c>ExportReadService</c> makes about its own <c>budgets</c> predicate.
    /// </para>
    /// <para>
    /// <b>What it can catch is exactly one thing, and that is the thing worth catching.</b> Five of the
    /// six sets carry the <c>BudgetIsolation</c> query filter, which the banned-symbols list makes
    /// impossible to switch off, so they are scoped whether or not the implementation thinks about it.
    /// <c>budgets</c> carries no filter — it is what registration writes and what session
    /// authentication reads before any budget is ambient — so the owner predicate on that one set is
    /// written by hand or not at all, and this case is what says it was written. A gate missing it
    /// reports the other account's untouched budget as a row still to do and refuses a rotation that
    /// finished.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Completeness_WithASecondAccountLeftEntirelyUnstamped_IsUnaffected()
    {
        // Arrange — two furnished accounts on one database. The first is fully rewritten; the second
        // has never been rotated at all and is a stranger to this run.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        SeededAccount mine = await SeedAccountAsync(host, "google-owner", "owner@example.com", "Household");
        SeededAccount theirs = await SeedAccountAsync(host, "google-other", "other@example.com", "Theirs");
        Guid rotationId = Guid.CreateVersion7();
        await StampEveryRowAsync(admin, mine, rotationId);

        // The arrangement — mine is complete on the database's own reading, and theirs is untouched.
        // Without the second line this case is a copy of the first test in the file.
        await Assert.That(await TablesHoldingAnUnstampedRowAsync(admin, mine, rotationId)).IsEmpty();
        await Assert.That(await TablesWithNoRowsAsync(admin, theirs)).IsEmpty();
        await Assert.That(await TablesHoldingAStampedRowAsync(admin, theirs)).IsEmpty();

        // Act
        bool complete = await AskAsync(host, mine, rotationId);

        // Assert
        await Assert.That(complete).IsTrue();
    }

    /// <summary>
    /// An account owning a second budget gets a refusal, because five of the gate's six arms cannot see
    /// inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gate's question is about an account and five of its six reads can only see one budget.</b>
    /// The <c>budgets</c> arm is scoped by owner and sees every budget the account holds; the other five
    /// ride the <c>BudgetIsolation</c> query filter, which scopes to the <em>ambient</em> budget, takes
    /// no argument and cannot be re-pointed part-way through a request. So on two budgets the gate would
    /// compare both budgets' name stamps against one budget's contents — and answer <b>complete</b> with
    /// an entire second budget unrotated, after which the promotion overwrites the only wrapped copies
    /// of the key that budget is sealed under. There is no repair.
    /// </para>
    /// <para>
    /// <b>The arrangement is the realistic failure shape, and it is narrower than "a second budget".</b>
    /// A second budget that is named and unstamped is caught by the <c>budgets</c> arm without any of
    /// this, because that arm sees every owned budget. The hole opens only when the second budget's own
    /// row is stamped while its <em>contents</em> are not — which is exactly what a real rotation
    /// produces, because the statement stamping budgets is scoped on <c>user_id</c> and gets both rows
    /// for free while the five that follow are scoped on one <c>budget_id</c>. So the second budget is
    /// given one narrative row of its own, and the ordinary stamping is then run unchanged.
    /// </para>
    /// <para>
    /// <b>The first arrangement assertion is the crux of the case.</b> "No table holds an unstamped row"
    /// by the guard's own budget-scoped reckoning is the statement that the gate <em>would have answered
    /// <see langword="true" /></em> — that this is not merely an account the gate happens to refuse, but
    /// the precise state in which refusing and lying are the only two available answers.
    /// </para>
    /// <para>
    /// <b>It refuses rather than answering <see langword="false" />.</b> False is indistinguishable from
    /// "rows still to do", so the client would re-seal everything it can see and the gate would go on
    /// refusing — the rotation that never converges. A throw says the server cannot answer, which is
    /// what is true. <c>docs/business-logic/export.md</c> is the authority for the rule and
    /// <c>ExportDataHandler</c> makes the same refusal in the same spelling.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Completeness_WhenTheAccountOwnsASecondBudget_Refuses()
    {
        // Arrange — one account, two budgets, and one payee inside the budget the request is not in.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        SeededAccount account = await SeedAccountAsync(host, "google-owner", "owner@example.com", "Household");
        Guid secondBudgetId = await host.SeedAdditionalBudgetAsync(account.UserId, "Cabin");
        Guid unreachablePayeeId = await AddPayeeAsync(host, secondBudgetId, "Cabin");
        Guid rotationId = Guid.CreateVersion7();
        await StampEveryRowAsync(admin, account, rotationId);

        // The arrangement, and the first line is the whole reason the case is worth writing: scoped the
        // way the gate is scoped, this account looks finished. The second line says the row that makes
        // that a lie exists and is bare of any stamp, and the third says the account really does hold
        // two budgets rather than one the seeder quietly failed to add to.
        await Assert.That(await TablesHoldingAnUnstampedRowAsync(admin, account, rotationId)).IsEmpty();
        await Assert.That(await StampOfAsync(admin, "payees", unreachablePayeeId)).IsNull();
        await Assert.That(await OwnedBudgetCountAsync(admin, account.UserId)).IsEqualTo(2);

        // Act
        RotationScopeException refusal = await ThrowsAsync<RotationScopeException>(
            () => AskAsync(host, account, rotationId));

        // Assert — that it refused at all is the claim. The message is deliberately not pinned: it names
        // counts and never budget ids, because the Development branch of GlobalExceptionHandler echoes
        // it into the response body, and asserting its wording here would make that constraint read as
        // a phrasing test. ExportDataHandlerTests says the same beside its own refusal.
        await Assert.That(refusal).IsNotNull();
    }

    /// <summary>
    /// The other direction: the account owns exactly one budget, and it is not the one the request is
    /// inside. <b>The direction a <c>Count &gt; 1</c> guard passes cleanly.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Set equality in both directions is what separates this from a count, and the two directions
    /// fail differently.</b> Owning a budget the request is not inside means rows are invisible to the
    /// gate — the case above. Being inside a budget the account does not own means the gate reads
    /// <em>another account's</em> rows and reports them as this account's progress: the five filtered
    /// arms answer for the ambient budget whoever owns it, so an account that has rotated nothing can be
    /// reported complete on the strength of somebody else's finished work. A count refuses only the
    /// first direction, and this account's count is one.
    /// </para>
    /// <para>
    /// <b>It is arranged with the first account fully stamped on purpose.</b> The gate has to refuse
    /// before it reads anything, so the case has to be one where reading would produce a confident wrong
    /// answer rather than an accidentally right one — and a fully stamped first account is what makes
    /// the difference between refusing and answering visible in the result rather than only in the type.
    /// </para>
    /// <para>
    /// Nothing in the product hands these two ids apart today; the ambient budget arrives from the
    /// session and belongs to the signed-in user. The mistake modelled is the handler that does not
    /// exist yet wiring the read up wrongly, which is why the refusal lives in the read and not in a
    /// caller — the note on <see cref="AskAsync(RepositoryTestHost, Guid, Guid, Guid)" /> argues the
    /// overload that makes the arrangement possible at all.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Completeness_WhenTheAmbientBudgetIsNotOneTheAccountOwns_Refuses()
    {
        // Arrange — two separate accounts, each with one budget. The first is fully rewritten.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = await OpenAdminAsync(host);
        SeededAccount mine = await SeedAccountAsync(host, "google-owner", "owner@example.com", "Household");
        SeededAccount theirs = await SeedAccountAsync(host, "google-other", "other@example.com", "Theirs");
        Guid rotationId = Guid.CreateVersion7();
        await StampEveryRowAsync(admin, mine, rotationId);

        // The arrangement. The first line is what makes this the direction a count cannot refuse — the
        // account owns exactly one budget — the second says the budget being asked from belongs to
        // somebody else, and the third says the account's own rows are finished, so a gate that read
        // them would have every reason to answer true.
        await Assert.That(await OwnedBudgetCountAsync(admin, mine.UserId)).IsEqualTo(1);
        await Assert.That(theirs.BudgetId).IsNotEqualTo(mine.BudgetId);
        await Assert.That(await TablesHoldingAnUnstampedRowAsync(admin, mine, rotationId)).IsEmpty();

        // Act — this account's user, asked from inside the other account's budget.
        RotationScopeException refusal = await ThrowsAsync<RotationScopeException>(
            () => AskAsync(host, mine.UserId, theirs.BudgetId, rotationId));

        // Assert
        await Assert.That(refusal).IsNotNull();
    }

    /// <summary>
    /// Puts the question the way production will: through the read port, over a context bound to the
    /// account's ambient budget.
    /// </summary>
    /// <remarks>
    /// The port is resolved as its interface rather than as the concrete class so that the test binds to
    /// the contract the Application ring will depend on. The context is disposed here and not shared
    /// with the arrangement, which runs on raw Npgsql — a tracked entity left over from the seeding
    /// could otherwise answer a query out of the change tracker rather than out of the database.
    /// </remarks>
    private static Task<bool> AskAsync(RepositoryTestHost host, SeededAccount account, Guid rotationId) =>
        AskAsync(host, account.UserId, account.BudgetId, rotationId);

    /// <summary>
    /// The same question with the ambient budget named separately from the account, so that a case can
    /// ask about one account from inside a budget it does not own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The pairing this overload breaks is the point of it.</b> Every other case in this file asks
    /// about an account from inside that account's own budget, which is the only pairing a signed-in
    /// request can produce today — and it is exactly the pairing under which the gate's scope refusal
    /// never fires. The second direction of that refusal, "the ambient budget is not one this account
    /// owns", cannot be arranged at all while the two ids come from the same record, so it would be
    /// untested for want of a parameter rather than for want of a decision.
    /// </para>
    /// <para>
    /// It is not modelling a bug in session authentication: an ambient budget arrives from the session
    /// and belongs to the signed-in user, so nothing in the product today hands these two apart. What it
    /// models is the read being wired up by a handler that does not exist yet — the one mistake with no
    /// repair path, made in the one place no other case can reach.
    /// </para>
    /// </remarks>
    private static async Task<bool> AskAsync(
        RepositoryTestHost host,
        Guid userId,
        Guid ambientBudgetId,
        Guid rotationId)
    {
        await using BudgetoidDbContext db = CreateDb(host, ambientBudgetId);
        IRotationCompletenessReadService gate = new RotationCompletenessReadService(db);
        return await gate.EveryNarrativeRowIsStampedAsync(userId, rotationId);
    }

    /// <summary>
    /// One furnished account: a user, the one budget it owns, and one row in each of the five
    /// budget-owned tables that carry a stamp.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The budget is named</b>, through <see cref="RepositoryTestHost.SeedAdditionalBudgetAsync" />
    /// rather than through <c>SeedOwnerAsync</c>'s nameless default. A nameless budget carries no
    /// narrative value, so seeding one would make every claim this file makes about <c>budgets</c>
    /// depend on a question this commit does not settle. The owner ends up with exactly one budget
    /// either way.
    /// </para>
    /// <para>
    /// Every row carries a narrative value, including the two optional notes: the subject is which rows
    /// need a stamp, so a fixture that left a note out by accident would be arranging
    /// <see cref="Completeness_WhenATransactionCarriesNoNote_IgnoresThatRow" />'s case inside every
    /// other test.
    /// </para>
    /// </remarks>
    private static async Task<SeededAccount> SeedAccountAsync(
        RepositoryTestHost host,
        string googleSubject,
        string email,
        string label)
    {
        Guid userId = await host.SeedUserAsync(googleSubject, email);
        Guid budgetId = await host.SeedAdditionalBudgetAsync(userId, label);
        return await FurnishBudgetAsync(host, userId, budgetId, label);
    }

    /// <summary>
    /// Puts one row in each of the five budget-owned stamped tables into a budget that already exists.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="SeedAccountAsync" /> rather than duplicated, because one case needs the
    /// same five rows under a <b>nameless</b> budget and the budget is the only thing that differs.
    /// Written as "furnish a budget that exists" rather than as a flag on the seeder, so the call site
    /// that wants the nameless one names <c>SeedOwnerAsync</c> — the seeder registration itself uses —
    /// in its own arrangement, where a reader can see which budget it got.
    /// </remarks>
    private static async Task<SeededAccount> FurnishBudgetAsync(
        RepositoryTestHost host,
        Guid userId,
        Guid budgetId,
        string label)
    {
        await using BudgetoidDbContext db = CreateDb(host, budgetId);

        Account account = Account.Create(
            Guid.CreateVersion7(),
            budgetId,
            SealedNarrative.Indexed($"{label} checking"),
            AccountType.Checking,
            0m,
            "USD",
            UsdMinorUnit,
            SeedInstant);
        Payee payee = Payee.Create(
            Guid.CreateVersion7(), budgetId, SealedNarrative.Indexed($"{label} grocer"), SeedInstant);
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(),
            budgetId,
            SealedNarrative.Indexed($"{label} everyday"),
            SealedNarrative.Description($"{label} group note"),
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
            budgetId,
            group.Id,
            SealedNarrative.Indexed($"{label} groceries"),
            SealedNarrative.Description($"{label} category note"),
            0,
            SeedInstant);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        Transaction transaction = Transaction.Create(
            Guid.CreateVersion7(),
            budgetId,
            account.Id,
            -10m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            SealedNarrative.Description($"{label} groceries"),
            SeedInstant);
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();

        return new SeededAccount(
            userId, budgetId, account.Id, payee.Id, group.Id, category.Id, transaction.Id);
    }

    /// <summary>
    /// Adds a transaction with no note at all, after the stamping, so that it is unstamped by
    /// construction.
    /// </summary>
    private static async Task<Guid> AddNoteLessTransactionAsync(
        RepositoryTestHost host,
        SeededAccount account)
    {
        await using BudgetoidDbContext db = CreateDb(host, account.BudgetId);
        Transaction transaction = Transaction.Create(
            Guid.CreateVersion7(),
            account.BudgetId,
            account.AccountId,
            -3m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 13),
            description: null,
            SeedInstant);
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        return transaction.Id;
    }

    /// <summary>
    /// Adds a category group carrying a name and no description, after the stamping, so that it is
    /// unstamped by construction.
    /// </summary>
    /// <remarks>
    /// <b>The shape the fixture cannot produce.</b> Every group and category this file seeds carries a
    /// description, so "a group with a name and nothing else" — which is what a person who typed a name
    /// and skipped the note has — is never arranged. The gate's presence rule for these two tables keys
    /// on the <b>required</b> name and therefore has no presence test at all; an implementation that
    /// keyed it on <c>Description != null</c> instead would skip exactly this row, whose name still
    /// holds old-key ciphertext, and the promotion would take it.
    /// </remarks>
    private static async Task<Guid> AddDescriptionLessCategoryGroupAsync(
        RepositoryTestHost host,
        SeededAccount account)
    {
        await using BudgetoidDbContext db = CreateDb(host, account.BudgetId);
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(),
            account.BudgetId,
            SealedNarrative.Indexed("Household unnoted"),
            description: null,
            1,
            SeedInstant);
        db.CategoryGroups.Add(group);
        await db.SaveChangesAsync();
        return group.Id;
    }

    /// <summary>
    /// Adds one payee to a budget other than the account's own, which is the only narrative row the
    /// tenancy cases need — one is as invisible to a budget-scoped read as a thousand.
    /// </summary>
    private static async Task<Guid> AddPayeeAsync(RepositoryTestHost host, Guid budgetId, string label)
    {
        await using BudgetoidDbContext db = CreateDb(host, budgetId);
        Payee payee = Payee.Create(
            Guid.CreateVersion7(), budgetId, SealedNarrative.Indexed($"{label} grocer"), SeedInstant);
        db.Payees.Add(payee);
        await db.SaveChangesAsync();
        return payee.Id;
    }

    /// <summary>
    /// Writes one rotation identifier onto every row of the account's six tables, in one statement per
    /// table.
    /// </summary>
    /// <remarks>
    /// <c>budgets</c> is scoped on <c>user_id</c> and the other five on <c>budget_id</c>, which is the
    /// same asymmetry the gate itself has to carry: a budget belongs to an account, and the other five
    /// belong to a budget.
    /// </remarks>
    private static async Task StampEveryRowAsync(
        NpgsqlConnection admin,
        SeededAccount account,
        Guid rotationId)
    {
        await using NpgsqlCommand command = new(
            """
            update budgets set rotation_id = @rotation where user_id = @user;
            update accounts set rotation_id = @rotation where budget_id = @budget;
            update payees set rotation_id = @rotation where budget_id = @budget;
            update category_groups set rotation_id = @rotation where budget_id = @budget;
            update categories set rotation_id = @rotation where budget_id = @budget;
            update transactions set rotation_id = @rotation where budget_id = @budget;
            """, admin);
        command.Parameters.AddWithValue("rotation", rotationId);
        command.Parameters.AddWithValue("user", account.UserId);
        command.Parameters.AddWithValue("budget", account.BudgetId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Sets or clears one row's stamp. <paramref name="stamp" /> is <see langword="null" /> for the
    /// clear an ordinary narrative write performs.
    /// </summary>
    private static async Task SetStampAsync(
        NpgsqlConnection admin,
        string table,
        Guid rowId,
        Guid? stamp)
    {
        // The table name is interpolated because an identifier cannot be a parameter. It is never
        // caller-supplied text: every value reaching here comes from StampedTables or from an
        // [Arguments] attribute in this file.
        await using NpgsqlCommand command = new(
            $"update {table} set rotation_id = @rotation where id = @id", admin);
        command.Parameters.AddWithValue("rotation", (object?)stamp ?? DBNull.Value);
        command.Parameters.AddWithValue("id", rowId);
        int affected = await command.ExecuteNonQueryAsync();
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Arranging '{table}' touched {affected} rows where exactly one was meant.");
        }
    }

    /// <summary>
    /// The names of the tables holding no row for this account — the non-vacuity guard every case here
    /// leans on.
    /// </summary>
    /// <remarks>
    /// Returned as names rather than counted, because a count says a table is empty and only a name says
    /// which. <c>ErasureAtomicityTests</c> keeps the same guard for the same reason: "no table holds an
    /// unstamped row" is satisfied perfectly by six empty tables.
    /// </remarks>
    private static Task<IReadOnlyList<string>> TablesWithNoRowsAsync(
        NpgsqlConnection admin,
        SeededAccount account) =>
        TablesMatchingAsync(admin, account, "count(*) = 0", stamp: null);

    /// <summary>The names of the tables holding at least one row that is not stamped with this run.</summary>
    private static Task<IReadOnlyList<string>> TablesHoldingAnUnstampedRowAsync(
        NpgsqlConnection admin,
        SeededAccount account,
        Guid rotationId) =>
        TablesMatchingAsync(
            admin,
            account,
            "count(*) filter (where rotation_id is distinct from @rotation) > 0",
            rotationId);

    /// <summary>The names of the tables holding at least one row with a stamp of any kind.</summary>
    private static Task<IReadOnlyList<string>> TablesHoldingAStampedRowAsync(
        NpgsqlConnection admin,
        SeededAccount account) =>
        TablesMatchingAsync(admin, account, "count(*) filter (where rotation_id is not null) > 0", stamp: null);

    /// <summary>
    /// Walks all six tables and returns the ones whose rows satisfy <paramref name="having" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The arrangement is measured with <c>IS DISTINCT FROM</c> too, and it has to be.</b> A guard
    /// written with <c>&lt;&gt;</c> would carry the very three-valued-logic bug it is standing here to
    /// expose — it would report a table of entirely unstamped rows as fully stamped, and the guard
    /// would then agree with a broken gate instead of contradicting it.
    /// </para>
    /// <para>
    /// It reads on the superuser connection and scopes by hand, which is what makes it independent of
    /// the thing under test: a guard that used the same query filters could not disagree with a gate
    /// that scoped wrongly.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<string>> TablesMatchingAsync(
        NpgsqlConnection admin,
        SeededAccount account,
        string having,
        Guid? stamp)
    {
        List<string> matches = [];

        foreach (string table in StampedTables)
        {
            string ownerColumn = table == "budgets" ? "user_id" : "budget_id";
            await using NpgsqlCommand command = new(
                $"select {having} from {table} where {ownerColumn} = @owner", admin);
            command.Parameters.AddWithValue(
                "owner", table == "budgets" ? account.UserId : account.BudgetId);
            if (stamp is { } rotationId)
            {
                command.Parameters.AddWithValue("rotation", rotationId);
            }

            if ((bool)(await command.ExecuteScalarAsync())!)
            {
                matches.Add(table);
            }
        }

        return matches;
    }

    /// <summary>Reads one row's stamp back, as the database holds it.</summary>
    private static async Task<Guid?> StampOfAsync(NpgsqlConnection admin, string table, Guid rowId)
    {
        await using NpgsqlCommand command = new(
            $"select rotation_id from {table} where id = @id", admin);
        command.Parameters.AddWithValue("id", rowId);
        return await command.ExecuteScalarAsync() switch
        {
            Guid stamp => stamp,
            null or DBNull => null,
            var unexpected => throw new InvalidOperationException(
                $"'{table}'.rotation_id came back as '{unexpected}'."),
        };
    }

    /// <summary>
    /// Whether one narrative column of one row really holds nothing, asked of the column rather than of
    /// the entity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked of the column because presence is what the gate reads, and an entity would answer out of a
    /// value converter — <c>budgets.name</c> and the two descriptions all round-trip through one, and a
    /// converter that materialized an empty envelope for a <c>NULL</c> would make the entity disagree
    /// with the column the gate's SQL actually tests.
    /// </para>
    /// <para>
    /// Generic over table and column rather than one member per column: three of the four nullable
    /// narrative columns are arranged in this file now, and <b>the non-null direction is asked too</b> —
    /// a category group whose description is absent still has to prove its name is present, or the case
    /// establishes nothing about why the row is outstanding.
    /// </para>
    /// </remarks>
    private static async Task<bool> ColumnIsNullAsync(
        NpgsqlConnection admin,
        string table,
        string column,
        Guid rowId)
    {
        // Identifiers cannot be parameters, and neither of these is caller-supplied text: every value
        // reaching here is a literal in this file, as with SetStampAsync above.
        await using NpgsqlCommand command = new(
            $"select {column} is null from {table} where id = @id", admin);
        command.Parameters.AddWithValue("id", rowId);
        return await command.ExecuteScalarAsync() switch
        {
            bool isNull => isNull,
            _ => throw new InvalidOperationException(
                $"No row of '{table}' is filed under the id this arrangement asked about."),
        };
    }

    /// <summary>How many budgets the account owns, counted on the database rather than inferred.</summary>
    /// <remarks>
    /// The tenancy cases turn on this number and on nothing else visible in their arrangement — a
    /// seeding call that silently failed to add the second budget would leave them asserting a refusal
    /// that never had a reason to happen, and a refusal is indistinguishable from a gate that refuses
    /// everything. Counted here, and never used to make the assertion itself: the gate compares
    /// <em>sets</em> in both directions and a count would be the very simplification it refuses.
    /// </remarks>
    private static async Task<int> OwnedBudgetCountAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select count(*) from budgets where user_id = @user", admin);
        command.Parameters.AddWithValue("user", userId);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Runs an action expected to throw, and hands back the exception it threw.
    /// </summary>
    /// <remarks>
    /// The shape <c>ExportDataHandlerTests</c> uses beside its own refusal, and named exactly:
    /// <c>RotationScopeException</c> is an <see cref="InvalidOperationException" />, which is also what
    /// a mis-seeded fixture, a disposed context and this file's own arrangement guards throw. Catching
    /// the base type would let any of those pass as the refusal under test.
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
            $"Expected {typeof(TException).Name}, and the gate answered instead of refusing.");
    }

    /// <summary>
    /// One seeded account: its owner, its single budget, and the row it holds in each of the five
    /// budget-owned tables.
    /// </summary>
    /// <remarks>
    /// A <see langword="readonly" /> <see langword="record" /> <see langword="struct" /> for the reason
    /// <c>RepositoryTestHost.SeededOwner</c> gives: identifiers that are only meaningful together, never
    /// mutated and never compared by reference.
    /// </remarks>
    private readonly record struct SeededAccount(
        Guid UserId,
        Guid BudgetId,
        Guid AccountId,
        Guid PayeeId,
        Guid CategoryGroupId,
        Guid CategoryId,
        Guid TransactionId)
    {
        /// <summary>
        /// The identifier of this account's row in <paramref name="table" />, so the parameterized case
        /// above can name a table and get the one row it has to disturb.
        /// </summary>
        /// <remarks>
        /// It throws on an unknown name rather than returning a default, because
        /// <see cref="Guid.Empty" /> would match no row and <c>SetStampAsync</c>'s affected-row check
        /// would report it as an arrangement fault one layer away from its cause.
        /// </remarks>
        public Guid RowIn(string table) => table switch
        {
            "budgets" => BudgetId,
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
    /// Minor unit of the USD accounts seeded here. Precision is not what any case is about; the
    /// constant keeps a bare <c>2</c> from reading as a rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    /// <summary>
    /// Fixed UTC instant for every seeded row, matching <c>RepositoryTestHost</c>'s own.
    /// </summary>
    /// <remarks>
    /// PostgreSQL <c>timestamptz</c> rejects a non-UTC <see cref="DateTime" />, so
    /// <see cref="DateTimeKind.Utc" /> is load-bearing rather than decoration.
    /// </remarks>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private static BudgetoidDbContext CreateDb(RepositoryTestHost host, Guid budgetId) => new(
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
}
