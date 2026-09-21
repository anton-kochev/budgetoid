namespace Domain.Users;

/// <summary>
/// The staging side of a content-key rotation: which factors an account holds, and the one staged
/// generation — a row and its per-factor seals — that account has in flight.
/// </summary>
/// <remarks>
/// <para>
/// <b>A repository rather than a read service, even though the first member answers a question out of
/// columns.</b> The split <c>ICredentialReadService</c> states is about what the caller does with the
/// answer, and here the answer is an entity a rule is applied to: <see cref="KeyRotationSeal.For" />
/// takes the loaded <see cref="WrappedAccountKeys" /> row rather than a factor id, so the factor
/// listing has to hand back those rows or the factory's whole argument collapses. Splitting it would
/// mean a read service answering factor identifiers and a repository re-loading the rows they name,
/// which is two reads and one more place for the owner predicate to be forgotten.
/// </para>
/// <para>
/// <b>Everything this port answers is scoped by an explicit owner, and none of it rides a policy.</b>
/// <c>wrapped_account_keys</c>, <c>key_rotations</c> and <c>key_rotation_seals</c> are all policed by
/// <c>user_isolation</c>, so in production a query that lost its predicate answers <em>empty</em>
/// rather than answering somebody else's rows — and empty is the dangerous answer on every member
/// here. An empty factor listing refuses a valid begin, or worse agrees with an empty seal set; an
/// empty staged-rotation lookup is what turns a replacement into an insert. The predicates are
/// therefore written, exactly as <c>ExportReadService</c> writes its own over <c>budgets</c>, and are
/// not redundant beside the policy.
/// </para>
/// </remarks>
public interface IKeyRotationRepository
{
    /// <summary>
    /// <b>Every</b> factor the account holds, keyed on factor id and valued by the
    /// <see cref="WrappedAccountKeys" /> row that <em>is</em> the factor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every factor, and the widening is the contract rather than a filter an implementation
    /// dropped.</b> This member used to answer passkey factors alone, and the exclusion was right while
    /// a begin named <em>one</em> factor: "the factor this rotation began under" has no answer for a set
    /// of recovery codes, which is ten factors under one credential, and a set of codes produces no
    /// assertion for the gate to verify. A run no longer names one factor — it stages one
    /// <see cref="KeyRotationSeal" /> per factor — so passkey-only has become the dangerous answer
    /// rather than the careful one. A begin that staged a seal for the passkey and skipped the ten code
    /// factors would be judged complete against a listing naming one, promote, overwrite
    /// <see cref="WrappedAccountKeys.EncapsulatedAccountKeys" /> for the passkey, and leave every
    /// recovery code holding a copy of a content key that opens nothing — a card the person still has,
    /// still enrolled, that can no longer unlock the account. Nothing would report it.
    /// </para>
    /// <para>
    /// <b>The values are the entity because <see cref="KeyRotationSeal.For" /> takes a loaded
    /// row.</b> That factory reads the owner off the rotation and the factor off the loaded
    /// <see cref="WrappedAccountKeys" />, which gives it two statements of an owner from two sources and
    /// so lets it <b>refuse when they disagree</b> — a refusal no signature taking loose ids can make at
    /// all. A <see cref="Credential" /> value cannot feed it, and it could not stand in for a factor
    /// anyway: a set of recovery codes is ten factors under one credential, so ten keys of this
    /// dictionary would answer with the same object.
    /// </para>
    /// <para>
    /// <b><see cref="KeyRotation.Begin" /> still takes the loaded <see cref="Credential" />, and it no
    /// longer comes from here.</b> It is the credential the re-authentication gate verified an assertion
    /// against, handed to the caller by that gate — strictly stronger than one picked out of this
    /// listing, which is arbitrary the day an account holds two passkeys and arbitrary invisibly: the
    /// wrong one is a perfectly good passkey of the right account.
    /// </para>
    /// <para>
    /// <b>A dictionary rather than a list, because the caller's question is a lookup.</b> A begin has to
    /// decide two things about each factor a client named — that the account holds it, and which row a
    /// seal is to be built against — and a list would make the second a second search. Keyed on the
    /// factor because <c>wrapped_account_keys.factor_id</c> is that table's primary key, so the key of
    /// this dictionary cannot collide for the same reason two rows cannot.
    /// </para>
    /// <para>
    /// <b>The set it answers is what a begin is judged against in both directions.</b> A caller must
    /// not read it as "is the factor I named one of these" — see
    /// <c>BeginKeyRotationHandler</c>, which states what each direction costs — and must not read an
    /// <em>empty</em> answer as a set either. An account holding no factor at all is not a state any
    /// path produces: registration files eleven in one save, and every path that moves a factor set
    /// replaces rather than empties it. So empty here is a read that lost its owner predicate, and two
    /// empty sets compare equal.
    /// </para>
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, WrappedAccountKeys>> ListFactorsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same set <see cref="ListFactorsAsync" /> answers, <b>tracked</b> — the instances a promotion
    /// mutates and a save flushes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE TWO READS ARE NOT INTERCHANGEABLE AND THE NAMES ARE WHAT SAY SO.</b>
    /// <see cref="ListFactorsAsync" /> is <c>AsNoTracking</c>, which is what makes it safe to materialise
    /// an entity on a table this role holds no <c>DELETE</c> on; this one is the identity map, and it
    /// exists because <see cref="WrappedAccountKeys.Promote" /> has to be called on the instance the save
    /// will look at. A completion that promoted through the untracked read would mutate objects nothing
    /// flushes, emit not one <c>UPDATE</c>, and answer <c>200</c> having moved nothing — no exception, no
    /// SQLSTATE, and an account whose manifest names a generation none of its factors holds. Two members
    /// with one name and a <see langword="bool" /> would leave a reader picking between them by coin
    /// flip, which is why they are two.
    /// </para>
    /// <para>
    /// <b>What a caller owes in exchange</b> is the care every tracked read on this aggregate owes: these
    /// rows may be mutated and saved, and they may never be removed. The application role holds no
    /// <c>DELETE</c> on <c>wrapped_account_keys</c> at all, so a tracked row EF later decides to cascade
    /// into dies with <c>42501</c> — the hazard <c>GenerateRecoveryCodesHandler</c> carries at length.
    /// </para>
    /// <para>
    /// <b>Every other sentence <see cref="ListFactorsAsync" /> writes applies here unchanged</b> — every
    /// factor and not only the passkeys, keyed on the factor because <c>factor_id</c> is the primary key,
    /// the owner predicate written rather than left to <c>user_isolation</c>, and an <em>empty</em> answer
    /// read as a query that lost its scoping rather than as an account holding no factor.
    /// </para>
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, WrappedAccountKeys>> TrackFactorsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The seals <paramref name="userId" /> currently has staged, keyed on the factor each was
    /// encapsulated to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Keyed rather than listed, because the caller's question is a lookup</b> — which seal does
    /// <em>this</em> factor adopt. A list would make a completion search for each factor's seal, and the
    /// shortest spelling of that search is no search at all: walk the two sequences side by side and zip
    /// them. That gives every row a well-formed value of the right width and the right version that only
    /// some other factor's private key can open, and neither set carries an <c>ORDER BY</c> for the zip
    /// to be right about. The key cannot collide for the reason two rows cannot:
    /// <c>key_rotation_seals</c> is keyed on <c>(user_id, factor_id)</c> and an account has at most one
    /// run.
    /// </para>
    /// <para>
    /// <b>It answers the account's staged seals and never a particular run's</b>, because there is only
    /// ever one: the seals hang off a parent keyed on the account. A caller that needs to know
    /// <em>which</em> run staged them asks <see cref="FindStagedRotationAsync" />, which is the member
    /// carrying the identifier.
    /// </para>
    /// <para>
    /// <b>An empty answer is the dangerous one here too.</b> Two empty sets compare equal, so a read that
    /// lost its owner predicate agrees with an account holding no factor — and <c>user_isolation</c> makes
    /// a wrong query answer empty rather than wrong. The predicate is written.
    /// </para>
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, KeyRotationSeal>> ListStagedSealsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The account's one <c>factor_manifests</c> row, <b>tracked</b>, or <see langword="null" /> when it
    /// holds none.
    /// </summary>
    /// <remarks>
    /// <b>The third verbatim copy of a member two other ports already declare, and the duplication is
    /// argued rather than apologised for</b> — see <see cref="IRecoveryCodeRepository" />, where the same
    /// member is written out beside <see cref="IPasskeyRepository" />'s: each repository owns the
    /// statements its own catches read, and a shared one would be a fourth place for a caller to reach a
    /// row through a port that knows nothing about the save it is about to ride on. Tracked, and
    /// <c>AsNoTracking</c> may never be added: <see cref="FactorManifest.Promote" /> compares against the
    /// stored generation and EF builds <c>WHERE rotation_epoch = @original</c> from the value snapshotted
    /// at load, so a detached instance guards nothing and writes nothing.
    /// </remarks>
    Task<FactorManifest?> FindFactorManifestAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the promoted <paramref name="factorManifest" /> and every promoted factor in
    /// <paramref name="factors" /> as one unit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THIS SIGNATURE IS THE ONLY PLACE IN THE PRODUCT THAT SAYS THESE ROWS AND THIS MANIFEST MOVE
    /// TOGETHER.</b> Nothing else states it — not a constraint, not a policy, not a check anywhere below.
    /// A manifest promoted without its factors is an account whose one authenticated statement of its
    /// factor set describes a generation no factor holds; factors promoted without their manifest is the
    /// mirror, and every client that reads the manifest to decide what to encapsulate to is then working
    /// from a set that no longer matches what it can open. Both are unreachable by anything the person
    /// can do next, which is why the two arguments are one call and not two.
    /// </para>
    /// <para>
    /// <b>Both arguments are taken even though an implementation does not have to file either.</b> Every
    /// instance named here was loaded through this port and mutated in place, so a tracker already knows
    /// about them and the save is the whole of the work —
    /// <c>IPasskeyRepository.DeletePasskeyAsync</c> takes its manifest on exactly that footing. What the
    /// parameters buy is that a caller reaching this line without them is a caller that promoted nothing,
    /// and that is the failure worth refusing at the signature.
    /// </para>
    /// <para>
    /// <b>It is where a lost promotion becomes a conflict.</b> The concurrency token on
    /// <c>factor_manifests.rotation_epoch</c> fires when another change to the account's factor set landed
    /// between this call's read and its write, and an implementation answers that with
    /// <c>ConflictKind.FactorSetMoved</c> — the same remedy the two paths that <em>move</em> a factor set
    /// already raise, because the caller's next act is identical: read the account's keys back and run the
    /// ceremony again. It is deliberately not the <c>400</c>
    /// <see cref="FactorManifest.Promote" /> raises over the same rule: that caller's epoch was never one
    /// greater than the stored generation, and this caller's was, at the moment it was read.
    /// </para>
    /// <para>
    /// <b>It deletes nothing, and the staging row is deliberately left standing.</b> The role holds no
    /// <c>DELETE</c> on either rotation table, and the reason is this step: until the live rows are
    /// overwritten the staged seals are the only copies of the new generation, so a tidy-up that ran a
    /// moment early would destroy a generation the account has already been rewritten under. What
    /// therefore has to tell a finished run from a live one is the epoch, and that is the caller's check
    /// rather than this member's.
    /// </para>
    /// </remarks>
    Task PromoteAsync(
        FactorManifest factorManifest,
        IReadOnlyList<WrappedAccountKeys> factors,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The rotation <paramref name="userId" /> currently has staged, or <see langword="null" /> when it
    /// has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is not the thing a begin consults before staging, and must not become it.</b>
    /// <c>key_rotations.user_id</c> is the primary key precisely so that "at most one rotation in
    /// flight per account" is held declaratively; a handler that read this and then decided whether to
    /// write would be two statements with a window between them, and the window is exactly wide enough
    /// for the second browser tab. <see cref="KeyRotation" />'s own remarks argue that at length. What
    /// this member is for is the steps <em>after</em> a begin — a chunk saying which run it is
    /// continuing, and a completion saying which staged generation it is promoting — each of which has
    /// a rotation identifier to check the answer against.
    /// </para>
    /// <para>
    /// <b>A row here no longer means a run is in flight, and a completion is what changed that.</b>
    /// <see cref="PromoteAsync" /> deletes nothing, so a finished run leaves its row standing carrying
    /// the identifier the client is still quoting. What tells the two apart is the epoch: a live run's
    /// <see cref="KeyRotation.StagedRotationEpoch" /> is above the generation the account's manifest
    /// holds, and a completed one's is equal to it.
    /// </para>
    /// </remarks>
    Task<KeyRotation?> FindStagedRotationAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages <paramref name="rotation" /> and its <paramref name="seals" /> as the account's rotation
    /// in flight, <b>replacing</b> whatever that account had staged before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Replacement is this port's promise and not a convenience of one adapter.</b> Begin is the
    /// repair path: when a completion refuses because the account's live factor set moved — a passkey
    /// registered or revoked while a run was in flight — the only way forward is a begin carrying the
    /// corrected set. Answer that with a conflict and the client is left holding a staged row it cannot
    /// replace and a rotation it cannot finish, with no route that removes either. So an implementation
    /// is an <b>upsert</b>: it finds the account's row and updates it, or inserts one when there is
    /// none. A blind add is the shape that passes every unit test over an in-memory dictionary and
    /// meets <c>23505</c> on <c>PK_key_rotations</c> the first time a person begins twice.
    /// </para>
    /// <para>
    /// <b>Replacement reaches the children one by one, because the <c>ON DELETE CASCADE</c> from
    /// <c>key_rotations</c> does not fire on this path.</b> That cascade runs when the parent <em>row is
    /// deleted</em>, and a second begin <em>updates</em> that row in place — so a factor the previous run
    /// also sealed keeps its row and has its value rewritten, rather than the set being replaced
    /// wholesale.
    /// </para>
    /// <para>
    /// <b>It removes nothing, and it does not need to.</b> A seal for a factor the caller did not supply
    /// would have to be a seal whose <c>wrapped_account_keys</c> row is gone — the caller's own gate has
    /// established the supplied set equals the account's live factors — and the composite
    /// <c>ON DELETE CASCADE</c> from that table took the seal when the row went. An implementation may
    /// therefore hold no <c>DELETE</c> on <c>key_rotation_seals</c>, and the day a caller is allowed to
    /// supply a <em>subset</em> of the account's factors is the day that stops being true.
    /// </para>
    /// <para>
    /// <b>Convergence under replay is part of the promise, and it is why the burden sits here.</b>
    /// <c>ITransactionalExecutor</c> replays the unit of work under a retrying execution strategy,
    /// against a database that rolled the abandoned attempt back and a change tracker that did not — so
    /// this member is called more than once for one begin and must leave one row whichever attempt
    /// survives. The sibling handlers answer that hazard with
    /// <c>IPersistenceState.DiscardTrackedEntities</c> at the top of their delegate; this port answers
    /// it instead, so that a caller cannot get it wrong by omission and no handler has to hold a
    /// dependency it reads for one line.
    /// </para>
    /// <para>
    /// <b>What a caller owes in exchange is one instance — now one instance <em>set</em>.</b> The
    /// <see cref="KeyRotation" /> and every <see cref="KeyRotationSeal" /> handed to every attempt of
    /// one begin must be the same objects, built before the delegate is entered — which is where the
    /// clock should be read anyway, so that a replayed begin stamps one instant rather than whenever the
    /// surviving attempt happened to run. Minting fresh instances per attempt hands an adapter a second
    /// object with the same primary key while the first is still tracked, which EF refuses by name.
    /// Building the seals there also puts every refusal <see cref="KeyRotationSeal.For" /> can raise
    /// before anything is written.
    /// </para>
    /// <para>
    /// <b>It saves, and the parent and its children go in one save.</b> A begin writes this account's
    /// staged generation and nothing else, so there is no second repository to stay in step with and no
    /// reason to make the caller ask for a save it could forget — and a run whose row committed without
    /// its seals, or the other way round, is a staged generation that cannot be completed.
    /// </para>
    /// </remarks>
    Task StageAsync(
        KeyRotation rotation,
        IReadOnlyList<KeyRotationSeal> seals,
        CancellationToken cancellationToken = default);
}
