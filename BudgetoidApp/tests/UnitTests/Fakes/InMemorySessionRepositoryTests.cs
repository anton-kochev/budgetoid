using Domain.Sessions;
using Domain.Users;

namespace UnitTests.Fakes;

/// <summary>
/// Covers the two behaviours of <see cref="InMemorySessionRepository"/> that
/// <c>AuthenticateSessionHandlerTests</c> silently leans on. Everything else about the fake is
/// exercised through the handler tests; these two are not, and either would degrade without anything
/// going red.
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

    private static DateTime UtcNow() => new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private static Session SessionFor(Guid userId) => Session.Establish(
        Credential.CreatePasskey(userId, UtcNow().AddHours(-1)),
        UtcNow().AddMinutes(-1),
        UtcNow().AddHours(1));
}
