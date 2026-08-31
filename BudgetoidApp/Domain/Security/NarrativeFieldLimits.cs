namespace Domain.Security;

/// <summary>
/// How large a sealed narrative field is allowed to be, in envelope bytes: one number for the fields
/// that hold a name and one for the fields that hold free text.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two constants over field <em>classes</em>, not eight over fields.</b> Eight columns across six
/// entities carry a narrative envelope, and the specification does not state eight caps — it states a
/// two-row table, one row for names and one for descriptions. Written out per column, that one rule
/// would have eight owners and six of them would be redundant restatements of the other two, which is
/// six chances for the table and the code to disagree and no way to see the disagreement: a column
/// whose constant drifted upward still stores, still reads back, still opens, and differs from every
/// other column of its class only in what it will accept from a client nobody is testing that day.
/// The class is the unit the rule is written in, so the class is the unit the code declares.
/// </para>
/// <para>
/// <b>Envelope bytes, not plaintext bytes, and the difference is not slack to reclaim.</b> The number
/// bounds what a column stores — a whole
/// <see cref="CiphertextEnvelope"/>, version byte and nonce and tag included — because that is the only
/// length anything on this side can measure. AES-GCM ciphertext is exactly as long as its plaintext,
/// so the text underneath a name is <see cref="CiphertextEnvelope.MinimumLength"/> bytes shorter than
/// the cap; the server cannot say so, and must not be "corrected" into a cap on characters. It never
/// sees a character. A limit expressed in anything but stored bytes would be a limit this side could
/// not enforce, and a limit nothing enforces is a comment.
/// </para>
/// <para>
/// <b>In <c>Domain</c> because the persistence check constraints are written from these numbers</b> —
/// the argument <see cref="CiphertextEnvelope"/> makes for living here, unchanged. A limits type one
/// ring out would be a second home for a rule the innermost ring already owns, and the column
/// constraint would then be built from whichever copy the configuration happened to import.
/// </para>
/// <para>
/// <b><see langword="const"/> rather than computed, for the reason
/// <see cref="Users.WrappedAccountKeys.EnvelopeLength"/> gives about itself.</b> These are read in
/// <c>[Arguments(...)]</c> by the specs that pin them and in the interpolated strings that build the
/// <c>length(...) &lt;= …</c> check constraints, and an attribute argument admits nothing but a
/// constant expression. A static property would not compile at the first of those call sites and would
/// have nothing to offer at the second.
/// </para>
/// <para>
/// <b>Neither number is derived from the other and neither is derived from the format.</b> They are
/// product decisions about how much a person may type into two different kinds of field, so there is
/// no arithmetic that would make one follow from anything — unlike
/// <see cref="Users.WrappedAccountKeys.EnvelopeLength"/>, where the framing plus one fixed plaintext
/// genuinely is the width. Writing them as sums over
/// <see cref="CiphertextEnvelope.MinimumLength"/> would dress a choice up as a consequence.
/// </para>
/// </remarks>
public static class NarrativeFieldLimits
{
    /// <summary>
    /// The cap on a sealed name — <c>payees.name</c>, <c>accounts.name</c>, <c>categories.name</c>,
    /// <c>category_groups.name</c> and <c>budgets.name</c>.
    /// </summary>
    /// <remarks>
    /// A name is a label somebody scans a list of, not a place to write in. This leaves roughly a
    /// kilobyte of UTF-8 underneath the framing, which is far past any label a person types and still
    /// small enough that a column of them is a column of labels.
    /// </remarks>
    public const int NameBytes = 1024;

    /// <summary>
    /// The cap on a sealed description — <c>transactions.description</c>,
    /// <c>categories.description</c> and <c>category_groups.description</c>.
    /// </summary>
    /// <remarks>
    /// Free text a person writes for themselves: a note on what a transaction was for, a sentence
    /// about what belongs in a category. Larger than <see cref="NameBytes"/> because the two are
    /// different acts, and not larger still because a ledger row is not a document store.
    /// </remarks>
    public const int DescriptionBytes = 2560;
}
