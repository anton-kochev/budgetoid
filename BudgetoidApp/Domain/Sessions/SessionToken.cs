using System.Security.Cryptography;
using Domain.Common;
using Domain.Users;

namespace Domain.Sessions;

/// <summary>
/// The handle one <see cref="Session"/> is presented by, stored as the SHA-256 of the token rather
/// than as anything the token can be recovered from.
/// </summary>
/// <remarks>
/// <para>
/// Its own entity referencing its session and its user by id, for the reason
/// <see cref="RecoveryCodeHash"/> gives: hanging it off <see cref="User"/> would grow a root that is
/// loaded on every authenticated request. It is a table of its own for a sharper reason than that,
/// and the reason is the whole of this change. A presented token has to be looked up <b>before</b>
/// the request has an identity, and <c>sessions</c> is policed by <c>user_isolation</c>, keyed on
/// <c>app.current_user_id</c> — which is exactly the value the lookup exists to produce. A column on
/// <c>sessions</c> would therefore be read by a statement the policy refuses, and refuse it loudly:
/// an unset setting reaches the policy as <c>''::uuid</c> and raises <c>22P02</c>. So the discovery
/// key goes on its own exempt table and everything read <i>after</i> the answer stays on the policed
/// one, which is the split
/// <see href="../../../docs/decisions/0012-split-a-passkeys-material-by-whether-it-is-read-before-identity.md">ADR
/// 0012</see> already argues for a passkey's material.
/// </para>
/// <para>
/// <b>What the hash buys, stated precisely, because it is not what
/// <see cref="RecoveryCodeHash"/> buys.</b> A recovery code never reaches this server at all. A
/// session token does — this server mints it and reads it on every request that presents it — so the
/// digest is not a claim that the value is unknown here. It is a claim about what a copy of this
/// table is worth: a backup, a replica or a single unbounded read yields digests, and a digest of a
/// 256-bit uniform value cannot be turned back into the token a request would have to present. The
/// live token exists in the browser's cookie jar and in the memory of the request handling it, and
/// nowhere else.
/// </para>
/// <para>
/// <b>No timestamps, and their absence is a decision.</b> The <c>sessions</c> row carries when the
/// session began, when it expires and whether it was revoked; a copy here would be a second set of
/// the same facts to keep in step, and the first divergence would be a token outliving the session it
/// opens or the reverse. This row says which session a presented handle names, and nothing else.
/// </para>
/// </remarks>
public sealed class SessionToken
{
    /// <summary>
    /// The exact width of the token a request presents, once decoded.
    /// </summary>
    /// <remarks>
    /// Refused from <b>both</b> sides, and never truncated. Short is a shorter secret than the design
    /// claims, and it would hash to a perfectly well-formed 32-byte row nothing downstream could tell
    /// from a real one — <c>CK_session_tokens_token_hash_length</c> watches the <em>digest</em>, which
    /// is 32 bytes whatever went into it, so the database cannot catch a short token and this factory
    /// is the only place it stops. Long means the minting path and this one disagree about what a
    /// token is. Truncating would store the hash of a prefix, and the session would never be found by
    /// the handle its own cookie carries.
    /// <para>
    /// Numerically equal to <see cref="HashLength"/> and independent of it: the token's width is a
    /// choice about how much entropy a handle carries, and the digest's width is SHA-256's. The two
    /// must not be folded into one constant, for the reason
    /// <c>RecoveryCodeHashConfiguration</c> keeps its column width separate from
    /// <see cref="RecoveryCodeHash.VerifierLength"/>.
    /// </para>
    /// </remarks>
    public const int TokenLength = 32;

    /// <summary>The width of the SHA-256 this type stores, and therefore of the column.</summary>
    /// <remarks>
    /// Read by <c>SessionTokenConfiguration</c> rather than copied there, because both bounds describe
    /// the <em>same</em> value — the digest <see cref="HashOf"/> produces. That is the
    /// <c>WrappedAccountKeysConfiguration</c> case rather than the
    /// <c>RecoveryCodeHashConfiguration</c> one: a local copy of 32 in the configuration would not be
    /// a second fact, it would be the same fact able to disagree with itself.
    /// </remarks>
    public const int HashLength = 32;

    private SessionToken()
    {
    }

    /// <summary>
    /// The SHA-256 of the presented token, and the identity of the row: a request arrives carrying a
    /// token and nothing else, so this is what the row is found by.
    /// </summary>
    public ReadOnlyMemory<byte> TokenHash { get; private set; }

    /// <summary>The session this token opens.</summary>
    public Guid SessionId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>
    /// Files the handle <paramref name="session"/> is presented by, storing the hash of
    /// <paramref name="token"/> and never the token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It takes the session rather than two loose ids</b>, the argument
    /// <see cref="Session.Establish"/>, <see cref="Domain.Users.PasskeyPublicKey.Register"/> and
    /// <see cref="RecoveryCodeHash.From"/> all make. Both copied columns are compared against
    /// <c>sessions(id, user_id)</c> by a composite foreign key, so a row whose owner disagreed with
    /// its session's is unstorable — and what makes that matter is the same thing that makes it matter
    /// on a recovery-code hash: this lookup runs <b>anonymous</b> and the request adopts the owner it
    /// finds here, on a table nothing beneath the application polices. A factory taking two ids is one
    /// transposed argument away from signing a caller into somebody else's account.
    /// </para>
    /// <para>
    /// <b>It hashes internally</b>, so no shape of this call stores an unhashed value. A factory
    /// taking a digest the caller computed would mean a raw token could be assigned to an object
    /// something can persist, and every call site would be a place to get it wrong once.
    /// </para>
    /// <para>
    /// <paramref name="token"/> is a <see cref="ReadOnlySpan{T}"/> rather than a
    /// <see cref="ReadOnlyMemory{T}"/>, and that is the one thing on this type the compiler enforces
    /// rather than a reviewer: a ref struct cannot be assigned to a field, captured by a lambda or
    /// closed over by an async method, so the presented token structurally cannot travel out of this
    /// method body. Everything else here — no property returning it, no overload accepting a digest —
    /// is a rule reflection can check but the language cannot.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">No session was supplied.</exception>
    /// <exception cref="ValidationException">
    /// The token is not <see cref="TokenLength"/> bytes.
    /// </exception>
    public static SessionToken For(Session session, ReadOnlySpan<byte> token)
    {
        // The session and the owner are both read off the session, so there is nothing to file
        // without one. No user typed this; a caller handed over nothing.
        ArgumentNullException.ThrowIfNull(session);

        Dictionary<string, string[]> errors = new();

        // Keyed on the property the value ends up in, as PasskeyPublicKey.Register and
        // RecoveryCodeHash.From key their own length refusals. Nothing a request supplies can reach
        // this: a token is minted by one line of this system, so a bad width is a bug in that line
        // rather than a caller's choice — the same kind of guard Session.Establish's expiry check is,
        // and stated the same way so the two read alike.
        if (token.Length != TokenLength)
        {
            errors[nameof(TokenHash)] = [$"Token must be exactly {TokenLength} bytes."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return new SessionToken
        {
            // HashOf and nothing else, so the two spellings of "the hash of a token" cannot drift:
            // see the remarks there.
            TokenHash = HashOf(token),
            SessionId = session.Id,
            UserId = session.UserId,
        };
    }

    /// <summary>
    /// The value a token is stored under — the same hash <see cref="For"/> computes, without a session
    /// to compute it against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Unsalted SHA-256, and neither half of that is an omission.</b> The argument is the one
    /// <c>docs/business-logic/recovery-codes.md</c> already settles, reached here by a different route
    /// to the same two premises rather than re-argued.
    /// </para>
    /// <para>
    /// <b>No salt</b>, because a request arrives carrying a token and no identity at all — no session,
    /// no account — so the row has to be findable by its hash alone. A per-row salt is a value the
    /// lookup cannot know before it has found the row it needs the salt to find. That is also why this
    /// member exists at all rather than callers going through <see cref="For"/>: the lookup has no
    /// session to hand it.
    /// </para>
    /// <para>
    /// <b>No slow KDF, and no package.</b> A work factor exists to make a <em>guessable</em> input
    /// expensive to enumerate; the input here is a uniform 256-bit value this server generated, so
    /// there is no dictionary to slow down and the work would buy nothing but latency — on the one
    /// request every authenticated request makes. Adding a hashing package would also move a pinned
    /// row in <c>ProjectReferenceGraphTests</c> and put a third-party dependency on <c>Domain</c>,
    /// which declares none.
    /// </para>
    /// <para>
    /// <b>The two spellings must agree or no session in the system is ever found</b>, and the symptom
    /// is silent: every request simply arrives unauthenticated. That is why <see cref="For"/> calls
    /// this rather than repeating the digest.
    /// </para>
    /// </remarks>
    public static byte[] HashOf(ReadOnlySpan<byte> token) =>
        // SHA256.HashData allocates the result, so — unlike PasskeyPublicKey.Register, which copies a
        // buffer the caller still owns — there is nothing here aliasing the argument. It returns the
        // array rather than a ReadOnlyMemory<byte> because its other caller is a repository lookup,
        // which needs the value a bytea parameter is bound from.
        SHA256.HashData(token);
}
