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
    /// The rotation <paramref name="userId" /> currently has staged, or <see langword="null" /> when it
    /// has none.
    /// </summary>
    /// <remarks>
    /// <b>It is not the thing a begin consults before staging, and must not become it.</b>
    /// <c>key_rotations.user_id</c> is the primary key precisely so that "at most one rotation in
    /// flight per account" is held declaratively; a handler that read this and then decided whether to
    /// write would be two statements with a window between them, and the window is exactly wide enough
    /// for the second browser tab. <see cref="KeyRotation" />'s own remarks argue that at length. What
    /// this member is for is the steps <em>after</em> a begin — a chunk saying which run it is
    /// continuing, and a completion saying which staged generation it is promoting — each of which has
    /// a rotation identifier to check the answer against.
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
