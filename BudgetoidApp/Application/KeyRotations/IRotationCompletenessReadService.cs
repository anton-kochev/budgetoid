namespace Application.KeyRotations;

/// <summary>
/// The read a content-key rotation's completion step consults before it promotes the staged wrapped
/// keys: has every narrative-bearing row of this account already been rewritten under the new keys?
/// </summary>
/// <remarks>
/// <para>
/// <b>What hangs on the answer is total, and in one direction only.</b> Promotion overwrites the live
/// wrapped keys in place and destroys the only copies of the generation still in force —
/// <c>docs/business-logic/key-rotation.md</c> argues under "Why staging" that no ordering exists in
/// which the old keys survive it. A <see langword="false" /> that should have been
/// <see langword="true" /> costs a rotation that has to be resumed. A <see langword="true" /> that
/// should have been <see langword="false" /> costs every row that was still holding old-key ciphertext,
/// permanently, with nothing thrown and nothing logged.
/// </para>
/// <para>
/// <b>A read service rather than a member on a repository</b>, the split <c>ICredentialReadService</c>
/// and <c>IAccountKeyReadService</c> both state: a repository loads entities that rules are applied to,
/// and this answers a question out of columns. Here the split also keeps the completion step from ever
/// holding the rows it is about to decide the fate of — the question is about an entire account, and
/// materialising an account's worth of tracked entities to ask it would be the one shape this path must
/// not build.
/// </para>
/// <para>
/// <b>The answer is about stamps and about nothing else, and that is narrower than it sounds.</b> What a
/// full house establishes is that every narrative-bearing row was written by a statement this rotation
/// issued. Not that the bytes are right: the server holds no key and can check no ciphertext, so
/// "re-sealed under the new content key" is the browser's to prove and this read cannot contribute to
/// it. What it buys is the one property the destructive step needs — that no row was left unwritten.
/// </para>
/// <para>
/// <b>It refuses rather than answering for part of an account.</b> The question is about an account;
/// five of the six sets it reads are scoped to the <em>ambient</em> budget by a query filter that takes
/// no argument. So unless the set of budgets the account owns is exactly the ambient budget, an
/// implementation throws <see cref="RotationScopeException" /> — set equality in both directions, never
/// a count. This is <c>ExportDataHandler</c>'s rule and <c>docs/business-logic/export.md</c> is the
/// authority for it; what differs is only the price of getting it wrong, which here is the account.
/// </para>
/// <para>
/// <b>It is a snapshot and not a lock.</b> Nothing here holds the account still: a second tab can clear
/// a stamp the instant after this returns, which is exactly what an ordinary narrative write does. That
/// is a property of the promotion's own transaction rather than of this read, and an implementation must
/// not grow a lock, a hint or a transaction of its own to paper over it.
/// </para>
/// </remarks>
public interface IRotationCompletenessReadService
{
    /// <summary>
    /// Whether every row of the account <paramref name="userId" /> names that <em>carries a narrative
    /// value</em> is stamped with <paramref name="rotationId" />, across the six tables that hold a
    /// stamp — <c>budgets</c>, <c>accounts</c>, <c>payees</c>, <c>category_groups</c>, <c>categories</c>
    /// and <c>transactions</c>.
    /// </summary>
    /// <param name="userId">
    /// The account being rotated. Not redundant beside the isolation policies — see below.
    /// </param>
    /// <param name="rotationId">The run in flight, as <c>KeyRotation.RotationId</c> spells it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// <see langword="true" /> when no row is outstanding, which is the only answer that lets a
    /// promotion run.
    /// </returns>
    /// <exception cref="RotationScopeException">
    /// The account owns a budget other than the one this request operates inside, or operates inside a
    /// budget it does not own — either way the read cannot see every row the question is about, so it
    /// refuses rather than answering for the part it can reach. See that type; the rule and its
    /// spelling are the export's, argued in <c>docs/business-logic/export.md</c>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>"Not this rotation" must reach PostgreSQL as <c>IS DISTINCT FROM</c>, and that is the whole of
    /// the correctness here.</b> The stamp is nullable, so <c>rotation_id &lt;&gt; @current</c> over an
    /// untouched row answers <c>NULL</c> rather than <c>true</c> and the row drops silently out of "rows
    /// still to do" — on a first rotation that is every row in the account, so the gate answers "complete"
    /// before a single chunk has run. <c>BudgetConfiguration</c> records the same trade where the column
    /// is declared. <b>Written as EF LINQ, <c>row.RotationId != rotationId</c> is safe</b>: EF Core's null
    /// compensation emits the <c>IS DISTINCT FROM</c> semantics, measured rather than assumed. The
    /// spelling that is not safe is <c>RotationId.HasValue &amp;&amp; RotationId.Value != rotationId</c>,
    /// which reads as a careful null guard and drops every never-stamped row; it must not be written here
    /// in any form. <c>RotationCompletenessTests</c> holds that with a case for an account that has never
    /// been rotated.
    /// </para>
    /// <para>
    /// <b>The rule is presence-aware: a row carrying no narrative value at all needs no stamp and must not
    /// block.</b> A transaction with no note has nothing to re-seal, so a chunk never visits it and a
    /// finished rotation leaves it exactly as it was. The first reason is scale — an account of ten
    /// thousand transactions, most of them without a note, would otherwise owe ten thousand writes whose
    /// only effect is to satisfy this read — and the second is that it is also right in the case that
    /// costs something: a note <em>added</em> mid-rotation by a second tab arrives as
    /// narrative-with-no-stamp, which is outstanding under this rule and blocks. Dropping the
    /// presence test would not make the gate stricter in any useful direction; it would make a rotation
    /// on an ordinary account unable to finish, which is the failure
    /// <c>docs/business-logic/key-rotation.md</c> calls harder to diagnose than a crash.
    /// </para>
    /// <para>
    /// <b>Which columns count as narrative is a fact about the schema and not a list to maintain here.</b>
    /// Four of the eight sealed columns are nullable, and only two of them are the whole of their row's
    /// narrative: <c>budgets.name</c> and <c>transactions.description</c>. On <c>category_groups</c> and
    /// <c>categories</c> a nullable description sits beside a required name, and <c>accounts</c> and
    /// <c>payees</c> carry a required name and nothing else — so rows in those four tables always bear a
    /// narrative value and always owe a stamp.
    /// </para>
    /// <para>
    /// <b>The account is an explicit parameter because <c>budgets</c> carries no query filter.</b> Five of
    /// the six sets carry <c>BudgetIsolation</c>, which the banned-symbols list makes impossible to switch
    /// off, so they are scoped to the ambient budget whether or not an implementation thinks about it.
    /// <c>budgets</c> carries none — it is what registration writes and what session authentication reads
    /// before any budget is ambient — so the owner predicate on that one set is written by hand or not at
    /// all. <c>ExportReadService</c> makes and documents the same split. The <c>user_isolation</c> policy
    /// sits under it in production and does not replace it, for the reason that file gives: a policy makes
    /// a wrong query answer <em>empty</em>, not <em>correct</em>, and an empty answer here reads as
    /// "complete".
    /// </para>
    /// </remarks>
    Task<bool> EveryNarrativeRowIsStampedAsync(
        Guid userId,
        Guid rotationId,
        CancellationToken cancellationToken = default);
}
