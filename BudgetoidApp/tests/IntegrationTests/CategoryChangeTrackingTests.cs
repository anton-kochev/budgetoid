using Domain.Categories;
using Domain.CategoryGroups;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The change-tracking apparatus <see cref="CategoryConfiguration" /> declares for its two narrative
/// columns and its blind index, measured by the statements PostgreSQL is handed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The <see cref="CategoryGroupChangeTrackingTests" /> shape, on the table where the third case
/// finally has teeth.</b> That file's three comparers are these three, its argument about what a
/// missing comparer does is unchanged, and none of it is restated here. What is this table's own is
/// <see cref="Saving_ANewNameBesideARestatedNote_NamesOnlyTheNamePair" />: the emission behaviour it
/// pins — one <c>IndexedName</c> argument producing one <c>UPDATE</c> that names <c>name</c> and
/// <c>name_key</c> together — is the exact behaviour the <c>categories</c> grant story turned on, and
/// until this file existed nothing asserted it directly on this table.
/// </para>
/// <para>
/// <b>That story is worth one paragraph, because it is why a reader should not treat this file as a
/// copy.</b> <c>app-role-grants.sql</c> granted <c>UPDATE (name, description, position,
/// category_group_id)</c> on <c>categories</c> and withheld <c>name_key</c>. A rename therefore
/// answered <c>42501: permission denied for table categories</c> while a description-only edit —
/// which restates the same name under a fresh nonce, so the envelope differs and the index does not —
/// emitted one granted column and succeeded. The hole was loud in the schema and silent in the run:
/// the suite issued ZERO genuine renames, because every case that renamed a category died earlier on
/// a seeding 400. <see cref="AppRoleGrantMatrixTests" /> could not see it either, because its
/// expectation named the same four columns the SQL did. The case below is the one place the emission
/// itself is asserted, independent of any grant.
/// </para>
/// <para>
/// <b>The grant catches none of what this file covers, exactly as on category groups.</b> A comparer
/// can only ever cause one of the FIVE granted columns to be named, so every mutation here stays
/// inside the grant and comes back as a clean commit affecting rows it should not have touched. That
/// is the whole reason these cases read the statement text rather than a row count.
/// </para>
/// <para>
/// <b>The snapshot arm, <c>CopyEnvelope</c>, is NOT covered here and could not be — measured, not
/// assumed.</b> Return the instance instead of <c>NarrativeField.FromStore(field.Envelope)</c> and the
/// tracker's record of the old value aliases the live one; for that to be observable something must
/// overwrite an accepted envelope's bytes IN PLACE, and nothing can. The buffer is private,
/// <see cref="Domain.Security.NarrativeField.Envelope" /> hands back a
/// <see cref="ReadOnlyMemory{T}" /> over a copy the factory took, and every write path replaces the
/// whole field rather than editing one. <b>MEASURED ON THIS TABLE, NOT INHERITED:</b> that mutation was
/// run over the full suite and killed <b>nothing</b> — not here, not in the two sibling change-tracking
/// files, not in the unit project. Three of the four arms these comparers carry are covered by the cases
/// below; the fourth is held by the argument at the member, and this paragraph is the record of a run
/// rather than a prediction or an omission.
/// </para>
/// <para>
/// <b>The connection is the container superuser</b>, for
/// <see cref="CategoryGroupChangeTrackingTests" />'s reason: these claims are about the statement EF
/// composes, which is decided before any privilege is checked.
/// </para>
/// </remarks>
public sealed class CategoryChangeTrackingTests
{
    [Test]
    public async Task Saving_ANameAndNoteRebuiltFromIdenticalBytes_SendsNoStatementAtAll()
    {
        // Arrange — seeded on a context of its own, so every write the recorder holds afterwards belongs
        // to the Act. Both fixtures are deterministic in their label, which is what makes "rebuilt from
        // identical bytes" spellable: the values handed to Update are byte-for-byte what the row holds
        // and are NEW INSTANCES, which is precisely what a reference comparison gets wrong.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        var categoryId = Guid.CreateVersion7();
        await SeedAsync(options, budgetId, categoryId);

        StatementRecorder recorder = new();
        await using BudgetoidDbContext db = new(
            CreateOptions(host, recorder), new TestBudgetContext(budgetId));
        Category category = await db.Categories.SingleAsync(row => row.Id == categoryId);

        // Act
        category.Update(
            SealedNarrative.Indexed("Groceries"),
            SealedNarrative.Description("Weekly food shop"));
        int restatement = await db.SaveChangesAsync();

        // The control, and it is not decoration: without it every assertion below is satisfied by an
        // entity nothing was tracking, a context that never opened, or an Update that threw. A change
        // this apparatus has no opinion about — position is an int the server can still read — has to
        // produce a statement on the very same context.
        category.Place(category.CategoryGroupId, 7);
        int realChange = await db.SaveChangesAsync();

        // Assert — the count of writes is the load-bearing line. Exactly one reached the table and it is
        // the control's; the restatement contributed none. Any of the three comparers missing turns the
        // restatement into a second UPDATE and this becomes two.
        await Assert.That(restatement).IsEqualTo(0);
        await Assert.That(realChange).IsEqualTo(1);
        IReadOnlyList<string> writes = recorder.WritesTo("categories");
        await Assert.That(writes.Count).IsEqualTo(1);
        await Assert.That(UpdatedColumns(writes[0])).IsEqualTo("position");
    }

    [Test]
    public async Task Saving_ANewNoteBesideARestatedName_NamesOnlyTheDescription()
    {
        // Arrange — the Act restates the name pair byte for byte while genuinely replacing the note.
        // This is the case that ATTRIBUTES: a statement naming name_key says the blind index comparer is
        // gone, one naming name says HasSameEnvelope stopped comparing bytes. The case above sees both
        // too, but only as "a write happened"; here the columns themselves say which.
        //
        // It is also the shape of the commonest edit on this table, and therefore the one the broken
        // grant was quiet on: one granted column, a clean commit, nothing to notice.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        var categoryId = Guid.CreateVersion7();
        await SeedAsync(options, budgetId, categoryId);

        StatementRecorder recorder = new();
        await using BudgetoidDbContext db = new(
            CreateOptions(host, recorder), new TestBudgetContext(budgetId));
        Category category = await db.Categories.SingleAsync(row => row.Id == categoryId);

        // Act
        category.Update(
            SealedNarrative.Indexed("Groceries"),
            SealedNarrative.Description("Corner shop and the market"));
        int affected = await db.SaveChangesAsync();

        // Assert — one row and one column. The affected count is the non-vacuity: a case asserting only
        // that name and name_key are absent would pass on a save that wrote nothing at all.
        await Assert.That(affected).IsEqualTo(1);
        IReadOnlyList<string> writes = recorder.WritesTo("categories");
        await Assert.That(writes.Count).IsEqualTo(1);
        await Assert.That(UpdatedColumns(writes[0])).IsEqualTo("description");
    }

    [Test]
    public async Task Saving_ANewNameBesideARestatedNote_NamesOnlyTheNamePair()
    {
        // Arrange — THE CASE THIS FILE EXISTS FOR, and the one whose subject is not a comparer at all.
        // It pins that a genuine rename emits `name` and `name_key` IN ONE STATEMENT. That is the fact
        // the whole categories grant story rests on: measured this slice, this statement answers
        // `42501: permission denied for table categories` under a GRANT UPDATE list missing name_key,
        // while the description-only edit above answers UPDATE 1 under the same list. Nothing asserted
        // it on this table before, which is why the hole survived a test named for grants and a test
        // named for tenancy.
        //
        // PostgreSQL names only the RELATION in that 42501 — `permission denied for table categories`,
        // from aclcheck_error — so the error never says which column. This case is where a reader
        // arriving from one learns what the statement actually names.
        //
        // The note is restated byte for byte as a NEW NarrativeField while the name pair genuinely
        // moves; drop EnvelopeContentComparer from the description property and the statement names a
        // third column holding exactly what it already held. The row is seeded WITH a note on purpose:
        // seeded NULL, the description property holds no instance for a reference comparison to get
        // wrong, and this case would be green under the very mutation it exists for.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        var categoryId = Guid.CreateVersion7();
        await SeedAsync(options, budgetId, categoryId);

        StatementRecorder recorder = new();
        await using BudgetoidDbContext db = new(
            CreateOptions(host, recorder), new TestBudgetContext(budgetId));
        Category category = await db.Categories.SingleAsync(row => row.Id == categoryId);

        // Act
        category.Update(
            SealedNarrative.Indexed("Food Shopping"),
            SealedNarrative.Description("Weekly food shop"));
        int affected = await db.SaveChangesAsync();

        // Assert — BOTH halves of the name and nothing else. The pair travels together, so a statement
        // naming one of them would be its own defect: half a rename leaves a uniqueness value describing
        // a name the row no longer holds, and nothing on this side can recompute the digest to notice.
        //
        // THIS CASE CATCHES MORE THAN ITS NAME PROMISES, and the mutation run is what showed it. It was
        // written for the description's comparer — restate the note, watch it stay out of the statement —
        // but falsifying HasSameEnvelope to `=> false` dirties the restated DESCRIPTION as well, so the
        // statement grows a third column and this case reddens alongside its two neighbours. Measured:
        // that mutation killed THREE cases here, not the two predicted. The equality arm is therefore
        // held by every case in this file rather than by the two that name it.
        await Assert.That(affected).IsEqualTo(1);
        IReadOnlyList<string> writes = recorder.WritesTo("categories");
        await Assert.That(writes.Count).IsEqualTo(1);
        await Assert.That(UpdatedColumns(writes[0])).IsEqualTo("name, name_key");
    }

    /// <summary>
    /// A group and one category in it, written on a context of their own.
    /// </summary>
    /// <remarks>
    /// A category cannot exist without a group — the reference is composite and <c>RESTRICT</c> — so the
    /// seeder writes both. The group's own narrative values are irrelevant to every case here and are
    /// deliberately unlike the category's, so a statement against the wrong table would be visible in
    /// the recorder rather than plausible.
    /// </remarks>
    private static async Task SeedAsync(
        DbContextOptions<BudgetoidDbContext> options,
        Guid budgetId,
        Guid categoryId)
    {
        await using BudgetoidDbContext seed = new(options, new TestBudgetContext(budgetId));
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(),
            budgetId,
            SealedNarrative.Indexed("Essentials"),
            null,
            0,
            UtcNow());
        seed.CategoryGroups.Add(group);
        seed.Categories.Add(Category.Create(
            categoryId,
            budgetId,
            group.Id,
            SealedNarrative.Indexed("Groceries"),
            SealedNarrative.Description("Weekly food shop"),
            0,
            UtcNow()));
        await seed.SaveChangesAsync();
    }

    /// <summary>
    /// The columns <paramref name="statement" />'s <c>SET</c> clause assigns to, ordered and joined, or
    /// the whole statement back when it carries no such clause.
    /// </summary>
    /// <remarks>
    /// <b>Parsed as assignments rather than searched for as words, because <c>name_key</c> contains
    /// <c>name</c>.</b> A <c>Contains("name")</c> test is true of a statement naming only the index, so
    /// every claim about which half of the pair moved would be unwritable — the same trap
    /// <c>SchemaConstraintSnapshotTests</c> avoids by comparing a whole <c>indexdef</c>. Handing the
    /// statement back unparsed on a miss is deliberate: an assertion then fails carrying the SQL that
    /// surprised it, instead of comparing an empty string against an expectation.
    /// </remarks>
    private static string UpdatedColumns(string statement)
    {
        int set = statement.IndexOf("SET ", StringComparison.Ordinal);
        if (set < 0)
        {
            return statement;
        }

        string assignments = statement[(set + 4)..];
        int where = assignments.IndexOf("WHERE", StringComparison.Ordinal);
        if (where >= 0)
        {
            assignments = assignments[..where];
        }

        return string.Join(
            ", ",
            assignments
                .Split(',')
                .Select(assignment => assignment.Split('=')[0].Trim().Trim('"'))
                .Order(StringComparer.Ordinal));
    }

    private static DbContextOptions<BudgetoidDbContext> CreateOptions(
        RepositoryTestHost host,
        StatementRecorder? recorder = null)
    {
        DbContextOptionsBuilder<BudgetoidDbContext> builder = new();
        builder.UseNpgsql(host.ConnectionString);
        if (recorder is not null)
        {
            builder.AddInterceptors(recorder);
        }

        return builder.Options;
    }

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static DateTime UtcNow() => new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
