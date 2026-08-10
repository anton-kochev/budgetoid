using Domain.Common;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// What <see cref="PasskeyRepository" /> answers when the passkey it was handed is no longer in the
/// table — the one failure of the delete that is a statement about the resource rather than a fault.
/// </summary>
/// <remarks>
/// <para>
/// Turning a persistence failure into a domain answer is Infrastructure's job, and this is the lowest
/// layer that can be measured doing it. <c>UserRepository.DeleteAsync</c> and
/// <c>TransactionRepository.DeleteAllForAmbientBudgetAsync</c> already catch the same EF exception in
/// the same place, and <see cref="UserRepositoryTests" /> is where that catch is tested; this file is
/// the passkey equivalent, written to the same shape rather than to a second one.
/// </para>
/// <para>
/// It cannot be written above this layer, and the attempt is what makes the placement worth stating.
/// Naming <c>DbUpdateConcurrencyException</c> in a handler puts the EF assembly on
/// <c>Application.csproj</c> — a reference pointing the wrong way down a dependency direction that
/// runs <c>Infrastructure → Application</c> — and a fake standing in for
/// <see cref="IPasskeyRepository" /> could only raise that type by modelling something the port never
/// surfaces. What the port promises is the line below: a credential that is already gone raises
/// <see cref="NotFoundException" />.
/// </para>
/// <para>
/// Every row is written and removed on <see cref="RepositoryTestHost.ConnectionString" /> — the
/// container superuser. What is measured here is the repository's own translation, not the isolation
/// policies; <c>RlsIsolationTests</c> owns those.
/// </para>
/// </remarks>
public sealed class PasskeyRepositoryTests
{
    [Test]
    public async Task DeletePasskeyAsync_WhenTheRowIsAlreadyGone_ThrowsNotFound()
    {
        // Arrange — a real passkey, read back through FindPasskeyCredentialAsync, the one query that
        // materialises a Credential the table actually holds and which names the owner and the type in
        // its predicate, and then removed out of band on a separate connection. That lookup is not the
        // only way to obtain a Credential: CreateFederated and CreatePasskey are both public. Each of
        // them mints its own Guid.CreateVersion7() though, so a fabricated instance names no existing
        // row — which leaves the guarantee at "naming an existing row of the caller's choosing takes a
        // new query on PasskeyRepository", a rule review enforces over one class rather than something
        // the type prevents. It has to be enforced, because credentials is exempt from row-level
        // security and this predicate is the entire scope of the delete below.
        //
        // The arrangement here is that distinction made concrete, and worth reading as one: the scoped
        // lookup yields a credential naming a real row, the out-of-band delete takes the row away, and
        // what is left is a tracked entity naming a row that no longer exists — the state an unscoped
        // source would hand the delete for free. It is also precisely what a lost delete race leaves
        // behind: both requests resolve the
        // credential, both clear the last-passkey floor, and the loser's DELETE matches zero rows
        // where EF expected one. No race is arranged and none should be — interleaving two
        // transactions at a chosen statement buys a timing-dependent test for a branch whose entire
        // input is this state, and a flaky assertion about a rare path is worse than none.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId);
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new PasskeyRepository(db);
        Credential credential = await repository.FindPasskeyCredentialAsync(credentialId, userId)
            ?? throw new InvalidOperationException(
                "The seeded passkey was not readable through the repository before the act.");
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        int removedByTheWinner = await DeleteCredentialAsync(admin, credentialId);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.DeletePasskeyAsync(credential));

        // Assert — the premise first. A run in which the out-of-band delete matched nothing would be
        // arranging the opposite of this test, and could still go green against a repository that
        // threw for some entirely unrelated reason.
        await Assert.That(removedByTheWinner).IsEqualTo(1);

        // 404 is the honest answer, and it is a decision rather than a convenience. The row is gone,
        // which is what this route means by "not found", and it is the answer the caller would have
        // received a moment earlier had its own lookup run after the winner's delete instead of
        // before — so the two orderings of one pair of requests become indistinguishable, which is
        // what makes a client's retry safe. A retry would find nothing, so there is nothing left to
        // retry; a 409 would say the work could not be done and invite exactly that retry at work
        // already completed. Letting the DbUpdateConcurrencyException escape is worse than either: a
        // 500 logged as a fault, describing a removal that in fact succeeded.
        await Assert.That(escaped).IsTypeOf<NotFoundException>();
    }

    /// <summary>
    /// The WebAuthn handle the seeded passkey carries. Nothing here verifies a signature, so the
    /// bytes are arbitrary — but there are
    /// <see cref="PasskeyPublicKey.MinWebAuthnCredentialIdLength" /> of them, because a shorter handle
    /// is refused by the domain factory and the seed would fail before the act.
    /// </summary>
    private static readonly byte[] WebAuthnCredentialId =
        [.. Enumerable.Range(1, PasskeyPublicKey.MinWebAuthnCredentialIdLength).Select(value => (byte)value)];

    /// <summary>
    /// Removes one <c>credentials</c> row, standing in for the request that won the race.
    /// </summary>
    /// <remarks>
    /// On its own connection, and that is the whole point rather than tidiness: issued through the
    /// repository's own context it would travel inside that context's unit of work, where the DELETE
    /// under test could not fail to see it. Committed by another session is the only version of this
    /// state a real race produces. The dependent <c>passkey_public_keys</c> and
    /// <c>passkey_signature_counters</c> rows leave with it by the database's own cascade, exactly as
    /// they would have under the winner.
    /// </remarks>
    private static async Task<int> DeleteCredentialAsync(NpgsqlConnection admin, Guid credentialId)
    {
        await using NpgsqlCommand command = new("delete from credentials where id = @id", admin);
        command.Parameters.AddWithValue("id", credentialId);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Runs <paramref name="action" /> and hands back whatever escaped, or <see langword="null" />
    /// when nothing did. Deliberately untyped, as in <see cref="UserRepositoryTests" />: the question
    /// this file asks is <i>which</i> exception surfaces, so catching a specific one in the helper
    /// would decide the answer before the assertion reads it — and the failure this test is written
    /// to catch is a <c>DbUpdateConcurrencyException</c> arriving where a
    /// <see cref="NotFoundException" /> was promised.
    /// </summary>
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// Builds a context with no ambient budget, which is safe here because <c>Credential</c> carries
    /// no budget query filter — a credential belongs to a person, not to a tenant.
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
