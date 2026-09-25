using Domain.Common;

namespace Domain.Security;

/// <summary>
/// The one rule a content-key rotation can be held to by a side that can decrypt nothing: a column that
/// held a value still holds one, and a column that held nothing still holds nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read this paragraph before building anything on it, because the rule is weaker than its name
/// suggests.</b> A rotation re-encrypts every narrative field of an account under a new content key.
/// This server holds no key for any of it, so it cannot compare the text going in against the text
/// coming out — a re-sealed envelope is byte-for-byte indistinguishable from an unrelated one, because
/// a fresh nonce changes the bytes either way and the associated data is never carried inside the
/// envelope. A client that asked to rotate and shipped somebody else's text, or its own text with a
/// word changed, is <em>accepted</em> by everything here. <b>Presence is the one property left that can
/// be checked without a key</b>: whether a column holds something is visible without opening it, and to
/// a party in this position that is the whole of what separates "the same text under a new key" from
/// "different text". <b>It is a weak rule, and it is the strongest one available</b> — both halves have
/// to be held together, because a reader who takes this for an integrity check will build a completion
/// step, an audit or a support answer on sand. Whether the new generation opens at all is the browser's
/// question, and it is asked after the run.
/// </para>
/// <para>
/// <b>One owner rather than the same four lines inside six entities.</b> Eight narrative columns across
/// six entities will pass through this rule. Restated six times it would be a rule that drifts five
/// ways, and this is drift nobody sees: the copy that forgot the null arm still stores, still reads back
/// and still opens, differing from the others only in what it lets a rotation do to a column nobody
/// exercised that day. The argument is <see cref="NarrativeFieldLimits"/>' about its two constants,
/// applied one ring up.
/// </para>
/// <para>
/// <b>Length is not checked here, and the absence is deliberate.</b> Caps already have three owners —
/// <see cref="NarrativeFieldLimits"/> states the two numbers, <see cref="NarrativeField.Sealed"/>
/// applies whichever one its caller names, and the column's <c>CHECK</c> constraint is built from the
/// same constants — so by the time a value reaches a reseal it has been through all three. A fourth
/// opinion could only agree redundantly or disagree silently, and the disagreeing version still stores,
/// still reads back and still opens: it differs only in which rotations it refuses, which is how a
/// description re-measured against <see cref="NarrativeFieldLimits.NameBytes"/> would reach production
/// refusing values its own column accepts.
/// </para>
/// <para>
/// <b>The two refusals are told apart by their sentences, which is a compromise rather than a
/// preference.</b> Both arms key on the same column, so the key cannot separate them; both are 400s, so
/// the exception type cannot either; and <see cref="ValidationException"/> carries nothing else — it is
/// a message dictionary and no more. "You cleared a field" and "you invented one" are opposite client
/// bugs with opposite remedies, so a shared sentence would refuse correctly and report uselessly. What
/// would make the distinction checkable without touching prose is the arrangement
/// <see cref="ConflictKind"/> already is one status code up: a closed vocabulary beside the sentence.
/// <see cref="ValidationException"/> has no equivalent, and giving it one is a change to
/// <c>Domain/Common</c> touching every refusal in the product — a decision to take deliberately, not a
/// side effect of wanting a firmer distinction here. Until it is taken, the sentence is the only
/// distinguisher, and <c>docs/design/voice.md</c> owns the wording.
/// </para>
/// <para>
/// <b>A non-nullable narrative column cannot reach this rule at all.</b> <c>accounts.name</c>,
/// <c>payees.name</c>, <c>categories.name</c> and <c>category_groups.name</c> are typed
/// <see cref="NarrativeField"/> rather than <see cref="NarrativeField"/><c>?</c>, so presence is carried
/// by the signature and there is no absence to judge. The rule's reach is exactly the four nullable
/// columns: the three descriptions and <c>budgets.name</c>, which is nullable because a budget is
/// provisioned nameless by the one path that creates an account.
/// </para>
/// </remarks>
public static class NarrativeReseal
{
    /// <summary>
    /// Judges one column of a content-key rotation on the only property this side can see — whether the
    /// column held something and whether the rotation supplied something — and answers with the value
    /// the column will hold.
    /// </summary>
    /// <param name="current">The envelope the column holds today, or <see langword="null"/> if it holds
    /// nothing.</param>
    /// <param name="incoming">The envelope the rotation supplied for it, or <see langword="null"/> if it
    /// supplied none.</param>
    /// <param name="column">
    /// The member the value lands in, as the request spells it — supplied by the caller because this
    /// rule is shared by every nullable narrative column and owns none of them. It is the key a refusal
    /// is filed under, so a 400 names a member the request actually carries rather than a word invented
    /// here. Keys ship verbatim to the client.
    /// </param>
    /// <returns>
    /// The incoming envelope when the column held one, or <see langword="null"/> when it held nothing and
    /// the rotation left it alone.
    /// </returns>
    /// <exception cref="ValidationException">
    /// The rotation changed the column's presence: it supplied nothing for a column that holds a value,
    /// or a value for a column that holds nothing. Keyed on <paramref name="column"/> either way; the two
    /// are separated by the sentence and by nothing else.
    /// </exception>
    public static NarrativeField? Resealed(
        NarrativeField? current,
        NarrativeField? incoming,
        string column)
    {
        // No user typed this. A blank key would file a refusal under a member no client can find, which
        // presents as a 400 nobody can act on rather than as the caller defect it is.
        ArgumentException.ThrowIfNullOrWhiteSpace(column);

        // The arm that carries the data loss. The column is nullable, so writing the absence through
        // produces a legal row violating no constraint and byte-identical to one belonging to somebody
        // who deliberately filed no note — the failure Category.Description describes as invisible where
        // the same omission on a NOT NULL name is 23502. Nothing in the schema can tell that bug from an
        // operation, so the refusal has to be here.
        if (current is not null && incoming is null)
        {
            throw Refused(
                column,
                "Re-encrypting cannot clear a field. Send this one again, sealed under the new key.");
        }

        // The quieter arm, and the one a reviewer will propose relaxing: filling in an empty description
        // harms no data. It is refused because presence is the only thing this side can check, so an arm
        // admitting a change of presence gives up the one property the rule is made of — and what lands
        // in that column is text this server cannot read, attributed to a person who never wrote it, in a
        // run they authorised as "re-encrypt what I have".
        if (current is null && incoming is not null)
        {
            throw Refused(
                column,
                "Re-encrypting cannot add a value to a field that has none. Leave it out, then add it as "
                + "a separate edit.");
        }

        // Both surviving cases agree on presence, and in both the answer is what arrived: the new
        // envelope where the column holds one, null where it holds nothing. Never `current` — a rule that
        // judged the pair correctly and then handed back the envelope already in the column would leave
        // rows sealed under a key the promotion step at the end of the run destroys, with nothing red
        // anywhere until the account stops opening.
        return incoming;
    }

    /// <summary>
    /// The refusal both arms raise, keyed on the column the caller named — one construction, so the two
    /// cannot drift into differing about anything but their sentence.
    /// </summary>
    private static ValidationException Refused(string column, string message) =>
        new(new Dictionary<string, string[]> { [column] = [message] });
}
