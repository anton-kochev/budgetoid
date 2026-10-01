using Domain.Accounts;
using Domain.Transactions;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The change-tracking apparatus <see cref="TransactionConfiguration" /> declares for its one narrative
/// column, measured by the statements PostgreSQL is handed.
/// </summary>
/// <remarks>
/// <para>
/// <b>ONE COMPARER, TWO CASES — AND ONLY ONE OF THEM CAN SEE THE COMPARER.</b> Measured, and it is the
/// correction this file most needs a reader to have: dropping the comparer kills
/// <see cref="Saving_ANoteRebuiltFromIdenticalBytes_SendsNoStatementAtAll" /> alone.
/// <see cref="Saving_ANewNote_NamesOnlyTheDescription" /> stays green and STRUCTURALLY MUST, because
/// <c>description</c> is the only converted property on this table: there is no second narrative column
/// for a spurious dirty flag to add, so a comparer reporting everything dirty still emits exactly
/// <c>SET description = …</c>. Its sibling on <c>categories</c> reddens only because <c>name</c> and
/// <c>name_key</c> are there to be dragged in. The second case here is not empty — it holds that a real
/// note change reaches the column and drags nothing with it — but it is silent about comparers, and a
/// reader counting two cases as two guards would be wrong.
/// </para>
/// <para>
/// <b>The arithmetic below is still why the THIRD case is absent.</b>
/// <see cref="CategoryChangeTrackingTests" /> and <see cref="CategoryGroupChangeTrackingTests" /> each
/// carry three cases because those tables carry three comparers — an envelope comparer on the name, a
/// blind-index comparer on <c>name_key</c>, and the envelope comparer again on the description. This is
/// the first sealed table in the product WITH NO NAME COLUMN: no <c>IndexedName</c>, no blind index, no
/// <c>name_key</c>, no unique index but the key. There is exactly one converted property here, so the
/// third case those files carry — the one that says which half of a name pair moved — has nothing to be
/// about and is absent rather than adapted.
/// </para>
/// <para>
/// <b>What a missing comparer does is not a crash, which is why it survives review.</b>
/// <see cref="Domain.Security.NarrativeField" /> is a class with no <c>Equals</c> of its own, so EF's
/// default comparison for it is reference equality, and a note rebuilt from IDENTICAL bytes reads as an
/// edit. Nothing breaks — the row is rewritten with what it already held — so the symptom is a statement
/// that should not have been sent, and the only place that is visible is the statement itself.
/// </para>
/// <para>
/// <b>The grant catches none of it, and on this table that is not even close.</b> <c>description</c> was
/// already on <c>GRANT UPDATE</c> before this slice and no column was added, so there is no half-grant
/// here for a spurious statement to trip over: every mutation these two cases cover names a granted
/// column and commits cleanly. The control column is <c>amount</c> rather than a position — a value the
/// server still reads and still bounds with <c>CK_transactions_amount</c>.
/// </para>
/// <para>
/// <b>The snapshot arm, <c>CopyEnvelope</c>, is NOT covered here and could not be — measured, not
/// assumed.</b> The argument is <see cref="CategoryChangeTrackingTests" />'s and holds unchanged: for an
/// aliased snapshot to diverge, something must overwrite an accepted envelope's bytes IN PLACE, and
/// <see cref="Domain.Security.NarrativeField" />'s private buffer and copying factories prevent it.
/// <b>MEASURED ON THIS TABLE:</b> that mutation was run over the full suite and killed <b>nothing</b>,
/// here or anywhere. Of the two arms this comparer carries, the equality one is covered by exactly ONE
/// case below — not two, for the reason the opening paragraph gives — and the snapshot one is held by an
/// argument living in <c>CategoryConfiguration</c> rather than at this table's own member, which is a
/// gap staged for the code owner rather than one this file can close.
/// </para>
/// <para>
/// <b>The connection is the container superuser</b>, for the reason its two siblings give: these claims
/// are about the statement EF composes, which is decided before any privilege is checked.
/// </para>
/// </remarks>
public sealed class TransactionChangeTrackingTests
{
    private const int UsdMinorUnit = 2;

    [Test]
    public async Task Saving_ANoteRebuiltFromIdenticalBytes_SendsNoStatementAtAll()
    {
        // Arrange — seeded on a context of its own, so every write the recorder holds afterwards belongs
        // to the Act. The fixture is deterministic in its label, which is what makes "rebuilt from
        // identical bytes" spellable: the value handed to Update is byte-for-byte what the row holds and
        // is a NEW INSTANCE, which is precisely what a reference comparison gets wrong.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        var transactionId = Guid.CreateVersion7();
        Guid accountId = await SeedAsync(options, budgetId, transactionId);

        StatementRecorder recorder = new();
        await using BudgetoidDbContext db = new(
            CreateOptions(host, recorder), new TestBudgetContext(budgetId));
        Transaction transaction = await db.Transactions.SingleAsync(row => row.Id == transactionId);

        // Act — every argument is what the row already holds, including the note.
        transaction.Update(
            accountId,
            -42.50m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            SealedNarrative.Description("Corner shop"));
        int restatement = await db.SaveChangesAsync();

        // The control, and it is not decoration: without it every assertion below is satisfied by an
        // entity nothing was tracking, a context that never opened, or an Update that threw. A change
        // this apparatus has no opinion about — amount is a decimal the server still reads — has to
        // produce a statement on the very same context.
        transaction.Update(
            accountId,
            -19.99m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            SealedNarrative.Description("Corner shop"));
        int realChange = await db.SaveChangesAsync();

        // Assert — the count of writes is the load-bearing line. Exactly one reached the table and it is
        // the control's; the restatement contributed none. The comparer missing turns the restatement
        // into a second UPDATE and this becomes two — and it would also add `description` to the
        // control's statement, which is what the column assertion catches.
        await Assert.That(restatement).IsEqualTo(0);
        await Assert.That(realChange).IsEqualTo(1);
        IReadOnlyList<string> writes = recorder.WritesTo("transactions");
        await Assert.That(writes.Count).IsEqualTo(1);
        await Assert.That(UpdatedColumns(writes[0])).IsEqualTo("amount");
    }

    [Test]
    public async Task Saving_ANewNote_NamesOnlyTheDescription()
    {
        // Arrange — the mirror: the note genuinely moves while every other editable field is restated.
        // Account, amount and date are handed back exactly as the row holds them, so a statement naming
        // any of them would be a defect in a comparer this file is not about — and the assertion below
        // would say which, by name.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        var transactionId = Guid.CreateVersion7();
        Guid accountId = await SeedAsync(options, budgetId, transactionId);

        StatementRecorder recorder = new();
        await using BudgetoidDbContext db = new(
            CreateOptions(host, recorder), new TestBudgetContext(budgetId));
        Transaction transaction = await db.Transactions.SingleAsync(row => row.Id == transactionId);

        // Act
        transaction.Update(
            accountId,
            -42.50m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            SealedNarrative.Description("Coffee run"));
        int affected = await db.SaveChangesAsync();

        // Assert — one row and one column. The affected count is the non-vacuity: a case asserting only
        // that amount and date are absent would pass on a save that wrote nothing at all.
        await Assert.That(affected).IsEqualTo(1);
        IReadOnlyList<string> writes = recorder.WritesTo("transactions");
        await Assert.That(writes.Count).IsEqualTo(1);
        await Assert.That(UpdatedColumns(writes[0])).IsEqualTo("description");
    }

    /// <summary>
    /// An account and one transaction on it, written on a context of their own. Returns the account's
    /// identifier, which both cases restate so that <c>account_id</c> never appears in a statement.
    /// </summary>
    /// <remarks>
    /// The transaction is seeded WITH a note on purpose. Seeded NULL, the description property holds no
    /// instance for a reference comparison to get wrong, and both cases would be green under the very
    /// mutation they exist for.
    /// </remarks>
    private static async Task<Guid> SeedAsync(
        DbContextOptions<BudgetoidDbContext> options,
        Guid budgetId,
        Guid transactionId)
    {
        await using BudgetoidDbContext seed = new(options, new TestBudgetContext(budgetId));
        Account account = Account.Create(
            Guid.CreateVersion7(),
            budgetId,
            SealedNarrative.Indexed("Checking"),
            AccountType.Checking,
            0m,
            "USD",
            UsdMinorUnit,
            UtcNow());
        seed.Accounts.Add(account);
        await seed.SaveChangesAsync();

        seed.Transactions.Add(Transaction.Create(
            transactionId,
            budgetId,
            account.Id,
            -42.50m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            SealedNarrative.Description("Corner shop"),
            UtcNow()));
        await seed.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>
    /// The columns <paramref name="statement" />'s <c>SET</c> clause assigns to, ordered and joined, or
    /// the whole statement back when it carries no such clause.
    /// </summary>
    /// <remarks>
    /// Parsed as assignments rather than searched for as words, for the reason
    /// <see cref="CategoryChangeTrackingTests" /> gives about <c>name_key</c> containing <c>name</c>.
    /// The trap does not arise on this table — there is no column here whose name is a prefix of
    /// another's — but the helper is the siblings' and is kept identical rather than simplified,
    /// because a reader comparing the three files should find one instrument and not two.
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
    private static DateTime UtcNow() => new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
