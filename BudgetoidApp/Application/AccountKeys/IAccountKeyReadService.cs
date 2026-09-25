namespace Application.AccountKeys;

/// <summary>
/// The read side of an account's key custody: which envelopes its factors hold, and which generation of
/// which manifest names that set.
/// </summary>
/// <remarks>
/// <para>
/// <b>It answers at two levels, and the member returns both because its consumer's job is to compare
/// them.</b> The factor rows come from <c>wrapped_account_keys</c>; the manifest and its epoch are one
/// row of <c>factor_manifests</c>, or the absence of one. <see cref="AccountKeyCustody" /> carries the
/// argument for why they arrive together rather than through two members a concurrent write can sit
/// between, and for why an absent manifest is <see langword="null" /> at epoch <c>0</c> rather than an
/// error. <b>One member is necessary for that and does not achieve it</b>: what makes the two halves
/// describe one instant is that an implementation reads them in <b>one statement</b>, which the shipped
/// one does and which this signature cannot compel. <b>An account registered before the writer existed
/// still answers <see langword="null" /> at <c>0</c>, and that is not an error</b>: registration now
/// writes the first manifest at epoch 1 in the same save as the account, so an account created since
/// answers bytes at 1 — but nothing backfills the accounts created before it, and no migration can,
/// because a manifest is sealed under a content key this server has never held.
/// </para>
/// <para>
/// <b>The member is still called <c>ListForAccountAsync</c> although it no longer returns a bare
/// list.</b> Renaming it is the honest move and is deliberately not made here: four documentation
/// chapters name this member by that spelling, and this repository's rule is that a doc changes in the
/// commit that invalidates it. A rename belongs in the commit that can carry them.
/// </para>
/// <para>
/// A read service rather than a member on a repository, the split <c>ICredentialReadService</c> and
/// <c>IUserAccountReadService</c> both state: a repository loads entities that rules are applied to, and
/// this projects columns for a response. The split earns more here than on either of them, because on
/// this table materializing an entity is itself a hazard — the application role holds <b>no
/// <c>DELETE</c></b> on <c>wrapped_account_keys</c>, so a tracked row that EF later decides to cascade
/// into dies with <c>42501</c>. That is the never-materialise rule <c>GenerateRecoveryCodesHandler</c>
/// carries, and this port is the shape that keeps a display read from ever being the thing that trips it.
/// </para>
/// <para>
/// <b>The <c>DELETE</c> is the whole of that premise, and it is the half of it that is left.</b> The
/// role holds a column-listed <c>UPDATE</c> over one of this table's two payload columns — the one a
/// content-key rotation rewrites, carrying the account's content and index keys <em>encapsulated
/// to</em> the factor's public key — argued in <c>app-role-grants.sql</c> where it is granted, so an
/// edit to that column on a materialized row would now <em>commit</em> rather than die with
/// <c>42501</c>. The other payload is the factor's own private key <em>wrapped under</em> the
/// key-encryption key that factor derives; a rotation re-encapsulates to the same factor public keys
/// and never touches that key, so there is nothing to rewrite, and the column is left off the list
/// rather than clawed back afterwards — in this schema an omission from a <c>GRANT UPDATE</c> column
/// list <em>is</em> the immutability, never a <c>REVOKE</c>. The rule above binds harder for that, not
/// less: a row this context is holding is a row two mistakes can reach, only one of them still
/// announces itself, and whether a stray write is the loud kind or the silent one now turns on which
/// column it lands in. Nothing assigns either payload today — the entity exposes no mutator for them —
/// so what moved is what the mistake would cost, not how near it is.
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
    /// What the account <paramref name="userId" /> names holds: its manifest of factor public keys and
    /// that manifest's generation, and every wrapped-key row filed across all of its credentials —
    /// <b>one</b> row per registered passkey and <b>ten</b> per set of recovery codes — ordered by
    /// <see cref="FactorEnvelopes.FactorId" />. The list is empty when the account holds no factor row
    /// this request can see; the manifest is <see langword="null" /> at epoch <c>0</c> when it has no
    /// manifest row, which is every account registered before registration began writing one.
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
    /// argument for not turning it into a refusal belongs, and where the causes are enumerated. The same
    /// holds one level up: an account with no manifest is answered, not refused, and the two absences are
    /// independent — a factor list can be full while the manifest is missing, which is the state every
    /// account in every database is in today.
    /// </para>
    /// <para>
    /// <b>The owner argument scopes both halves, and the second half is policed by the same policy.</b>
    /// <c>factor_manifests</c> is policed by <c>user_isolation</c> on <c>user_id</c> exactly as
    /// <c>wrapped_account_keys</c> is, so the paragraph above about a policy answering <em>empty</em>
    /// rather than <em>correct</em> covers the manifest read as well as the factor read, and the
    /// shipped implementation names the owner on both arms; there is no second owner and nothing here
    /// takes one.
    /// </para>
    /// </remarks>
    Task<AccountKeyCustody> ListForAccountAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
