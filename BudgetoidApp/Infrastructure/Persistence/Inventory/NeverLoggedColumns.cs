namespace Infrastructure.Persistence.Inventory;

/// <summary>
/// One column whose value no log record may carry, with the argument for keeping it out.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no public constructor, for the reason <see cref="ColumnClassificationEntry" /> gives
/// at its own.</b> A column reaches the list through one of two factories, and they are two because
/// the columns reach it for two different reasons. <see cref="Identifying" /> takes a table, a column
/// and a sentence, and is how <see cref="NeverLoggedColumns.Entries" /> names a column that identifies
/// a person. <see cref="Narrative" /> takes an inventory entry rather than a pair of strings, and
/// refuses one the inventory does not classify narrative — so the narrative half of
/// <see cref="NeverLoggedColumns.All" /> can only be reached through
/// <see cref="DataInventory" />, and a narrative column typed out here by hand is not a value anybody
/// can construct through that door.
/// </para>
/// <para>
/// The reason is required and null-guarded, and not judged. A length floor over it is asserted next
/// door and, as at the inventory's exclusions, all it can do is make writing nothing impossible.
/// </para>
/// </remarks>
public sealed record NeverLoggedColumn
{
    /// <summary>
    /// The one constructor, private so that a column can only be reached through the factory that
    /// knows why it is on the list.
    /// </summary>
    private NeverLoggedColumn(string table, string column, string reason)
    {
        Table = table;
        Column = column;
        Reason = reason;
    }

    /// <summary>The table name as the EF model maps it, which is how <c>pg_class</c> stores it.</summary>
    public string Table { get; }

    /// <summary>The column name as the EF model maps it.</summary>
    public string Column { get; }

    /// <summary>Why a log record carrying this column's value would be a harm.</summary>
    public string Reason { get; }

    /// <summary>The pair as <c>table.column</c>, which is how the inventory names a column.</summary>
    /// <remarks>
    /// Computed by the rule <see cref="ColumnClassificationEntry.Qualified" /> uses, so the list and
    /// the inventory can be compared by string without either side re-rendering the other.
    /// </remarks>
    public string Qualified => $"{Table}.{Column}";

    /// <summary>
    /// Names a column that identifies a person without being narrative.
    /// </summary>
    /// <param name="table">The table name as the model maps it.</param>
    /// <param name="column">The column name as the model maps it.</param>
    /// <param name="because">What a log record carrying the value would give away, and to whom.</param>
    /// <returns>The entry.</returns>
    public static NeverLoggedColumn Identifying(string table, string column, string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(because);

        return new NeverLoggedColumn(table, column, because);
    }

    /// <summary>
    /// Carries a column the inventory classifies narrative onto the list.
    /// </summary>
    /// <param name="entry">An inventory entry classified <see cref="ColumnClassification.Narrative" />.</param>
    /// <param name="because">The reason every narrative column shares.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="entry" /> is classified anything but narrative.
    /// </exception>
    public static NeverLoggedColumn Narrative(ColumnClassificationEntry entry, string because)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(because);

        if (entry.Classification != ColumnClassification.Narrative)
        {
            throw new ArgumentException(
                $"{entry.Qualified} is classified {entry.Classification}, not narrative.", nameof(entry));
        }

        return new NeverLoggedColumn(entry.Table, entry.Column, because);
    }
}

/// <summary>
/// The columns whose value no log record the API writes may carry, in any rendering.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a second axis beside the classification, not a fourth word in it.</b>
/// <see cref="DataInventory.Entries" /> says what the product owes the person for a column — sealed,
/// exported, withheld from the export. This says what the product owes everybody who is <i>not</i>
/// the person: that operating the service never hands them a value that names somebody or repeats
/// what somebody wrote. The two disagree on purpose. <c>users.email</c> is
/// <see cref="ColumnClassification.Arithmetic" />, so the export carries it, and it is still never
/// logged — the export goes to the person, and a log goes to whoever operates, ships and reads logs.
/// Folding "not in a log" into the classification would force one of those two answers to be wrong.
/// </para>
/// <para>
/// <b>Two halves with two owners.</b> Every narrative column is on the list, and
/// <see cref="All" /> draws them from <see cref="DataInventory.Of" /> at type initialisation rather
/// than naming them, so a ninth sealed column is never-logged the day the inventory classifies it and
/// no edit here can fall behind. <see cref="Entries" /> is the other half and nothing else: the
/// columns that identify a person without being narrative, which the inventory has no word for.
/// </para>
/// <para>
/// <b>What a green census over this list does not say.</b> The container test reads every value of
/// every column here back from the database and searches the records a run wrote for each one, so it
/// can only find what the traffic drove and what its renderings cover. A column missing from this list
/// is not searched at all, which is why adding one is a decision about what identifies a person rather
/// than a test edit — and why <c>users.id</c> is deliberately absent: an internal identifier is what a
/// record is <i>meant</i> to refer to a person by.
/// </para>
/// </remarks>
public static class NeverLoggedColumns
{
    /// <summary>
    /// The reason every narrative column shares, because the reason is the classification rather
    /// than the column.
    /// </summary>
    private const string NarrativeReason =
        "what the person wrote, sealed in their browser so that this server only ever holds ciphertext "
        + "it cannot open. A log record carrying the stored envelope would be a copy the account does "
        + "not govern: an erasure deletes the row and leaves the record, and a key rotation re-seals "
        + "the column and leaves the record sealed under the key the rotation retired, kept for as long "
        + "as the log pipeline keeps anything and readable by whoever it lets read";

    /// <summary>
    /// The columns that identify a person, or a person's authenticator, without being narrative.
    /// </summary>
    /// <remarks>
    /// Never a narrative column: one written here would be a hand copy of the inventory that stays
    /// behind when the inventory changes. Declared before <see cref="All" />, which reads it, because
    /// static initialisers run in the order they are written.
    /// </remarks>
    public static IReadOnlyList<NeverLoggedColumn> Entries { get; } =
    [
        NeverLoggedColumn.Identifying(
            "users",
            "email",
            "the address a person registered with, which is the person rather than a fact about them: "
            + "it reaches them directly, it is the same string at every other service they use, and it "
            + "is the key anybody holding a log would join on. It is exported because the export goes "
            + "to them; a log record goes to whoever operates, ships and reads the logs, and none of "
            + "that work needs to know who a request belongs to beyond the account's internal id"),
        NeverLoggedColumn.Identifying(
            "credentials",
            "subject",
            "the provider's permanent identifier for the person, issued once and never reissued, so it "
            + "outlives a changed address and a deleted account here. A log record carrying it ties "
            + "that record to every other service the same provider account signs into, and nothing "
            + "about operating this service needs to know which provider account a request came from"),
        NeverLoggedColumn.Identifying(
            "passkey_public_keys",
            "webauthn_credential_id",
            "the durable identifier a passkey presents on every assertion — one this server did not "
            + "issue and cannot rotate, which names one person wherever it is seen. Listing it is what "
            + "makes 'a record refers to a user by internal identifier only' checkable: users.id is "
            + "that identifier, and this is a durable value a passkey sign-in holds in hand that "
            + "could otherwise pass for one"),
    ];

    /// <summary>
    /// Every column no log record may carry: the inventory's narrative columns, then
    /// <see cref="Entries" />.
    /// </summary>
    /// <remarks>
    /// Computed once from <see cref="DataInventory.Of" /> and never written out, so the narrative half
    /// can only agree with the inventory — which in turn can only agree with the model's
    /// <c>NarrativeField</c> properties.
    /// </remarks>
    public static IReadOnlyList<NeverLoggedColumn> All { get; } =
    [
        .. DataInventory.Of(ColumnClassification.Narrative)
            .Select(entry => NeverLoggedColumn.Narrative(entry, NarrativeReason)),
        .. Entries,
    ];
}
