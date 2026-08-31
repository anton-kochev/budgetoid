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
}
