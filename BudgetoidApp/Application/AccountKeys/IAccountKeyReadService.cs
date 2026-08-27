namespace Application.AccountKeys;

/// <summary>
/// The read side of <c>wrapped_account_keys</c>: which envelopes an account's factors hold.
/// </summary>
/// <remarks>
/// <para>
/// A read service rather than a member on a repository, the split <c>ICredentialReadService</c> and
/// <c>IUserAccountReadService</c> both state: a repository loads entities that rules are applied to, and
/// this projects columns for a response. The split earns more here than on either of them, because on
/// this table materializing an entity is itself a hazard — the application role holds <b>no
/// <c>UPDATE</c> and no <c>DELETE</c></b> on <c>wrapped_account_keys</c>, so a tracked row that EF later
/// decides to cascade into dies with <c>42501</c>. That is the never-materialise rule
/// <c>GenerateRecoveryCodesHandler</c> carries, and this port is the shape that keeps a display read
/// from ever being the thing that trips it.
/// </para>
/// <para>
/// <b>An implementation therefore projects; it does not load and map.</b> The rows come back through a
/// <c>Select</c> into <see cref="FactorEnvelopes" /> — never <c>ToListAsync</c> on the entity set
/// followed by a mapping step, which is the same statement plus a change tracker holding the one kind of
/// row this role cannot delete. <b>Nothing fails today if that is got wrong</b>, because a read-only
/// request has no cascade to walk; it fails on whichever later request removes a credential through the
/// same context. This paragraph is the only thing standing between those two moments.
/// </para>
/// <para>
/// <b>The unit this port reads by is the <em>account</em>, and no member may be added that narrows it to
/// a credential.</b> The keys belong to the account rather than to any one way into it: a factor's
/// envelopes open under a key-encryption key derived from <em>that factor</em>, and which factor a
/// browser is holding is decided by a ceremony rather than by the session it is asking on. A signature
/// taking a credential id makes the wrong read the easy one to write, which is how the narrow shape
/// arrived the first time. <c>GetAccountKeysHandler</c> carries the argument in full.
/// </para>
/// <para>
/// <b>No paging, and the reason is the domain rather than the size of the table.</b> The answer is
/// bounded by how many factors one account can have — one per registered passkey, ten per set of
/// recovery codes — so it is a fixed small set rather than an unbounded collection that happens to be
/// short today. Paging it would also break the only consumer there will be: the client opens the account
/// by trying each pair in turn until one authenticates, which needs the whole set in hand. A page size
/// arriving here later would be a way to hand somebody nine of their ten ways back in.
/// </para>
/// <para>
/// <b>No member here may take, return or imply an unwrapped key.</b> The server cannot open either
/// envelope and holds nothing that could; a port member suggesting otherwise would be an invitation to
/// add the one thing <c>docs/business-logic/account-keys.md</c> forbids.
/// </para>
/// </remarks>
public interface IAccountKeyReadService
{
    /// <summary>
    /// Every wrapped-key row filed on the account <paramref name="userId" /> names, across all of its
    /// credentials — <b>one</b> row per registered passkey and <b>ten</b> per set of recovery codes —
    /// ordered by <see cref="FactorEnvelopes.FactorId" />. Empty when the account holds none this
    /// request can see.
    /// </summary>
    /// <param name="userId">The account whose rows may be read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// <para>
    /// <b>The owner is an explicit parameter even though <c>user_isolation</c> polices this table, and it
    /// is not redundant.</b> It is the same argument <c>ExportReadService</c> makes for its
    /// <c>budgets</c> predicate: a policy makes a wrong query answer <em>empty</em>, not
    /// <em>correct</em>. The policy appends <c>user_id = current_setting('app.current_user_id')</c>
    /// underneath whatever this statement asks for, so an implementation that named no owner at all
    /// would still never return another account's rows — and would still be wrong, because the scoping
    /// would then exist in exactly one layer, the one that fails open when a policy is missed on a table
    /// added later. Naming the owner is also what makes the statement selective rather than a scan the
    /// policy then filters: <c>IX_wrapped_account_keys_user_id</c> exists for precisely this predicate,
    /// and <c>WrappedAccountKeysConfiguration</c> says so where it declares it.
    /// </para>
    /// <para>
    /// <b>The list is the contract and the count is not.</b> A passkey carries one factor; a set of
    /// recovery codes carries ten, because each code derives its own key-encryption key and a person
    /// redeems whichever one they still have — so an ordinary account holding both is eleven rows across
    /// two credentials. An implementation reaching for <c>SingleOrDefault</c> against this table would
    /// work perfectly for every account holding one passkey and nothing else, and the discovery happens
    /// in a browser, months later, on the day somebody who has lost their authenticator finds their
    /// account still locked.
    /// </para>
    /// <para>
    /// <b>Ordering is by <see cref="FactorEnvelopes.FactorId" /> because it is the primary key</b>, so no
    /// two rows can tie and no second sort key is needed to make repeated reads of unchanged data agree.
    /// What is promised is that determinism and nothing about the <em>particular</em> sequence; the
    /// reason usually given for that is false, and the corrected version — including what
    /// <see cref="Guid.ToByteArray()" /> does that <see cref="Guid.CompareTo(Guid)" /> does not — is
    /// stated once on <c>GetAccountKeysHandler</c> rather than restated here. Nothing downstream depends
    /// on the order at all: the client tries each pair in turn, and the associated data decides which one
    /// opens.
    /// </para>
    /// <para>
    /// <b>An empty answer is a normal answer</b> — see <c>GetAccountKeysHandler</c>, which is where the
    /// argument for not turning it into a refusal belongs, and where the causes are enumerated.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<FactorEnvelopes>> ListForAccountAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
