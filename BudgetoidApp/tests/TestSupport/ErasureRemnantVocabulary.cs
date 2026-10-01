using Infrastructure.Persistence.Provisioning;

namespace TestSupport;

/// <summary>
/// The way in which a column or relation name says a row would survive the erasure that was supposed
/// to remove it.
/// </summary>
/// <remarks>
/// The five are FR-026's own five nouns, in the requirement's order — "erasure leaves no soft-delete
/// flag, tombstone, deletion record, anonymized remnant, or archived copy". Derived from the clause
/// rather than invented, so a reviewer facing a red is handed the exact sentence they tripped instead
/// of a bucket name somebody chose. The category is not decoration on a yes/no answer either: the
/// remedy differs per noun. A soft-delete flag is a delete that never happened and is fixed by
/// deleting; a deletion record is a delete that happened and was written down anyway, and is fixed by
/// not writing it down.
/// </remarks>
public enum ErasureRemnantCategory
{
    /// <summary>A column marking a row as gone while the row stays where it was.</summary>
    SoftDeleteFlag,

    /// <summary>A stub kept behind so the system still remembers that the row existed.</summary>
    Tombstone,

    /// <summary>A record kept <i>about</i> a deletion, outliving the thing it describes.</summary>
    DeletionRecord,

    /// <summary>A row whose identifying columns were overwritten instead of removed.</summary>
    AnonymizedRemnant,

    /// <summary>A copy taken so the rows survive their own deletion.</summary>
    ArchivedCopy,
}

/// <summary>One remnant-naming pattern, the category it names, and the argument for refusing it.</summary>
/// <remarks>
/// <paramref name="Reason" /> is the member that keeps this list arguable rather than merely obeyed,
/// exactly as on <see cref="ProhibitedColumnRule" />. Every entry here will one day red a column
/// somebody had a reason to add, and the only thing that can be weighed against their reason at that
/// moment is the one written down when the pattern was added. A reason reading "forbidden" restates
/// the verdict and gives the next reader nothing to disagree with. It is a whole rule rather than a
/// category that <see cref="ErasureRemnantVocabulary.Classify" /> hands back, so the reason can reach
/// the sentence a red is read in instead of waiting in a file the reader has to know to open.
/// </remarks>
/// <param name="Pattern">
/// One token, or several joined by <c>_</c>, matched against an identifier's tokens as described on
/// <see cref="ErasureRemnantVocabulary" />. Not a substring and not a regular expression.
/// </param>
/// <param name="Category">Which of the five ways of surviving an erasure this pattern is an instance of.</param>
/// <param name="Reason">
/// What the pattern names and why an erasure that left it would not be an erasure — prose a reviewer
/// can argue with, in the sentence the refusal will be read next to.
/// </param>
public sealed record ErasureRemnantRule(
    string Pattern,
    ErasureRemnantCategory Category,
    string Reason);

/// <summary>
/// The single spelling of the column and relation names that would let a row outlive the erasure of
/// the person it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>sibling</b> of <see cref="ProhibitedColumnVocabulary" /> and deliberately not a fifth
/// category on it. That enum's own summary reads "the kind of thing a prohibited column name says the
/// schema is keeping <i>about a person</i>"; a <c>deleted_at</c> says nothing about a person, it says
/// the row is still there. The remedy differs with the question: an analytics identifier red means
/// "delete the column, the product measures nothing", while a <c>deleted_at</c> red means "you built
/// a soft delete, hard-delete instead". So does the owning document —
/// <c>docs/business-logic/erasure.md</c> rather than <c>users-and-ownership.md</c>. Folded together,
/// the two would produce a classifier that cannot tell a reviewer which of the two arguments they are
/// having.
/// </para>
/// <para>
/// The mechanism is <see cref="IdentifierTokens" />, the one the sibling reads too: an identifier is
/// split into tokens on <c>_</c> and on case boundaries, and a pattern matches when its own tokens
/// appear as a <i>contiguous run</i> of the identifier's tokens. Shared as a single spelling because
/// the two vocabularies are read over the same identifiers by neighbouring checks and a name that
/// tokenized one way for one and another way for the other would make the pair of verdicts
/// unexplainable. It is read against relation names as well as columns because this refusal
/// arrives at table grain more often than at column grain — <c>deletions</c> and <c>users_archive</c>
/// are tables, not columns.
/// </para>
/// <para>
/// Every pattern is a <b>bare noun or past participle, never a suffix rule</b>, and that is what keeps
/// the security records clear by construction rather than by exemption. <c>revoked_at_utc</c>,
/// <c>consumed_at</c>, <c>expires_at_utc</c> and <c>last_used_at</c> match nothing here because
/// <c>revoked</c>, <c>consumed</c>, <c>expires</c> and <c>used</c> are not on the list and will not
/// be; the same holds for an outbox's <c>event_type</c>, <c>event_id</c>, <c>event_data</c>,
/// <c>occurred_at</c> and <c>processed_at</c>. A rule shaped as "any past participle beside a
/// timestamp" would have caught all of them.
/// </para>
/// <para>
/// <b><c>archived</c>, <c>archived_at</c> and <c>is_archived</c> are permitted</b>, and only the noun
/// <c>archive</c> is refused. Hiding or closing an account somebody no longer uses is a plausible
/// live-row product state in a budgeting app, and a rule that cannot tell "this account is closed"
/// from "this user's data was copied aside" refuses the feature to catch the copy. <b>What that costs
/// is stated rather than waved away.</b> The account row is guarded on this axis by a narrower check
/// — <c>DataMinimizationSchemaTests.Schema_PinsTheColumnsOfTheUserRow</c> pins <c>users</c> to exactly
/// its three columns, so an <c>archived_at</c> cannot land there at all — and that pin, with the two
/// beside it, reaches three of the thirteen tables this schema maps. On the other ten, a
/// <c>transactions.is_archived</c> is refused by neither the pins nor this vocabulary. That is the
/// deliberate choice and not an oversight: the only rule that would reach those tables is one
/// refusing the word <c>archived</c> outright, and it would buy them by refusing a live-row product
/// state the product may well want.
/// </para>
/// <para>
/// <b><c>pseudonymized</c> and <c>pseudonymised</c> are permitted.</b> A pseudonymised remnant is
/// still a row that stayed, so it arrives beside one of the patterns below and is caught there;
/// meanwhile <c>pseudonym</c> could plausibly name a user-chosen alias this product may one day want.
/// <b><c>cancelled</c> and <c>cancelled_at</c> are permitted</b> for a simpler reason: they belong to
/// a schedule, not to an erasure.
/// </para>
/// <para>
/// <b><c>backup</c> and <c>history</c> are deliberately not refused</b>, and each stays out against a
/// collision that exists today rather than one somebody imagined. <c>__EFMigrationsHistory</c> is a
/// relation in <c>public</c> — <c>app-role-grants.sql</c> revokes and re-grants <c>SELECT</c> on it
/// under that exact spelling. EF owns the name and this repository cannot rename it, so a
/// <c>history</c> pattern would red a relation with no remedy available to the person meeting the
/// red, which is the shape of red that teaches a reader to stop believing the check. It is a ledger
/// of applied migrations and holds nobody's row. <c>backup_eligible</c> and <c>backup_state</c> are
/// the names a credential row carries the WebAuthn authenticator-data flags under; they say a passkey
/// syncs, which is a fact about a device rather than about a deleted row, and
/// <c>DataMinimizationSchemaTests.Schema_PinsTheColumnsOfThePasskeyPublicKeyRow</c> names
/// <c>backup_eligible</c> outright as a column that must not arrive on the row where it would matter.
/// Every other omission on this list is argued somewhere, so silence on these two would read as
/// oversight; a width control holds both, rather than this paragraph alone.
/// </para>
/// <para>
/// The first matching rule wins, and the ordering of <see cref="Rules" /> carries no meaning. That
/// claim is about the list <see cref="CompiledRules" /> executes rather than the one written below —
/// every pattern in the form it is written <i>and</i> in the plural — and it was checked there: none
/// of those token runs is a contiguous run inside any other, so no rule can shadow another and
/// reordering cannot change the verdict for any name carrying one of them. A rule's two forms answer
/// with the same rule object, so the pair cannot disagree with itself either. The only way to reach a
/// second rule is a name deliberately carrying two of the vocabularies at once —
/// <c>deleted_users_archive</c> — where the first answer wins and the second is a red the same
/// reviewer meets on their next pass. Each of the patterns can also fire on a name no other one
/// catches, which is what stops a rule being a dead entry that only looks like coverage. A future
/// pattern that overlaps an existing one has to say in its reason which category it means to win,
/// because at that point the order stops being incidental.
/// </para>
/// <para>
/// <b>This is a test-only deny-list and it lives in <c>TestSupport</c></b>, which is where shared
/// test-only code with more than one reader lives — the project's own comment describes that pattern,
/// and both test projects already reference it. This list has two readers, one in each of them: a
/// unit test over the EF design-time model, and an integration test over the live catalog.
/// <c>TestSupport</c> is the one place both already reach, and one spelling in one place is what
/// stops the two from disagreeing. Two written-down copies of one rule have no adjudicator when they
/// disagree.
/// </para>
/// <para>
/// Nothing that ships reads it — not the API, and deliberately not the deploy-time verifier in
/// <c>Tools/DbProvision</c>, which would now mean a deploy tool reaching into test support. That
/// costs nothing. A column or a relation can only arrive through a migration, and CI runs this list
/// over the model on every pull request into <c>main</c>, so a deploy-time scan would catch nothing
/// the build has not already caught, at the cost of one more way for a deploy to fail. Row-level
/// security is verified there instead because it fails open and can drift from outside the
/// repository; a name cannot.
/// </para>
/// </remarks>
public static class ErasureRemnantVocabulary
{
    /// <summary>
    /// Every remnant-naming pattern, with the category it names and the argument for refusing it.
    /// </summary>
    /// <remarks>
    /// Written down rather than discovered, which is safe in this direction: the list decides what is
    /// <i>refused</i>, so a pattern nobody added leaves a name allowed — the same shape the schema
    /// already had — whereas an unlisted table in a coverage check leaves a table unpoliced while
    /// reporting green. The list only ever adds refusals, so it can be short without being dishonest.
    /// </remarks>
    public static IReadOnlyList<ErasureRemnantRule> Rules { get; } =
    [
        new(
            "deleted",
            ErasureRemnantCategory.SoftDeleteFlag,
            "names a row's own deletion in a product that hard-deletes everywhere, which makes it a "
            + "row that outlived the delete it records. One token rather than a phrase, because the "
            + "flag is spelled differently every time it arrives — deleted_at, is_deleted, "
            + "deleted_by, isDeleted — and the token is the only part all of them share"),
        new(
            "soft_delete",
            ErasureRemnantCategory.SoftDeleteFlag,
            "names the mechanism outright, and is listed because the matcher does not stem: "
            + "soft_delete_flag carries no 'deleted' token and would walk straight past the rule "
            + "above it. A phrase rather than the bare token 'delete', which is the word this "
            + "product uses for the behaviour it actually wants"),
        new(
            "trashed",
            ErasureRemnantCategory.SoftDeleteFlag,
            "names a trash somebody can pull a row back out of — a named rejected alternative in the "
            + "decision log, and this is the column it arrives as when it is built anyway. The row "
            + "has not gone anywhere; only the listing it appears in has changed"),
        new(
            "restored",
            ErasureRemnantCategory.SoftDeleteFlag,
            "records a row coming back, which is proof it never left. Whether an endpoint offers a "
            + "restore is an argument about the route table and is had there; this catches only the "
            + "trace building one leaves in the schema — which is the part a name scan can see, and "
            + "a restore nobody has exposed yet still needs a column to read"),
        new(
            "discarded",
            ErasureRemnantCategory.SoftDeleteFlag,
            "marks a row as discarded while it stays exactly where it was. 'Discard' is this "
            + "product's own word for an intentional hard delete, which reads like an argument for "
            + "permitting it and is not: this classifier is only ever handed catalog and model "
            + "names, and a hard delete leaves no column behind, so the behaviour the word describes "
            + "correctly can never appear as an identifier. Every discarded_at that reaches here is "
            + "therefore a soft delete wearing the product's own hard-delete word — the one spelling "
            + "a reviewer waves through"),
        new(
            "removed",
            ErasureRemnantCategory.SoftDeleteFlag,
            "is 'deleted' under a word that sounds like a step in a process rather than a "
            + "destruction. is_removed says about the row precisely what is_deleted says, and reads "
            + "more neutral saying it, which is what makes it the spelling that lands without "
            + "anybody stopping on it"),
        new(
            "trash",
            ErasureRemnantCategory.SoftDeleteFlag,
            "is the bare noun, which is the table form of the rule above it: 'trashed' catches the "
            + "column and walks straight past a relation simply named trash, and this vocabulary "
            + "meets the remnant at table grain more often than at column grain. A relation called "
            + "trash is a list of rows somebody can still reach in and pull back"),
        new(
            "tombstone",
            ErasureRemnantCategory.Tombstone,
            "is the word the documentation already uses for the thing an erasure must not leave: a "
            + "stub kept so the system still remembers a row existed. Stated here as a rule at last "
            + "rather than only as a reference, because a reference persuades and a rule refuses"),
        new(
            "erased",
            ErasureRemnantCategory.Tombstone,
            "marks a row as erased while keeping it, which is a contradiction the schema should not "
            + "be able to write down. The past participle only — the noun 'erasure' stays legal "
            + "deliberately, because it names the endpoint and would name a scheduled-erasure row"),
        new(
            "purged",
            ErasureRemnantCategory.Tombstone,
            "is the same marker under the operations word, listed separately because it arrives from "
            + "a retention job rather than from a designer. Nobody reviews it as a schema decision, "
            + "which is exactly the case a name scan is worth having for"),
        new(
            "deletion",
            ErasureRemnantCategory.DeletionRecord,
            "names a record kept about a deletion, as the singular form appears in a compound — "
            + "deletion_log, deletion_reason. What it describes is gone and the fact that a "
            + "particular person was here is not, which is the remnant the requirement means"),
        new(
            "erasure_log",
            ErasureRemnantCategory.DeletionRecord,
            "is the likeliest name a deletion record would take in this product, 'erasure' being the "
            + "word used for it everywhere else. A phrase rather than the bare token, which has to "
            + "stay legal: a rule refusing 'erasure' would refuse the endpoint's own name"),
        new(
            "anonymized",
            ErasureRemnantCategory.AnonymizedRemnant,
            "names a row whose identifying columns were overwritten rather than removed, which is "
            + "the row still sitting there — the requirement names the anonymized remnant "
            + "explicitly. Overwriting a name is a decision about a column's contents; the erasure "
            + "was a decision about the row"),
        new(
            "anonymised",
            ErasureRemnantCategory.AnonymizedRemnant,
            "is the other spelling, listed separately for the reason 'referrer' and 'referer' are on "
            + "the sibling list: neither is more likely than the other, and both arrive by habit "
            + "rather than by choice"),
        new(
            "redacted",
            ErasureRemnantCategory.AnonymizedRemnant,
            "is the same remnant under the word a compliance conversation reaches for. The word "
            + "sounds like a duty discharged, which is what makes it worth refusing by name: the row "
            + "is intact and the duty was to remove it"),
        new(
            "archive",
            ErasureRemnantCategory.ArchivedCopy,
            "names a place rows are copied to so that they survive their own deletion — "
            + "users_archive, transaction_archive. The noun only: the adjective 'archived' stays "
            + "legal, because closing an account somebody no longer uses is a live-row product state "
            + "and a rule that cannot tell it from a copy kept aside would refuse the feature"),
    ];

    /// <summary>
    /// The patterns above, pre-split into the tokens an identifier's tokens are matched against —
    /// each of them twice, in the form it is written and in the plural.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The executed list is longer than the written one, and this is where the two differ.</b>
    /// Every rule contributes two entries pointing back at the same rule: its pattern as written, and
    /// the same pattern with its last token pluralised. <see cref="IdentifierTokens.ContainsRun" />
    /// compares whole tokens ordinally and does not stem, so <c>archives</c> reaches no rule
    /// <c>archive</c> reaches — and the plural is the shape a remnant <i>relation</i> arrives in,
    /// every table this schema maps being named in the plural. The last token is the one pluralised
    /// because an English composite pluralises its head noun and the head noun sits last:
    /// <c>erasure_log</c> becomes <c>erasure_logs</c> and never <c>erasures_log</c>. Both entries
    /// carry the rule itself rather than a copy of its category, so a plural match hands back the
    /// same argument the singular does.
    /// </para>
    /// <para>
    /// The pluralisation itself lives in <see cref="IdentifierTokens" />, but as a <b>separate,
    /// opt-in member rather than a step inside the matching</b> those other readers share, whose
    /// remarks argue that reading a name is one question with one answer — a stemming step hidden in
    /// the matcher would change what those readers mean without anybody touching their lists. It is
    /// applied here, by this list asking for it, so a reader that never asks for a plural sees the
    /// tokens and the verdicts it saw before. Applied by expansion rather than written out as plural
    /// twins, because doubling the entries doubles the number of arguments a reviewer has to answer
    /// while adding none, and the fifteenth twin is the one nobody remembers.
    /// </para>
    /// <para>
    /// The expansion runs in the safe direction, the same one the written list runs in: it only ever
    /// adds refusals. A plural nobody wanted leaves a name refused that a reviewer can argue about
    /// against the singular's own reason, while a missing plural leaves a remnant table allowed by a
    /// list that reports covering it.
    /// </para>
    /// <para>
    /// <b>The plural is <see cref="IdentifierTokens.PluralOf" />'s</b>, the one place this codebase
    /// spells a pluralisation rule: <c>+es</c> after a sibilant — <c>s</c>, <c>x</c>, <c>z</c>,
    /// <c>ch</c> or <c>sh</c> — and <c>+s</c> otherwise. On this list the sibilant branch reaches
    /// exactly one pattern, the mass noun <c>trash</c>, whose expansion <c>trashes</c> is the
    /// spelling a relation holding trashed rows would carry. The <c>y</c> ending and the irregulars
    /// English inflects some other way are not handled, and nothing here needs them: no pattern on
    /// the list ends in <c>y</c> and none inflects irregularly. Where an ending is wrong the result
    /// is a non-word — <c>deleteds</c> — which matches nothing rather than something wrong. A
    /// pattern that genuinely needs an irregular plural should be written into <see cref="Rules" />
    /// as its own entry carrying its own argument, which is cheaper to read than an inflection
    /// engine sitting between the list and its verdict.
    /// </para>
    /// <para>
    /// At this size a plain array beats a frozen collection: the work is a short scan of short token
    /// runs, and a hash-based structure could not answer a contiguous-run question anyway.
    /// </para>
    /// </remarks>
    private static readonly (string[] Tokens, ErasureRemnantRule Rule)[] CompiledRules =
        Rules
            .SelectMany(rule => new[]
            {
                (Tokens: IdentifierTokens.Tokenize(rule.Pattern), Rule: rule),
                (Tokens: IdentifierTokens.Tokenize(IdentifierTokens.PluralOf(rule.Pattern)), Rule: rule),
            })
            .ToArray();

    /// <summary>
    /// Says which rule a column or relation name trips, or <see langword="null" /> when the name
    /// leaves nothing behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule rather than its category</b>, because the category alone is a bucket name and
    /// <see cref="ErasureRemnantRule.Reason" /> is the member that makes this list arguable. Handing
    /// the whole rule back lets a caller print the argument for the refusal in the same line as the
    /// offender it refused, which is what the 40-character floor the reasons are held to is for. A
    /// caller wanting only the bucket reads <see cref="ErasureRemnantRule.Category" /> off the answer.
    /// </para>
    /// <para>
    /// Null on a null or blank input rather than an exception, for the reason its sibling gives: this
    /// is a classifier reading names out of a catalog or a model, not a validator of its caller's
    /// arguments, and a scan that threw partway through would report <i>fewer</i> offenders than
    /// exist — the fail-open direction.
    /// </para>
    /// <para>
    /// <b>The parameter is nullable because its callers' inputs are.</b>
    /// <c>IEntityType.GetTableName()</c> and <c>IProperty.GetColumnName()</c> both answer
    /// <see langword="null" /> for an entity or a property the model maps to no table or column, so a
    /// non-null parameter would leave every model-reading caller either reaching for <c>!</c> —
    /// asserting something the model does not promise — or dropping the name before the classifier
    /// ever sees it. Either way the null guard below could not fire, which would make the fail-open
    /// argument for it a claim about unreachable code. Taking the null the callers actually hold is
    /// what leaves that argument true.
    /// </para>
    /// <para>
    /// Comparison is ordinal on invariantly lower-cased tokens. PostgreSQL folds unquoted identifiers
    /// to lower case, but a quoted one keeps its case and a caller may pass anything, so the folding
    /// happens here too. Culture-sensitive comparison is avoided outright: the Turkish dotless
    /// <c>i</c> alone would make <c>is_deleted</c> and <c>anonymized</c> match or miss depending on
    /// the machine the build ran on.
    /// </para>
    /// </remarks>
    /// <param name="identifier">
    /// A column or relation name, in any casing and from any source, or <see langword="null" /> when
    /// the model or catalog the caller read it from had none.
    /// </param>
    /// <returns>
    /// The rule the name trips — its pattern, the category it names and the argument for refusing it
    /// — or <see langword="null" /> when the name trips none.
    /// </returns>
    public static ErasureRemnantRule? Classify(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        string[] tokens = IdentifierTokens.Tokenize(identifier);

        foreach ((string[] patternTokens, ErasureRemnantRule rule) in CompiledRules)
        {
            if (IdentifierTokens.ContainsRun(tokens, patternTokens))
            {
                return rule;
            }
        }

        return null;
    }
}
