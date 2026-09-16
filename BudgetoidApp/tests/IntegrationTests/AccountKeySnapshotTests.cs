using Application.AccountKeys;
using Infrastructure.Persistence;
using Infrastructure.ReadServices;
using Microsoft.EntityFrameworkCore;

namespace IntegrationTests;

/// <summary>
/// That an account's manifest and its factors come off <b>one</b> SQL statement, and therefore off one
/// snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>The property the whole response shape exists for, and the only file that can see it.</b>
/// <c>AccountKeyCustody</c> carries the manifest and the factor list on one value because the client
/// compares them: a manifest names a set of factor public keys, the factor list names a set of
/// factors, and a disagreement between the two is what the browser reports to a person as
/// <em>tampering</em>. That comparison is only meaningful if both halves describe one instant. Two
/// awaited queries on one connection are two autocommitted statements taking two
/// <c>READ COMMITTED</c> snapshots, so an enrolment committing between them produces a correct
/// manifest beside a correct factor list that disagree — and the person who was merely enrolling a
/// factor is shown an accusation. <b>Splitting the read into two awaits passes every other test in
/// this suite</b>, which is the gap this file closes.
/// </para>
/// <para>
/// <b>Read through the read service rather than over HTTP, and that is the opposite of
/// <see cref="AccountKeysEndpointTests" />'s rule for a reason.</b> That file measures which account a
/// request arrives as, so it addresses the route and reads the wire body and deliberately names no
/// type belonging to this endpoint. What is measured here is the statement PostgreSQL was handed,
/// which no response body can show: the same two rows arrive on the wire identically whether they came
/// off one statement or two. The subject is the SQL, so the subject has to be the thing that composes
/// it — and a file measuring SQL belongs beside the change-tracking files that already do, not inside
/// the one whose first paragraph forbids naming a read service.
/// </para>
/// <para>
/// <b>The connection is the container superuser</b>, for the reason
/// <c>CategoryChangeTrackingTests</c> gives: these are claims about the statement EF composes, which is
/// decided before any privilege or policy is consulted. Row-level security appends its predicate
/// underneath, changes no count, and would only make the arrangement harder to reach.
/// </para>
/// </remarks>
public sealed class AccountKeySnapshotTests
{
    /// <summary>
    /// The read issues exactly one <c>SELECT</c>, and that one statement names both tables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both halves, and neither is the interesting one alone.</b> A count of one, on its own, is
    /// satisfied by a read that dropped the manifest entirely — one statement over
    /// <c>wrapped_account_keys</c> and nothing else. Two table names, on their own, are satisfied by
    /// two statements that between them mention both. Together they say the one thing the design rests
    /// on: both levels were resolved against a single snapshot.
    /// </para>
    /// <para>
    /// <b>Counted over <c>SELECT</c>s rather than over every command, deliberately.</b> A context that
    /// opens a connection may issue setup statements of its own, and a test written against the raw
    /// total would go red on a change that has nothing to do with this read. What the argument is about
    /// is how many times this query asks the two tables.
    /// </para>
    /// <para>
    /// <b>The account is populated on both levels</b> — a manifest and several factors — or the
    /// statement EF composes is not the statement production composes. An empty arrangement would let a
    /// provider answer from a plan this test never meant to measure, and the table-name assertions would
    /// be read off SQL nobody exercised.
    /// </para>
    /// <para>
    /// The seeded factors are several rather than one, because the cartesian shape this read accepts —
    /// the manifest repeated once per factor row in the outer projection — only exists above one row,
    /// and a reader arriving here from the read service's own remarks about that cost should find an
    /// arrangement where it happens.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ListForAccount_AsksThePostgresServerOnce_NamingBothTables()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        SeededAccount account = await SeedAccountAsync(host, "google-account-key-snapshot");

        StatementRecorder recorder = new();
        await using BudgetoidDbContext db = CreateDb(host, account.BudgetId, recorder);

        // Act
        AccountKeyCustody custody = await new AccountKeyReadService(db)
            .ListForAccountAsync(account.UserId);

        // Assert — the arrangement really is populated on both levels, or every claim below is about a
        // statement that was asked nothing.
        await Assert.That(custody.Manifest.HasValue).IsTrue();
        await Assert.That(custody.Factors.Count).IsEqualTo(SeededFactorCount);

        // Exactly one read reached the server.
        IReadOnlyList<string> reads = ReadsOf(recorder);
        await Assert.That(reads.Count).IsEqualTo(1);

        // And it is the read that answers both levels. Asserted on the one statement rather than over
        // the whole transcript, so two statements each naming one table cannot satisfy it.
        await Assert.That(reads[0]).Contains(ManifestTable);
        await Assert.That(reads[0]).Contains(FactorTable);
    }

    /// <summary>
    /// Still one <c>SELECT</c> when the context is configured to split every query it can — which is
    /// the only thing holding <c>AsSingleQuery</c> in the read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured, not feared.</b> Adding
    /// <c>UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)</c> to the options this context
    /// is built from — one line on the <c>AddDbContext</c> in <c>Api/Program.cs</c>, the ordinary way a
    /// solution adopts split queries — makes EF issue this read as <b>two</b> commands against a seeded
    /// account: the first carrying <c>users</c> and the manifest lateral, the second re-selecting the
    /// same user row and joining the factors. Two commands are two autocommitted statements taking two
    /// snapshots, which is precisely the window the case above says one statement closes. The read
    /// carries <c>AsSingleQuery</c>, the per-query override that survives the global setting, and
    /// <b>deleting that one call reddens nothing else anywhere</b>: the case above passes either way
    /// under today's configuration, because today nothing sets the global behaviour.
    /// </para>
    /// <para>
    /// <b>The control is what makes this a measurement rather than a pin, and it is not decoration.</b>
    /// A configuration line that silently did nothing — a builder method that stopped applying, an
    /// option overridden downstream, a provider that ignores it — would leave this case green over a
    /// read that had lost its override. So the same shape of query is issued <em>without</em> the
    /// override on the very same context, and it has to split. If that control ever stops splitting,
    /// this case is no longer holding anything and says so by failing.
    /// </para>
    /// <para>
    /// <b>The control's query is a deliberate copy of the read's shape and must not be mistaken for a
    /// restatement of it.</b> It asserts nothing about the product. Its only job is to prove that split
    /// behaviour is in force on this context, so it needs to be <em>a</em> query EF would split, not
    /// <em>the</em> query under test — and it is written out here rather than obtained from the read
    /// service precisely because the read service is the thing that must not split.
    /// </para>
    /// <para>
    /// The control asserts "more than one" rather than "exactly two". Two is what was observed; what
    /// the control has to establish is that splitting happens at all, and an EF version that split into
    /// three would still be establishing it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ListForAccount_UnderAGlobalSplitQueryBehaviour_StillAsksOnce()
    {
        // Arrange — the same seeding, over options that ask for split queries everywhere.
        await using RepositoryTestHost host = await StartHostAsync();
        SeededAccount account = await SeedAccountAsync(host, "google-account-key-split");

        StatementRecorder control = new();
        await using (BudgetoidDbContext probe = CreateSplittingDb(host, account.BudgetId, control))
        {
            // The control: this context really does split a query of this shape when nothing overrides
            // it. Materialised rather than composed, or nothing is sent at all.
            _ = await probe.Users
                .AsNoTracking()
                .Where(user => user.Id == account.UserId)
                .Select(user => new
                {
                    Manifest = probe.FactorManifests
                        .Where(row => row.UserId == account.UserId)
                        .Select(row => new { row.Manifest, row.RotationEpoch })
                        .FirstOrDefault(),
                    Factors = probe.WrappedAccountKeys
                        .Where(keys => keys.UserId == account.UserId)
                        .OrderBy(keys => keys.FactorId)
                        .Select(keys => keys.FactorId)
                        .ToList(),
                })
                .FirstOrDefaultAsync();
        }

        await Assert.That(ReadsOf(control).Count).IsGreaterThan(1);

        StatementRecorder recorder = new();
        await using BudgetoidDbContext db = CreateSplittingDb(host, account.BudgetId, recorder);

        // Act
        AccountKeyCustody custody = await new AccountKeyReadService(db)
            .ListForAccountAsync(account.UserId);

        // Assert — populated on both levels first, for the reason the case above gives.
        await Assert.That(custody.Manifest.HasValue).IsTrue();
        await Assert.That(custody.Factors.Count).IsEqualTo(SeededFactorCount);

        IReadOnlyList<string> reads = ReadsOf(recorder);
        await Assert.That(reads.Count).IsEqualTo(1);
        await Assert.That(reads[0]).Contains(ManifestTable);
        await Assert.That(reads[0]).Contains(FactorTable);
    }

    /// <summary>The table the account's manifest lives in, as the statement spells it.</summary>
    private const string ManifestTable = "factor_manifests";

    /// <summary>The table the per-factor envelopes live in, as the statement spells it.</summary>
    private const string FactorTable = "wrapped_account_keys";

    /// <summary>
    /// How many factors the seeded account holds.
    /// </summary>
    /// <remarks>
    /// More than one, so the arrangement reaches the shape the read's own remarks argue about — the
    /// manifest repeated once per factor row — and so a projection that collapsed the list is visible
    /// in the count beside the statement claims. Fewer than a set of recovery codes, because nothing
    /// here is about how many rows a credential files.
    /// </remarks>
    private const int SeededFactorCount = 3;

    /// <summary>The manifest bytes the seeded account holds.</summary>
    /// <remarks>
    /// Distinct bytes at a width that is not round, for the reason <c>AccountKeysEndpointTests</c>
    /// argues about its own fixture: a value every implementation would render identically teaches a
    /// failure message nothing. Nothing here reads the bytes back, so the width is the only part that
    /// matters — it has to be a manifest <c>FactorManifest.For</c> accepts.
    /// </remarks>
    private static readonly byte[] SeededManifest =
        [0x4D, 0x41, 0x4E, 0x1F, 0x2E, 0x3D, 0x4C, 0x5B, 0x6A, 0x79, 0x88, 0x97, 0xA6];

    /// <summary>
    /// The generation the seeded manifest claims: neither 0, which is the absence of a row, nor 1,
    /// which is the floor a stored row may claim.
    /// </summary>
    private const int SeededRotationEpoch = 7;

    /// <summary>One furnished account: the owner, its budget, and the credential its factors hang off.</summary>
    private readonly record struct SeededAccount(Guid UserId, Guid BudgetId);

    /// <summary>
    /// An account holding a manifest and <see cref="SeededFactorCount" /> factors under one passkey.
    /// </summary>
    /// <remarks>
    /// A budget is seeded with the owner because <see cref="BudgetoidDbContext" /> is built against an
    /// ambient budget, not because anything here is budget-owned: <c>factor_manifests</c> and
    /// <c>wrapped_account_keys</c> are both policed on the user. The factors are filed one call at a
    /// time, because <c>PK_wrapped_account_keys</c> is the factor id and each row needs its own.
    /// </remarks>
    private static async Task<SeededAccount> SeedAccountAsync(
        RepositoryTestHost host,
        string googleSubject)
    {
        // Seeded WITHOUT the default manifest, because the one this method files a few lines down is
        // the row these tests read: SeededManifest is a value this file chose, and user_id is the
        // primary key, so a default row would refuse the insert below with a 23505.
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(
            googleSubject, $"{googleSubject}@example.com", withFactorManifest: false);
        Guid credentialId = await host.SeedPasskeyAsync(owner.UserId, WebAuthnCredentialId);

        for (int index = 0; index < SeededFactorCount; index++)
        {
            await host.SeedWrappedAccountKeysAsync(credentialId, Guid.CreateVersion7());
        }

        await RepositoryTestHost.SeedFactorManifestOnAsync(
            host.ConnectionString, owner.UserId, SeededManifest, SeededRotationEpoch);

        return new SeededAccount(owner.UserId, owner.BudgetId);
    }

    /// <summary>
    /// The handle an authenticator is known by. Fixed rather than random: one account is seeded per
    /// test host, so nothing here can collide, and a fixed value keeps a failure reproducible.
    /// </summary>
    private static readonly byte[] WebAuthnCredentialId =
        [0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A];

    /// <summary>
    /// Every <c>SELECT</c> the recorder saw, with anything else dropped.
    /// </summary>
    /// <remarks>
    /// A prefix test on the trimmed text, the shape <see cref="StatementRecorder.WritesTo" /> keeps for
    /// the other verbs: a statement is a read because it <em>starts</em> with the verb, never because
    /// the word appears somewhere inside it.
    /// </remarks>
    private static IReadOnlyList<string> ReadsOf(StatementRecorder recorder) =>
    [
        .. recorder.Statements.Where(statement =>
            statement.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)),
    ];

    private static BudgetoidDbContext CreateDb(
        RepositoryTestHost host,
        Guid budgetId,
        StatementRecorder recorder) =>
        new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.ConnectionString)
                .AddInterceptors(recorder)
                .Options,
            new TestBudgetContext(budgetId));

    /// <summary>
    /// The same context, built the way a solution that had adopted split queries globally would build
    /// it.
    /// </summary>
    /// <remarks>
    /// This is the configuration the product does not have and could acquire in one line. Nothing here
    /// argues for adopting it — the point is that the read has to survive somebody else doing so.
    /// </remarks>
    private static BudgetoidDbContext CreateSplittingDb(
        RepositoryTestHost host,
        Guid budgetId,
        StatementRecorder recorder) =>
        new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(
                    host.ConnectionString,
                    npgsql => npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .AddInterceptors(recorder)
                .Options,
            new TestBudgetContext(budgetId));

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();

        return host;
    }
}
