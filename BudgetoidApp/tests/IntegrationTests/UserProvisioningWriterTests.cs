using System.Net;
using Api.Infrastructure;
using Application.Users.EnsureUser;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Which collaborator user provisioning names the request's identity and tenant through.
/// </summary>
/// <remarks>
/// <para>
/// <c>IUserContextWriter</c> exists narrowed to the identity-mutating capability so that the capability
/// is visible in exactly one constructor, and <c>CurrentUserWriter.ResolveUser</c> carries the rule that
/// publishing a user clears the ambient budget — placed there, rather than at the caller, precisely so
/// that the next person to publish an identity cannot forget it. Neither claim survives a second writer.
/// <c>UserProvisioningMiddleware</c> takes the scoped <see cref="CurrentUser" /> and assigns both fields
/// itself, so the rule holds on that path by the middleware's own construction rather than by the
/// writer's.
/// </para>
/// <para>
/// Nothing is exploitable today — the middleware sets both fields in one helper, always together. What
/// is missing is the <em>enforcement</em>: any later edit that publishes an identity on that path
/// without a budget beside it reinstates the "user of one account, budget of another" pairing, and every
/// test in the suite stays green, because the writer's own tests only exercise the writer.
/// <c>budget_isolation</c> is <c>FOR ALL</c>, so the first budget-scoped statement under such a pairing
/// is scoped to a stranger, matches nothing and reports success — a data-loss report nobody files.
/// </para>
/// <para>
/// <b>Why this asserts on a collaboration rather than on state.</b> The resulting state is already
/// correct and would be correct either way, so a state assertion cannot tell the two designs apart. The
/// collaboration <em>is</em> the rule being pinned: that provisioning goes through the one writer, which
/// is what makes the clearing rule apply to it. This is the narrow case where verifying an interaction
/// is the honest test rather than the brittle one.
/// </para>
/// </remarks>
public sealed class UserProvisioningWriterTests
{
    [Test]
    public async Task Provisioning_PublishesTheBudgetThroughTheUserContextWriter()
    {
        // Arrange — a subject the product has never seen, so this request runs the whole minting path.
        const string subject = "google-writer-observed";
        PublicationLog log = new();
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using ApiFactory factory = host.CreateFactory(configureServices: services =>
            services.Replace(ServiceDescriptor.Scoped<IUserContextWriter>(provider =>
                new SpyingUserContextWriter(
                    // The real writer underneath, resolved rather than newed up so this test does not
                    // own its constructor.
                    ActivatorUtilities.CreateInstance<CurrentUserWriter>(provider),
                    log))));
        HttpClient client = factory.CreateAuthenticatedClient(subject);

        // Act — the first authenticated request, on the one route group allowed to bring an account into
        // existence. Driven through the real pipeline rather than by calling the middleware directly:
        // what is under test is which collaborator the composed application publishes through.
        HttpResponseMessage response = await client.GetAsync(ApiFactory.AccountProvisioningPath);

        // Assert — the request really worked first. A pipeline that failed halfway would publish part of
        // the pair and the sequence assertion below would then be measuring the failure.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Both members arrived, and in that order. The tail rather than the whole sequence: provisioning
        // publishes the identity from inside the handlers too — once, or twice on the conflict path — and
        // pinning that count here would make this test fail on a change to a file it is not about.
        //
        // The order is the load-bearing half. ResolveUser clears the ambient budget, so a budget named
        // before it is a budget the rest of the request does not have, and every budget-scoped statement
        // below would meet an unresolved budget.
        await Assert.That(string.Join(", ", log.Publications.TakeLast(2).Select(publication => publication.Member)))
            .IsEqualTo("ResolveUser, ResolveBudget");

        // And the pair names one account rather than two. Read on the container superuser, because
        // user_isolation is FOR ALL and a policed connection reports another account's row exactly as it
        // reports a missing one.
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await ReadUserIdAsync(admin, subject);
        Guid budgetId = await ReadBudgetIdAsync(admin, userId);

        Publication[] pair = [.. log.Publications.TakeLast(2)];
        await Assert.That(pair[0].Value).IsEqualTo(userId);
        await Assert.That(pair[1].Value).IsEqualTo(budgetId);
    }

    /// <summary>One call into <c>IUserContextWriter</c>: which member, and the id it was handed.</summary>
    private sealed record Publication(string Member, Guid Value);

    /// <summary>
    /// Every publication the request made, oldest first.
    /// </summary>
    /// <remarks>
    /// Owned by the test rather than registered in the container, because the writer is scoped and the
    /// scope dies with the request: a log resolved from the container would be gone by the time the
    /// assertions run. No synchronisation — one request, one scope, and its publications are sequential.
    /// </remarks>
    private sealed class PublicationLog
    {
        private readonly List<Publication> _publications = [];

        public IReadOnlyList<Publication> Publications => _publications;

        public void Record(string member, Guid value) => _publications.Add(new Publication(member, value));
    }

    /// <summary>
    /// Records every publication and passes it straight on to the real writer.
    /// </summary>
    /// <remarks>
    /// A spy rather than a stub, and it has to be. The identity published here is what reaches
    /// <c>app.current_user_id</c> on the next connection open, and the <c>users</c> INSERT is checked
    /// against it: a writer that recorded and discarded would have the minting request refused by its own
    /// <c>WITH CHECK</c> before the middleware ever reached the line under test, leaving this test red
    /// for a reason that has nothing to do with the rule it states.
    /// </remarks>
    private sealed class SpyingUserContextWriter(IUserContextWriter inner, PublicationLog log)
        : IUserContextWriter
    {
        public void ResolveUser(Guid userId)
        {
            log.Record(nameof(ResolveUser), userId);
            inner.ResolveUser(userId);
        }

        public void ResolveBudget(Guid budgetId)
        {
            log.Record(nameof(ResolveBudget), budgetId);
            inner.ResolveBudget(budgetId);
        }
    }

    /// <summary>
    /// The account provisioning minted for <paramref name="subject" />. Nothing the API returns names it,
    /// so the lookup goes through the credential the middleware resolved on.
    /// </summary>
    private static async Task<Guid> ReadUserIdAsync(NpgsqlConnection connection, string subject)
    {
        await using NpgsqlCommand command = new(
            "select user_id from credentials where provider = 'google' and subject = @subject",
            connection);
        command.Parameters.AddWithValue("subject", subject);

        return await ReadGuidAsync(command, $"no account for subject '{subject}'");
    }

    /// <summary>
    /// The default budget that account owns. Filtered by owner rather than taken as "the only budget
    /// there is": the claim is that the published pair belongs to one account, and a bare <c>select id
    /// from budgets</c> would hold for any budget the run happened to leave behind.
    /// </summary>
    private static async Task<Guid> ReadBudgetIdAsync(NpgsqlConnection connection, Guid userId)
    {
        await using NpgsqlCommand command = new("select id from budgets where user_id = @userId", connection);
        command.Parameters.AddWithValue("userId", userId);

        return await ReadGuidAsync(command, $"no budget for user '{userId}'");
    }

    /// <summary>
    /// Reads a single id, refusing anything else. Pattern-matched rather than cast-and-null-forgive: an
    /// empty result means provisioning wrote nothing, and that should fail here naming what was missing
    /// rather than at an assertion comparing against a default.
    /// </summary>
    private static async Task<Guid> ReadGuidAsync(NpgsqlCommand command, string absence) =>
        await command.ExecuteScalarAsync() switch
        {
            Guid id => id,
            var unexpected => throw new InvalidOperationException(
                $"Expected an id — {absence}, got '{unexpected ?? "null"}'."),
        };
}
