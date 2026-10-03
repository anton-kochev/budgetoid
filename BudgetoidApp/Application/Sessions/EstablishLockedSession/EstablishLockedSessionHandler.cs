using Application.Abstractions;
using Application.Sessions.ReadSession;
using Application.Users;
using Domain.Erasure;
using Domain.Sessions;
using Domain.Users;

namespace Application.Sessions.EstablishLockedSession;

/// <summary>
/// Turns a provider identity the bearer handler has already verified into a
/// <see cref="SessionKind.Locked"/> session over the account's federated credential — or into nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order of the steps is the security property, as it is on every establishing path.</b>
/// <c>credentials</c> is exempt from row-level security, so the discovery read runs before this handler
/// publishes anyone; <c>sessions</c>, <c>session_tokens</c> and <c>erasure_schedules</c> are policed, so
/// everything after it needs the identity first or meets <c>''::uuid</c> and dies with <c>22P02</c>.
/// <see cref="Passkeys.CompleteAssertion.CompleteAssertionHandler"/> orders the same steps the same way.
/// </para>
/// <para>
/// <b>"Before this handler publishes anyone" is not "with nobody published".</b> A request carrying a
/// live locked session cookie arrives with that cookie's account already published by the cookie scheme,
/// which ran as the default scheme before the route (a live full one never gets here: the route answers
/// it 409 first). The discovery read is unaffected, because the table is exempt, and the publication in
/// step 3 replaces that account with the credential's owner — which is what lets one browser's locked
/// session be replaced by a sign-in to another account.
/// </para>
/// <para>
/// <b>Publishing on the strength of the lookup alone is sound here for the reason it is sound on the
/// cookie path:</b> the proof has already been checked. The provider's signature was verified by the
/// bearer handler before this route was reached, and the claim gate refused a token whose address the
/// provider does not vouch for, so the subject is not a value the caller chose.
/// </para>
/// <para>
/// <b>No transaction, and no <see cref="ITransactionalExecutor"/>.</b> There is one write — the session
/// and its handle in a single save — so an atomic unit would protect nothing, and a transaction opened
/// before the publication would configure its connection while the identity is still empty. The read
/// of the schedule after the save depends on nothing the save wrote.
/// </para>
/// <para>
/// <b>It takes no logger</b>, for the reason <c>ScheduleErasureHandler</c> gives: it holds the id of an
/// account that may have asked to be forgotten, and this is the sign-in such a person arrives on.
/// </para>
/// <para>
/// <b>A race it records rather than handles.</b> An erasure committing between the discovery read and
/// the save removes the credential the session names, so the insert would fail its foreign key with
/// <c>23503</c> and the request answer 500, and a retry would find no credential and answer 404. Nothing
/// in the suite drives that interleaving.
/// </para>
/// </remarks>
public sealed class EstablishLockedSessionHandler(
    IUserRepository users,
    ISessionRepository sessions,
    IErasureScheduleRepository schedules,
    IUserContextWriter userContextWriter,
    TimeProvider timeProvider) : ICommandHandler<EstablishLockedSessionCommand, LockedSignInOutcome>
{
    public async Task<LockedSignInOutcome> HandleAsync(
        EstablishLockedSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 1. The discovery read: credentials alone, keyed on the provider identity, returning the
        //    credential itself. Not the account id followed by a second read for "the account's
        //    federated credential" — an email change landing between the two would hand the session a
        //    credential this token never named — and never the account's credentials in general, since a
        //    passkey here would open a Full session, the one thing a provider sign-in must not reach.
        Credential? credential = await users.FindFederatedCredentialBySubjectAsync(
            Credential.GoogleProvider,
            command.Subject,
            cancellationToken);

        // 2. No account: the handler publishes nothing and writes nothing. A published identity on a
        //    request that then answers 404 is residue sessions.md warns about; this handler adds none. (A
        //    request carrying a live locked cookie still holds that cookie's account, published by the
        //    cookie scheme before the route ran — its own, and not this handler's doing.)
        if (credential is null)
        {
            return new LockedSignInOutcome.NoAccount();
        }

        // 3. Only now is the account published, and it is the credential's owner — never anything the
        //    request carried. Every policed statement below runs as this account.
        userContextWriter.ResolveUser(credential.UserId);

        // 4. The clock and the handle, read and drawn once. There is no retrying delegate on this path,
        //    so nothing replays them; they sit outside any unit of work for the reason every establishing
        //    path gives, so a later edit wrapping the save cannot turn one sign-in into several secrets.
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        SessionHandle handle = SessionHandle.Mint();

        // 5. The session over the credential the token named. Session.Establish derives the kind from the
        //    credential's type, so a federated credential yields Locked and nothing here can ask for
        //    anything else; the lifetime is the product's one value, shared with every other path.
        Session session = Session.Establish(credential, now, now + SessionPolicy.Lifetime);

        // 6. The session and its handle in ONE save, which is the only shape ISessionRepository offers.
        await sessions.AddAsync(session, handle.TokenFor(session), cancellationToken);

        // 7. The account's pending erasure, if one is filed, so a locked tab learns on arrival what the
        //    person came back to see. Read by the credential's owner — the identity published above — so
        //    the explicit owner predicate and the policy beneath it agree on whose row this is.
        ErasureSchedule? schedule = await schedules.FindAsync(credential.UserId, cancellationToken);

        return new LockedSignInOutcome.Established(
            new SessionSummary(session.Kind, session.ExpiresAtUtc, schedule?.TakesEffectAtUtc),
            handle.IssuedFor(session));
    }
}
