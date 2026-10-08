namespace Domain.Sessions;

/// <summary>
/// The <c>session_tokens</c> row a presented handle names.
/// </summary>
/// <remarks>
/// One member, and the port stays one member wide on purpose. A token is written in the same
/// <c>SaveChanges</c> as the session it opens, by whichever path established that session, so a
/// second method here would be a second way to write one — and the row that could then exist is a
/// token naming a session that was never committed.
/// </remarks>
public interface ISessionTokenRepository
{
    /// <summary>
    /// The token stored under <paramref name="tokenHash"/>, or <see langword="null"/> when no session
    /// in the installation is presented by it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The discovery read. It names no owner, and the exemption <c>session_tokens</c> carries
    /// exists for exactly this statement.</b> A request arrives carrying a cookie and nothing else, so
    /// there is no account to scope by until this read has answered, and a <c>user_isolation</c>
    /// policy keyed on <c>app.current_user_id</c> would refuse the very query that produces the value
    /// it wants to compare against. It would refuse it loudly rather than quietly: an unset setting
    /// reaches the policy as <c>''::uuid</c> and raises <c>22P02</c>. That is the argument
    /// <c>docs/decisions/0016-give-recovery-code-hashes-their-own-exempt-table.md</c> makes for a
    /// redemption; this is the same argument for every authenticated request in the product.
    /// </para>
    /// <para>
    /// <b>It is the fourth read in the system with that shape, and it earns the exemption the way
    /// the other three do.</b>
    /// <see cref="Domain.Users.IPasskeyRepository.FindByWebAuthnCredentialIdAsync"/>
    /// is the same statement over passkey material,
    /// <see cref="Domain.Users.IRecoveryCodeRepository.FindByVerifierHashAsync"/> the same over a
    /// recovery code's digest, and <c>IWebAuthnChallengeStore.ConsumeAsync</c> the same over a nonce.
    /// Each runs before the request has an identity, each reads a table exempt for that reason, and
    /// each sits on the ordinary port beside the scoped reads of its own subject. What bounds this
    /// read is written below, not the set of collaborators that can reach it.
    /// </para>
    /// <para>
    /// <b>Unscoped is not unbounded.</b> The caller's own input names the row: the hash is SHA-256 of
    /// a 256-bit value the client must present in full, so a caller selecting a row they cannot name
    /// is guessing it — the argument the challenge consume and the recovery-code lookup both run on.
    /// What must never appear here is a join to any other table: a join to <c>sessions</c> or to
    /// <c>users</c> on this statement would put a policed relation on a connection that has published
    /// nobody, and fail for exactly the reason the exemption exists.
    /// </para>
    /// <para>
    /// <b>What this establishes is an account and a session, not a licence to act.</b> Whether the
    /// session it names is live — unexpired and unrevoked — is a question about the <c>sessions</c>
    /// row, which is policed and is read <em>after</em> the identity this answer publishes. This
    /// member deliberately cannot answer it: the row it returns carries no expiry and no revocation
    /// instant, so a caller cannot mistake "a token matched" for "a session is open".
    /// </para>
    /// <para>
    /// <b>This is the one member of this port that may omit an owner, and it is the only member.</b>
    /// Per <c>docs/decisions/0011-police-the-user-owned-tables.md</c>, an exempt table scopes nothing,
    /// so only the statement that establishes the identity is allowed to run without a filter. A
    /// second member added here without one is what would turn "the discovery read" from a description
    /// of this statement into a hole in the rule.
    /// </para>
    /// <para>
    /// <see cref="SessionToken.HashOf"/> produces the argument. The caller hashes, rather than handing
    /// over the presented token for this to hash, so the raw token stops at the boundary that decoded
    /// it and no persistence port has a member a live token can travel through.
    /// </para>
    /// </remarks>
    Task<SessionToken?> FindByTokenHashAsync(
        byte[] tokenHash,
        CancellationToken cancellationToken = default);
}
