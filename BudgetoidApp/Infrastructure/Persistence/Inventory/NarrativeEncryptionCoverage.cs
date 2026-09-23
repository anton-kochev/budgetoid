using Domain.Security;

namespace Infrastructure.Persistence.Inventory;

/// <summary>
/// The four ways a column the inventory calls <see cref="ColumnClassification.Narrative" /> can fail
/// to be stored as ciphertext.
/// </summary>
/// <remarks>
/// <para>
/// Four members and not one boolean, because the four have four different remedies and a gate that
/// reported only "this column is not encrypted" would send a reader looking at the wrong file. A
/// provider type that is not <c>byte[]</c> is a <b>substituted</b> value converter; a store type that
/// is not <c>bytea</c> is a <c>HasColumnType</c> call that disagrees with it; a missing check is a
/// deleted <c>HasCheckConstraint</c>.
/// </para>
/// <para>
/// <b>Substituted, not lost, and the difference is that only one of the two can reach this gate.</b>
/// Measured over nine configuration variants: <i>removing</i> a narrative property's converter refuses
/// the model build outright with an <see cref="InvalidOperationException" />, taking the whole unit
/// tier down with an EF message that names no column — so a lost converter is the one cause that can
/// never produce <see cref="ProviderTypeIsNotBytes" />. What reaches here is a converter pointed at
/// another type, say <see cref="string" />, which builds cleanly.
/// </para>
/// <para>
/// <b>One substitution can report one defect or two, and the second line may name a call nobody
/// wrote.</b> EF <i>derives</i> the store type from the provider type when <c>HasColumnType</c> is
/// absent. All eight narrative columns state it today, so
/// <see cref="ProviderTypeIsNotBytes" /> and <see cref="StoreTypeIsNotOpaque" /> are independent and a
/// substitution reports the first alone. A ninth column omitting that call recouples them: the same
/// single edit then reports both, and a reader chasing the store-type line goes looking for a
/// <c>HasColumnType</c> that does not exist in the file.
/// </para>
/// <para>
/// <b>The two type members are a floor and the two check members are the ceiling, and reading the
/// pair the other way round is the mistake available here.</b> Measured over this model: <b>22</b> of
/// the 110 mapped columns already have an effective provider type of <c>byte[]</c>, and <b>14</b> of
/// those are not narrative — the four <c>name_key</c> blind indexes, <c>session_tokens.token_hash</c>,
/// <c>recovery_code_hashes.verifier_hash</c>, both of <c>wrapped_account_keys</c>' payload columns,
/// <c>key_rotations.staged_manifest</c>, <c>key_rotation_seals.encapsulated_account_keys</c>,
/// <c>passkey_public_keys</c>' two columns, <c>webauthn_challenges.challenge</c> and
/// <c>factor_manifests.manifest</c>. One of the fourteen,
/// <c>passkey_public_keys.public_key_cose</c>, is <i>public</i> key material the server holds in the
/// clear rather than anything sealed or hashed; <c>factor_manifests.manifest</c> and
/// <c>key_rotations.staged_manifest</c>, the next generation of it, <i>are</i> sealed — AEAD envelopes
/// under a content key — yet carry no version check, because the stored rule is a length band and
/// nothing else. None of them is told apart from a narrative column by this pair of members: a
/// version byte and an envelope are facts about the bytes, not about the type or the store type. So
/// raw bytes
/// carrying no envelope at all satisfy <see cref="ProviderTypeIsNotBytes" /> and
/// <see cref="StoreTypeIsNotOpaque" /> both, and a column misclassified as narrative would pass two of
/// the four checks on shape alone. What distinguishes a sealed column from a hashed one is
/// <see cref="VersionCheckMissing" /> and <see cref="LengthCheckMissing" />; the type pair only rules
/// out a column the server can read directly.
/// </para>
/// <para>
/// <b>There is no member for "the model-side type is wrong", and the reason is circularity rather than
/// oversight.</b> The narrative set is <i>derived</i> from that type:
/// <c>DataInventoryCoverageTests.Narrative_IsExactlyTheColumnsTypedForASealedValue</c> defines it as
/// the columns whose <see cref="MappedColumn.ClrType" /> is
/// <see cref="Domain.Security.NarrativeField" />. A defect kind checking the same thing would ask
/// whether the columns selected for being typed that way are typed that way, and could never fire.
/// That is why <see cref="StoredColumn.ClrType" /> is carried and never asserted on — argued at the
/// member itself.
/// </para>
/// <para>
/// <b>There is also no member for "the ciphertext does not open".</b> Nothing on this side can ask
/// that question — every key-encryption key is derived in a browser from a recovery factor this server
/// never sees — which is the limit <see cref="CiphertextEnvelope" /> states about itself. A member here
/// implying otherwise would be a slot nothing could ever fill, read by the next person as a check that
/// exists.
/// </para>
/// </remarks>
public enum EncryptionDefectKind
{
    /// <summary>
    /// The provider is handed something other than <c>byte[]</c>, so the column stores a value this
    /// server can read.
    /// </summary>
    ProviderTypeIsNotBytes,

    /// <summary>
    /// The column is declared as something other than <c>bytea</c>, so the database is free to
    /// interpret, collate or fold what it holds.
    /// </summary>
    StoreTypeIsNotOpaque,

    /// <summary>
    /// No <c>CHECK</c> on the table pins the envelope's leading version byte for this column.
    /// </summary>
    VersionCheckMissing,

    /// <summary>
    /// No <c>CHECK</c> on the table bounds this column between the envelope's floor and its field
    /// class's cap.
    /// </summary>
    LengthCheckMissing,
}

/// <summary>
/// One column, one thing wrong with how it is stored, and both sides of the comparison that found it.
/// </summary>
/// <remarks>
/// <para>
/// A column can carry several of these at once and does not collapse into one. The reachable case is a
/// <b>substituted</b> value converter on a column that states no <c>HasColumnType</c>: EF derives the
/// store type from the provider type, so one edit reports both
/// <see cref="EncryptionDefectKind.ProviderTypeIsNotBytes" /> and
/// <see cref="EncryptionDefectKind.StoreTypeIsNotOpaque" />, and reporting only the first would let the
/// second come back later under a green gate. A <i>removed</i> converter is not among the causes —
/// it refuses the model build, as <see cref="EncryptionDefectKind" /> sets out.
/// </para>
/// <para>
/// <paramref name="Expected" /> and <paramref name="Found" /> are carried rather than composed into a
/// sentence for the reason <see cref="DataInventoryCoverage.Compare" /> gives about returning data: a
/// build gate wants an exception and a test wants an assertion, and the wording of either is not this
/// type's to choose. They are strings and not types or predicates because the two sides of a defect
/// are not always the same kind of thing — a provider type against a provider type, but a predicate
/// against a list of constraint names, since what a missing constraint leaves behind is an absence
/// with no shape of its own.
/// </para>
/// </remarks>
/// <param name="Qualified">The column as <c>table.column</c>, rendered the way the inventory names one.</param>
/// <param name="Kind">Which of the four things is wrong.</param>
/// <param name="Expected">What this column would carry if it were stored as ciphertext.</param>
/// <param name="Found">What it carries instead, or what the table offered in place of the missing check.</param>
public sealed record EncryptionDefect(
    string Qualified,
    EncryptionDefectKind Kind,
    string Expected,
    string Found);

/// <summary>
/// The two ways a narrative inventory and a schema can disagree about encryption, reported together
/// because a caller has to see both to know the gate examined anything.
/// </summary>
/// <remarks>
/// <paramref name="Unmapped" /> is not a duplicate of <see cref="InventoryCoverage.Stale" />, though a
/// full inventory run would redden both. This comparison's schema side is whatever the caller passed,
/// and an entry naming no column in it is a column this gate <b>did not examine</b> — so dropping it
/// silently is how a green answer comes to mean "found nothing to look at". Reported here so that the
/// gate's own blind spot is in the gate's own output, rather than depending on somebody having run the
/// coverage comparison first.
/// </remarks>
/// <param name="Defects">Every column-and-defect pair, in the order the schema declares the columns.</param>
/// <param name="Unmapped">
/// Narrative entries naming a column the schema side does not hold, as <c>table.column</c>.
/// </param>
public sealed record EncryptionCoverage(
    IReadOnlyList<EncryptionDefect> Defects,
    IReadOnlyList<string> Unmapped);

/// <summary>
/// Checks that every column the inventory classifies as narrative is stored as opaque, version-pinned,
/// length-bounded ciphertext — FR-057's gate, over the inventory NFR-022 makes the single source.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both sides are parameters, and neither is <see cref="DataInventory.Entries" />.</b> The argument
/// is <see cref="DataInventoryCoverage.Compare" />'s, unchanged: a classifier reaching for the shipped
/// inventory could only be trusted, never tested, because the shipped inventory agrees with the
/// shipped schema and an implementation ignoring both its arguments answers "nothing wrong" to every
/// question anybody could put to it. It builds no model either, for the same reason at the other end.
/// </para>
/// <para>
/// <b>It reads <see cref="ColumnClassificationEntry.Classification" />, where its neighbour refuses
/// to, and the difference is the subject rather than an inconsistency.</b> Coverage asks whether every
/// column was decided about, so which way it was decided is a different axis and folding it in would
/// make one report hold two unrelated problems. Here the classification <i>is</i> the subject: FR-057
/// is a rule about narrative columns and about no others. Filtering inside rather than trusting the
/// caller to pre-filter is what stops a caller who passed the whole inventory from demanding ciphertext
/// of <c>transactions.amount</c> — a failure that would be loud, but loud in a way that teaches the
/// next reader to widen the gate.
/// </para>
/// <para>
/// <b>A column's cap is not checked at all — not its value, not its direction, not its agreement with
/// the other columns of its class.</b> Which of the two <see cref="NarrativeFieldLimits" /> numbers a
/// column earns is a judgement about field class — a label somebody scans against free text somebody
/// writes — and the model states nothing a derivation could read it off, so a length check is accepted
/// when it names <i>either</i>. Calling that "a swap is invisible" understates it in two directions,
/// both measured. Widening a name column from
/// <see cref="NarrativeFieldLimits.NameBytes" /> to <see cref="NarrativeFieldLimits.DescriptionBytes" />
/// is a silent <i>loosening</i>: the column quietly accepts two and a half times what its class allows.
/// Narrowing a description the other way is a silent <i>refusal</i>, which is the worse of the two — a
/// note that stored yesterday answers <c>23514</c> today, and nothing here says a word. And the two
/// caps are checked per column with no cross-column opinion, so <c>accounts.name</c> at 2560 beside
/// <c>payees.name</c> at 1024 is green: <see cref="NarrativeFieldLimits" /> holds <b>two</b> constants
/// over field classes precisely so that eight columns cannot disagree about one rule, and this gate
/// does not restore that property. A ninth narrative column arrives free to pick either number.
/// </para>
/// <para>
/// <b>What holds a cap depends on whether the column already exists, and the two answers are
/// opposite.</b> For an <b>existing</b> column, changing the cap moves the model, so the migration
/// drift guard <c>BudgetoidDbContextConstructionTests.Migrations_MatchTheModel</c> reddens on the
/// configuration edit itself — measured, and container-free — and
/// <c>SchemaConstraintSnapshotTests.Schema_PinsEveryCheckConstraint</c> reddens again once a migration
/// carrying it has run, since it holds all eight narrative length constraints as literals with their
/// numbers in them. That is the same window and the same guard the constraint-name discussion below
/// names; this gate is the one thing in the sequence with no opinion.
/// </para>
/// <para>
/// <b>For a <i>new</i> narrative column, review alone decides the number, in every tier.</b> The
/// snapshot asserts an equivalent set over the whole constraint list, so a ninth column contributes a
/// <i>new</i> element: the test reports an unexpected item, the author pastes in the rendered
/// constraint carrying whatever cap they chose, and nothing anywhere judges the number. The drift
/// guard sees a model that agrees with its migration. This gate accepts either
/// <see cref="NarrativeFieldLimits" /> value by construction. So the ninth column's cap is a review
/// decision with no mechanical second opinion at any stage — which is worth knowing before trusting
/// the paragraph above to cover the case it does not.
/// </para>
/// <para>
/// <b>One vacuous input is caught here and the other is the caller's, and the asymmetry is worth
/// knowing which way round.</b> An empty <i>schema</i> side reddens: every narrative entry lands in
/// <see cref="EncryptionCoverage.Unmapped" /> — measured, all eight — which is what that member is for.
/// An empty <i>inventory</i> is the case nothing here can see, because no pure comparison can tell a
/// list somebody forgot to fill from a schema that genuinely classifies nothing as narrative. That
/// assertion belongs with the caller that knows which of the two it meant, the split the
/// container-backed reconciliation next door already makes about itself.
/// </para>
/// <para>
/// <b>A narrative column named twice by the inventory collapses here in silence</b>, because the
/// narrative side is reduced to a set before anything is compared. That is not this comparison's claim
/// to make and it is not unheld: <c>DataInventoryCoverageTests.Inventory_NamesEachColumnAtMostOnce</c>
/// groups the whole inventory by qualified name and reports any column entered twice, which is the
/// claim — pointed at rather than restated, the way <see cref="DataInventoryCoverage.Compare" /> points
/// next door for the same claim about its own two sides.
/// </para>
/// <para>
/// <b>A column appearing twice on the <i>schema</i> side is judged on its first appearance and the
/// rest are skipped, so the verdict depends on the order.</b> Measured: a good entry followed by a
/// broken one reports no defect, the same pair reversed reports four. Unreachable through
/// <see cref="MappedSchema.StoredColumnsOf" />, which emits each column once — and the completeness
/// pin holds that walk <i>equal to</i> <see cref="MappedSchema.ColumnsOf" /> rather than free of
/// duplicates, so a duplicate common to both walks would survive it. Recorded because a caller
/// assembling a list by hand, or a future walk over two entity types sharing a table, meets it with
/// nothing to warn them.
/// </para>
/// <para>
/// <b>Every limit above names a tier and a stage, and that is a convention to keep rather than a habit
/// of one author.</b> A gate is read for what it does <i>not</i> reach, so "held" without "this far" is
/// the specific way prose in a file like this goes wrong: it reads as a modest technical note, which is
/// exactly what carries it past a reviewer. When adding a limit here, say which suite observes it and
/// where in the edit-to-migration sequence it looks — and when recording a measurement, name the tier
/// that was run rather than the suite that was not.
/// </para>
/// </remarks>
public static class NarrativeEncryptionCoverage
{
    /// <summary>
    /// The store type a sealed column is declared as: bytes PostgreSQL will not interpret, collate or
    /// fold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written here rather than imported from a configuration, because a gate that read the value it
    /// checks out of the thing it checks would agree with any change made to it — including with its
    /// deletion.
    /// </para>
    /// <para>
    /// <b>This member is worth having only because the model builder accepts the wrong answer.</b>
    /// Removing <c>HasColumnType("bytea")</c> changes nothing — Npgsql maps a <c>byte[]</c> provider
    /// type to <c>bytea</c> by default — so an assertion about the <i>call</i> would be decoration;
    /// but <c>HasColumnType("text")</c> over that same provider type is accepted and does change the
    /// answer, which is what makes an assertion about the resulting <i>store type</i> real. Both
    /// measured on a synthetic context by the story that asked for this member; not re-run here, and
    /// not restated in detail, because a quotation of somebody else's measurement goes stale the first
    /// time its source is edited.
    /// </para>
    /// <para>
    /// Why <c>bytea</c> rather than <c>text</c> is a decision the schema already paid for and already
    /// argues at the column: see <c>AccountConfiguration</c>, where the removal of the
    /// <c>case_insensitive</c> collation is written up as a forced consequence of the type rather than
    /// a choice. The point here is only that a narrative column silently declared <c>text</c> would
    /// still store the same envelope and still read it back, differing only in what the database
    /// believes it may do with the bytes.
    /// </para>
    /// </remarks>
    public const string OpaqueStoreType = "bytea";

    /// <summary>
    /// Reports every narrative column the schema does not store as ciphertext, and every narrative
    /// entry the schema does not map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure, and it returns data rather than throwing or logging — the split
    /// <see cref="DataInventoryCoverage.Compare" /> and
    /// <see cref="Provisioning.RowLevelSecurityCoverage" /> both draw, for the same reason: the shape a
    /// build gate wants and the shape a test wants are different, and neither belongs in the code doing
    /// the comparing.
    /// </para>
    /// <para>
    /// <b>Identifiers are compared ordinally and the store type is not, which is a distinction rather
    /// than an inconsistency.</b> Every qualified name — matching the inventory, reporting a defect,
    /// filling <see cref="EncryptionCoverage.Unmapped" /> — is ordinal, because <c>pg_class</c> keeps a
    /// name exactly as EF spells it and a loose comparison would let an entry claim a column it does
    /// not name. The store type is compared with <see cref="StringComparison.OrdinalIgnoreCase" />, so
    /// <c>BYTEA</c> passes — measured — because a PostgreSQL type name is not case-sensitive and a
    /// configuration spelling it in capitals has declared the same column. The prose was corrected to
    /// the code here rather than the reverse: tightening the type comparison would redden a
    /// configuration that is right.
    /// </para>
    /// </remarks>
    /// <param name="stored">
    /// The schema as the store sees it, ordinarily from <see cref="MappedSchema.StoredColumnsOf" />.
    /// </param>
    /// <param name="inventory">
    /// The written-down classifications. Entries that are not
    /// <see cref="ColumnClassification.Narrative" /> are ignored, so the whole inventory is the
    /// ordinary argument.
    /// </param>
    /// <returns>The disagreement, empty in both directions when every narrative column is sealed.</returns>
    public static EncryptionCoverage Compare(
        IReadOnlyList<StoredColumn> stored,
        IReadOnlyList<ColumnClassificationEntry> inventory)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(inventory);

        HashSet<string> narrative = new(
            inventory
                .Where(entry => entry.Classification == ColumnClassification.Narrative)
                .Select(entry => entry.Qualified),
            StringComparer.Ordinal);

        List<EncryptionDefect> defects = [];
        HashSet<string> examined = new(StringComparer.Ordinal);

        foreach (StoredColumn column in stored)
        {
            if (!narrative.Contains(column.Qualified) || !examined.Add(column.Qualified))
            {
                continue;
            }

            defects.AddRange(DefectsOf(column));
        }

        List<string> unmapped =
        [
            .. narrative.Where(qualified => !examined.Contains(qualified)).Order(StringComparer.Ordinal),
        ];

        return new EncryptionCoverage(defects, unmapped);
    }

    /// <summary>
    /// Everything wrong with one narrative column, in the order a reader would check it: what the
    /// provider holds, what the column is declared as, then what the database refuses.
    /// </summary>
    /// <remarks>
    /// All four are evaluated; none short-circuits the rest, because one edit can produce more than one
    /// line — a substituted converter on a column stating no <c>HasColumnType</c> reports both type
    /// defects, and a configuration rewritten by hand can lose a constraint at the same time. A report
    /// naming only the first defect is a report that hides the work still left after somebody fixes it.
    /// </remarks>
    private static IEnumerable<EncryptionDefect> DefectsOf(StoredColumn column)
    {
        if (column.ProviderClrType != typeof(byte[]))
        {
            yield return new EncryptionDefect(
                column.Qualified,
                EncryptionDefectKind.ProviderTypeIsNotBytes,
                typeof(byte[]).FullName!,
                column.ProviderClrType.FullName ?? column.ProviderClrType.Name);
        }

        if (!string.Equals(column.StoreType, OpaqueStoreType, StringComparison.OrdinalIgnoreCase))
        {
            yield return new EncryptionDefect(
                column.Qualified,
                EncryptionDefectKind.StoreTypeIsNotOpaque,
                OpaqueStoreType,
                column.StoreType ?? "(the model states no store type)");
        }

        string version = VersionCheckFor(column.Column);

        if (!DeclaresPredicate(column, version))
        {
            yield return new EncryptionDefect(
                column.Qualified,
                EncryptionDefectKind.VersionCheckMissing,
                version,
                ConstraintNamesOf(column));
        }

        string[] lengths =
        [
            LengthCheckFor(column.Column, NarrativeFieldLimits.NameBytes),
            LengthCheckFor(column.Column, NarrativeFieldLimits.DescriptionBytes),
        ];

        if (!lengths.Any(candidate => DeclaresPredicate(column, candidate)))
        {
            yield return new EncryptionDefect(
                column.Qualified,
                EncryptionDefectKind.LengthCheckMissing,
                string.Join(" or ", lengths),
                ConstraintNamesOf(column));
        }
    }

    /// <summary>
    /// Whether any check on the column's table is <paramref name="predicate" /> once both are
    /// canonicalised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The association rule is the shape of the predicate, and the two obvious alternatives are
    /// both broken here.</b> Four tables carry a <c>name_key</c> beside their narrative <c>name</c>.
    /// Matching by SQL substring hands <c>length(name_key) = 32</c> to the column <c>name</c>, because
    /// the digest's predicate mentions <c>name</c>. Matching by constraint-name prefix does the same,
    /// because <c>CK_accounts_name_key_length</c> starts with <c>CK_accounts_name</c>. Under either,
    /// deleting the real <c>CK_accounts_name_length</c> leaves a constraint "about name" still standing
    /// and the gate still green — which is the exact failure FR-057 exists to make impossible. A whole
    /// predicate built from <see cref="CiphertextEnvelope.MinimumLength" /> and a
    /// <see cref="NarrativeFieldLimits" /> cap matches neither the digest's equality nor its width, so
    /// the pair is told apart by what the constraint <i>says</i> rather than by what it is called.
    /// </para>
    /// <para>
    /// <b>The <c>substring(… from 1 for 1)</c> spelling is pinned here too, and the word is
    /// <i>too</i>.</b> <c>SchemaConstraintSnapshotTests.Schema_PinsEveryCheckConstraint</c> already
    /// carries that predicate as a literal in its expected set, so the spelling was never unheld. What
    /// this adds is a tier and a stage: it answers off the model, with no container, on the
    /// configuration edit — where the snapshot, reading the applied catalog, answers only once a
    /// migration carrying the change has run. The two stages are the subject of the paragraph below and
    /// are not restated here. Why the spelling is not interchangeable with <c>get_byte</c>, and why the
    /// difference is nonetheless unreachable through any <c>INSERT</c> this schema admits, is argued and
    /// measured at <c>AccountConfiguration</c>'s version check — read it there rather than here.
    /// Nothing in this file re-ran it; this member's contribution is the whole of what it claims: a
    /// version check rewritten as <c>get_byte(name, 0) = 1</c> is a different string and reddens.
    /// </para>
    /// <para>
    /// <b>It matches predicates and never reads a constraint name.</b> Renaming
    /// <c>CK_accounts_name_length</c> to anything at all is invisible here, and so is deleting it and
    /// adding an unrelated constraint carrying the same predicate — both measured green. As a check on
    /// <i>encryption</i> that is honest, because PostgreSQL enforces a <c>CHECK</c> whatever it is
    /// called. What would not be honest is naming a single neighbour as the thing that holds the name:
    /// it takes two, and they cover different stages of one edit.
    /// </para>
    /// <para>
    /// A rename living in a <b>configuration</b> alone is caught by the migration drift guard,
    /// <c>BudgetoidDbContextConstructionTests.Migrations_MatchTheModel</c>, which diffs the migrations
    /// snapshot against the design-time model and refuses a model that has moved without one. It opens
    /// no connection — but it lives in <c>tests/IntegrationTests</c>, and that placement is the thing
    /// to know: skip the container tier and the only configuration-stage guard over constraint names
    /// <i>and</i> over caps does not run at all, leaving both to review with nothing saying so. A
    /// rename that has
    /// reached an <b>applied</b> schema is caught by <c>SchemaConstraintSnapshotTests</c>, which reads
    /// <c>pg_constraint</c> on a live database and pins each constraint's name beside its rendered
    /// definition — and, because it reads the catalog rather than the model, it can only see a rename
    /// once a migration carrying it exists and has run. Neither covers the other's stage, so "the
    /// snapshot catches a rename the moment somebody makes one" is the wrong summary and the one worth
    /// heading off here.
    /// </para>
    /// <para>
    /// <b>The sharp end</b> is that the alphabetical ordering which decides <i>which</i> of a column's
    /// checks fires first is a property of the <b>name</b>. So the single edit that would remove the
    /// shielding the paragraph above points at is exactly the edit this member cannot see, and it is
    /// those two guards in sequence — not this one — that stand under it.
    /// </para>
    /// <para>
    /// <b>Whitespace is collapsed and case is not folded, and the asymmetry is deliberate.</b> The
    /// length predicate is built in the configurations by concatenating two interpolated fragments
    /// across a line break, so re-wrapping that expression changes spacing and nothing else — a
    /// meaning-free edit a gate should survive. Keyword case is not meaning-free in the same way: no
    /// configuration in this schema spells one in upper case, so a predicate arriving as
    /// <c>LENGTH(name) BETWEEN …</c> is somebody rewriting the constraint, and that deserves a second
    /// pair of eyes. The trade is a loud false failure a human re-approves against a silent pass, and
    /// for a gate whose whole job is to notice a deleted constraint, loud is the right side of it.
    /// </para>
    /// </remarks>
    private static bool DeclaresPredicate(StoredColumn column, string predicate)
    {
        string wanted = Canonical(predicate);

        return column.TableCheckConstraints.Any(
            check => string.Equals(Canonical(check.Sql), wanted, StringComparison.Ordinal));
    }

    /// <summary>
    /// The length band this gate expects over <paramref name="column" />, rendered from the two
    /// constants that own the numbers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A restatement of what the configuration writes, and that is what a gate is.</b> The numbers
    /// come from <c>Domain</c> so only the predicate's shape is written twice; importing the
    /// string from the configuration instead would give this check nothing to compare against — it
    /// would agree with whatever the configuration was changed to, including with its deletion.
    /// </para>
    /// <para>
    /// A band, inclusive both ends, because AES-GCM ciphertext is exactly the length of its plaintext:
    /// a name is as long as whatever somebody typed, and <see cref="CiphertextEnvelope.MinimumLength" />
    /// is a floor over an empty plaintext rather than a width. An expected predicate written as a
    /// ceiling alone would accept a column that admits a three-byte value no version of this system can
    /// interpret.
    /// </para>
    /// </remarks>
    /// <param name="column">The column name as the model maps it.</param>
    /// <param name="cap">The field class's cap in envelope bytes.</param>
    /// <returns>The predicate as the configuration would spell it.</returns>
    private static string LengthCheckFor(string column, int cap) =>
        FormattableString.Invariant(
            $"length({column}) between {CiphertextEnvelope.MinimumLength} and {cap}");

    /// <summary>
    /// The version pin this gate expects over <paramref name="column" />, rendered from the constant
    /// that owns the version.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>substring</c> and two hex digits, matching the one spelling the configurations use, rendered
    /// from <see cref="CiphertextEnvelope.Version" /> rather than typed.
    /// </para>
    /// <para>
    /// <b>STOP HERE BEFORE DEFINING A VERSION 2. This renders exactly one accepted version, and on the
    /// day a second exists this gate demands the one migration that destroys data.</b> Measured against
    /// the shipped gate: <c>= '\x01'::bytea</c> passes, and
    /// <c>in ('\x01'::bytea, '\x02'::bytea)</c>, <c>&lt;= '\x02'::bytea</c> and
    /// <c>= '\x02'::bytea</c> under <see cref="CiphertextEnvelope.Version" /> of 1 are each reported
    /// <see cref="EncryptionDefectKind.VersionCheckMissing" />. So bumping the constant re-renders this
    /// expectation and all eight configurations together, the gate goes green, and the schema now
    /// <b>refuses every row written under version 1</b> — while the one constraint that is actually
    /// correct, the one accepting both versions, is the constraint this gate reddens. It pushes toward
    /// the only wrong migration, and the cheap reaction under a deadline is to widen the matcher, which
    /// is the single edit every argument in this file exists to prevent.
    /// </para>
    /// <para>
    /// <b>The remedy is to change the shape before widening anything:</b> make the accepted versions a
    /// set — the domain naming which versions may be stored, this method rendering a predicate over all
    /// of them — so that adding a version relaxes the schema and the gate in one direction together. A
    /// matcher loosened to make a version-2 constraint pass, without that, buys a green suite by
    /// retiring the check.
    /// </para>
    /// <para>
    /// <b>One thing partly saves this, and it is an accident worth not tidying away.</b> The synthetic
    /// controls over this member hold <c>'\x01'</c> as a <i>literal</i>, so bumping
    /// <see cref="CiphertextEnvelope.Version" /> reddens them and somebody is made to look. That works
    /// only because those expectations were not themselves rendered from the domain constant; folding
    /// them into rendering — which reads like removing a duplicated literal — would make them agree
    /// with any bump and the tripwire would go silent. [The consequence of folding is reasoned from
    /// string equality, not run.]
    /// </para>
    /// </remarks>
    /// <param name="column">The column name as the model maps it.</param>
    /// <returns>The predicate as the configuration would spell it.</returns>
    private static string VersionCheckFor(string column) =>
        FormattableString.Invariant(
            $"substring({column} from 1 for 1) = '\\x{CiphertextEnvelope.Version:x2}'::bytea");

    /// <summary>
    /// The names of every check on the column's table, which is what a missing constraint leaves in
    /// its place.
    /// </summary>
    /// <remarks>
    /// The table's whole set and not a filtered one: a filter would have to guess which constraints
    /// were meant for this column, which is the association problem this class solves by matching
    /// predicates instead. Reading the list is how somebody sees that
    /// <c>CK_accounts_name_key_length</c> is present and <c>CK_accounts_name_length</c> is not.
    /// </remarks>
    private static string ConstraintNamesOf(StoredColumn column) =>
        column.TableCheckConstraints.Count == 0
            ? $"(no check constraint on {column.Table})"
            : string.Join(", ", column.TableCheckConstraints.Select(check => check.Name));

    /// <summary>
    /// One predicate with every run of whitespace reduced to a single space and the ends trimmed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Splitting on whitespace rather than replacing it, so that a tab, a newline and a run of spaces
    /// all land on the same canonical form. Nothing else is touched — not case, not the parentheses,
    /// not the hex literal — because everything else in a predicate is meaning.
    /// </para>
    /// <para>
    /// <b>It splits with no regard for quoting, and that is a latent trap rather than a present bug.</b>
    /// A predicate carrying a string literal with a space inside it would have that space folded, so two
    /// predicates differing only in <c>'Credit  Card'</c> against <c>'Credit Card'</c> would compare
    /// equal. Unreachable today: the only literal either narrative predicate contains is
    /// <c>'\x01'::bytea</c>, which has no interior whitespace. The day this helper is pointed at a
    /// predicate over a text column is the day it bites, and it is written down here so that is
    /// discovered by reading rather than by a green gate.
    /// </para>
    /// </remarks>
    private static string Canonical(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
