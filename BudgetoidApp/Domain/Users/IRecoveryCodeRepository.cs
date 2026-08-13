namespace Domain.Users;

/// <summary>
/// The <c>credentials</c> row standing for an account's set of recovery codes, and the
/// <c>recovery_code_hashes</c> rows hanging off it.
/// </summary>
public interface IRecoveryCodeRepository
{
    /// <summary>
    /// The credential standing for <paramref name="userId"/>'s set of recovery codes, or
    /// <see langword="null"/> when that account holds no set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This produces a <see cref="Credential"/> naming a real row</b> — the thing
    /// <c>docs/decisions/0014-scope-the-credential-delete-in-the-application.md</c> says review has to
    /// catch, because <c>credentials</c> is exempt from row-level security and a credential that
    /// reached <see cref="DeleteSetAsync"/> from an unscoped read would leave that delete scoped by
    /// nothing at all. The property survives here for the reason
    /// <see cref="IPasskeyRepository.FindPasskeyCredentialAsync"/>'s does: <b>the owner and the type
    /// are both in the predicate</b>, so what this can hand back is one of the caller's own sets or
    /// nothing. A lookup added below that returns a <see cref="Credential"/> without naming its owner
    /// is what would break the chain.
    /// </para>
    /// <para>
    /// The type predicate is a rule rather than tidiness. Every account also holds the federated
    /// Google credential provisioning minted for it, and — once the account registers one — passkeys;
    /// an untyped lookup would hand the caller whichever of those it found first and offer it to a
    /// delete.
    /// </para>
    /// <para>
    /// The entity rather than its id, because that is what <see cref="DeleteSetAsync"/> takes and what
    /// <see cref="RecoveryCodeHash.From"/> reads its three copied columns off.
    /// </para>
    /// </remarks>
    Task<Credential?> FindRecoveryCodeCredentialAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The one unredeemed code whose stored hash is <paramref name="verifierHash"/>, or
    /// <see langword="null"/> when no code in the installation hashes to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The discovery read. It names no owner, and the exemption <c>recovery_code_hashes</c> carries
    /// exists for exactly this statement.</b> A redemption arrives anonymous — somebody redeeming a code
    /// has lost the authenticator that would have proved who they are — so there is no account to scope
    /// by until this read has answered, and a <c>user_isolation</c> policy keyed on
    /// <c>app.current_user_id</c> would refuse the very query that produces the value it wants to compare
    /// against. It would refuse it loudly rather than quietly: an unset setting reaches the policy as
    /// <c>''::uuid</c> and raises <c>22P02</c>. ADR 0016 records that; this is the statement it was
    /// written for.
    /// </para>
    /// <para>
    /// <b>It is not the only read of that shape, and it is declared here rather than on a port of its
    /// own because of that.</b> <see cref="IPasskeyRepository.FindByWebAuthnCredentialIdAsync"/> is the
    /// same statement over passkey material and <c>IWebAuthnChallengeStore.ConsumeAsync</c> is the same
    /// over a nonce; each runs before the request has an identity, each reads a table exempt for that
    /// reason, and each sits on the ordinary port beside the scoped reads of its own subject. What
    /// bounds this read is written below, not the set of collaborators that can reach it.
    /// </para>
    /// <para>
    /// <b>Unscoped is not unbounded.</b> The caller's own input names the row: the hash is SHA-256 of a
    /// 256-bit secret the client must present in full, so a caller selecting a row they cannot name is
    /// guessing it — the argument the challenge consume runs on too. What must never appear here is a
    /// join to any other table: a join to <c>users</c> or to <c>credentials</c> on this statement would
    /// put a second relation on a connection that has published nobody. The set's credential is loaded
    /// <em>after</em> the identity this read establishes, by
    /// <see cref="FindRecoveryCodeCredentialAsync"/>, which names its owner.
    /// </para>
    /// <para>
    /// <b>This is the one member of this port that may omit an owner, and every other read or write of
    /// the table carries its own</b>, per ADR 0011: an exempt table scopes nothing, so only the
    /// statement that establishes the identity is allowed to run without one.
    /// <see cref="FindOwnedByVerifierHashAsync"/> is this same lookup for a caller that has one, and
    /// <c>IRecoveryCodeReadService.CountRemainingForUserAsync</c> is the count; both name the owner.
    /// Letting a second member omit one is what would turn "the discovery read" from a description of
    /// this statement into a hole in the rule.
    /// </para>
    /// <para>
    /// <b>What this establishes is an account, not a row to spend.</b> The entity carries the
    /// <c>user_id</c> a redemption adopts; the row a redemption then removes is read again — by that
    /// owner, through <see cref="FindOwnedByVerifierHashAsync"/> — so what scopes the delete is never a
    /// statement that named nobody.
    /// </para>
    /// </remarks>
    Task<RecoveryCodeHash?> FindByVerifierHashAsync(
        ReadOnlyMemory<byte> verifierHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The one unredeemed code stored under <paramref name="verifierHash"/> <em>and</em> owned by
    /// <paramref name="userId"/>, or <see langword="null"/> when that account holds no such code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same lookup as <see cref="FindByVerifierHashAsync"/>, for the half of a redemption that
    /// already knows whose account it is.</b> The discovery read omits an owner because a redemption
    /// arrives anonymous and there is none to name yet; by the time there is an entity to spend, the
    /// account the discovery read resolved has been published, so this names it. That is ADR 0011's
    /// rule applied where it applies rather than waived twice for one route.
    /// </para>
    /// <para>
    /// <b>The owner is not belt-and-braces over the hash</b>, the same way
    /// <see cref="FindRecoveryCodeCredentialAsync"/>'s type predicate is not belt-and-braces over its
    /// owner. The discovery read decides <em>whose account this is</em>; this read decides <em>which row
    /// is removed</em>, and naming the owner is what makes the second answer to the first by
    /// construction. Today they cannot disagree — the verifier hash is the primary key and
    /// <c>credentials.user_id</c> is immutable, so one digest names one row of one account — but nothing
    /// in a caller makes them agree, and the failure if they ever stopped is a session established for
    /// one account over a code deleted from another: the unpoliced delete ADR 0014 exists to refuse.
    /// </para>
    /// <para>
    /// <b>A code this does not find is not a distinguishable answer.</b> It is refused exactly as a code
    /// that was never stored and a code already spent are, so a caller cannot learn "that code is real,
    /// but not yours" — which is a fact about what is stored and about somebody else's account at once.
    /// </para>
    /// <para>
    /// The entity rather than its ids, because that is what <see cref="ConsumeAsync"/> takes: this is
    /// the read ADR 0014's third leg names, the one that has to share a transaction with the write
    /// removing what it returned.
    /// </para>
    /// </remarks>
    Task<RecoveryCodeHash?> FindOwnedByVerifierHashAsync(
        Guid userId,
        ReadOnlyMemory<byte> verifierHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Spends one code by removing its row, leaving the set's credential — and every code still on the
    /// card — standing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Consuming is deleting, and there is no second spelling of it.</b> The table holds no
    /// <c>UPDATE</c> grant of any shape, so there is no used flag a bug can clear and no stamp a reader
    /// has to remember to filter on, and nothing is left behind for the erasure remnant vocabulary to
    /// find. FR-054 says a redeemed code is invalidated; a removed row is the spelling of that which
    /// needs no second mechanism to be believed. ADR 0017 holds the decision.
    /// </para>
    /// <para>
    /// <b>It takes the loaded entity and never a hash the caller supplied</b>, per ADR 0014 and for the
    /// reason <see cref="DeleteSetAsync"/> carries. This is an unpoliced <c>DELETE</c> that an
    /// <em>anonymous</em> request performs — the table is exempt — so what bounds it is the application,
    /// and nothing beneath. Taking bytes would let a caller's own input reach a <c>DELETE</c> predicate
    /// directly; taking the entity means the only row that can be removed is one
    /// <see cref="FindOwnedByVerifierHashAsync"/> returned, in the same unit of work, which is the third
    /// of ADR 0014's three legs. That read names the owner, so what bounds this delete is a predicate
    /// over the account and the hash rather than the hash alone.
    /// </para>
    /// <para>
    /// <b>The set's credential is deliberately out of reach here.</b> An emptied set looks like a row
    /// with no purpose, and removing it would cascade away the session the redemption just opened —
    /// <c>sessions</c> references <c>credentials(id, user_id, type)</c> <c>ON DELETE CASCADE</c> — and
    /// it would mean an <em>anonymous</em> request deleting a <c>credentials</c> row, on a table
    /// nothing beneath the application polices. This signature — one code and nothing else — is what
    /// keeps that out of reach, and <see cref="DeleteSetAsync"/> below is what a redemption must never
    /// call.
    /// </para>
    /// </remarks>
    Task ConsumeAsync(RecoveryCodeHash hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a set: the credential standing for it, one <see cref="RecoveryCodeHash"/> per code, and
    /// one <see cref="WrappedAccountKeys"/> per code — in one save, so a refusal leaves none of them
    /// behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One save is the rule rather than an optimisation. A credential with no codes is a set that
    /// counts as issued and can never be redeemed, and codes with no credential are unstorable
    /// anyway — the composite foreign key to <c>credentials(id, user_id, type)</c> refuses them.
    /// </para>
    /// <para>
    /// <b>The wrapped keys are a list because a set is ten factors, not one.</b> A passkey is one
    /// credential, one key-encryption key and one pair of envelopes; a set of recovery codes is ten
    /// separate secrets under a single credential, and the client derives a key-encryption key from each
    /// <em>code</em>. A single share for the whole set would seal the account under whichever code that
    /// share belonged to, and nine of the ten would open nothing — the defect ADR 0018 moved this
    /// table's key to <c>factor_id</c> to make storable in the first place.
    /// </para>
    /// <para>
    /// <b>What the wrapped keys buy is that a set holding no copy of the account keys is unreachable
    /// rather than merely uncustomary</b> — the enforceable half of FR-060, and the same guarantee
    /// <see cref="IPasskeyRepository.TryAddAsync"/> carries for a passkey. Both envelope columns are
    /// <c>NOT NULL</c> and every row lands in this save, so there is no partial failure and no second
    /// request that could leave a person holding a printed card whose codes derive a key-encryption key
    /// with nothing to open. It is the <em>set's own</em> credential every row is filed against, never
    /// the passkey that authorized the issue: those two factors derive different key-encryption keys,
    /// and nothing in the schema can tell that mistake from a correct row.
    /// </para>
    /// <para>
    /// A <see cref="WrappedAccountKeys"/> whose factor identifier is already stored raises
    /// <see cref="Domain.Common.ConflictException"/>. The value is client-minted and unique across the
    /// whole table, so at 122 random bits a collision means a reused identifier or a replayed request
    /// rather than an accident. Two rows of one list colliding with each other is the same violation and
    /// would arrive here as the same conflict, which is why the caller refuses a set repeating an
    /// identifier before it reaches this call — by then the previous set is already gone.
    /// </para>
    /// </remarks>
    Task AddSetAsync(
        Credential credential,
        IReadOnlyList<RecoveryCodeHash> hashes,
        IReadOnlyList<WrappedAccountKeys> wrappedAccountKeys,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a set: the <c>credentials</c> row, and with it every unredeemed code hanging off it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It takes the loaded entity and never an id</b>, per ADR 0014, and the guarantee that buys is
    /// the narrow one <see cref="IPasskeyRepository.DeletePasskeyAsync"/> spells out:
    /// <see cref="Credential.CreateRecoveryCodes"/> is public, so a fabricated credential can certainly
    /// reach this call — but it carries a freshly minted <c>Guid.CreateVersion7()</c> naming no stored
    /// row, so the delete matches nothing rather than taking a stranger's set. The one read producing a
    /// credential the table actually holds is <see cref="FindRecoveryCodeCredentialAsync"/>, and it
    /// carries the owner in its predicate.
    /// </para>
    /// <para>
    /// <b>The set's <c>recovery_code_hashes</c> rows are not named here, and must never be
    /// materialised on this path.</b> They leave by the database's own <c>ON DELETE CASCADE</c>, which
    /// runs with the referencing table owner's privileges. Load them and EF cascades into the copies it
    /// can see and emits its own <c>DELETE FROM recovery_code_hashes</c> — and unlike <c>sessions</c>,
    /// the app role <em>is</em> granted <c>DELETE</c> there, so that statement quietly succeeds and the
    /// rows leave by the application instead of by the cascade, with no SQLSTATE to say so. This
    /// signature — the set's credential and nothing else — is what keeps the temptation out of reach.
    /// </para>
    /// </remarks>
    Task DeleteSetAsync(Credential credential, CancellationToken cancellationToken = default);
}
