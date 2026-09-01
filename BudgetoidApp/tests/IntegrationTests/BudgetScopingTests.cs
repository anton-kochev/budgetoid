using Domain.Accounts;
using Domain.CategoryGroups;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// Real-PostgreSQL behaviour for the two rules the budget re-scope moves off the user: name
/// uniqueness and ordering are per budget.
/// </summary>
/// <remarks>
/// <para>
/// Deliberate coverage judgement: uniqueness is proven end-to-end for <c>Account</c> only. Category
/// groups, categories and payees carry an identical-in-shape <c>(BudgetId, Name)</c> unique index, so
/// <c>Model_ScopesNameUniquenessToTheBudget</c> covers them and a fourth Testcontainer would buy
/// nothing. This is not an oversight.
/// </para>
/// <para>
/// <b>"Byte-for-byte identical" and "on the same collation" have both stopped being true of
/// <c>Account</c>, which is why they are gone from the sentence above.</b> Those three tables still
/// index the name COLUMN under <c>case_insensitive</c>; accounts indexes <c>name_key</c>, a blind
/// index, and carries no collation at all because <c>bytea</c> cannot. The judgement survives the
/// difference — the rule being proven is still "one name per budget", and the model test still covers
/// the other three — but the two claims about sameness did not, and leaving them in would have made
/// this remark evidence for a schema it had stopped describing.
/// </para>
/// </remarks>
public sealed class BudgetScopingTests
{
    /// <summary>
    /// Minor unit of the USD accounts these tests seed. Precision is not what any of them is
    /// about; the constant keeps a bare <c>2</c> from reading as a rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    [Test]
    public async Task Accounts_WithTheSameNameInDifferentBudgets_BothPersist()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetA = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid budgetB = await host.SeedBudgetAsync("google-b", "b@example.com");

        // Act
        await using (BudgetoidDbContext dbA = CreateDb(host, budgetA))
        {
            dbA.Accounts.Add(CreateAccount(budgetA, "Checking"));
            await dbA.SaveChangesAsync();
        }

        await using BudgetoidDbContext dbB = CreateDb(host, budgetB);
        dbB.Accounts.Add(CreateAccount(budgetB, "Checking"));
        await dbB.SaveChangesAsync();

        // Assert
        await Assert.That(await dbB.Accounts.CountAsync()).IsEqualTo(1);
        await Assert.That(await CountAccountsAsync(host)).IsEqualTo(2L);
    }

    /// <summary>
    /// Two accounts whose names index alike cannot both live in one budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This case used to be spelled "Checking" against "checking", and that spelling has stopped
    /// being writable rather than having been simplified away.</b> The uniqueness rule is the same rule
    /// and it did not move layers — it moved COLUMNS, from <c>name</c> to <c>name_key</c>, because
    /// <c>accounts.name</c> is a sealed narrative field now and every seal draws a fresh nonce, so two
    /// rows holding one name hold different bytes. An index left on the envelope would exist, be
    /// unique, be rendered by <c>pg_get_indexdef</c>, and refuse nothing.
    /// </para>
    /// <para>
    /// <b>What DID leave this server is the case folding, and saying so is the point of this remark.</b>
    /// The <c>case_insensitive</c> collation went with the column type — <c>bytea</c> is not collatable
    /// — and the folding is now part of the normalisation the client applies before it computes the
    /// HMAC. This side cannot check that it happened: an index is a digest under a key that lives in a
    /// browser, so "Checking" and "checking" are the same account or two accounts entirely according to
    /// a step no constraint here can be written to. A reader who notices the old case-only case is gone
    /// must not restore it against the schema — it would seed two labels, get two digests, and assert a
    /// refusal that is now the client's to produce.
    /// </para>
    /// <para>
    /// So the honest subject left on this side is the one asserted below: EQUAL INDEX VALUES IN ONE
    /// BUDGET ARE REFUSED. The seeding uses one label twice rather than two, because
    /// <see cref="SealedNarrative.Indexed" /> is deterministic in its label and that determinism is the
    /// one property of a real blind index a fixture can reproduce. The two rows' envelopes are equal
    /// too, which is a fixture artefact and not the subject — the index is what the constraint is over,
    /// and <see cref="Accounts_WithTheSameNameInDifferentBudgets_BothPersist" /> next door is what says
    /// the refusal is scoped to the budget rather than global.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Accounts_WithTheSameBlindIndexInOneBudget_AreRejected()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        await using BudgetoidDbContext db = CreateDb(host, budgetId);
        db.Accounts.Add(CreateAccount(budgetId, "Checking"));
        await db.SaveChangesAsync();

        // Act
        db.Accounts.Add(CreateAccount(budgetId, "Checking"));
        DbUpdateException? caught = null;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException exception)
        {
            caught = exception;
        }

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That((caught!.InnerException as PostgresException)?.SqlState)
            .IsEqualTo(PostgresErrorCodes.UniqueViolation);

        // THE CONSTRAINT NAME, not merely the SQLSTATE, and it is what keeps this case from passing on
        // the wrong refusal. 23505 is the answer to every unique violation on this table, including the
        // primary key and AK_accounts_id_budget_id — either of which a seeder that reused an id would
        // trip, with the same code, while the blind index was enforcing nothing at all.
        //
        // A LITERAL rather than AccountConfiguration.NameIndexName, for the reason
        // RepositoryConstraintAttributionTests states about the same string: that constant is what
        // AccountRepository matches PostgresException.ConstraintName against to decide whether a 23505
        // is the collision it models, so a test reading it agrees with the production filter by
        // construction and cannot see the two spellings drift apart.
        await Assert.That((caught.InnerException as PostgresException)?.ConstraintName)
            .IsEqualTo("IX_accounts_budget_id_name_key");
    }

    [Test]
    public async Task CategoryGroups_PositionsAreIndependentPerBudget()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetA = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid budgetB = await host.SeedBudgetAsync("google-b", "b@example.com");

        await using (BudgetoidDbContext dbA = CreateDb(host, budgetA))
        {
            dbA.CategoryGroups.Add(CategoryGroup.Create(budgetA, "Essentials", null, 0, UtcNow()));
            dbA.CategoryGroups.Add(CategoryGroup.Create(budgetA, "Lifestyle", null, 1, UtcNow()));
            await dbA.SaveChangesAsync();
        }

        // Act — budget B starts its own sequence at 0; positions in budget A must not interfere.
        await using BudgetoidDbContext dbB = CreateDb(host, budgetB);
        dbB.CategoryGroups.Add(CategoryGroup.Create(budgetB, "Essentials", null, 0, UtcNow()));
        await dbB.SaveChangesAsync();
        List<CategoryGroup> groupsOfB = await dbB.CategoryGroups
            .OrderBy(group => group.Position)
            .ToListAsync();

        // Assert
        await Assert.That(groupsOfB.Count).IsEqualTo(1);
        await Assert.That(groupsOfB[0].Position).IsEqualTo(0);
        await Assert.That(groupsOfB[0].BudgetId).IsEqualTo(budgetB);

        await using BudgetoidDbContext dbA2 = CreateDb(host, budgetA);
        List<int> positionsOfA = await dbA2.CategoryGroups
            .OrderBy(group => group.Position)
            .Select(group => group.Position)
            .ToListAsync();
        await Assert.That(positionsOfA).IsEquivalentTo(new[] { 0, 1 });
    }

    private static Account CreateAccount(Guid budgetId, string name) => Account.Create(
        Guid.CreateVersion7(),
        budgetId,
        SealedNarrative.Indexed(name),
        AccountType.Checking,
        0m,
        "USD",
        UsdMinorUnit,
        UtcNow());

    private static async Task<long> CountAccountsAsync(RepositoryTestHost host)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("select count(*) from accounts", connection);
        object? scalar = await command.ExecuteScalarAsync();

        return scalar is long count
            ? count
            : throw new InvalidOperationException($"Expected a count, got '{scalar ?? "null"}'.");
    }

    private static BudgetoidDbContext CreateDb(RepositoryTestHost host, Guid budgetId) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options,
        new TestBudgetContext(budgetId));

    private static DateTime UtcNow() =>
        new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
