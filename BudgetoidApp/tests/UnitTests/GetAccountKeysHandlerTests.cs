using Application.AccountKeys;
using Application.AccountKeys.GetAccountKeys;
using Domain.Users;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// What the handler behind the account-keys read hands back: every factor the <b>account</b> holds,
/// across every credential it holds them under; an empty list when it holds none; and the one argument
/// it may reach the read service with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unit tests rather than endpoint tests, and the two counting cases are why.</b> The failures these
/// exist to refuse are an implementation that answers <em>one</em> row where a credential holds ten — a
/// <c>SingleOrDefault</c>, a <c>FirstOrDefault</c>, a return type of <c>FactorEnvelopes?</c> — and one
/// that answers <em>one credential's</em> rows where the account holds eleven across two. Every one of
/// those is correct for an account holding a single passkey, and the symptom of the others is a person
/// who has already lost their authenticator redeeming a code, being handed a session, and finding the
/// account still locked. Nothing on the server sees that happen, so there is no log line, no status code
/// and no row to assert against; only a fixture holding the real shape and a count can say it.
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
/// <b>No assertion here is about a sequence.</b> The port promises rows ordered by factor id, and what
/// that promise buys is determinism rather than a particular order; the corrected argument for that —
/// including what <see cref="Guid.ToByteArray()" /> does that <see cref="Guid.CompareTo(Guid)" /> does
/// not — is stated once on <c>GetAccountKeysHandler</c> and is not restated here.
/// <see cref="InMemoryAccountKeyReadService" /> therefore does not sort, and these tests compare factor
/// ids as a set — both sides put through the same .NET ordering, which is order-insensitive rather than
/// order-asserting. Ordering as such belongs to the read service's own tests, over a database.
/// </para>
/// <para>
/// <b>No session is arranged anywhere in this file, and that is structural rather than a tidy-up.</b>
/// The handler took an <c>ISessionRepository</c> while the answer was narrowed to the credential that
/// opened the session, and the narrowing was wrong: re-authentication looks a credential up <em>by
/// account</em> and the assertion options carry no <c>allowCredentials</c>, so the authenticator decides
/// which factor answers a ceremony and the read may not be narrower than the account.
/// <c>GetAccountKeysHandler</c> carries the argument in full. What used to be tested here — that a
/// session id the repository answered <see langword="null" /> for produced an empty list rather than a
/// throw — has no subject left: there is no id, no lookup and no null. Whether a session is live is the
/// <b>authentication pipeline's</b> answer, and <c>AccountKeysEndpointTests</c> is where that is now
/// pinned, over a request, which is the only place it was ever observable.
/// </para>
/// </remarks>
public sealed class GetAccountKeysHandlerTests
{
    /// <summary>
    /// One account, a passkey and a set of recovery codes, eleven factors across the two — and all
    /// eleven come back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case that would have caught the defect this handler was changed to fix.</b> While the read
    /// was narrowed to the credential that opened the session, an account in exactly this state — the
    /// state every real account is in — was answered one credential's rows, and which credential the
    /// browser could actually open was decided by an authenticator the server never hears from. Ten
    /// factors are filed under the set and one under the passkey, so an implementation that narrowed to
    /// either credential is red on the count, and one that narrowed to the <em>larger</em> is red on the
    /// bytes as well.
    /// </para>
    /// <para>
    /// Every seeded factor carries envelopes nothing else in the fixture carries, so an implementation
    /// that reached the right count by repeating one row is red on the bytes rather than green on the
    /// number.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheAccountHoldsFactorsUnderTwoCredentials_ReturnsAllOfThem()
    {
        // Arrange — one account; ten factors under its set of recovery codes and one under its passkey.
        Guid userId = Guid.CreateVersion7();
        Guid recoveryCodes = Credential.CreateRecoveryCodes(userId, UtcNow).Id;
        Guid passkey = Credential.CreatePasskey(userId, UtcNow).Id;

        InMemoryAccountKeyReadService readService = new();
        FactorEnvelopes[] codeFactors = [.. Enumerable.Range(0, FactorsPerRecoveryCodeSet).Select(Factor)];
        FactorEnvelopes passkeyFactor = Factor(FactorsPerRecoveryCodeSet);

        foreach (FactorEnvelopes factor in codeFactors)
        {
            readService.Seed(userId, recoveryCodes, factor);
        }

        readService.Seed(userId, passkey, passkeyFactor);

        FactorEnvelopes[] seeded = [.. codeFactors, passkeyFactor];

        GetAccountKeysHandler handler = new(new StubUserContext(userId), readService);

        // Act
        FactorEnvelopes[] returned = [.. await handler.HandleAsync(new GetAccountKeysQuery())];

        // Assert — the count first, because narrowing to either credential is what this refuses and
        // both wrong answers are a number.
        await Assert.That(returned.Length).IsEqualTo(FactorsPerRecoveryCodeSet + 1);

        await AssertCarriesExactlyAsync(returned, seeded);
    }

    /// <summary>
    /// A set of recovery codes is one credential and ten factors, and all ten come back.
    /// </summary>
    /// <remarks>
    /// Kept beside the eleven-factor case above rather than folded into it, because the two refuse
    /// different implementations. That one refuses a read narrowed to a credential; this one refuses a
    /// read that collapses a credential's rows to one — <c>SingleOrDefault</c>, <c>FirstOrDefault</c>, a
    /// nullable single-pair return — which is the shape that is correct for every passkey in the product
    /// and drops nine of every ten recovery-code envelopes. Neither implies the other, and an
    /// implementation with both defects is red here on the count and red above it on both.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenOneCredentialHoldsTenFactors_ReturnsAllTen()
    {
        // Arrange — one account, one recovery-code credential, ten wrapped rows filed under it.
        Guid userId = Guid.CreateVersion7();
        Guid recoveryCodes = Credential.CreateRecoveryCodes(userId, UtcNow).Id;

        InMemoryAccountKeyReadService readService = new();
        FactorEnvelopes[] seeded = [.. Enumerable.Range(0, FactorsPerRecoveryCodeSet).Select(Factor)];

        foreach (FactorEnvelopes factor in seeded)
        {
            readService.Seed(userId, recoveryCodes, factor);
        }

        GetAccountKeysHandler handler = new(new StubUserContext(userId), readService);

        // Act
        FactorEnvelopes[] returned = [.. await handler.HandleAsync(new GetAccountKeysQuery())];

        // Assert — the count first, because it is the whole defect: SingleOrDefault, FirstOrDefault
        // and a nullable return each answer one here and are correct for every passkey.
        await Assert.That(returned.Length).IsEqualTo(FactorsPerRecoveryCodeSet);

        await AssertCarriesExactlyAsync(returned, seeded);
    }

    /// <summary>
    /// The positive control for the empty-answer case below: a populated read comes back populated,
    /// with the bytes that were stored.
    /// </summary>
    /// <remarks>
    /// Without it that case is a decoration — a handler that returned an empty list unconditionally, or
    /// that never reached the read service at all, satisfies it perfectly. It takes a case that returns
    /// to tell a guard apart from a wall. A passkey rather than a set of codes, so the one-factor shape
    /// is also stated, and the envelope's version and width are read here because this is the only test
    /// that looks at a single row whole.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheAccountHoldsOneFactor_ReturnsThatFactorsEnvelopes()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        Guid passkey = Credential.CreatePasskey(userId, UtcNow).Id;

        FactorEnvelopes stored = Factor(0);
        InMemoryAccountKeyReadService readService = new();
        readService.Seed(userId, passkey, stored);

        GetAccountKeysHandler handler = new(new StubUserContext(userId), readService);

        // Act
        FactorEnvelopes[] returned = [.. await handler.HandleAsync(new GetAccountKeysQuery())];

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
    /// An account holding no factor rows is an empty list, not a throw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An empty collection is the honest shape of "nothing came back", and this read is taken for
    /// display. <c>GetAccountKeysHandler</c> enumerates the four ways the state is reached — including
    /// the one its earlier text denied, a factor whose wrapped row was never written, which the schema
    /// permits even though no path in the product produces it.
    /// </para>
    /// <para>
    /// <b>The read service is not empty, and that is the half that makes this more than a null check.</b>
    /// It holds a full account's worth of rows filed under <em>another</em> account, so a handler that
    /// answered every row it had — the shape the fake would have if it accepted its owner argument and
    /// dropped it — is red here rather than green on an empty store.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheAccountHoldsNoFactorRows_ReturnsAnEmptyList()
    {
        // Arrange — the asking account holds nothing; a bystander account holds eleven factors.
        Guid userId = Guid.CreateVersion7();
        Guid bystanderId = Guid.CreateVersion7();
        Guid bystanderCodes = Credential.CreateRecoveryCodes(bystanderId, UtcNow).Id;
        Guid bystanderPasskey = Credential.CreatePasskey(bystanderId, UtcNow).Id;

        InMemoryAccountKeyReadService readService = new();
        foreach (int ordinal in Enumerable.Range(0, FactorsPerRecoveryCodeSet))
        {
            readService.Seed(bystanderId, bystanderCodes, Factor(ordinal));
        }

        readService.Seed(bystanderId, bystanderPasskey, Factor(FactorsPerRecoveryCodeSet));

        GetAccountKeysHandler handler = new(new StubUserContext(userId), readService);

        // Act — awaiting is itself the "never a throw" half of the claim.
        IReadOnlyList<FactorEnvelopes> returned = await handler.HandleAsync(new GetAccountKeysQuery());

        // Assert
        await Assert.That(returned).IsEmpty();
    }

    /// <summary>
    /// The read service is reached with the resolved account, once, and with nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The only test that can catch a dropped or wrong owner argument, and it is not "was
    /// called".</b> <c>wrapped_account_keys</c> is policed by <c>user_isolation</c>, so an
    /// implementation that named no owner at all would still never return another account's rows: the
    /// policy makes a wrong query answer <em>empty</em>, not <em>incorrect</em>. There is therefore no
    /// database state that distinguishes the two and no integration test that can, which leaves the
    /// argument as the whole observable. Weakened to a call count, this test would pass on the very
    /// implementation it exists to refuse.
    /// </para>
    /// <para>
    /// The count is asserted beside the value because the read is the request: a handler that asked
    /// twice — once per credential, say, on the way to a union — would be reading the same rows through
    /// two round trips on the one route that returns key material.
    /// </para>
    /// <para>
    /// Rows are seeded under two credentials of the asking account and under a second account, so the
    /// resolved id is measured rather than "the only account in the fixture".
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_ReadsTheEnvelopesForTheResolvedAccountOnce()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        Guid bystanderId = Guid.CreateVersion7();

        InMemoryAccountKeyReadService readService = new();
        readService.Seed(userId, Credential.CreatePasskey(userId, UtcNow).Id, Factor(0));
        readService.Seed(userId, Credential.CreateRecoveryCodes(userId, UtcNow).Id, Factor(1));
        readService.Seed(bystanderId, Credential.CreatePasskey(bystanderId, UtcNow).Id, Factor(2));

        GetAccountKeysHandler handler = new(new StubUserContext(userId), readService);

        // Act
        await handler.HandleAsync(new GetAccountKeysQuery());

        // Assert — one read, and its only argument.
        await Assert.That(readService.Asked.Count).IsEqualTo(1);
        await Assert.That(readService.Asked[0]).IsEqualTo(userId);
    }

    /// <summary>
    /// That <paramref name="returned" /> is exactly <paramref name="seeded" />: the same factor ids as a
    /// set, and each factor's two envelopes compared as bytes.
    /// </summary>
    /// <remarks>
    /// The ids go through the same .NET ordering on both sides, so this is order-insensitive rather than
    /// an assertion about which factor comes first. The envelopes are compared through
    /// <c>ToArray()</c> per factor, because comparing two <see cref="FactorEnvelopes" /> whole would
    /// answer a question about buffers rather than about key material.
    /// </remarks>
    private static async Task AssertCarriesExactlyAsync(
        IReadOnlyList<FactorEnvelopes> returned,
        IReadOnlyList<FactorEnvelopes> seeded)
    {
        await Assert.That(returned.Select(factor => factor.FactorId).Order())
            .IsEquivalentTo(seeded.Select(factor => factor.FactorId).Order());

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
    /// The distinguishability is what makes the counting cases measure rows: envelopes built from a
    /// shared constant would let an implementation that answered one row eleven times satisfy every
    /// byte assertion.
    /// </para>
    /// <para>
    /// The factor id is a version-4 <see cref="Guid" /> rather than a version-7 one on purpose. A
    /// client mints it, so version 4 is what actually arrives; and minting eleven in a loop with
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
