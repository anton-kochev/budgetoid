using System.Security.Cryptography;
using Application.Passkeys;
using Domain.Sessions;

namespace Application.Sessions;

/// <summary>
/// The handle one session is presented by, minted here and nowhere else. The bytes stay inside this
/// object; the only two things that can be done with them are filing their digest against a session
/// and writing their presented spelling onto the client.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no accessor for the value, and that absence is the type.</b> The rule this exists to
/// hold is that the bytes the cookie carries are the bytes whose digest was stored — and the failure
/// when they are not is silent in the worst way: the sign-in answers 200, the row is there, and the
/// browser presents a handle no lookup will ever find, so the person is simply never signed in and
/// nothing anywhere names the cause. A property returning the array would restore exactly that
/// mistake, because two callers reading it are two places to hand the cookie something else.
/// <see cref="TokenFor"/> and <see cref="IssuedFor"/> read the one field, so the two values agree by
/// construction rather than by three handlers doing the same thing carefully.
/// </para>
/// <para>
/// <b>Both members take the <see cref="Session"/>, and neither takes an instant.</b> The expiry
/// written onto the client is therefore the row's own, never an interval computed at a call site —
/// which is the rule <c>SessionCookie</c>'s remarks state and cannot enforce, since a
/// <see cref="DateTime"/> parameter accepts whatever it is handed. A cookie outliving its session
/// shows a signed-in shell to somebody the server has already stopped honouring; one dying early
/// signs a person out mid-flow with nothing to say why.
/// </para>
/// <para>
/// <b>The width is <see cref="SessionToken.TokenLength"/> and never a local <c>32</c>.</b>
/// <see cref="SessionToken.For"/> refuses any other width, so a second copy of the number here would
/// not be a second fact — it would be the same fact able to disagree with itself, and the disagreement
/// would be every sign-in in the product throwing after the ceremony verified.
/// </para>
/// <para>
/// <b><see cref="RandomNumberGenerator"/> and nothing else.</b> The value is the entire proof of a
/// sign-in: anything predictable — <see cref="Random"/>, a <see cref="Guid"/>, a hash of something
/// about the account — is an account somebody else can sign into without a credential at all.
/// </para>
/// <para>
/// <b>A class rather than a <c>readonly struct</c>, deliberately.</b> A struct has a default value,
/// and the default of this one would be a handle whose bytes are <see langword="null"/> — a shape no
/// factory can produce and every consumer would have to defend against.
/// </para>
/// <para>
/// <b>It is in this layer rather than in <c>Domain</c>.</b> How wide a handle is belongs to
/// <see cref="SessionToken"/> and is read from there; that one is minted at all, and that it crosses
/// the wire as base64url, are facts about the transport this product chose — the same reason
/// <see cref="SessionPolicy.Lifetime"/> sits here rather than beside <see cref="Session.Establish"/>.
/// </para>
/// </remarks>
public sealed class SessionHandle
{
    private readonly byte[] _value;

    private SessionHandle(byte[] value) => _value = value;

    /// <summary>Draws a fresh handle from the system's cryptographic random source.</summary>
    /// <remarks>
    /// <b>Called before the transaction that establishes the session opens</b>, on all three paths, for
    /// the reason each of them reads its clock there: the transactional delegate is replayed by the
    /// retrying execution strategy, and a handle drawn inside it is a different secret per attempt.
    /// Drawn once, the digest every attempt files is the same digest, so the value handed back matches
    /// whichever attempt committed — rather than matching only because the delegate's return value
    /// happens to come from the surviving one.
    /// </remarks>
    public static SessionHandle Mint() =>
        new(RandomNumberGenerator.GetBytes(SessionToken.TokenLength));

    /// <summary>
    /// The row this handle is stored as: the digest of the value, against
    /// <paramref name="session"/>.
    /// </summary>
    /// <remarks>
    /// The hashing is <see cref="SessionToken.For"/>'s, which takes a <see cref="ReadOnlySpan{T}"/>
    /// precisely so the presented value cannot be captured or persisted, and reads both ids off the
    /// session so nothing here can file a handle against the wrong sign-in.
    /// </remarks>
    public SessionToken TokenFor(Session session) => SessionToken.For(session, _value);

    /// <summary>
    /// What the client is handed: this handle in the spelling a request presents it in, and the
    /// instant <paramref name="session"/> stops being live.
    /// </summary>
    /// <remarks>
    /// <see cref="PasskeyEncoding"/> despite the name, and borrowing it beats a second encoder — it is
    /// this codebase's one base64url dialect, and <c>SessionCookieAuthenticationHandler</c> reads the
    /// presented value back through the same type. Two dialects is how a value this server wrote stops
    /// decoding to the bytes it was written from.
    /// </remarks>
    public SessionHandoff IssuedFor(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new SessionHandoff(PasskeyEncoding.Encode(_value), session.ExpiresAtUtc);
    }
}
