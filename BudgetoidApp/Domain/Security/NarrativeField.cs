namespace Domain.Security;

/// <summary>
/// One sealed narrative value as a column holds it: the AEAD envelope over text this server has never
/// seen and holds no key for.
/// </summary>
/// <remarks>
/// <para>
/// <b>A type rather than <see cref="ReadOnlyMemory{T}"/> on the entity beside a shared validator, and
/// the two rejected alternatives fail in different ways.</b> Eight columns across six entities carry a
/// narrative envelope, so a private check per entity is six copies of one rule — and the copy that
/// drifts still stores, still reads back and still opens, differing from the others only in what it
/// admits from a client nobody exercised that day. A shared static validator fixes that and leaves the
/// worse half standing: the property is still typed as raw bytes, so it is still assignable from any
/// buffer in scope, and the next member added to the entity — an <c>Update</c>, a rename, a correction
/// applied on some later path — assigns bytes nothing judged, with the validator sitting one file over
/// looking like the rule was kept. What closes that is the property's <em>type</em>, because a type is
/// the one guard a later caller cannot forget to call.
/// </para>
/// <para>
/// <b>It is the strongest mechanical expression this codebase can give the requirement that no
/// narrative value is ever server-readable.</b> The only type a narrative column accepts has no
/// constructor, no factory and no conversion taking a <see cref="string"/>, and none may be added: with
/// the column typed this way, writing plaintext into one does not compile. That moves the rule out of
/// review and into the build — which matters because the mistake it prevents is invisible afterwards.
/// A row holding plaintext is a well-formed row; nothing reads back wrong, no constraint fires, and
/// the operator simply has the ledger. Compare the second account-creating path <c>CLAUDE.md</c>
/// describes: one line, nothing red. This is that shape of mistake with the compiler put in front of
/// it.
/// </para>
/// <para>
/// <b>A sealed class and deliberately not a <see langword="readonly"/> <see langword="struct"/>.</b> A
/// struct carries a public parameterless constructor no author can hide, so
/// <c>default(NarrativeField)</c> would be a narrative field holding no envelope — assignable to a
/// non-nullable property, satisfying every signature here, and carrying zero bytes into a column whose
/// whole point is that nothing reaches it unjudged. A field of an uninitialised struct is exactly what
/// an entity built by a path that forgot to seal a member would hold, which is the one case this type
/// exists to make impossible. The allocation is not a cost worth that.
/// </para>
/// <para>
/// <b>What it does not check is what nothing on this side can.</b> The framing is the whole of it —
/// the floor and the version byte <see cref="CiphertextEnvelope"/> owns, plus the caller's ceiling. A
/// nonce of zeros and a tag of zeros are well-formed by every rule here, and the server holds no value
/// that would say otherwise. Anything stronger needs a key, and a design in which this side had one is
/// the design the product exists to avoid.
/// </para>
/// <para>
/// <b>It throws rather than answering.</b> The <c>Try</c> shape belongs at the wire edge, where
/// <c>Application/Security/CiphertextEnvelopeText</c> already turns a malformed member into the
/// refusal sentence its own caller words. By the time bytes arrive here they have been decoded and
/// framed by that edge, so a value this factory refuses is a value the Application ring failed to put
/// through it — a defect in this codebase, not in a request, and the loud report is the correct one.
/// The refusal deliberately does <em>not</em> arrive as
/// <see cref="Common.ValidationException"/>, which the entity factories beside it use: that exception
/// keys its messages on the property a value lands in, and this type is shared by eight columns and
/// owns none of them. Every one of them would key under the same word, and a 400 naming a member no
/// request has is worse than no 400 at all.
/// </para>
/// </remarks>
public sealed class NarrativeField
{
    private readonly byte[] _envelope;

    /// <summary>
    /// Nothing constructs one but the factories below — the whole of what this type is for.
    /// </summary>
    /// <param name="envelope">
    /// The buffer this value owns outright. Every caller is a factory in this file and every one of
    /// them hands over a copy it made, which is what keeps <see cref="Envelope"/>'s documented rule a
    /// property of the type rather than of the path a particular instance came down.
    /// </param>
    private NarrativeField(byte[] envelope)
    {
        _envelope = envelope;
    }

    /// <summary>
    /// The envelope as the column holds it: <c>version(1) ‖ nonce(12) ‖ ciphertext ‖ tag(16)</c>.
    /// </summary>
    /// <remarks>
    /// A copy taken at the factory rather than a view onto the caller's array, the rule
    /// <see cref="Users.WrappedAccountKeys.For"/> keeps for its own two envelopes: a
    /// <see cref="ReadOnlyMemory{T}"/> is a window onto a buffer somebody else still owns, and a buffer
    /// reused for the next field would rewrite an envelope that has already been accepted.
    /// </remarks>
    public ReadOnlyMemory<byte> Envelope => _envelope;

    /// <summary>
    /// Takes <paramref name="envelope"/> as a sealed narrative value, refusing anything that is not a
    /// well-formed envelope of at most <paramref name="maxBytes"/> bytes.
    /// </summary>
    /// <param name="envelope">The envelope bytes, as the wire edge decoded them.</param>
    /// <param name="maxBytes">
    /// The largest envelope this column stores — one of <see cref="NarrativeFieldLimits"/>' two
    /// numbers. A parameter and not a constant here, for the reason
    /// <c>Application/Security/CiphertextEnvelopeText</c> gives about its own ceiling: a name and a
    /// description are two different caps, and one number standing for both would refuse whichever
    /// field it was not written for.
    /// </param>
    /// <returns>The value the column will hold.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxBytes"/> is zero or negative — a ceiling no envelope can satisfy, so a
    /// caller that computed one has a defect that would otherwise present as every field being
    /// refused.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="envelope"/> is shorter than <see cref="CiphertextEnvelope.MinimumLength"/>,
    /// longer than <paramref name="maxBytes"/>, or does not lead with
    /// <see cref="CiphertextEnvelope.Version"/>.
    /// </exception>
    public static NarrativeField Sealed(ReadOnlyMemory<byte> envelope, int maxBytes)
    {
        // The ceiling first, and the order is what keeps the two refusals apart. A ceiling no envelope
        // can satisfy is a defect in the caller's arithmetic, not in the value it was handed; checked
        // second, every field in that column would be refused as malformed and the next person to look
        // would go and read the browser. ArgumentOutOfRangeException is an ArgumentException, so
        // nothing but the exact type separates them at the call site either.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        // The floor and the version together, from the type that owns them, and never re-spelled here:
        // a second copy of "at least 29 bytes leading with 0x01" is a second thing to update the day a
        // version 2 exists. The floor is a >= inside that member — an empty plaintext is a legitimate
        // value and AES-GCM ciphertext is exactly the length of its plaintext, so a field somebody
        // cleared seals to a version, a nonce and a tag and nothing else.
        if (!CiphertextEnvelope.IsWellFormed(envelope.Span))
        {
            throw new ArgumentException(
                $"A sealed narrative value is at least {CiphertextEnvelope.MinimumLength} bytes and "
                + $"leads with version {CiphertextEnvelope.Version}.",
                nameof(envelope));
        }

        // The caller's number and never a constant of this type's own: a name and a description are
        // two different caps, and one number standing for both would refuse whichever field it was not
        // written for. Inclusive, because the cap names a length that is legal.
        if (envelope.Length > maxBytes)
        {
            throw new ArgumentException(
                $"A sealed narrative value of at most {maxBytes} bytes was expected.",
                nameof(envelope));
        }

        // A copy, not the caller's view. ReadOnlyMemory<byte> is a window onto a buffer somebody else
        // still owns, so a buffer reused for the next field would rewrite an envelope that has already
        // been accepted — and nothing downstream could notice, because the result is a well-formed row
        // holding ciphertext that will not open, months later.
        return new NarrativeField(envelope.ToArray());
    }

    /// <summary>
    /// Takes <paramref name="envelope"/> as a sealed narrative value the same way
    /// <see cref="Sealed"/> does, or answers <see langword="null"/> when the caller supplied no value
    /// at all.
    /// </summary>
    /// <param name="envelope">
    /// The envelope bytes, or <see langword="null"/> where the column is nullable and this row has
    /// nothing in it.
    /// </param>
    /// <param name="maxBytes">As <see cref="Sealed"/>.</param>
    /// <returns>
    /// The value the column will hold, or <see langword="null"/> when <paramref name="envelope"/> was
    /// absent.
    /// </returns>
    /// <remarks>
    /// <b>Its own member rather than a nullable parameter on <see cref="Sealed"/>, because "absent"
    /// and "empty" are one value in this parameter's type and two answers in the domain.</b> Three of
    /// the eight narrative columns are nullable — the descriptions — and for those, no value is a legal
    /// state of the row. But <c>default(ReadOnlyMemory&lt;byte&gt;)</c> is a non-null, zero-length
    /// buffer, which is precisely what a caller that passed nothing hands over; folded into
    /// <see cref="Sealed"/>, an absent description would be judged as an envelope, fail the floor, and
    /// be refused for a rule written about values that exist. Split, the question "is there one?" is
    /// answered by which member the caller names, before anything is measured, and a zero-length buffer
    /// that <em>was</em> supplied stays refused where it should be.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">As <see cref="Sealed"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A value was supplied and is not a well-formed envelope within <paramref name="maxBytes"/>.
    /// </exception>
    public static NarrativeField? SealedOrAbsent(ReadOnlyMemory<byte>? envelope, int maxBytes)
    {
        // Judged before the value is looked at, so that a caller which computed its ceiling wrongly
        // hears about it on the rows where this column happens to be empty as well. Deferred into
        // Sealed, the defect would surface only once somebody filed a description, which is the wrong
        // half of the population to learn it from.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        // Nullability decides, never emptiness. default(ReadOnlyMemory<byte>) — what a caller that
        // passed nothing hands over — is a non-null, zero-length buffer, so an implementation asking
        // "is there anything usable here?" would answer null for a value that WAS supplied and is
        // malformed, and a description somebody wrote would vanish into a nullable column with no
        // refusal anybody could act on.
        return envelope is { } supplied ? Sealed(supplied, maxBytes) : null;
    }

    /// <summary>
    /// Rebuilds a value from bytes a column already holds, without judging them.
    /// </summary>
    /// <param name="envelope">The stored bytes, exactly as the row has them.</param>
    /// <returns>The stored value.</returns>
    /// <remarks>
    /// <para>
    /// <b><see langword="internal"/> because a door that skips the rule does not belong on the public
    /// surface.</b> Public, this member would be the hole the type was built to close — a way to put
    /// unjudged bytes in a narrative column, on a call site that reads like bookkeeping and would pass
    /// any review that was not looking for it. A handler holding loose bytes has <see cref="Sealed"/>
    /// and nothing else.
    /// </para>
    /// <para>
    /// <b>What that costs, said plainly: today nothing outside this assembly can call it.</b> The
    /// solution carries no <c>InternalsVisibleTo</c> at all — checked — so the persistence
    /// configuration that will materialise these columns cannot reach this member as it stands. Closing
    /// that is a one-line grant on <c>Domain</c>, named at the assembly that gets it and reviewable as
    /// its own decision, which is the point: the alternative is a member every ring can reach so that
    /// one of them can.
    /// </para>
    /// <para>
    /// <b>It does not re-validate, and a reviewer will propose that it should. The symmetry is the
    /// mistake.</b> The write side judges what is arriving; the read side hands back what is already
    /// there, and the two are asked different questions. A validating read side makes the caps
    /// retroactive: lower <see cref="NarrativeFieldLimits.DescriptionBytes"/> by a byte and every row
    /// written under the old number stops materialising — not refused at some edge where somebody could
    /// be told, but thrown out of the middle of a query, so the screen that lists them fails whole and
    /// the value is unreachable by any path including the export. A limit change would have become data
    /// loss, silently, in a release whose diff is one integer. The same argument holds for the version
    /// byte: the day a version 2 exists, every version 1 row still has to come back so it can be read
    /// and rewritten, and a read side that refused it would have destroyed the migration it was meant
    /// to protect.
    /// </para>
    /// <para>
    /// This is <see cref="Users.WrappedAccountKeys"/>' own arrangement — a validating factory and a
    /// private, unchecked materialisation path — stated out loud, because there it is left implicit and
    /// the next reader has nothing to weigh the proposal against. What keeps stored bytes honest is the
    /// column's <c>CHECK</c> constraint and the fact that this factory is the only way they got there,
    /// not a second inspection on the way out.
    /// </para>
    /// <para>
    /// <b>It copies, though it does not judge, and the two are separate questions.</b> Only the second
    /// is the one argued above. <see cref="Envelope"/> promises a buffer nobody else holds, and a
    /// promise kept on one construction path and not the other would oblige every later reader to know
    /// which factory built the instance in front of them — which is the kind of thing a reader has no
    /// way to check and every reason to assume.
    /// </para>
    /// </remarks>
    internal static NarrativeField FromStore(ReadOnlyMemory<byte> envelope) =>
        new(envelope.ToArray());
}
