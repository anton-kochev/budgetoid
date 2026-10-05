using System.Security.Cryptography;
using Domain.Sessions;
using Domain.Users;

namespace UnitTests.Fakes;

/// <summary>
/// Covers the behaviours of <see cref="InMemorySessionRepository"/> that the handler tests silently
/// lean on: the two <c>AuthenticateSessionHandlerTests</c> needs, and the sweep every establishing
/// handler's test reads through. Everything else about the fake is exercised through the handler
/// tests; these are not, and any of them would degrade without anything going red.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ordering probe is the reason this file exists.</b> A snapshot member that always reported the
/// published id — or that never reported anything at all and left the list empty — would make the
/// handler's ordering test agree with itself forever, which is the one failure mode of an ordering
/// assertion nobody notices. The test below drives the probe both ways round, so the value that stands
/// for <c>''::uuid</c> is shown to be reachable rather than assumed.
/// </para>
/// <para>
/// <c>InMemorySessionTokenRepository</c>'s own probe is the same three lines against the same recorder
/// and is not covered again here. What is not shared between them is the <em>reading</em> — empty is
/// the expected answer on the exempt table and the failure on the policed one — and that lives in each
/// test's assertion rather than in the fakes.
/// </para>
/// </remarks>
public sealed class InMemorySessionRepositoryTests
{
    /// <summary>
    /// That a session which has ended comes back rather than coming back as nothing.
    /// </summary>
    /// <remarks>
    /// Asserted here rather than left to fall out of the lookup, because the neighbouring
    /// <see cref="InMemorySessionRepository.RevokeAsync"/> narrows on exactly the opposite and a fake
    /// that copied its predicate would look perfectly reasonable. Under that fake the authentication
    /// handler would report an ended session as one nobody ever issued, and signing out twice would
    /// answer 401 with a dead cookie left on the client forever.
    /// </remarks>
    [Test]
    public async Task FindByIdAsync_WithARevokedSession_StillReturnsIt()
    {
        // Arrange
        var repository = new InMemorySessionRepository();
        Session session = SessionFor(Guid.CreateVersion7());
        session.Revoke(UtcNow());
        await repository.AddAsync(session);

        // Act
        Session? found = await repository.FindByIdAsync(session.Id);

        // Assert — the entity, with its revocation intact. Whether it is live is Session.IsActiveAt's
        // answer and stays in the domain; a repository that decided it would be a second reading of
        // "live" able to disagree with the domain's own.
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Id).IsEqualTo(session.Id);
        await Assert.That(found.RevokedAtUtc).IsEqualTo(UtcNow());
    }

    /// <summary>
    /// That the ordering probe reports the identity that was published <em>before</em> the read, and
    /// reports <see cref="Guid.Empty"/> when nothing had been.
    /// </summary>
    /// <remarks>
    /// Both directions in one test on purpose: each alone is satisfied by a probe stuck on the answer it
    /// happens to expect. <see cref="Guid.Empty"/> is not a placeholder for "unknown" — it is what an
    /// unset <c>app.current_user_id</c> reaches a <c>user_isolation</c> policy as, which raises
    /// <c>22P02</c> rather than matching nothing, so a read that snapshots it is a read that would have
    /// failed the request in production.
    /// </remarks>
    [Test]
    public async Task IdentityWhenFindByIdWasEntered_ReportsWhatWasPublishedBeforeEachRead()
    {
        // Arrange
        var userId = Guid.CreateVersion7();
        var writer = new RecordingUserContextWriter();
        var repository = new InMemorySessionRepository();
        Session session = SessionFor(userId);
        await repository.AddAsync(session);
        repository.ObservePublicationsDuring(writer);

        // Act — the wrong order first, then the right one, through the same armed fake.
        await repository.FindByIdAsync(session.Id);
        writer.ResolveUser(userId);
        await repository.FindByIdAsync(session.Id);

        // Assert
        await Assert.That(repository.IdentityWhenFindByIdWasEntered.Count).IsEqualTo(2);
        await Assert.That(repository.IdentityWhenFindByIdWasEntered[0]).IsEqualTo(Guid.Empty);
        await Assert.That(repository.IdentityWhenFindByIdWasEntered[1]).IsEqualTo(userId);
    }

    /// <summary>
    /// That adding a session removes every session of the same owner that has ended at the new
    /// session's creation instant, with its handle, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fake has to sweep because the real repository does</b>, in the same save as the insert.
    /// A handler test asserting "the account holds exactly its live sessions" against a fake that kept
    /// every ended row would be red for a reason that is the fake's, and one asserting the opposite
    /// would be green for a repository production does not have.
    /// </para>
    /// <para>
    /// <b>Keyed on the session's owner, and the stranger is what says so.</b> The real sweep names no
    /// owner and leaves that to <c>user_isolation</c>; a fake has no policy, so it has to restate the
    /// owner itself, and a fake that swept every account would pass a single-owner arrangement. The
    /// stranger holds both kinds of ended row. The owner's revoked row sits on a second credential, so
    /// a fake keyed on the establishing credential keeps it.
    /// </para>
    /// <para>
    /// <b>The handles go with the sessions</b>, because in the database they leave by the cascade from
    /// <c>sessions</c>. A fake that left them would hold a handle naming nothing, which the store cannot
    /// produce.
    /// </para>
    /// <para>
    /// Everything is seeded live at one instant and revoked afterwards. Seeding a revoked row first
    /// would let the fake's own sweep remove it while the next row was being seeded.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AddAsync_RemovesTheOwnersEndedSessionsAndTheirHandles_AndNothingElse()
    {
        // Arrange
        var ownerId = Guid.CreateVersion7();
        var strangerId = Guid.CreateVersion7();
        var repository = new InMemorySessionRepository();
        Credential ownCredential = Credential.CreatePasskey(ownerId, UtcNow().AddHours(-1));
        Credential ownOtherCredential = Credential.CreatePasskey(ownerId, UtcNow().AddHours(-1));
        Credential strangerCredential = Credential.CreatePasskey(strangerId, UtcNow().AddHours(-1));
        Session ownRevoked = Session.Establish(ownOtherCredential, UtcNow(), UtcNow().AddDays(1));
        Session ownExpired = Session.Establish(ownCredential, UtcNow(), UtcNow().AddHours(1));
        Session ownLive = Session.Establish(ownCredential, UtcNow(), UtcNow().AddDays(1));
        Session strangerRevoked = Session.Establish(strangerCredential, UtcNow(), UtcNow().AddDays(1));
        Session strangerExpired = Session.Establish(strangerCredential, UtcNow(), UtcNow().AddHours(1));
        foreach (Session session in new[] { ownRevoked, ownExpired, ownLive, strangerRevoked, strangerExpired })
        {
            await repository.AddAsync(session);
        }

        ownRevoked.Revoke(UtcNow().AddMinutes(30));
        strangerRevoked.Revoke(UtcNow().AddMinutes(30));

        // Established two hours on, after both one-hour sessions have run out.
        Session established = Session.Establish(ownCredential, UtcNow().AddHours(2), UtcNow().AddDays(14));
        Dictionary<Guid, string> labels = new()
        {
            [ownRevoked.Id] = "own revoked",
            [ownExpired.Id] = "own expired",
            [ownLive.Id] = "own live",
            [strangerRevoked.Id] = "stranger revoked",
            [strangerExpired.Id] = "stranger expired",
            [established.Id] = "established",
        };

        // Act
        await repository.AddAsync(
            established,
            SessionToken.For(established, RandomNumberGenerator.GetBytes(SessionToken.TokenLength)));

        // Assert
        await Assert.That(Render(repository.Sessions.Select(session => session.Id), labels))
            .IsEqualTo("established, own live, stranger expired, stranger revoked");
        await Assert.That(Render(repository.Tokens.Select(token => token.SessionId), labels))
            .IsEqualTo("established, own live, stranger expired, stranger revoked");
    }

    /// <summary>The ids as their labels, sorted and joined, so a failure names the row.</summary>
    private static string Render(IEnumerable<Guid> ids, IReadOnlyDictionary<Guid, string> labels) =>
        string.Join(
            ", ",
            ids.Select(id => labels.TryGetValue(id, out string? label) ? label : $"unlabelled {id}")
                .Order(StringComparer.Ordinal));

    private static DateTime UtcNow() => new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private static Session SessionFor(Guid userId) => Session.Establish(
        Credential.CreatePasskey(userId, UtcNow().AddHours(-1)),
        UtcNow().AddMinutes(-1),
        UtcNow().AddHours(1));
}
