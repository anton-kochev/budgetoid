using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// That a <b>zero-initialised</b> credential is a row the database refuses — the state a cleared buffer,
/// a struct default, or a deserializer that never saw a <c>type</c> member leaves behind.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim is about the shape the default member permits, never about which member happens to be
/// declared first.</b> A test asserting <c>default(CredentialType) == CredentialType.Federated</c> would
/// pin an ordinal, teach a reader nothing about why the order was chosen, and go red on a reordering that
/// preserved the property it was meant to protect. What is actually bought is fail-closed behaviour: the
/// value nobody chose has to describe a row nothing will store, so a credential that came into being by
/// accident cannot reach the table wearing a shape the schema finds acceptable.
/// </para>
/// <para>
/// <b>Both halves of the probe row come from production rather than from this file.</b> The
/// <c>type</c> column carries whatever <c>CredentialConfiguration</c>'s own value converter spells for
/// <c>default(CredentialType)</c> — reached through the built EF model, never copied — and
/// <c>(provider, subject)</c> are <see langword="null" /> because that is what a freshly constructed
/// <c>Credential</c> carries: both are reference types with no initializer, so a zero-initialised
/// instance has nothing else it could hold. Nothing here names a member, and that is what keeps the test
/// silent about the ordinal.
/// </para>
/// <para>
/// <b>No control row is written, and that is deliberate rather than an omission.</b> The refusal is
/// pinned to a named constraint, so every other way this statement can fail reports itself differently: a
/// missing account is <c>23503</c>, a mistyped column is <c>42703</c>, a spelling outside the vocabulary
/// is <c>CK_credentials_type</c> rather than <c>CK_credentials_type_shape</c>. A test that merely
/// expected "some refusal" would need a control; one that names the constraint is its own.
/// </para>
/// <para>
/// The account is a bare <c>users</c> row written with raw SQL on the container superuser, deliberately
/// without the federated credential a real account holds. That row would occupy
/// <c>IX_credentials_user_id_federated</c>, and a probe refused by a unique index instead of by the shape
/// check would be measuring the wrong rule. Nothing in this file signs in, so the account needs no
/// credential to be resolvable.
/// </para>
/// </remarks>
public sealed class CredentialTypeDefaultTests
{
    /// <summary>
    /// The constraint that says what shape a credential of a given type may take. Restated here rather
    /// than referenced, so that one defect reports one name: a test reading the same constant the schema
    /// was rendered from would agree with itself no matter what either said.
    /// </summary>
    private const string TypeShapeConstraintName = "CK_credentials_type_shape";

    [Test]
    public async Task Database_RefusesAZeroInitialisedCredential()
    {
        // Arrange — a bare account, and the row a Credential nobody initialised would carry: the default
        // type as the schema spells it, and no issuer of any kind.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await InsertUserRowAsync(admin, "person@example.com");
        string defaultSpelling = ColumnSpellingOf(default);

        // Act
        PostgresException? refusal = await RefusalOfAsync(
            BuildCredentialInsert(admin, userId, defaultSpelling));

        // Assert — that a refusal arrived at all is asserted first and on its own, because a database
        // that ACCEPTED this row is the defect, and reading that outcome as a null-reference further
        // down would bury it.
        await Assert.That(refusal).IsNotNull();

        // Then which rule refused it. The shape check is the one that has to: it is the only constraint
        // that reads type together with provider and subject, and therefore the only one that can say
        // the default member describes a row this schema will not store.
        await Assert.That(refusal?.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal?.ConstraintName).IsEqualTo(TypeShapeConstraintName);

        // And nothing landed. A refusal that still wrote a row would be a rollback nobody performed.
        await Assert.That(await CountCredentialsAsync(admin, userId)).IsEqualTo(0L);
    }

    /// <summary>
    /// The <c>credentials.type</c> spelling <c>CredentialConfiguration</c> writes for
    /// <paramref name="type" />.
    /// </summary>
    /// <remarks>
    /// Reached through the built EF model rather than copied into this file: a copy would be a second
    /// opinion about the column, and the probe below is only meaningful if the value it carries is the
    /// one production would have written. Building the model opens no connection — the connection string
    /// exists only because the provider insists on one.
    /// </remarks>
    private static string ColumnSpellingOf(CredentialType type) =>
        TypeColumnConverter.ConvertToProvider(type) as string
        ?? throw new InvalidOperationException(
            $"The credentials.type converter produced no string for {nameof(CredentialType)}.{type}.");

    private static readonly ValueConverter TypeColumnConverter = BuildTypeColumnConverter();

    private static ValueConverter BuildTypeColumnConverter()
    {
        using BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql("Host=localhost;Port=5432;Database=budgetoid;Username=postgres;Password=postgres")
                .Options);

        IProperty type = db.Model.FindEntityType(typeof(Credential))!.FindProperty(nameof(Credential.Type))!;

        return type.GetValueConverter()
            ?? throw new InvalidOperationException(
                "credentials.type carries no value converter, so nothing here can ask the schema how it "
                + "spells a CredentialType member.");
    }

    /// <summary>
    /// Fixed UTC instant for the rows this file writes. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The credential a zero-initialised <c>Credential</c> would write: the default type, and both issuer
    /// columns null because a reference type with no initializer has nothing else to be.
    /// </summary>
    private static NpgsqlCommand BuildCredentialInsert(
        NpgsqlConnection admin,
        Guid userId,
        string columnValue)
    {
        NpgsqlCommand command = new(
            """
            insert into credentials (id, user_id, type, provider, subject, created_at_utc)
            values (@id, @user_id, @type, null, null, @created_at_utc)
            """,
            admin);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("type", columnValue);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        return command;
    }

    /// <summary>
    /// Runs one statement and hands back the refusal it raised, or <see langword="null" /> when it landed.
    /// </summary>
    /// <remarks>
    /// Null rather than a throw on the accepting path, unlike the <c>RefusalOfAsync</c> helpers elsewhere
    /// in this suite. A database that accepts this row is exactly the outcome under test, so it has to
    /// reach an assertion that can name it rather than escape as a helper's own exception.
    /// </remarks>
    private static async Task<PostgresException?> RefusalOfAsync(NpgsqlCommand command)
    {
        await using (command)
        {
            try
            {
                await command.ExecuteNonQueryAsync();
            }
            catch (PostgresException refusal)
            {
                return refusal;
            }
        }

        return null;
    }

    private static async Task<Guid> InsertUserRowAsync(NpgsqlConnection admin, string email)
    {
        Guid userId = Guid.CreateVersion7();
        await using NpgsqlCommand command = new(
            """
            insert into users (id, email, created_at_utc)
            values (@id, @email, @created_at_utc)
            """,
            admin);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        await command.ExecuteNonQueryAsync();
        return userId;
    }

    private static async Task<long> CountCredentialsAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select count(*) from credentials where user_id = @userId",
            admin);
        command.Parameters.AddWithValue("userId", userId);

        // Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the query
        // changed shape, and that should fail loudly here instead of at the assertion.
        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count of credentials, got '{unexpected ?? "null"}'."),
        };
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
