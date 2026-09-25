using System.Security.Cryptography;

namespace Application.Registration;

/// <summary>
/// The account identifier a registration ceremony derives from its own challenge, so that the two legs
/// of the ceremony reach one value without carrying it between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>A derivation rather than a fresh uuid.</b> The <c>user.id</c> handed to the authenticator when
/// options are minted and the <c>users.id</c> written when the ceremony finishes have to be the same
/// value: an authenticator stores the handle it was given, and an account row created under a different
/// id answers no assertion that device will ever produce. That failure is silent and permanent — every
/// later sign-in from that authenticator simply does not match, with no error naming the cause — so the
/// two are made equal by construction rather than by carrying a value between two requests. The only
/// thing both legs hold is the server-minted single-use challenge, which is why the identifier comes
/// off those bytes and off nothing else.
/// </para>
/// <para>
/// <b>Derive only after the challenge store has confirmed it issued and spent exactly these bytes for
/// exactly the account-registration pool.</b> The finish leg's only source for the challenge is the
/// client's own <c>clientDataJSON</c>, so a caller that derives before the store has answered is
/// deriving from a value the caller chose — an account identifier of their choosing, wearing the shape
/// of one this server minted. Moving the call earlier reads like tidying, and it is the one change that
/// turns this type into a vulnerability.
/// </para>
/// <para>
/// <b>The domain-separation prefix is part of the construction, not decoration.</b> The same challenge
/// bytes are the material the authenticator signs over, and anything else this system later derives
/// from a challenge must not reach this value; a bare <c>SHA-256</c> of the challenge is a value two
/// unrelated purposes would arrive at independently, with neither able to say so.
/// </para>
/// <para>
/// <b>What this accepts.</b> Every other identifier in this schema is <see cref="Guid.CreateVersion7()"/>
/// for index locality — see <c>User</c>, <c>Budget</c>, <c>Session</c> and
/// <c>DbWebAuthnChallengeStore</c> — and a hash-derived identifier scatters <c>users</c> primary-key
/// inserts instead of appending at the right-hand edge of the index. That table is low-volume and one
/// row per account is the rarest insert in the product, so the trade is right; it is a real cost rather
/// than a wash, and this is where it is recorded. <see cref="Guid.CreateVersion7()"/> is not an option
/// here at any price: it is not a function of its input at all, which is the entire requirement.
/// </para>
/// </remarks>
public static class RegistrationAccountId
{
    /// <summary>
    /// How many bytes a registration challenge carries.
    /// </summary>
    /// <remarks>
    /// Restated rather than shared with the challenge store that mints the bytes: that store sits in
    /// Infrastructure, which this ring may not reference. The refusal below is what keeps the two
    /// honest — a store minting any other width stops deriving anything at all, rather than deriving
    /// from less entropy than the design claims.
    /// </remarks>
    private const int ChallengeLength = 32;

    /// <summary>
    /// How many bytes of the digest the identifier is read from.
    /// </summary>
    /// <remarks>
    /// The width of a uuid, and the reason the digest is truncated at all rather than folded: RFC 9562
    /// spends two of these bytes on the version and variant nibbles, so the identifier carries 122 bits
    /// of the digest and no derivation of any width can do better inside a uuid.
    /// </remarks>
    private const int IdentifierLength = 16;

    /// <summary>
    /// The purpose this derivation is separated by, hashed in front of the challenge.
    /// </summary>
    /// <remarks>
    /// A UTF-8 literal, so the bytes are the specification rather than whatever an encoder was
    /// configured to produce. It carries a version so that a later derivation over the same input can
    /// differ from this one deliberately instead of by accident.
    /// </remarks>
    private static ReadOnlySpan<byte> DomainSeparation => "budgetoid/registration-account-id/v1"u8;

    /// <summary>
    /// Derives the account identifier belonging to <paramref name="challenge"/>.
    /// </summary>
    /// <param name="challenge">
    /// The server-minted bytes of the registration challenge, after the challenge store has confirmed
    /// it issued and spent exactly these for the account-registration pool.
    /// </param>
    /// <returns>An RFC 9562 version 8 uuid: the same one for the same challenge, on every call.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="challenge"/> is not exactly the width a challenge is minted at.
    /// </exception>
    public static Guid For(ReadOnlySpan<byte> challenge)
    {
        // It throws rather than padding, truncating or answering a sentinel, because no caller can do
        // anything with a partial answer: the value is the account's identity, and a quietly reshaped
        // challenge would create the account under an identifier the authenticator was never handed. A
        // short challenge is the dangerous one — it derives a perfectly well-formed identifier nothing
        // downstream can tell from a real one, off fewer bytes than the design claims.
        ArgumentOutOfRangeException.ThrowIfNotEqual(challenge.Length, ChallengeLength, nameof(challenge));

        Span<byte> material = stackalloc byte[DomainSeparation.Length + ChallengeLength];
        DomainSeparation.CopyTo(material);
        challenge.CopyTo(material[DomainSeparation.Length..]);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(material, digest);

        // A one-way function of the whole challenge, never a slice of it. Truncating the challenge is
        // deterministic, distinct, non-empty and well-shaped — and hands anybody who can influence a
        // challenge the account identifier of their choosing. The challenge itself is sent to a browser
        // in the clear; the hash is what keeps the identifier unpredictable to everybody but this
        // server.
        Span<byte> layout = digest[..IdentifierLength];

        // Version 8 is RFC 9562's own slot for a value an application derived rather than drew at random
        // or built from a timestamp, which is exactly what this is, and the variant bits say the layout
        // is the standard one. Stamped on the big-endian layout — the one the specification numbers and
        // the one `new Guid(span, bigEndian: true)` consumes — so a reader inspecting the stored uuid
        // finds the two nibbles where they are meant to be. Written against .NET's mixed-endian layout
        // instead, these two bytes land inside fields nobody meant to touch.
        layout[6] = (byte)((layout[6] & 0x0F) | 0x80);
        layout[8] = (byte)((layout[8] & 0x3F) | 0x80);

        return new Guid(layout, bigEndian: true);
    }
}
