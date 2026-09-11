namespace Domain.Users;

/// <summary>
/// The staging side of a content-key rotation: which factors an account's live passkeys are filed
/// under, and the one row — if any — that account has staged.
/// </summary>
/// <remarks>
/// <para>
/// <b>A repository rather than a read service, even though the first member answers a question out of
/// columns.</b> The split <c>ICredentialReadService</c> states is about what the caller does with the
/// answer, and here the answer is an entity a rule is applied to: <see cref="KeyRotation.Begin" />
/// takes the loaded <see cref="Credential" /> rather than an owner id, so the factor listing has to
/// hand back credentials or the factory's whole argument collapses. Splitting it would mean a read
/// service answering factor identifiers and a repository re-loading the credential they name, which is
/// two reads and one more place for the owner predicate to be forgotten.
/// </para>
/// <para>
/// <b>Everything this port answers is scoped by an explicit owner, and none of it rides a policy.</b>
/// <c>wrapped_account_keys</c> and <c>key_rotations</c> are both policed by <c>user_isolation</c>, so
/// in production a query that lost its predicate answers <em>empty</em> rather than answering somebody
/// else's rows — and empty is the dangerous answer on both members here. An empty factor listing
/// refuses a valid begin; an empty staged-rotation lookup is what turns a replacement into an insert.
/// The predicates are therefore written, exactly as <c>ExportReadService</c> writes its own over
/// <c>budgets</c>, and are not redundant beside the policy.
/// </para>
/// </remarks>
public interface IKeyRotationRepository
{
    /// <summary>
    /// The account's <b>live passkey</b> factors, keyed on factor id and valued by the passkey each one
    /// is filed against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Passkey factors and nothing else, and the exclusion is the contract rather than a filter an
    /// implementation chose.</b> A set of recovery codes is ten factors under one credential, so "the
    /// factor this rotation began under" would have ten answers and the client would have to pick one
    /// without proving it holds the matching code; and a begin is gated on a server-verified passkey
    /// assertion, which a set of codes cannot produce. <see cref="KeyRotation.Begin" /> refuses a
    /// non-passkey credential for the same reasons from the other end. The consequence is deliberate
    /// and <c>docs/business-logic/key-rotation.md</c> records it as a position: somebody who lost their
    /// authenticator and signed in with a code must register a replacement passkey before they can
    /// begin a rotation.
    /// </para>
    /// <para>
    /// <b>A dictionary rather than a list, because the caller has one question and it is a lookup.</b>
    /// A begin names a factor and has to decide two things about it — that the account holds it, and
    /// which credential it is filed against — and a list would make the second a second search. Keyed
    /// on the factor because <c>wrapped_account_keys.factor_id</c> is that table's primary key, so the
    /// key of this dictionary cannot collide for the same reason two rows cannot.
    /// </para>
    /// <para>
    /// <b>The set it answers is what a begin is judged against in both directions.</b> A caller must
    /// not read it as "is the factor I named one of these" — see
    /// <c>BeginKeyRotationHandler</c>, which states what the weaker reading costs on the day an account
    /// holds two passkeys.
    /// </para>
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, Credential>> ListPasskeyFactorsAsync(
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
    /// Stages <paramref name="rotation" /> as the account's rotation in flight, <b>replacing</b>
    /// whatever that account had staged before.
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
    /// <b>What a caller owes in exchange is one instance.</b> The <see cref="KeyRotation" /> handed to
    /// every attempt of one begin must be the same object, built before the delegate is entered —
    /// which is where the clock should be read anyway, so that a replayed begin stamps one instant
    /// rather than whenever the surviving attempt happened to run. Minting a fresh instance per attempt
    /// hands an adapter a second object with the same primary key while the first is still tracked,
    /// which EF refuses by name.
    /// </para>
    /// <para>
    /// <b>It saves.</b> A begin writes one row and nothing else, so there is no second repository to
    /// stay in step with and no reason to make the caller ask for a save it could forget.
    /// </para>
    /// </remarks>
    Task StageAsync(KeyRotation rotation, CancellationToken cancellationToken = default);
}
