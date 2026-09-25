using Domain.CategoryGroups;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The change-tracking apparatus <see cref="CategoryGroupConfiguration" /> declares for its two
/// narrative columns and its blind index, measured by the statements PostgreSQL is handed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three comparers' worth of argument, and until this file existed none of it was held by
/// anything.</b> Measured on this tree, each of these killed no test in the suite: dropping
/// <c>EnvelopeContentComparer</c> from the <c>description</c> property, dropping
/// <c>BlindIndexContentComparer</c> from <c>name_key</c>, and rewriting <c>HasSameEnvelope</c> as
/// <c>=&gt; false</c>. <c>MovingAndDeletingGroups_WorksWhetherOrNotTheyHoldADescription</c> proves the
/// column materialises without throwing, which is a different claim and the only one anything made.
/// </para>
/// <para>
/// <b>What a missing comparer does is not a crash, which is why it survived so long.</b>
/// <see cref="Domain.Security.NarrativeField" /> is a class with no <c>Equals</c> of its own, so EF's
/// default comparison for it is reference equality; <see cref="ReadOnlyMemory{T}" /> compares object
/// identity, offset and length. Both read a value rebuilt from IDENTICAL bytes as an edit. Nothing
/// breaks — the row is rewritten with what it already held — so the symptom is a statement that should
/// not have been sent, and the only place that is visible is the statement itself.
/// </para>
/// <para>
/// <b>The grant does not catch any of it, and it is worth saying so rather than leaving a reader to
/// assume it does.</b> <c>app-role-grants.sql</c> makes <c>budget_id</c> and <c>created_at_utc</c>
/// immutable by omission from <c>GRANT UPDATE (name, name_key, description, position)</c>, so a
/// statement naming one of those two answers <c>42501</c> — but a comparer can only ever cause one of
/// the FOUR granted columns to be named, so every mutation this file covers stays inside the grant
/// and comes back <c>0</c> rows affected and a clean commit. That is the whole reason these cases
/// read the statement text.
/// </para>
/// <para>
/// <b>The snapshot arm, <c>CopyEnvelope</c>, is NOT covered here and could not be.</b> It exists so
/// the tracker's record of the old value does not share a buffer with the live one; return the
/// instance instead of <c>NarrativeField.FromStore(field.Envelope)</c> and the two alias. For that
/// aliasing to be observable, something has to overwrite an accepted envelope's bytes IN PLACE — and
/// nothing can: the buffer is private, <see cref="Domain.Security.NarrativeField.Envelope" /> hands
/// back a <see cref="ReadOnlyMemory{T}" /> over a copy the factory took, and every write path replaces
/// the whole field rather than editing one. A case that reached in with
/// <c>MemoryMarshal</c> to produce the state would be measuring a hazard no route can reach. The
/// argument at that member is therefore held by review, and this paragraph is the record of that
/// rather than an omission.
/// </para>
/// <para>
/// <b>The connection is the container superuser, as in
/// <see cref="RepositoryConstraintAttributionTests" />.</b> These claims are about the statement EF
/// composes, which is decided before any privilege is checked, and the paragraph above is why the app
/// role would answer identically.
/// </para>
/// </remarks>
public sealed class CategoryGroupChangeTrackingTests
{
    [Test]
    public async Task Saving_ANameAndNoteRebuiltFromIdenticalBytes_SendsNoStatementAtAll()
    {
        // Arrange — the row is seeded on a context of its own, so every write the recorder holds
        // afterwards belongs to the Act. Both fixtures are deterministic in their label, which is what
        // makes "rebuilt from identical bytes" spellable at all: the values handed to Update below are
        // byte-for-byte what the row already holds and are NEW INSTANCES, which is precisely the case
        // a reference comparison gets wrong.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        var groupId = Guid.CreateVersion7();
        await using (BudgetoidDbContext seed = new(options, new TestBudgetContext(budgetId)))
        {
            seed.CategoryGroups.Add(CategoryGroup.Create(
                groupId,
                budgetId,
                SealedNarrative.Indexed("Essentials"),
                SealedNarrative.Description("Rent, food and the bus"),
                0,
                UtcNow()));
            await seed.SaveChangesAsync();
        }

        StatementRecorder recorder = new();
        await using BudgetoidDbContext db = new(
            CreateOptions(host, recorder), new TestBudgetContext(budgetId));
        CategoryGroup group = await db.CategoryGroups.SingleAsync(row => row.Id == groupId);

        // Act
        group.Update(
            SealedNarrative.Indexed("Essentials"),
            SealedNarrative.Description("Rent, food and the bus"));
        int restatement = await db.SaveChangesAsync();

        // The control, and it is not decoration: without it every assertion below is satisfied by an
        // entity nothing was tracking, a context that never opened, or an Update that threw. A change
        // this apparatus has no opinion about — position is an int the server can still read — has to
        // produce a statement on the very same context.
        group.SetPosition(7);
        int realChange = await db.SaveChangesAsync();

        // Assert — the count of writes is the load-bearing line. Exactly one reached the table, and it
        // is the control's; the restatement contributed none. Any of the three comparers missing turns
        // the restatement into a second UPDATE, and this becomes two.
        await Assert.That(restatement).IsEqualTo(0);
        await Assert.That(realChange).IsEqualTo(1);
        IReadOnlyList<string> writes = recorder.WritesTo("category_groups");
        await Assert.That(writes.Count).IsEqualTo(1);
        await Assert.That(UpdatedColumns(writes[0])).IsEqualTo("position");
    }

    [Test]
    public async Task Saving_ANewNoteBesideARestatedName_NamesOnlyTheDescription()
    {
        // Arrange — the same row, and the Act restates the name pair byte for byte while genuinely
        // replacing the note. This is the case that attributes: a statement naming name_key says the
        // blind index comparer is gone, and one naming name says HasSameEnvelope stopped comparing
        // bytes. The previous case sees both too, but only as "a write happened"; here the columns
        // themselves say which.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        var groupId = Guid.CreateVersion7();
        await using (BudgetoidDbContext seed = new(options, new TestBudgetContext(budgetId)))
        {
            seed.CategoryGroups.Add(CategoryGroup.Create(
                groupId,
                budgetId,
                SealedNarrative.Indexed("Essentials"),
                SealedNarrative.Description("Rent, food and the bus"),
                0,
                UtcNow()));
            await seed.SaveChangesAsync();
        }

        StatementRecorder recorder = new();
        await using BudgetoidDbContext db = new(
            CreateOptions(host, recorder), new TestBudgetContext(budgetId));
        CategoryGroup group = await db.CategoryGroups.SingleAsync(row => row.Id == groupId);

        // Act
        group.Update(
            SealedNarrative.Indexed("Essentials"),
            SealedNarrative.Description("Rent, food and the tram"));
        int affected = await db.SaveChangesAsync();

        // Assert — one row and one column. The affected count is the non-vacuity: a case asserting only
        // that name and name_key are absent would pass on a save that wrote nothing at all.
        await Assert.That(affected).IsEqualTo(1);
        IReadOnlyList<string> writes = recorder.WritesTo("category_groups");
        await Assert.That(writes.Count).IsEqualTo(1);
        await Assert.That(UpdatedColumns(writes[0])).IsEqualTo("description");
    }

    [Test]
    public async Task Saving_ANewNameBesideARestatedNote_NamesOnlyTheNamePair()
    {
        // Arrange — the mirror, and the one that sees the description's comparer. The note is restated
        // byte for byte as a NEW NarrativeField while the name pair genuinely moves; drop
        // EnvelopeContentComparer from the description property and the statement names a third column
        // holding exactly what it already held.
        //
        // The row is seeded WITH a note on purpose. Seeded NULL, the description property holds no
        // instance for a reference comparison to get wrong, and this case would be green under the very
        // mutation it exists for.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        var groupId = Guid.CreateVersion7();
        await using (BudgetoidDbContext seed = new(options, new TestBudgetContext(budgetId)))
        {
            seed.CategoryGroups.Add(CategoryGroup.Create(
                groupId,
                budgetId,
                SealedNarrative.Indexed("Essentials"),
                SealedNarrative.Description("Rent, food and the bus"),
                0,
                UtcNow()));
            await seed.SaveChangesAsync();
        }

        StatementRecorder recorder = new();
        await using BudgetoidDbContext db = new(
            CreateOptions(host, recorder), new TestBudgetContext(budgetId));
        CategoryGroup group = await db.CategoryGroups.SingleAsync(row => row.Id == groupId);

        // Act
        group.Update(
            SealedNarrative.Indexed("Essential Obligations"),
            SealedNarrative.Description("Rent, food and the bus"));
        int affected = await db.SaveChangesAsync();

        // Assert — BOTH halves of the name and nothing else. The pair travels together, so a statement
        // naming one of them would be its own defect: half a rename leaves a uniqueness value
        // describing a name the row no longer holds, and nothing on this side can recompute the digest
        // to notice.
        await Assert.That(affected).IsEqualTo(1);
        IReadOnlyList<string> writes = recorder.WritesTo("category_groups");
        await Assert.That(writes.Count).IsEqualTo(1);
        await Assert.That(UpdatedColumns(writes[0])).IsEqualTo("name, name_key");
    }

    /// <summary>
    /// The columns <paramref name="statement" />'s <c>SET</c> clause assigns to, ordered and joined,
    /// or the whole statement back when it carries no such clause.
    /// </summary>
    /// <remarks>
    /// <b>Parsed as assignments rather than searched for as words, because <c>name_key</c> contains
    /// <c>name</c>.</b> A <c>Contains("name")</c> test is true of a statement naming only the index,
    /// so every claim about which half of the pair moved would be unwritable. Handing the statement
    /// back unparsed on a miss is deliberate too: an assertion then fails carrying the SQL that
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
