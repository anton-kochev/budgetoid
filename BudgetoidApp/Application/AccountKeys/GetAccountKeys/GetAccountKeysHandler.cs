using Application.Abstractions;
using Domain.Sessions;

namespace Application.AccountKeys.GetAccountKeys;

/// <summary>
/// Hands the browser the wrapped copies of the account's content key and index key that the credential
/// which opened this session can derive a key-encryption key for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two reads, in this order.</b> Resolve the session named by
/// <see cref="GetAccountKeysQuery.SessionId" /> through
/// <see cref="ISessionRepository.FindByIdAsync" />, take <c>Session.CredentialId</c> off it, then read
/// the envelopes for <c>(userContext.UserId, credentialId)</c> through
/// <see cref="IAccountKeyReadService.ListForCredentialAsync" />. The credential is the whole reason the
/// session is read at all: an account holding a passkey and a set of recovery codes has eleven wrapped
/// rows across two credentials, and only the ones under the credential that just authenticated can be
/// opened by anything the browser is holding.
/// </para>
/// <para>
/// <b><see cref="ISessionRepository.FindByIdAsync" /> deliberately carries no owner predicate — do not
/// add one above it.</b> Its own remarks give the reason: <c>sessions</c> is policed by
/// <c>user_isolation</c>, so PostgreSQL appends the owner comparison underneath the read and somebody
/// else's session is <em>not found</em> rather than found and rejected. A filter here would be a second
/// source of tenancy able to disagree with the policy, and the first disagreement is a request that can
/// read its own session through one and not the other.
/// </para>
/// <para>
/// <b>The owner argument to the read service comes from <see cref="IUserContext.UserId" />, not from
/// <c>Session.UserId</c>.</b> The two cannot disagree — the policy is what let the session row be
/// visible at all, and it compares against the very value the context published — but one of them is the
/// publication the row-level-security model itself reads, and the other is a column that happens to
/// agree with it. Taking the one the policy is keyed on keeps a single answer to "who is this request",
/// which is the same discipline <c>AuthenticatedSession</c> keeps by refusing to hand back a budget id.
/// </para>
/// <para>
/// <b>A session this request cannot see answers an empty list, never a throw.</b> Never established,
/// already ended, and belonging to another account all arrive as <see langword="null" /> from the
/// repository and must stay indistinguishable from one another — that indistinguishability is what stops
/// a caller learning that a session id is real but not theirs, and a refusal keyed on the
/// <see langword="null" /> would re-open exactly that oracle on the one route that names an account's
/// key custody.
/// </para>
/// <para>
/// <b>It asks nothing about whether the session is live, and must not start.</b>
/// <see cref="ISessionRepository.FindByIdAsync" /> hands back a revoked or expired session as an
/// entity rather than filtering it — its own remarks and
/// <c>Infrastructure/Repositories/SessionRepository.cs</c> both say so deliberately — because whether
/// a session is live is <see cref="Session.IsActiveAt" />'s answer, and the <b>authentication
/// pipeline</b> is what applies it, before any handler in this ring is reached. An
/// <c>IsActiveAt</c> or <c>RevokedAtUtc is null</c> check added here would be a second copy of that
/// rule sitting behind the first, and the copy that drifts is the one deciding whether a request is
/// authenticated at all. <b>This handler's own tests would not catch one being added</b>, which was
/// measured rather than assumed: every session they seed is live and unrevoked, so a check on
/// <c>RevokedAtUtc</c> was applied here and all 717 unit tests stayed green. A check reading ambient
/// wall-clock time is the one exception, and it is not the tests holding the rule either — the
/// fixture's instants are fixed calendar dates, so such a check goes red once they fall in the past,
/// which is the fixture ageing rather than a case that models a revoked session. What holds the line
/// is the route table and review, and this paragraph is the only place that says so.
/// </para>
/// <para>
/// <b>A credential holding no factor rows answers an empty list too</b>, and for the reason
/// <c>ListCredentialsHandler</c> gives about its own: an empty collection is the honest shape of
/// "nothing came back", and this read is taken for display. The state is not one the product can be left
/// in at rest — every path that brings a factor into existence writes its wrapped row in the same
/// <c>SaveChanges</c> as the credential — so an empty answer means the credential was revoked, or the
/// account erased, between this request authenticating and this read running. That is a race, not a
/// corruption, and a handler that threw on it would be a rule keyed on a read taken for display.
/// </para>
/// <para>
/// <b>The answer is a list because a factor is not a credential, and it must never be written as "the"
/// pair.</b> A passkey is one credential and one factor: one row. A set of recovery codes is one
/// credential and <b>ten</b> factors, because each code derives its own key-encryption key and a person
/// redeems whichever one they still have. <c>SingleOrDefault</c>, <c>FirstOrDefault</c>, or a return type
/// of <c>FactorEnvelopes?</c> would each be correct for every passkey in the product and would drop nine
/// of every ten recovery-code envelopes — the exact failure that moved <c>wrapped_account_keys</c>'
/// primary key from <c>credential_id</c> to <c>factor_id</c>, and one whose symptom is a person who has
/// already lost their authenticator redeeming a code, being handed a session, and finding the account
/// still locked. Nothing on the server can see it happen.
/// </para>
/// <para>
/// <b>The order is <c>FactorEnvelopes.FactorId</c> ascending, and what that buys is determinism rather
/// than a meaning.</b> The client tries each pair in turn and the associated data decides which one
/// opens, so no sequence is more useful to a caller than another; what a caller does need is that two
/// reads of unchanged rows agree, which an unordered read of a ten-row set does not promise. The primary
/// key is the sort because it cannot tie, so no second key is needed. The <em>particular</em> sequence
/// is still not part of the contract, though the reason usually given for that is false:
/// <see cref="Guid.CompareTo(Guid)" /> <b>is</b> a byte comparison of the canonical RFC 4122 form —
/// over 200,000 random pairs on .NET 10 it disagreed with big-endian byte order zero times — so a
/// .NET sort and a PostgreSQL <c>order by</c> are expected to agree, granted that <c>uuid_cmp</c> is
/// a <c>memcmp</c> over those same sixteen bytes, which is not something anyone here has run against
/// a live server. The trap worth naming is <see cref="Guid.ToByteArray()" /> instead: its default
/// layout is little-endian across the first three fields, and over those same 200,000 pairs it
/// ordered differently from <see cref="Guid.CompareTo(Guid)" /> on close to half of them. So a
/// hand-rolled comparison, or an expected order built in a test out of those bytes, matches neither
/// side. Determinism is what a caller may rest on; which factor comes first is what none of them may.
/// </para>
/// <para>
/// <b>It takes no <c>ILogger</c>, and must never take one</b>, for the reason
/// <c>ListCredentialsHandler</c> and <c>GetSignedInUserHandler</c> give:
/// the identifiers on this path name an account's key custody, and a log line copies them into a sink
/// with a different retention policy and a different audience from the table they came from. It is
/// sharper here than on either of those — a factor id is the associated data both of a row's envelopes
/// were sealed with, so a log holding factor ids beside envelopes is a partial reconstruction of the
/// account's key material in a place nobody is guarding. No gate anywhere reads a log line from here, so
/// there is nothing to trade against.
/// </para>
/// <para>
/// <b>No <c>ITransactionalExecutor</c>, and this is not an oversight.</b> Two reads and no write: there
/// is no unit of work to make atomic, and a snapshot spanning them would buy nothing, since a credential
/// revoked between them is the empty answer described above rather than a torn one. Opening a
/// transaction here would also put the authentication path's ordering trap back in play — a transaction
/// configures its connection when it opens, and every policed statement inside one opened before the
/// identity was published meets <c>''::uuid</c> and raises <c>22P02</c>. That is the rule
/// <c>CLAUDE.md</c> states and <c>RegisterAccountHandler</c>, <c>CompleteAssertionHandler</c> and
/// <c>RedeemRecoveryCodeHandler</c> each restate inline; nothing here is worth re-opening it for.
/// </para>
/// </remarks>
public sealed class GetAccountKeysHandler(
    IUserContext userContext,
    ISessionRepository sessionRepository,
    IAccountKeyReadService readService)
    : IQueryHandler<GetAccountKeysQuery, IReadOnlyList<FactorEnvelopes>>
{
    public async Task<IReadOnlyList<FactorEnvelopes>> HandleAsync(
        GetAccountKeysQuery query,
        CancellationToken cancellationToken = default)
    {
        // No owner predicate above this read, and no liveness check below it: user_isolation scopes
        // the row, and whether the session is live was decided by the authentication pipeline that
        // let this request reach a handler at all.
        Session? session = await sessionRepository.FindByIdAsync(query.SessionId, cancellationToken);

        // Never established, already ended, and belonging to somebody else all arrive here as the
        // same null and stay indistinguishable — a refusal keyed on it is the oracle.
        if (session is null)
        {
            return [];
        }

        // The owner from the context the policy is keyed on, the credential from the session. Every
        // factor under that credential, which is one row for a passkey and ten for a set of codes.
        return await readService.ListForCredentialAsync(
            userContext.UserId,
            session.CredentialId,
            cancellationToken);
    }
}
