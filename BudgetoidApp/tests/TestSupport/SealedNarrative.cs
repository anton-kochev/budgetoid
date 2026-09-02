using System.Text;
using Domain.Security;

namespace TestSupport;

/// <summary>
/// The sealed value a narrative column holds, for a test that needs one and cannot make a real one.
/// </summary>
/// <remarks>
/// <para>
/// <b>One helper rather than an envelope literal per seeder.</b> Every caller wants the same thing — an
/// envelope this server will accept — and a literal copied into each test file is that many places for
/// the framing to drift from the one <see cref="CiphertextEnvelope" /> owns. Here it is spelled from
/// that type's own constants, so a version byte or a floor that changes moves one line.
/// </para>
/// <para>
/// <b>The bytes are filler and not ciphertext, and nothing on this side can tell the difference.</b>
/// A key-encryption key is derived in a browser from a recovery factor this server never sees, so what
/// a test can build is exactly what the server can judge: the version byte, the framing's floor, and a
/// length under the column's cap. Nothing in either suite opens one, because nothing here holds a value
/// that could.
/// </para>
/// <para>
/// <b>The label is not a name and is never read back as one.</b> It is what makes two seeded rows
/// differ by eye in a failure message and in bytes in the database — the same job the string literals
/// did when this column held text. It is a closer model than a fixed buffer, because two people who
/// both call a budget "Household" store different envelopes anyway: every seal draws a fresh nonce. A
/// test that wants to assert on a name asserts on the envelope it passed in.
/// </para>
/// </remarks>
public static class SealedNarrative
{
    /// <summary>
    /// A well-formed sealed name, as long as the framing's floor plus <paramref name="label" />.
    /// </summary>
    /// <param name="label">
    /// What distinguishes this fixture from the next one. The default is the empty label, which
    /// produces the shortest envelope the format can carry — what a name somebody cleared seals to,
    /// since AES-GCM ciphertext is exactly the length of its plaintext.
    /// </param>
    /// <returns>The value a <c>name</c> column will hold.</returns>
    public static NarrativeField Name(string label = "")
    {
        ArgumentNullException.ThrowIfNull(label);

        byte[] labelBytes = Encoding.UTF8.GetBytes(label);
        byte[] envelope = new byte[CiphertextEnvelope.MinimumLength + labelBytes.Length];

        // Position-varying so that a row read back into the wrong column, or an entity that assigned
        // one field to two, is visible rather than a buffer of one repeated byte that matches anything.
        for (int position = 1; position < envelope.Length; position++)
        {
            byte source = labelBytes.Length == 0
                ? (byte)0
                : labelBytes[(position - 1) % labelBytes.Length];

            envelope[position] = (byte)(source + (position * 31));
        }

        // Written last, so a filler loop that walked from zero could not overwrite it.
        envelope[0] = CiphertextEnvelope.Version;

        // Through the judging factory and never through the unchecked door: a fixture that skipped the
        // rule would seed rows the production write path could not have produced, and the tests reading
        // them would be describing a system that does not exist.
        return NarrativeField.Sealed(envelope, NarrativeFieldLimits.NameBytes);
    }

    /// <summary>
    /// A well-formed sealed description, as long as the framing's floor plus <paramref name="label" />.
    /// </summary>
    /// <param name="label">
    /// What distinguishes this fixture from the next one. The default is the empty label, which produces
    /// the shortest envelope the format can carry — what a note somebody emptied seals to, since AES-GCM
    /// ciphertext is exactly the length of its plaintext. That value is not the same as no description at
    /// all, which is <see langword="null" /> and reaches a nullable column as NULL.
    /// </param>
    /// <returns>The value a <c>description</c> column will hold.</returns>
    /// <remarks>
    /// <para>
    /// <b>Not a wrapper over <see cref="Name(string)" />, because the cap is the honest part of the
    /// fixture.</b> <see cref="NarrativeFieldLimits" /> carries two ceilings over field <em>classes</em> —
    /// <see cref="NarrativeFieldLimits.NameBytes" /> for the five name columns and
    /// <see cref="NarrativeFieldLimits.DescriptionBytes" /> for the three description ones — so a
    /// description fixture built on the name's factory would be refused for a limit written about a
    /// different field. A test wanting a two-thousand-byte note has to be able to build one.
    /// </para>
    /// <para>
    /// <b>There is deliberately no <c>Indexed</c> twin for a description, and there must not be one.</b> A
    /// blind index answers "which row holds this name". A description is not looked up, is not unique and
    /// is not a name; an index over one would be a deterministic per-account fingerprint of somebody's
    /// free text with nothing on the other side asking for it.
    /// </para>
    /// <para>
    /// <b>It caps by construction, which is why the cases about the cap cannot use it.</b> A test that
    /// needs an envelope one byte over <see cref="NarrativeFieldLimits.DescriptionBytes" /> has to build
    /// it inline — this helper would refuse to produce the one value that catches a widened ceiling.
    /// </para>
    /// </remarks>
    public static NarrativeField Description(string label = "")
    {
        ArgumentNullException.ThrowIfNull(label);

        byte[] labelBytes = Encoding.UTF8.GetBytes(label);
        byte[] envelope = new byte[CiphertextEnvelope.MinimumLength + labelBytes.Length];

        // Position-varying, and the SAME filler the name uses, which is what makes a description and a
        // name derived from ONE label distinguishable from each other only by their lengths — while two
        // DIFFERENT labels share no byte at any offset. That is the property the entity tests rely on to
        // see a factory that assigned one parameter to two fields.
        for (int position = 1; position < envelope.Length; position++)
        {
            byte source = labelBytes.Length == 0
                ? (byte)0
                : labelBytes[(position - 1) % labelBytes.Length];

            envelope[position] = (byte)(source + (position * 31));
        }

        // Written last, so a filler loop that walked from zero could not overwrite it.
        envelope[0] = CiphertextEnvelope.Version;

        // Through the judging factory and never through the unchecked door, for the reason
        // Name(string) gives — and under the DESCRIPTION's cap, which is the whole point of this member
        // existing beside it.
        return NarrativeField.Sealed(envelope, NarrativeFieldLimits.DescriptionBytes);
    }

    /// <summary>
    /// The blind index over <paramref name="label" />: exactly
    /// <see cref="IndexedName.BlindIndexLength" /> bytes, equal for equal labels and different for
    /// different ones.
    /// </summary>
    /// <param name="label">
    /// The text this index stands for. The same label always yields the same bytes, which is the one
    /// property of a real blind index a fixture can reproduce and the only one any test relies on.
    /// </param>
    /// <returns>The value a <c>name_key</c> column will hold.</returns>
    /// <remarks>
    /// <para>
    /// <b>A plain digest and not an HMAC, because there is no key to take one under and a fixture must
    /// not invent one.</b> A production index is <c>HMAC-SHA-256</c> under the account's index key,
    /// which lives in a browser; this server holds no such key and neither does either suite. What the
    /// server can judge is the width and nothing else — it cannot recompute an index, cannot check one
    /// against the name beside it, and cannot tell a correct value from a fabricated one of the right
    /// length. So the fixture reproduces exactly what is judgeable: 32 bytes, deterministic in the
    /// label. <see cref="System.Security.Cryptography.SHA256" /> is chosen for emitting that width by
    /// construction rather than by a truncation somebody would have to maintain.
    /// </para>
    /// <para>
    /// <b>Determinism is load-bearing rather than convenience.</b> The uniqueness rule this column
    /// carries — one name per budget — comes back as equality of digests, so a seeder that wanted two
    /// rows to collide, or to be sure they do not, has no other lever: the envelopes beside them differ
    /// for equal names anyway, since every seal draws a fresh nonce.
    /// </para>
    /// </remarks>
    public static ReadOnlyMemory<byte> BlindIndex(string label = "")
    {
        ArgumentNullException.ThrowIfNull(label);

        return System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(label));
    }

    /// <summary>
    /// A sealed name and the index over the same label, as the one value the column pair holds.
    /// </summary>
    /// <param name="label">What distinguishes this fixture from the next one, applied to both halves.</param>
    /// <returns>The value <see cref="Domain.Accounts.Account.Create" /> and <c>Update</c> accept.</returns>
    /// <remarks>
    /// <para>
    /// <b>One label drives both halves, which is what makes this fixture model the client rather than
    /// the columns.</b> A browser seals a name and indexes <em>that same text</em>; a helper taking two
    /// labels would offer a spelling for a row whose index describes a name it does not hold — the
    /// exact defect <see cref="IndexedName" /> exists to make unspellable. A test that wants the two
    /// halves to disagree has to build that disagreement itself, in the open, where a reviewer sees it.
    /// </para>
    /// <para>
    /// <b>NOTHING ON THIS SIDE COMPARES THE TWO HALVES AGAINST EACH OTHER, HERE OR IN PRODUCTION.</b>
    /// The server holds no index key, so "is this the index of that name?" is a question it cannot ask
    /// — not in <see cref="IndexedName.Of" />, not in a check constraint, not anywhere. A reader who
    /// assumes the pair is validated as a pair will build on a guarantee that does not exist.
    /// </para>
    /// <para>
    /// <b>What that leaves is a transposition, and it is caught by shape alone and only <em>almost</em>
    /// always.</b> Hand the index in as the envelope and it is refused by the version check roughly 255
    /// times in 256 — an index would have to happen to begin with
    /// <see cref="CiphertextEnvelope.Version" /> to pass as one — while it sails through the length band,
    /// since 32 bytes sits between the framing's floor and
    /// <see cref="NarrativeFieldLimits.NameBytes" />. Hand the envelope in as the index and it is
    /// refused by the width check unless the envelope happens to be exactly
    /// <see cref="IndexedName.BlindIndexLength" /> bytes — which, for
    /// <see cref="Name(string)" />, is every three-byte label. Both at once is rare rather than
    /// impossible, and when it happens the row stores, reads back and violates nothing: the name opens
    /// under nobody's key and the index matches nothing for the life of the account. There is no check
    /// to add. The defence is that neither <see cref="Domain.Accounts.Account.Create" /> nor
    /// <c>Update</c> offers a spelling for half a name, so a transposition has to be written as two
    /// arguments swapped on one line a reviewer reads.
    /// </para>
    /// </remarks>
    public static IndexedName Indexed(string label = "") =>
        IndexedName.Of(Name(label).Envelope, BlindIndex(label));

    /// <summary>
    /// The sealed name over <paramref name="label" /> in the alphabet the API carries it in: unpadded
    /// base64url.
    /// </summary>
    /// <param name="label">What distinguishes this fixture from the next one.</param>
    /// <returns>The value a request body's <c>name</c> member, and a response's, holds.</returns>
    /// <remarks>
    /// <para>
    /// <b>Here rather than copied into each test file, because the alphabet is a contract and two
    /// spellings of it disagree silently.</b> Unpadded base64url is what every binary member of this API
    /// crosses JSON in and what the client's strict decoder reads — not
    /// <c>System.Text.Json</c>'s own <see cref="byte" /><c>[]</c> handling, which emits padded standard
    /// base64. A test file that reached for the built-in would send a value the route refuses and would
    /// then be measuring the refusal rather than the case it was written for.
    /// </para>
    /// <para>
    /// <b>This is also the encoding an assertion about a RESPONSE has to use, and that is the change a
    /// reader will find surprising.</b> A case that seeded an account called "Owners Current Account"
    /// and asserted the list payload contained those words was reading a name off the wire; there is no
    /// name on the wire any more, so the same claim — "this budget's rows came back and that budget's
    /// did not" — is made against the envelope. It is still per-row and still distinguishing, because
    /// the envelope is deterministic in the label; what it no longer is, is readable, which is the
    /// product working.
    /// </para>
    /// <para>
    /// <b>A CASE THAT PINS THE ENCODING RATHER THAN THE VALUE MUST CHOOSE ITS LABEL, AND MOST LABELS
    /// CANNOT SEE THE DEFECT.</b> Padded standard base64 and unpadded base64url differ in exactly two
    /// ways — the trailing <c>=</c> characters, and the two alphabet slots 62 and 63 (<c>+/</c> against
    /// <c>-_</c>) — so an envelope whose length is a multiple of three AND none of whose six-bit groups
    /// lands on 62 or 63 spells identically under both. Such a label makes an assertion against this
    /// member green whichever encoder the production code reached for, which is the whole thing the
    /// assertion was written to catch.
    /// </para>
    /// <para>
    /// This is measured rather than theoretical: <c>Name("Essentials")</c> is 39 bytes and satisfies
    /// both conditions, and a mutation replacing <c>PasskeyEncoding.Encode</c> with
    /// <c>Convert.ToBase64String</c> in <c>CategoryGroupDto.FromCategoryGroup</c> killed no test while
    /// every encoding case in the category-group suite used it. <b>Neither condition is on its own the
    /// rule</b> — <c>Name("Sinking Funds")</c> is 42 bytes and emits no padding, and is still caught,
    /// because it happens to carry a 62/63 byte. A label is safe when the two spellings differ, which is
    /// a property to check rather than to reason about from the length. Across the suite as it stands,
    /// "Essentials" was the only label of either kind that was blind.
    /// </para>
    /// </remarks>
    public static string EncodedName(string label = "") =>
        Base64UrlText.Encode(Name(label).Envelope.Span);

    /// <summary>
    /// The sealed description over <paramref name="label" /> in the alphabet the API carries it in:
    /// unpadded base64url.
    /// </summary>
    /// <param name="label">What distinguishes this fixture from the next one.</param>
    /// <returns>The value a request body's <c>description</c> member, and a response's, holds.</returns>
    /// <remarks>
    /// <b>The alphabet argument is <see cref="EncodedName(string)" />'s and is not restated; what is this
    /// member's own is the cap.</b> It encodes <see cref="Description(string)" />, so it carries that
    /// member's ceiling — a test reaching for <see cref="EncodedName(string)" /> to build a description
    /// body would be sending a value capped at the wrong number and would find out only for labels
    /// between the two limits. <b>An absent description is <see langword="null" /> and never the output of
    /// this member with an empty label</b>: that produces a legal twenty-nine-byte envelope, which is a
    /// note somebody emptied, and the route tells the two apart.
    /// </remarks>
    public static string EncodedDescription(string label = "") =>
        Base64UrlText.Encode(Description(label).Envelope.Span);

    /// <summary>
    /// The blind index over <paramref name="label" /> in the same alphabet.
    /// </summary>
    /// <param name="label">The text this index stands for.</param>
    /// <returns>The value a write request's <c>nameKey</c> member holds.</returns>
    /// <remarks>
    /// <b>There is no read counterpart to this and there must not be one.</b> The index is on no
    /// response by design — a client recomputes it from the name it just decrypted, under a key only it
    /// holds — so this helper belongs on the write side alone. An assertion that a response carried this
    /// value would be pinning a deterministic, per-account fingerprint of a name into the API's output,
    /// which is the one property of the pair that survives having no key.
    /// </remarks>
    public static string EncodedIndex(string label = "") =>
        Base64UrlText.Encode(BlindIndex(label).Span);
}
