using Application.RecoveryCodes;
using Domain.Users;

namespace UnitTests.Fakes;

/// <summary>
/// The recovery-code tables in memory: the <c>credentials</c> row standing for a set, the
/// <c>recovery_code_hashes</c> rows hanging off it, and the set's share of the account keys.
/// </summary>
/// <remarks>
/// <para>
/// Two lists rather than one, and the split is the behaviour of the real stack that a retried unit of
/// work turns on. Rows a handler <em>seeded</em> are rows the database holds; rows it
/// <see cref="AddSetAsync" />ed inside the current unit of work are queued inserts living in the
/// change tracker, which a <c>ROLLBACK</c> never sees and which
/// <see cref="DiscardTrackedEntities" /> is what removes. A fake with one list would let a handler
/// that never discards look correct while production wrote two sets, and
/// <see cref="InMemorySessionRepository" /> models the same distinction for the same reason.
/// </para>
/// <para>
/// <see cref="FindRecoveryCodeCredentialAsync" /> reads the committed rows only. That is not an
/// omission: a query does not return an entity that is merely tracked as Added, so a fake answering
/// with pending rows would let a handler read back a set it had queued in this very attempt and call
/// it the account's existing one.
/// </para>
/// <para>
/// <b>The redemption path — <see cref="FindByVerifierHashAsync" />,
/// <see cref="FindOwnedByVerifierHashAsync" /> and <see cref="ConsumeAsync" /> — is modelled here
/// too, and what it deliberately does not model is the change tracker.</b> ADR 0014's third leg asks
/// that the entity spent be one <em>this unit of work</em> is tracking, and that is a property of a
/// real EF context and a real transaction: this fake matches a code by the value of its verifier
/// hash, so an entity fabricated elsewhere carrying a stored hash would be accepted where production
/// has the read and the delete on one connection to hold it. <c>RecoveryCodeRedemptionTests</c> is
/// what holds that leg; nothing in memory can.
/// </para>
/// <para>
/// <b>The two lookups are two statements rather than one written twice, and a reader who collapses
/// them deletes the rule.</b> Only the statement that establishes an identity may omit an owner:
/// <c>recovery_code_hashes</c> is exempt from row-level security, and an exempt table scopes nothing
/// (ADR 0011), so every other read or write of it carries its own owner predicate.
/// <see cref="FindByVerifierHashAsync" /> is that one statement — a redemption arrives anonymous, and
/// there is no account to scope it by until it has answered. <see cref="FindOwnedByVerifierHashAsync" />
/// is the same lookup for the half of a redemption that already has the answer, and naming the owner
/// is what makes the row <see cref="ConsumeAsync" /> removes belong to the account being signed in by
/// construction rather than by argument. A fake offering one lookup for both — or a second that took
/// a user id and ignored it — would let a handler that waived the rule twice for one route stay green.
/// </para>
/// <para>
/// The other behaviour modelled faithfully is the refusal: spending a code that is no longer there
/// raises rather than quietly doing nothing, because production reaches it through a zero-row
/// <c>DELETE</c> that EF raises on.
/// </para>
/// <para>
/// <paramref name="cascadeFromCredential" /> is the database's own <c>ON DELETE CASCADE</c> from
/// <c>credentials</c>, mirrored rather than stubbed —
/// <see cref="InMemoryPasskeyRepository.DeletePasskeyAsync" /> makes the same choice by holding its
/// three rows in one entry. It is supplied by the caller rather than wired to a session repository
/// here so that this fake keeps no opinion about sessions: it knows a credential row went, and the
/// test says what else the database would have taken with it.
/// </para>
/// </remarks>
public sealed class InMemoryRecoveryCodeRepository(Action<Credential>? cascadeFromCredential = null)
    : IRecoveryCodeRepository
{
    private readonly List<Set> _committed = [];
    private readonly List<Set> _pending = [];
    private readonly List<Guid> _ownerScopedLookups = [];

    /// <summary>
    /// Every set the database would hold if this unit of work committed now — the rows already there
    /// plus the ones queued for insert.
    /// </summary>
    public IReadOnlyList<Credential> Credentials =>
        [.. _committed.Concat(_pending).Select(set => set.Credential)];

    /// <summary>Every unredeemed code the database would hold if this unit of work committed now.</summary>
    public IReadOnlyList<RecoveryCodeHash> Hashes =>
        [.. _committed.Concat(_pending).SelectMany(set => set.Hashes)];

    /// <summary>
    /// Every set's share of the account keys the database would hold if this unit of work committed
    /// now — one row per issued set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read off the sets rather than kept in a list of its own, so it leaves when its credential does:
    /// the row cascades from <c>credentials</c> exactly as the code hashes do, and a fake that took the
    /// set away and left its envelopes behind would hold a row the database could not.
    /// </para>
    /// <para>
    /// A set filed by <see cref="Seed" /> carries none, which is
    /// <c>RepositoryTestHost.SeedPasskeyAsync</c>'s choice rather than an oversight: the wrapped keys
    /// are a row of their own, and nothing this fake answers reads a seeded set's envelopes. What
    /// <see cref="AddSetAsync" /> writes is not a simplification, because the single save is what makes
    /// a set without its share of the keys unreachable.
    /// </para>
    /// </remarks>
    public IReadOnlyList<WrappedAccountKeys> WrappedKeys =>
    [
        .. _committed.Concat(_pending)
            .Select(set => set.WrappedAccountKeys)
            .OfType<WrappedAccountKeys>(),
    ];

    /// <summary>
    /// How many times the unscoped discovery lookup ran.
    /// </summary>
    /// <remarks>
    /// Recorded because the discovery read's <em>placement</em> is a rule and no row count can express
    /// it. It runs once, before the transaction opens, because the account it establishes has to be
    /// published before a connection is configured; a handler that moved it inside a replayed unit of
    /// work would read once per attempt, and one that refused a malformed verifier by looking it up
    /// anyway would read on a request that should have cost the database nothing.
    /// </remarks>
    public int DiscoveryLookupCallCount { get; private set; }

    /// <summary>
    /// Every account <see cref="FindOwnedByVerifierHashAsync" /> was asked to scope by, oldest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The values rather than a count, and the list rather than the last one.</b> The claim this
    /// supports is that the row a redemption spends is read a second time, inside the transaction, by
    /// the account the discovery read resolved — three facts, and a call counter states none of them.
    /// An empty list is a handler that spent the entity the unscoped read produced, which is ADR 0014's
    /// third leg deleted; a list naming another account is the failure that leg exists to refuse; and
    /// one entry per attempt is what says the read sits inside the delegate rather than above it.
    /// </para>
    /// <para>
    /// It cannot see the <em>repository's</em> own predicate — a production lookup that dropped its
    /// <c>where user_id = …</c> would still be called with the right argument, and this would still
    /// record it. That half is unobservable from any fake and unobservable from the route as well,
    /// because the owner is read off the very row being matched; it is held by review and by the
    /// predicate being written down in <see cref="IRecoveryCodeRepository" />.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Guid> OwnerScopedLookups => _ownerScopedLookups;

    /// <summary>
    /// Run once by <see cref="FindOwnedByVerifierHashAsync" />, after the row has been chosen and
    /// before it is handed back — the window a concurrent redemption lands in.
    /// </summary>
    /// <remarks>
    /// The only place this fake can express the race <see cref="ConsumeAsync" />'s refusal exists for:
    /// two requests carrying one verifier both find the row, and the loser's <c>DELETE</c> matches
    /// nothing. Production reaches it as a zero-row delete EF raises
    /// <c>DbUpdateConcurrencyException</c> on, which <c>RecoveryCodeRepository</c> translates into the
    /// sentence below; a test spends the contested code from here and the loser meets the same refusal.
    /// Left unset it is a no-op, which is what every test not about the race wants.
    /// </remarks>
    public Func<Task>? OnOwnerScopedLookup { get; set; }

    /// <summary>How many times a set was asked to be deleted.</summary>
    /// <remarks>
    /// Recorded because "the account had no previous set and none was deleted" is a claim about a call
    /// that did not happen, and the row counts cannot express it: an unconditional delete of a set
    /// that is not there leaves the same empty table an absent delete does.
    /// </remarks>
    public int DeleteSetCallCount { get; private set; }

    /// <summary>
    /// A question this fake asks once, at the moment <see cref="DeleteSetAsync" /> is entered and
    /// before anything is removed, so a test can observe the world exactly as the delete finds it.
    /// </summary>
    /// <remarks>
    /// Ordering is what this exists for, and a pair of call counters compared before and after cannot
    /// express it: two counters that both moved say both things happened, never that one preceded the
    /// other. The fake knows nothing about what is being asked — the caller supplies the predicate —
    /// which is the shape <see cref="InMemoryPasskeyRepository.ObserveAtDelete" /> already uses.
    /// </remarks>
    public Func<Credential, bool>? ObserveAtDelete { get; set; }

    /// <summary>
    /// What <see cref="ObserveAtDelete" /> answered, or <see langword="null" /> when the delete never
    /// ran at all — a distinction a plain <see langword="bool" /> could not make, and the two mean
    /// very different things to a test about ordering.
    /// </summary>
    public bool? ObservationAtDelete { get; private set; }

    /// <summary>Files a set the way a committed generation would have left it.</summary>
    public void Seed(Credential credential, IReadOnlyList<RecoveryCodeHash> hashes)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(hashes);

        _committed.Add(new Set(credential, [.. hashes], WrappedAccountKeys: null));
    }

    /// <summary>
    /// Forgets every set queued for insert, which is what clearing the change tracker does to rows a
    /// save has not written yet. Committed rows are untouched, because a discard is not a rollback of
    /// the database.
    /// </summary>
    public void DiscardTrackedEntities() => _pending.Clear();

    /// <summary>
    /// The account's set, if it holds one — with the type predicate the real query carries.
    /// </summary>
    /// <remarks>
    /// The type filter selects everything this fake is ever given, and is written out anyway for the
    /// reason <see cref="InMemoryPasskeyRepository.CountPasskeysForUserAsync" /> writes out its own:
    /// the day a passkey or a federated credential is seeded here is the day an unfiltered lookup
    /// starts answering a different question, and it would answer it by handing a caller the
    /// credential their Google sign-in hangs off.
    /// </remarks>
    public Task<Credential?> FindRecoveryCodeCredentialAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_committed
            .Find(set =>
                set.Credential.UserId == userId
                && set.Credential.Type == CredentialType.RecoveryCodes)
            ?.Credential);

    /// <summary>
    /// The one unredeemed code stored under <paramref name="verifierHash" />, or
    /// <see langword="null" /> when no code in this fake hashes to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It names no owner, and none is missing.</b> A redemption arrives anonymous, so the account is
    /// what this answer establishes rather than something the caller may supply — the reason
    /// <c>recovery_code_hashes</c> is exempt from row-level security. A fake taking a user id here would
    /// let a handler that scoped the discovery read look correct while production had nothing to scope
    /// it with. It is the only member here allowed to omit one:
    /// <see cref="FindOwnedByVerifierHashAsync" /> is this same lookup for a caller that has an account
    /// to name, and it names it.
    /// </para>
    /// <para>
    /// <b>One match or none, never the first of several.</b> The verifier hash is the primary key, so a
    /// second row answering to one digest is not a case to choose between — it is a fake holding rows
    /// the database could not, which is what <see cref="ConsumeAsync" /> sums its removals to catch.
    /// Production says the same thing with <c>SingleOrDefault</c>; a fake picking one would answer
    /// quietly where it raises.
    /// </para>
    /// <para>
    /// <b>Compared by content, never by <see cref="ReadOnlyMemory{T}.Equals(object)" />.</b> That
    /// comparison is of buffer, offset and length, so a caller hashing its own verifier would find
    /// nothing here while production matched the row: what reaches PostgreSQL through the property's
    /// value converter is a <c>bytea</c> comparison of the bytes. A fake using the struct's own equality
    /// would fail every honest test and pass only tests that handed it back the very instance it stored.
    /// </para>
    /// <para>
    /// The committed rows only, exactly as <see cref="FindRecoveryCodeCredentialAsync" /> reads them and
    /// for the same reason: a query does not return an entity that is merely tracked as Added.
    /// </para>
    /// </remarks>
    public Task<RecoveryCodeHash?> FindByVerifierHashAsync(
        ReadOnlyMemory<byte> verifierHash,
        CancellationToken cancellationToken = default)
    {
        DiscoveryLookupCallCount++;

        return Task.FromResult(_committed
            .SelectMany(set => set.Hashes)
            .SingleOrDefault(hash => Matches(hash, verifierHash)));
    }

    /// <summary>
    /// The one unredeemed code stored under <paramref name="verifierHash" /> <em>and</em> owned by
    /// <paramref name="userId" />, or <see langword="null" /> when this fake holds no such code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same rows as <see cref="FindByVerifierHashAsync" />, through the predicate that read is
    /// forbidden to carry.</b> This is the re-read a redemption performs inside its transaction, once
    /// the discovery read has resolved an account and the handler has published it. There is an owner
    /// to name by then, and <c>recovery_code_hashes</c> being exempt from row-level security means
    /// naming it here is the only thing that scopes the entity travelling on to
    /// <see cref="ConsumeAsync" />.
    /// </para>
    /// <para>
    /// <b>The id is honoured, not accepted and dropped.</b> A fake that matched on the hash alone would
    /// pass every test the scoped member exists for, including one seeding two accounts, because the
    /// hash selects the same single row either way today — and the bug it would wave through is the one
    /// this predicate refuses: a session established for one account over a code removed from another.
    /// </para>
    /// <para>
    /// <b>Another account's code is refused exactly as a code that was never stored is</b> — the same
    /// <see langword="null" />, because "that code is real, but not yours" is a fact about what is
    /// stored and about somebody else's account at once.
    /// </para>
    /// <para>
    /// Content comparison, one match or none, and the committed rows only: all three for the reasons
    /// <see cref="FindByVerifierHashAsync" /> writes out.
    /// </para>
    /// </remarks>
    public async Task<RecoveryCodeHash?> FindOwnedByVerifierHashAsync(
        Guid userId,
        ReadOnlyMemory<byte> verifierHash,
        CancellationToken cancellationToken = default)
    {
        _ownerScopedLookups.Add(userId);

        RecoveryCodeHash? found = _committed
            .SelectMany(set => set.Hashes)
            .SingleOrDefault(hash => hash.UserId == userId && Matches(hash, verifierHash));

        // After the row is chosen and before the caller has it: see OnOwnerScopedLookup. A concurrent
        // redemption spending the same code from here leaves this caller holding an entity whose row is
        // gone, which is exactly the state ConsumeAsync's refusal is about.
        if (OnOwnerScopedLookup is not null)
        {
            await OnOwnerScopedLookup();
        }

        return found;
    }

    /// <summary>
    /// Spends one code by removing its row, leaving the set's credential — and every code still on the
    /// card — standing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One row and nothing else: the credential the code hangs off is deliberately out of reach, so a
    /// handler that emptied a set cannot take the set with it and cascade away the session the
    /// redemption just opened. That is why <paramref name="hash" /> is a code rather than a set.
    /// </para>
    /// <para>
    /// <b>A code that is no longer there is refused rather than ignored.</b> In production the entity
    /// reaches a <c>DELETE</c> that matches zero rows and EF raises
    /// <c>DbUpdateConcurrencyException</c>, which the repository translates into the very sentence
    /// below — one redemption per code, whether the second presentation arrives a second later or at
    /// the same instant. A fake that no-opped here would let a handler establish a session over a code
    /// it never spent and stay green, which is the one outcome the consume-before-establish ordering
    /// exists to prevent.
    /// </para>
    /// <para>
    /// A queued row is removed rather than refused, because removing an entity a save has not written
    /// yet detaches it instead of emitting a statement. Nothing on the redemption path queues a code —
    /// this arm exists so the fake does not answer a question production would not have asked.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">No code was supplied.</exception>
    /// <exception cref="RecoveryCodeRedemptionException">The code was already spent.</exception>
    public Task ConsumeAsync(RecoveryCodeHash hash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hash);

        // The inner lists are what change; the two outer lists are only read, so removing a code while
        // enumerating the sets is safe. Summed rather than short-circuited: the verifier hash is the
        // primary key, so a second match would be a fake holding a row the database could not.
        int removed = _committed
            .Concat(_pending)
            .Sum(set => set.Hashes.RemoveAll(candidate => Matches(candidate, hash.VerifierHash)));

        if (removed == 0)
        {
            // The repository's own sentence, restated rather than referenced: a test pinning what a
            // second presentation gets should be pinning the words production produces.
            throw new RecoveryCodeRedemptionException(
                "The presented code was consumed by a concurrent redemption.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Queues the whole set — its credential, its codes and its share of the account keys — as one
    /// unit of work's worth of inserts.
    /// </summary>
    /// <remarks>
    /// <paramref name="wrappedAccountKeys" /> is queued beside the other rows rather than accepted and
    /// dropped, for the reason the hashes are: the single save is what makes a set that holds no
    /// envelopes unreachable, and a fake that took the argument and forgot it would let a handler
    /// filing them against the passkey that authorized the request — the credential nearest to hand,
    /// and the mistake nothing beneath the application can catch — look correct here.
    /// </remarks>
    public Task AddSetAsync(
        Credential credential,
        IReadOnlyList<RecoveryCodeHash> hashes,
        WrappedAccountKeys wrappedAccountKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(hashes);
        ArgumentNullException.ThrowIfNull(wrappedAccountKeys);

        _pending.Add(new Set(credential, [.. hashes], wrappedAccountKeys));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes the set's credential row and, with it, every unredeemed code hanging off it.
    /// </summary>
    /// <remarks>
    /// It takes the loaded entity and never an id, per ADR 0014, and the guarantee that buys is the
    /// narrow one <see cref="IPasskeyRepository.DeletePasskeyAsync" /> spells out:
    /// <see cref="Credential.CreateRecoveryCodes" /> is public, so a fabricated credential can reach
    /// this call — but it carries a freshly minted id naming no stored row, so the removal below
    /// matches nothing rather than taking a stranger's set. The one lookup that produces a credential
    /// this fake actually holds is <see cref="FindRecoveryCodeCredentialAsync" />, and it carries the
    /// owner in its predicate.
    /// </remarks>
    public Task DeleteSetAsync(Credential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        // Asked first, before a single row moves: see ObserveAtDelete.
        ObservationAtDelete = ObserveAtDelete?.Invoke(credential);
        DeleteSetCallCount++;

        _committed.RemoveAll(set => set.Credential.Id == credential.Id);
        _pending.RemoveAll(set => set.Credential.Id == credential.Id);

        cascadeFromCredential?.Invoke(credential);

        return Task.CompletedTask;
    }

    /// <summary>
    /// True when <paramref name="hash" /> is stored under <paramref name="verifierHash" /> — of the
    /// bytes, never of the buffer they happen to sit in. See <see cref="FindByVerifierHashAsync" />.
    /// </summary>
    private static bool Matches(RecoveryCodeHash hash, ReadOnlyMemory<byte> verifierHash) =>
        hash.VerifierHash.Span.SequenceEqual(verifierHash.Span);

    /// <summary>
    /// One set: the <c>credentials</c> row and the codes hanging off it. The codes are mutable because
    /// a redemption spends one of them and leaves the rest — see <see cref="ConsumeAsync" />.
    /// </summary>
    /// <param name="WrappedAccountKeys">
    /// The set's share of the account keys, or <see langword="null" /> for a set <see cref="Seed" />
    /// filed — see <see cref="WrappedKeys" /> for why a seed files none.
    /// </param>
    private sealed record Set(
        Credential Credential,
        List<RecoveryCodeHash> Hashes,
        WrappedAccountKeys? WrappedAccountKeys);
}
