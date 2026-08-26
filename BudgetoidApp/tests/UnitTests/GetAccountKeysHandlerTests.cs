using Application.AccountKeys;
using Application.AccountKeys.GetAccountKeys;
using Domain.Sessions;
using Domain.Users;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// What the handler behind the account-keys read hands back: every factor the session's credential
/// holds, an empty list for the two ways there is nothing to hand back, and the one pair of
/// arguments it may reach the read service with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unit tests rather than endpoint tests, and the ten-row case is why.</b> The failure these exist
/// to refuse is an implementation that answers <em>one</em> row where the credential holds ten — a
/// <c>SingleOrDefault</c>, a <c>FirstOrDefault</c>, a return type of <c>FactorEnvelopes?</c>. Every
/// one of those is correct for every passkey in the product, and the symptom of the recovery-code
/// case is a person who has already lost their authenticator redeeming a code, being handed a
/// session, and finding the account still locked. Nothing on the server sees that happen, so there is
/// no log line, no status code and no row to assert against; only a fixture holding ten factors and a
/// count can say it.
/// </para>
/// <para>
/// <b>Neither envelope is ever compared as part of a whole <see cref="FactorEnvelopes" />.</b> The
/// record's synthesized equality compares each member through <c>EqualityComparer&lt;T&gt;.Default</c>,
/// which for a <see cref="ReadOnlyMemory{T}" /> is the struct's own equality — the same buffer, the
/// same offset, the same length — so two values holding identical bytes in distinct arrays compare
/// unequal. That is the comparison <c>WrappedAccountKeysConfiguration</c> declares a content comparer
/// to keep EF's change tracker away from, and nothing does the same for a record. Every assertion
/// below compares <see cref="FactorEnvelopes.FactorId" /> and then the bytes through
/// <c>ToArray()</c>, which is how <c>PasskeyPublicKeyTests</c> already compares this shape.
/// </para>
/// <para>
/// <b>No assertion here is about a sequence.</b> The port promises rows ordered by factor id, and
/// what that promise buys is determinism rather than a particular order: <c>uuid</c> collation orders
/// bytes in PostgreSQL and <see cref="Guid.CompareTo(Guid)" /> does not, so the same expectation
/// written against a real database would disagree with an in-memory one while both were right.
/// <see cref="InMemoryAccountKeyReadService" /> therefore does not sort, and these tests compare
/// factor ids as a set — both sides put through the same .NET ordering, which is order-insensitive
/// rather than order-asserting. Ordering as such belongs to the read service's own tests, over a
/// database.
/// </para>
/// <para>
/// <b>"Already ended" is not a separate arrangement here, and the reason is the repository.</b>
/// <see cref="ISessionRepository.FindByIdAsync" /> carries no <c>revoked_at_utc</c> predicate — the
/// real one says so at length, and <see cref="InMemorySessionRepository" /> copies it deliberately —
/// so a revoked session comes back as an entity rather than as <see langword="null" />. Seeding one
/// and expecting an empty answer would be asking this handler to hold a liveness rule it does not
/// own, and would fail a correct implementation. What is modelled below is the answer all three
/// unreachable sessions genuinely share: the repository returning <see langword="null" />.
/// </para>
/// </remarks>
public sealed class GetAccountKeysHandlerTests
{
    /// <summary>
    /// A set of recovery codes is one credential and ten factors, and all ten come back.
    /// </summary>
    /// <remarks>
    /// The load-bearing case. Each seeded factor carries envelopes nothing else in the fixture
    /// carries, so an implementation that answered the right count by repeating one row is red on the
    /// bytes rather than green on the number.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheSessionsCredentialHoldsTenFactors_ReturnsAllTen()
    {
        // Arrange — one account, one recovery-code credential, ten wrapped rows filed under it.
        Guid userId = Guid.CreateVersion7();
        Credential credential = Credential.CreateRecoveryCodes(userId, UtcNow);
        Session session = Session.Establish(credential, UtcNow, UtcNow.AddHours(1));

        InMemorySessionRepository sessions = new();
        await sessions.AddAsync(session);

        InMemoryAccountKeyReadService readService = new();
        FactorEnvelopes[] seeded = [.. Enumerable.Range(0, FactorsPerRecoveryCodeSet).Select(Factor)];

        foreach (FactorEnvelopes factor in seeded)
        {
            readService.Seed(userId, credential.Id, factor);
        }

        GetAccountKeysHandler handler = new(new StubUserContext(userId), sessions, readService);

        // Act
        FactorEnvelopes[] returned = [.. await handler.HandleAsync(new GetAccountKeysQuery(session.Id))];

        // Assert — the count first, because it is the whole defect: SingleOrDefault, FirstOrDefault
        // and a nullable return each answer one here and are correct for every passkey.
        await Assert.That(returned.Length).IsEqualTo(FactorsPerRecoveryCodeSet);

        // The ids as a set, put through the same ordering on both sides so this is order-insensitive
        // rather than an assertion about which factor comes first.
        await Assert.That(returned.Select(factor => factor.FactorId).Order())
            .IsEquivalentTo(seeded.Select(factor => factor.FactorId).Order());

        // The bytes, per factor, through ToArray. Comparing two FactorEnvelopes whole would answer a
        // question about buffers rather than about key material.
        foreach (FactorEnvelopes expected in seeded)
        {
            FactorEnvelopes actual = returned.Single(factor => factor.FactorId == expected.FactorId);

            await Assert.That(actual.WrappedContentKey.ToArray())
                .IsEquivalentTo(expected.WrappedContentKey.ToArray());
            await Assert.That(actual.WrappedIndexKey.ToArray())
                .IsEquivalentTo(expected.WrappedIndexKey.ToArray());
        }
    }

    /// <summary>
    /// The positive control for the two empty-answer cases below: a populated read comes back
    /// populated, with the bytes that were stored.
    /// </summary>
    /// <remarks>
    /// Without it those two are decorations — a handler that returned an empty list unconditionally,
    /// one whose null check had been written the wrong way round, one that never reached the read
    /// service at all, satisfies both perfectly. It takes a case that returns to tell a guard apart
    /// from a wall. A passkey rather than a set of codes, so the one-factor shape is also stated.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheSessionsCredentialHoldsOneFactor_ReturnsThatFactorsEnvelopes()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        Credential credential = Credential.CreatePasskey(userId, UtcNow);
        Session session = Session.Establish(credential, UtcNow, UtcNow.AddHours(1));

        InMemorySessionRepository sessions = new();
        await sessions.AddAsync(session);

        FactorEnvelopes stored = Factor(0);
        InMemoryAccountKeyReadService readService = new();
        readService.Seed(userId, credential.Id, stored);

        GetAccountKeysHandler handler = new(new StubUserContext(userId), sessions, readService);

        // Act
        FactorEnvelopes[] returned = [.. await handler.HandleAsync(new GetAccountKeysQuery(session.Id))];

        // Assert
        await Assert.That(returned.Length).IsEqualTo(1);
        await Assert.That(returned[0].FactorId).IsEqualTo(stored.FactorId);
        await Assert.That(returned[0].WrappedContentKey.ToArray())
            .IsEquivalentTo(stored.WrappedContentKey.ToArray());
        await Assert.That(returned[0].WrappedIndexKey.ToArray())
            .IsEquivalentTo(stored.WrappedIndexKey.ToArray());

        // The version byte, cast because IsEqualTo(1) against a byte compiles and then throws.
        await Assert.That(returned[0].WrappedContentKey.Span[0])
            .IsEqualTo(WrappedAccountKeys.EnvelopeVersion);
        await Assert.That(returned[0].WrappedContentKey.Length)
            .IsEqualTo(WrappedAccountKeys.EnvelopeLength);
    }

    /// <summary>
    /// A session id the repository answers <see langword="null" /> for is an empty list, never a
    /// throw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never established and belonging to another account arrive here as the same
    /// <see langword="null" /> — the second because <c>user_isolation</c> makes a stranger's row
    /// <em>not found</em> rather than found and rejected — and they must stay indistinguishable. A
    /// refusal keyed on the null would tell a caller that a guessed id names a real row, on the one
    /// route that names an account's key custody.
    /// </para>
    /// <para>
    /// The store is not empty and the read service is not empty, which is the half that makes this
    /// more than a null check: a handler that ignored the query's id and took whatever session it
    /// could find would be handed a credential holding envelopes, and would answer them.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenNoSessionIsFoundForTheId_ReturnsAnEmptyList()
    {
        // Arrange — a live session and its envelopes exist; the query names a different id.
        Guid userId = Guid.CreateVersion7();
        Credential credential = Credential.CreatePasskey(userId, UtcNow);
        Session other = Session.Establish(credential, UtcNow, UtcNow.AddHours(1));

        InMemorySessionRepository sessions = new();
        await sessions.AddAsync(other);

        InMemoryAccountKeyReadService readService = new();
        readService.Seed(userId, credential.Id, Factor(0));

        GetAccountKeysHandler handler = new(new StubUserContext(userId), sessions, readService);

        // Act — awaiting is itself the "never a throw" half of the claim.
        IReadOnlyList<FactorEnvelopes> returned =
            await handler.HandleAsync(new GetAccountKeysQuery(Guid.CreateVersion7()));

        // Assert
        await Assert.That(returned).IsEmpty();
    }

    /// <summary>
    /// A credential holding no factor rows is an empty list too, not a throw.
    /// </summary>
    /// <remarks>
    /// An empty collection is the honest shape of "nothing came back", and this read is taken for
    /// display. The state is not one the product can be left in at rest — every path that brings a
    /// factor into existence writes its wrapped row in the credential's own <c>SaveChanges</c> — so
    /// an empty answer means the credential was revoked, or the account erased, between this request
    /// authenticating and this read running. That is a race, not a corruption.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheCredentialHoldsNoFactorRows_ReturnsAnEmptyList()
    {
        // Arrange — a session the handler can find, and a read service holding rows for a different
        // credential, so an implementation that answered every row it had would be red rather than
        // green on an empty store.
        Guid userId = Guid.CreateVersion7();
        Credential credential = Credential.CreatePasskey(userId, UtcNow);
        Session session = Session.Establish(credential, UtcNow, UtcNow.AddHours(1));

        InMemorySessionRepository sessions = new();
        await sessions.AddAsync(session);

        InMemoryAccountKeyReadService readService = new();
        readService.Seed(userId, Credential.CreateRecoveryCodes(userId, UtcNow).Id, Factor(0));

        GetAccountKeysHandler handler = new(new StubUserContext(userId), sessions, readService);

        // Act
        IReadOnlyList<FactorEnvelopes> returned =
            await handler.HandleAsync(new GetAccountKeysQuery(session.Id));

        // Assert
        await Assert.That(returned).IsEmpty();
    }

    /// <summary>
    /// The read service is reached with the resolved account and the session's own credential, and
    /// with nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The only test that can catch a dropped owner argument, and it is not "was called".</b>
    /// <c>wrapped_account_keys</c> is policed by <c>user_isolation</c>, so an implementation
    /// filtering on the credential alone would still never return another account's rows: the policy
    /// makes a wrong query answer <em>empty</em>, not <em>incorrect</em>. There is therefore no
    /// database state that distinguishes the two and no integration test that can, which leaves the
    /// arguments as the whole observable. Weakened to a call count, this test would pass on the very
    /// implementation it exists to refuse.
    /// </para>
    /// <para>
    /// A second credential and a second session are seeded for the same account so that "the
    /// session's credential" is measured rather than "the only credential in the fixture". The
    /// account being read from <see cref="Application.Abstractions.IUserContext" /> rather than from
    /// <c>Session.UserId</c> is deliberately <em>not</em> claimed here: the two cannot disagree — the
    /// policy is what let the row be visible and it compares against the value the context published
    /// — so no test can tell them apart, and that choice is held by review.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_ReadsTheEnvelopesForTheResolvedAccountAndTheSessionsCredential()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        Credential passkey = Credential.CreatePasskey(userId, UtcNow);
        Credential recoveryCodes = Credential.CreateRecoveryCodes(userId, UtcNow);
        Session passkeySession = Session.Establish(passkey, UtcNow, UtcNow.AddHours(1));
        Session recoveryCodeSession = Session.Establish(recoveryCodes, UtcNow, UtcNow.AddHours(1));

        InMemorySessionRepository sessions = new();
        await sessions.AddAsync(recoveryCodeSession);
        await sessions.AddAsync(passkeySession);

        InMemoryAccountKeyReadService readService = new();
        readService.Seed(userId, passkey.Id, Factor(0));
        readService.Seed(userId, recoveryCodes.Id, Factor(1));

        GetAccountKeysHandler handler = new(new StubUserContext(userId), sessions, readService);

        // Act
        await handler.HandleAsync(new GetAccountKeysQuery(passkeySession.Id));

        // Assert — one read, and both of its arguments.
        await Assert.That(readService.Asked.Count).IsEqualTo(1);
        await Assert.That(readService.Asked[0].UserId).IsEqualTo(userId);
        await Assert.That(readService.Asked[0].CredentialId).IsEqualTo(passkey.Id);
    }

    /// <summary>
    /// How many factors one set of recovery codes carries: one key-encryption key per code, because
    /// a person redeems whichever code they still have.
    /// </summary>
    private const int FactorsPerRecoveryCodeSet = 10;

    private static readonly DateTime UtcNow = new(2026, 8, 26, 11, 12, 13, DateTimeKind.Utc);

    /// <summary>
    /// One factor's stored share, distinguishable from every other <paramref name="ordinal" /> in
    /// both envelopes and distinguishable between the two envelopes of the same factor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinguishability is what makes the ten-row case measure ten rows: envelopes built from a
    /// shared constant would let an implementation that answered one row ten times satisfy every
    /// byte assertion.
    /// </para>
    /// <para>
    /// The factor id is a version-4 <see cref="Guid" /> rather than a version-7 one on purpose. A
    /// client mints it, so version 4 is what actually arrives; and minting ten in a loop with
    /// <see cref="Guid.CreateVersion7" /> would make seed order and ascending order the same
    /// sequence, which is exactly the coincidence that would let an order-asserting expectation slip
    /// in here and then break the day it was written against PostgreSQL's byte collation.
    /// </para>
    /// </remarks>
    private static FactorEnvelopes Factor(int ordinal) => new(
        Guid.NewGuid(),
        Envelope((byte)(0x10 + ordinal)),
        Envelope((byte)(0xA0 + ordinal)));

    /// <summary>
    /// A wrapped-key envelope of the width and version the schema refuses a row for breaking, marked
    /// with <paramref name="marker" /> so two of them can be told apart.
    /// </summary>
    private static byte[] Envelope(byte marker)
    {
        byte[] bytes = new byte[WrappedAccountKeys.EnvelopeLength];

        bytes[0] = WrappedAccountKeys.EnvelopeVersion;
        bytes[^1] = marker;

        return bytes;
    }
}
