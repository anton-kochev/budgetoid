using Domain.Common;

namespace Domain.Users;

/// <summary>
/// One surviving factor's copy of the next generation's account keys: the new content key and index key
/// encapsulated to that factor's public key, staged beside the run that produced it until a completion
/// step copies it into <see cref="WrappedAccountKeys.EncapsulatedAccountKeys"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One row per factor, because a rotation produces one value per factor.</b> The run draws a new
/// content key and a new index key once, and then encapsulates that one pair to every public key the
/// staged manifest names — so a child table rather than a column, and <c>(user_id, factor_id)</c> rather
/// than a rotation identifier, because <see cref="KeyRotation"/> is itself keyed on the account and an
/// account holds at most one run.
/// </para>
/// <para>
/// <b>This is the capability the reshape bought, said as a row.</b> Producing this value needs a public
/// key and nothing else, so a run can stage a copy for a passkey that is in a drawer, for a recovery code
/// written on a card, for every factor the account holds — none of them present, none of them touched.
/// Under the arrangement this replaced the same row would have needed the key-encryption key that factor
/// derives, which for a passkey exists only while somebody is holding the authenticator.
/// </para>
/// <para>
/// <b>The verb is <em>encapsulated to</em>, and it is not interchangeable with the other two.</b>
/// <em>Sealed under</em> is a key over data and <em>wrapped under</em> is a key over another key; this row
/// carries neither. Nothing here is wrapped, because the factor's private key is not the run's to touch —
/// it is already on <see cref="WrappedAccountKeys.WrappedPrivateKey"/>, wrapped under a key-encryption key
/// that survives the rotation untouched, and a run that re-wrapped it would need exactly the authenticator
/// this design exists to do without.
/// </para>
/// <para>
/// <b>No <c>CreatedAtUtc</c>, and that is a decision rather than an omission.</b> A seal lives entirely
/// inside one run, and the run carries <see cref="KeyRotation.StartedAtUtc"/>; a second instant on the
/// child would be a value nothing reads, nothing compares and nothing can act on, written once per factor
/// per run. The siblings that carry one carry it because something asks —
/// <see cref="WrappedAccountKeys.CreatedAtUtc"/> is what <c>GET /api/me/credentials</c> reports as a
/// credential's registration instant. Nobody asks this. It is stated here because the next reader will
/// add it for symmetry with the row next door.
/// </para>
/// <para>
/// <b>Nothing here takes an unwrapped key, a private key, a key-encryption key or a PRF output</b>, the
/// rule <see cref="WrappedAccountKeys"/> states at length. What this row holds is ciphertext under a
/// public key the server may hold in the clear, and a member accepting the private half would put the
/// whole account's plaintext within reach of the operator without reddening a single test.
/// </para>
/// </remarks>
public sealed class KeyRotationSeal
{
    private KeyRotationSeal()
    {
    }

    /// <summary>
    /// The account the seal belongs to, and the leading half of the primary key: read off the rotation,
    /// never off the factor, so that <see cref="For"/> has two independent statements of it to compare.
    /// </summary>
    public Guid UserId { get; private set; }

    /// <summary>
    /// The factor this copy was encapsulated to — the <see cref="WrappedAccountKeys.FactorId"/> of the row
    /// a promotion will write it into.
    /// </summary>
    public Guid FactorId { get; private set; }

    /// <summary>
    /// The next generation's content key and index key as one 64-byte plaintext, content key first,
    /// encapsulated to this factor's public key.
    /// </summary>
    /// <remarks>
    /// The same value, in the same framing, at the same width as
    /// <see cref="WrappedAccountKeys.EncapsulatedAccountKeys"/>, because that is the column a promotion
    /// copies it into. The order of the two halves is a client contract this server cannot check — see
    /// that type's remarks, which spell out what a reversed pair costs.
    /// </remarks>
    public ReadOnlyMemory<byte> EncapsulatedAccountKeys { get; private set; }

    /// <summary>
    /// Stages <paramref name="encapsulatedAccountKeys"/> as <paramref name="rotation"/>'s copy for
    /// <paramref name="factor"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two loaded entities, and here the idiom earns more than anywhere else it is used.</b> The
    /// siblings take one loaded object to avoid a transposed <see cref="Guid"/>;
    /// <see cref="FactorManifest.For"/> says outright that one id read off one object compares with
    /// nothing. This factory reads <see cref="UserId"/> off the rotation and <see cref="FactorId"/> off
    /// the factor, which gives it two statements of an owner from two sources — so it can <b>refuse when
    /// they disagree</b>, a refusal no signature taking loose ids can make at all. The composite foreign
    /// key this table is to carry will make a cross-account seal unstorable; the check here makes it
    /// <em>unconstructable</em>, so the mistake never reaches a <c>SaveChanges</c> that would raise
    /// <c>23503</c> part-way through a run, with some factors staged and some not.
    /// </para>
    /// <para>
    /// <b>The rejected alternatives, named so nobody simplifies back to one.</b> Three loose ids is the
    /// shape every sibling argues against and it is worse here, because the two ids that must agree would
    /// both be parameters and there would be nothing to compare them <em>to</em>.
    /// <c>(KeyRotation rotation, Guid factorId)</c> is the near miss: it reads the owner off the rotation
    /// and looks like the sibling idiom, but the factor goes unchecked entirely — a factor id belonging to
    /// another account is well-typed, well-formed and refused by nothing until the insert, which is the
    /// exact failure this signature is shaped to prevent.
    /// </para>
    /// <para>
    /// <b>The owner check is a guard against a caller's own mistake, and a route may not use it as its
    /// scoping.</b> It refuses as a <see cref="ValidationException"/> like every refusal on the siblings,
    /// so it reaches a client as a <c>400</c> naming <see cref="UserId"/> — which is an answer about a
    /// factor the caller named, and therefore the wrong shape for a rule about whose account that factor
    /// is. What scopes the read is the repository and <c>user_isolation</c> beneath it: a factor of
    /// another account must not be loadable in the first place, and if one ever is, this refusal is the
    /// second line and not the first.
    /// </para>
    /// <para>
    /// <b>The payload bound is read off <see cref="WrappedAccountKeys"/> rather than restated</b>, the
    /// rule <see cref="KeyRotation"/> keeps for the manifest's bounds and for the same reason: this value
    /// is what a promotion copies into <see cref="WrappedAccountKeys.EncapsulatedAccountKeys"/>, so a
    /// width or a version this type accepted and that one refused is a row that stores here and fails at
    /// promotion — at the one moment in the run where the old generation has already gone.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">No rotation, or no factor, was supplied.</exception>
    /// <exception cref="ValidationException">
    /// The rotation and the factor belong to different accounts, or the encapsulated account keys are not
    /// <see cref="WrappedAccountKeys.EncapsulatedAccountKeysLength"/> bytes carrying version
    /// <see cref="WrappedAccountKeys.EncapsulatedAccountKeysVersion"/>.
    /// </exception>
    public static KeyRotationSeal For(
        KeyRotation rotation,
        WrappedAccountKeys factor,
        ReadOnlyMemory<byte> encapsulatedAccountKeys)
    {
        // Both owners are read off loaded entities, so there is nothing to compare without both. No user
        // typed either; a caller handed over nothing.
        ArgumentNullException.ThrowIfNull(rotation);
        ArgumentNullException.ThrowIfNull(factor);

        Dictionary<string, string[]> errors = new();

        // Keyed on UserId because that is the column the disagreement is about: the row is filed under
        // the rotation's account, so a factor belonging to another one is a seal of this account's next
        // generation handed to somebody else's factor. The composite foreign key this table is to carry
        // will refuse it with 23503 — mid-run, after earlier factors have already been staged — and this
        // is the same refusal one ring up, where it costs a caller one exception instead of a partial run.
        if (rotation.UserId != factor.UserId)
        {
            errors[nameof(UserId)] =
                ["A rotation seal must be staged against a factor of the rotation's own account."];
        }

        // Keyed on the property the value lands in, as WrappedAccountKeys.For keys its own.
        if (DescribeMalformedEncapsulatedAccountKeys(encapsulatedAccountKeys) is { } problem)
        {
            errors[nameof(EncapsulatedAccountKeys)] = [problem];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return new KeyRotationSeal
        {
            UserId = rotation.UserId,
            FactorId = factor.FactorId,

            // Copied, not aliased, the rule WrappedAccountKeys.For keeps. A ReadOnlyMemory<byte> is a
            // view over an array the caller still owns, and the caller on this path is encapsulating one
            // pair of keys to every factor the account holds, with every reason to be reusing one buffer.
            EncapsulatedAccountKeys = encapsulatedAccountKeys.ToArray(),
        };
    }

    /// <summary>
    /// Says what is wrong with <paramref name="value"/> as an encapsulated pair of account keys, or
    /// <see langword="null"/> if it is well-formed.
    /// </summary>
    /// <remarks>
    /// <b>Both bounds come from <see cref="WrappedAccountKeys"/>.</b> A seal stages exactly the value that
    /// type stores, so it has no width and no version of its own to state, and a second copy of either
    /// number is the copy that drifts. Only the sentence is written here, because the sibling's is private
    /// to it.
    /// <para>
    /// <b>Width before version, and not merely for message quality</b> — the reason the sibling's own
    /// describers give: an empty value has no leading byte to read, so reading <c>Span[0]</c> first throws
    /// <see cref="IndexOutOfRangeException"/> out of the Domain and a client that sent an unset field is
    /// told the server broke rather than that its bytes were malformed.
    /// </para>
    /// </remarks>
    private static string? DescribeMalformedEncapsulatedAccountKeys(ReadOnlyMemory<byte> value) =>
        value.Length != WrappedAccountKeys.EncapsulatedAccountKeysLength
            ? "Encapsulated account keys must be exactly "
              + $"{WrappedAccountKeys.EncapsulatedAccountKeysLength} bytes."
            : value.Span[0] != WrappedAccountKeys.EncapsulatedAccountKeysVersion
                ? "Encapsulated account keys must carry encapsulation framing version "
                  + $"{WrappedAccountKeys.EncapsulatedAccountKeysVersion}."
                : null;
}
