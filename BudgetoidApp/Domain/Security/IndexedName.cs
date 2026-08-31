namespace Domain.Security;

/// <summary>
/// A sealed name and the blind index computed over it, as the one value a searchable name column pair
/// holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>A pair type because the requirement refuses halves, and a call is where a half is made.</b> A
/// name ciphertext without its index is a row no lookup can find and no uniqueness constraint can
/// police; an index without its ciphertext is a keyed fingerprint of text that is not stored anywhere.
/// The schema's two <c>NOT NULL</c> columns will say a <b>row</b> cannot be half. This type says a
/// <b>call</b> cannot be half. They are two guards over two different moments — one runs when a
/// statement reaches the database, the other when a factory is invoked — and the reason to have both is
/// that the first cannot see a caller that meant to write both and wrote one, on a path that also
/// writes something else the same transaction keeps.
/// </para>
/// <para>
/// <b>Collapsing them is the mistake, not the tidy-up.</b> Read as duplication, either can be deleted
/// with everything green: drop the pair type and the database still refuses a null, so nothing fails
/// until a caller finds the shape where it does not (an <c>ExecuteUpdate</c>, a projection, a second
/// write path that touches one column) — and by then the refusal arrives as a constraint name in a 500
/// rather than as a signature nobody could satisfy. Drop the <c>NOT NULL</c> and this type is the only
/// thing standing, which makes the guarantee a property of the application code alone, against the
/// rule that a rule belongs to the lowest layer that can enforce it declaratively. Neither is a
/// restatement of the other for error quality; they refuse different mistakes.
/// </para>
/// <para>
/// <b>There is no separate <c>BlindIndex</c> type, and that is a deliberate stop.</b> A blind index
/// never appears alone: it is computed from the name it indexes, written with it, replaced with it and
/// meaningless without it, so a type for it would have exactly one member, exactly one width check, and
/// exactly one place it could ever be constructed — this factory, one line further down. What that
/// third type would buy over the line below is nothing; what it would cost is a reader having to hold
/// three types to understand one column pair, and a plausible-looking way to build an index that is
/// not attached to a name. The name is the value; the index is how it is found.
/// </para>
/// <para>
/// <b>The index is not an envelope and this type must not be read as holding two.</b>
/// <see cref="Name"/> is AEAD ciphertext with framing <see cref="CiphertextEnvelope"/> owns and a key
/// only a browser holds. <see cref="BlindIndex"/> is a keyed digest — no version byte, no nonce, no
/// tag, nothing to open, and no way back to the text it was taken over. They travel together and are
/// judged by different rules, which is why the widths below come from two different places.
/// </para>
/// </remarks>
public sealed class IndexedName
{
    /// <summary>
    /// The width of a blind index: the output of HMAC-SHA-256, in bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The only shape check anything on this side can make, which is why it is exact.</b> The
    /// server cannot recompute an index — that needs the account's index key, which lives in a browser
    /// — so a truncated, re-encoded or entirely fabricated value of the right length is accepted here
    /// and is indistinguishable from a correct one: it is stable, it never collides, it keys perfectly,
    /// and it matches nothing for the life of the account. The width is the whole of the defence, so a
    /// value of any other length is refused rather than padded into shape.
    /// </para>
    /// <para>
    /// <b>A width, not a ceiling, and it follows from the algorithm rather than from a choice.</b>
    /// HMAC-SHA-256 emits 32 bytes; the client renders them as 43 characters of unpadded base64url and
    /// nothing truncates in between. There is no band of legal sizes to allow for.
    /// </para>
    /// <para>
    /// <see langword="const"/> and public for the reason
    /// <see cref="Users.WrappedAccountKeys.EnvelopeLength"/> is both: the wire edge and the persistence
    /// check constraint are written from this number, and it is read in <c>[Arguments(...)]</c>, which
    /// admits nothing but a constant expression. A second <c>32</c> spelled out at either of those
    /// sites is a way for a stored column and the text that fills it to disagree.
    /// </para>
    /// </remarks>
    public const int BlindIndexLength = 32;

    private readonly byte[] _blindIndex;

    /// <summary>
    /// Nothing constructs one but <see cref="Of"/> — the whole of what this type is for.
    /// </summary>
    /// <param name="name">The sealed name, already judged.</param>
    /// <param name="blindIndex">
    /// The buffer this value owns outright — the copy <see cref="Of"/> made, never the caller's array.
    /// </param>
    private IndexedName(NarrativeField name, byte[] blindIndex)
    {
        Name = name;
        _blindIndex = blindIndex;
    }

    /// <summary>The sealed name, judged as a narrative field under <see cref="NarrativeFieldLimits.NameBytes"/>.</summary>
    public NarrativeField Name { get; }

    /// <summary>
    /// The blind index over the normalised name: <c>HMAC-SHA-256</c> under the account's index key,
    /// equal across every row holding the same name in the same column of the same account.
    /// </summary>
    /// <remarks>
    /// A copy taken at the factory rather than a view onto the caller's array, the rule
    /// <see cref="Users.WrappedAccountKeys.For"/> keeps for its own envelopes.
    /// </remarks>
    public ReadOnlyMemory<byte> BlindIndex => _blindIndex;

    /// <summary>
    /// Takes a sealed name and its index as the one value the column pair holds, refusing either half
    /// on its own.
    /// </summary>
    /// <param name="envelope">The sealed name, as the wire edge decoded it.</param>
    /// <param name="blindIndex">The index the client computed over the normalised name.</param>
    /// <returns>The value the column pair will hold.</returns>
    /// <remarks>
    /// <para>
    /// <b>It names <see cref="NarrativeFieldLimits.NameBytes"/> itself and takes no ceiling.</b>
    /// <see cref="NarrativeField.Sealed"/> has to be told which cap applies because it serves both
    /// field classes; this factory serves one — every blind-indexed column in the product is a
    /// <c>name</c> — so a ceiling parameter here would be a way for a caller to file a description-sized
    /// value into a name column, offered for no reason anybody could state.
    /// </para>
    /// <para>
    /// <b>Both parameters are non-nullable, and that is what makes "a call cannot be half" true rather
    /// than merely intended.</b> <c>default(ReadOnlyMemory&lt;byte&gt;)</c> — what a caller that passed
    /// nothing hands over — is a zero-length buffer, which fails the envelope's floor on one side and
    /// the index's width on the other. Neither omission has a spelling this signature accepts, and
    /// neither is repaired.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="envelope"/> is not a well-formed envelope of at most
    /// <see cref="NarrativeFieldLimits.NameBytes"/> bytes, or <paramref name="blindIndex"/> is not
    /// exactly <see cref="BlindIndexLength"/> bytes.
    /// </exception>
    public static IndexedName Of(ReadOnlyMemory<byte> envelope, ReadOnlyMemory<byte> blindIndex)
    {
        // The name half is delegated whole, never re-spelled: the floor, the version and the cap are
        // one type's rules, and this factory's contribution is naming which cap — NameBytes, because
        // every blind-indexed column in the product is a name. The parameter is called `envelope` on
        // both sides, so the refusal reaches the caller already naming the half at fault.
        NarrativeField name = NarrativeField.Sealed(envelope, NarrativeFieldLimits.NameBytes);

        // An equality and not a ceiling, and both sides of it are this factory's own work — nothing
        // beneath a domain factory measures anything. HMAC-SHA-256 emits exactly this many bytes and
        // nothing truncates in between, so there is no band of legal sizes and a width written as an
        // upper bound would admit a short value silently. Zero length is in scope here too: it is what
        // a caller that passed nothing hands over, and the signature has no other spelling for it.
        //
        // Refused rather than padded or truncated into shape. Either repair stores a well-formed row
        // holding a value that is stable, never collides, keys perfectly and matches nothing for the
        // life of the account — and the row looks correct until somebody searches for the name it was
        // supposed to find.
        if (blindIndex.Length != BlindIndexLength)
        {
            throw new ArgumentException(
                $"A blind index of exactly {BlindIndexLength} bytes was expected.",
                nameof(blindIndex));
        }

        // A copy of the index for the reason the name half keeps one: a ReadOnlyMemory<byte> is a
        // window onto a buffer somebody else still owns, and a buffer reused for the next row rewrites
        // an index that has already been accepted.
        return new IndexedName(name, blindIndex.ToArray());
    }
}
