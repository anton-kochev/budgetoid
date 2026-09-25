namespace Application.KeyRotations;

/// <summary>
/// The completeness gate cannot see every narrative row the account owns, and refuses rather than
/// answering for the part it can reach.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the export's refusal, on the read whose wrong answer costs more.</b>
/// <c>ExportDataHandler</c> throws <c>ExportCompletenessException</c> when the set of budgets the user
/// owns is not exactly the ambient budget, because the five collection reads beneath it are scoped to
/// that one budget and a document assembled anyway would be a truncation shipped green. The gate has
/// the identical shape and the identical fix — <c>docs/business-logic/export.md</c> argues it in full
/// and is the authority — with one difference in what a wrong answer buys: a truncated export is a bad
/// file somebody can download again, while a gate that answers <see langword="true" /> for half an
/// account lets the promotion destroy the only wrapped copies of the key the other half is sealed
/// under. There is no second attempt.
/// </para>
/// <para>
/// <b>Both directions, because they fail differently</b>, and this is the half a <c>Count &gt; 1</c>
/// guard misses. Owning a budget the request is not inside means rows this rotation never rewrote are
/// invisible to the gate. Being inside a budget the user does not own means the gate is reading
/// somebody else's rows and reporting them as this account's progress. Set equality refuses both; a
/// count refuses only the first.
/// </para>
/// <para>
/// <b>It deliberately has no <c>IExceptionHandler</c> of its own</b>, for the reasons
/// <c>ExportCompletenessException</c> sets out where it makes the same choice: it falls to the
/// catch-all and surfaces as a 500 <c>application/problem+json</c>. 404 would claim the account does
/// not exist; 400 would blame a request with no field to correct; 409 would imply a resolution the
/// client can perform, and no endpoint creates, deletes or selects a budget. A <em>named</em> 5xx
/// mapping is the seam a later reader softens into "rotate the budget we can see and warn" — which is
/// precisely the truncation this refusal exists to prevent. Left on the catch-all, the only way to
/// change the answer is to change the throw.
/// </para>
/// <para>
/// <b>The message names counts and never identifiers</b>, the rule its sibling states: the Development
/// branch of <c>GlobalExceptionHandler</c> echoes <see cref="Exception.Message" /> and the stack trace
/// into the response body, so a budget id spelled in here is a budget id handed to the caller of a
/// request that was refused so that nothing would be.
/// </para>
/// <para>
/// <b>Not named <c>RotationCompletenessException</c>, though the sibling's naming pulls that way.</b>
/// "Completeness" already means something exact in this area — whether every narrative row carries the
/// stamp, which is the gate's <em>answer</em> — so that name would read as "the rotation is not
/// finished", which is a <see langword="false" /> and not a throw. What is wrong here is the read's
/// reach, not the rotation's progress.
/// </para>
/// </remarks>
public sealed class RotationScopeException(string message) : InvalidOperationException(message);
