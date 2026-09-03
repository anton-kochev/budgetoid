using Domain.Budgets;
using Domain.CategoryGroups;
using Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.Persistence.Configurations;

public sealed class CategoryGroupConfiguration : IEntityTypeConfiguration<CategoryGroup>
{
    // Pinned to the name EF's convention already produces, so the schema does not move:
    // CategoryGroupRepository matches it against PostgresException.ConstraintName so a 23505 from an
    // unrelated tracked row cannot come back as a duplicate group name nobody else holds.
    //
    // THE VALUE MOVED AND THE IDENTIFIER DID NOT, exactly as on accounts and payees. The index is over
    // name_key now, so the convention's own name for it ends in _name_key. The C# name still says
    // NameIndexName because that is what the index is for - refusing a name this budget already holds.
    public const string NameIndexName = "IX_category_groups_budget_id_name_key";

    // Public, and applied by HasName below, for the reason AccountConfiguration and PayeeConfiguration
    // made their own primary keys' names public: the id in this key arrives MINTED BY THE CLIENT, so a
    // 23505 under this name is a caller's identifier already being spoken for, which is a thing the
    // caller can be told something useful about. CategoryGroupRepository.AddAsync matches it, and it is
    // the constraint a retried POST hits - the same request body sent twice after a network timeout
    // collides here and nowhere else.
    //
    // The ordering behind that arm was MEASURED on postgres:17.10 for payees and is not re-run here: a
    // row violating both the key and the name index is reported under the key, because PostgreSQL checks
    // a relation's indexes in OID (creation) order and the primary key is created with the table. That is
    // a different rule from the alphabetical one that orders a column's CHECK constraints, below.
    //
    // AK_category_groups_id_budget_id is therefore unreachable as a reported name and no code matches it:
    // every row that violates it duplicates an id, so it violates this key too, and this key is checked
    // first.
    public const string PrimaryKeyName = "PK_category_groups";

    // Pinned for the reason AccountConfiguration pins its three: a constraint name is what PostgreSQL
    // reports and what a repository would have to match a PostgresException against, so it has to outlive
    // a property rename. Spelled the way that file spells its own - the column, then what is being
    // bounded - so the tables' narrative checks read as one family.
    public const string NameLengthCheckName = "CK_category_groups_name_length";

    public const string NameVersionCheckName = "CK_category_groups_name_version";

    public const string NameKeyLengthCheckName = "CK_category_groups_name_key_length";

    // The description's pair, and this table is the first to declare one. Same spelling, same family, a
    // different cap - see the constraints themselves.
    public const string DescriptionLengthCheckName = "CK_category_groups_description_length";

    public const string DescriptionVersionCheckName = "CK_category_groups_description_version";

    public const string PositionCheckName = "CK_category_groups_position";

    // ONE COMPARER SERVES BOTH NARRATIVE COLUMNS AND THAT IS SAFE, WHILE ONE CONVERTER WOULD NOT BE - see
    // the two converters below. ValueComparer<T> declares equality as Func<T?, T?, bool> and the hash and
    // snapshot arms as Func<T, ...> whatever T is, so the null-tolerant equality arm here is required by
    // the delegate type rather than by either column, and the other two are never handed a null by EF.
    // BudgetConfiguration argues that split at length over the same value type.
    //
    // Change tracking compares a property against the snapshot it took at load; NarrativeField is a class
    // with no Equals of its own, so the default comparison is reference equality - wrong in both
    // directions. A field rebuilt from identical bytes would read as an edit, and an envelope rewritten
    // inside the instance's own buffer would not. The snapshot copies rather than aliases, because a
    // value sharing the tracked instance's buffer is not a record of the old value.
    private static readonly ValueComparer<NarrativeField> EnvelopeContentComparer = new(
        (left, right) => HasSameEnvelope(left, right),
        field => ComputeEnvelopeHashCode(field),
        field => CopyEnvelope(field));

    // For a ReadOnlyMemory<byte> the default comparison is the struct's own equality - pointer, offset and
    // length - which reads a digest rebuilt from identical bytes as an edit and misses one rewritten in
    // place inside the same buffer. The second is what bites: an index the tracker does not notice
    // changing leaves a group whose uniqueness value describes a name the row no longer holds.
    private static readonly ValueComparer<ReadOnlyMemory<byte>> BlindIndexContentComparer = new(
        (left, right) => HasSameBytes(left, right),
        memory => ComputeHashCode(memory),
        memory => Copy(memory));

    // FromStore on the way in - the unchecked door, which is why Domain grants InternalsVisibleTo to this
    // assembly and why that grant is argued in Domain.csproj - and Envelope.ToArray() on the way out. The
    // read side deliberately does not re-validate: see NarrativeField.FromStore for why a validating read
    // turns a cap change into silent data loss.
    //
    // This is PayeeConfiguration's converter over a NON-NULLABLE model type, because CategoryGroup.Name is
    // non-nullable and the column is NOT NULL.
    private static readonly ValueConverter<NarrativeField, byte[]> EnvelopeConverter = new(
        name => name.Envelope.ToArray(),
        bytes => NarrativeField.FromStore(bytes));

    // THE SECOND NARRATIVE CONVERTER, AND IT IS DELIBERATELY NOT THE ONE ABOVE. This file is the first
    // configuration in the product holding two narrative converters of different nullability, and a
    // reviewer will propose unifying them on the nullable one. Refuse it: a nullable converter on the
    // NOT NULL name column would move "this arm never runs" from a fact about the PROPERTY's type - which
    // the compiler holds - to a fact about the schema, which is a layer further from the code that would
    // break it.
    //
    // This is BudgetConfiguration's converter, copied in substance. The model type is the nullable
    // NarrativeField because the property is one and the builder's signature follows it, so the write arm
    // has to say something about a null it will never be handed: EF does not apply a converter to a null
    // value, and a group with no description reaches the column as NULL without either arm running. It
    // says so by throwing rather than with a null-forgiving operator or a quiet fallback - the guarantee
    // is stated where it is relied on, and the day it stops holding the symptom is a loud one instead of
    // an empty buffer filed as somebody's note.
    private static readonly ValueConverter<NarrativeField?, byte[]> NullableEnvelopeConverter = new(
        description => EnvelopeOf(description),
        bytes => NarrativeField.FromStore(bytes));

    public void Configure(EntityTypeBuilder<CategoryGroup> builder)
    {
        // The six checks are declared inside the ToTable lambda rather than beside the properties so they
        // land on the entity type's table facet, which is what the regenerated baseline and
        // SchemaConstraintSnapshotTests both read.
        //
        // WHICH OF THEM FIRES FIRST IS DECIDED BY THE CONSTRAINT NAME, ALPHABETICALLY, AND ON THIS TABLE
        // THAT ORDERING NOW CROSSES TWO COLUMNS. Measured on postgres:17.10 over exactly these six: the
        // sort is description_length, description_version, name_key_length, name_length, name_version,
        // position, so a row violating a NAME rule and a DESCRIPTION rule is reported under the
        // description, and a row violating a description rule and the position rule is reported under the
        // description too. Nothing in this file depends on that - every narrative predicate is spelled
        // with substring, so none of them can raise and every ordering yields 23514 naming SOME
        // constraint. What it does forbid is a test asserting a constraint NAME for a row carrying more
        // than one violation: a zero-length-name case must leave the description NULL, or it reports the
        // description's constraint.
        builder.ToTable("category_groups", table =>
        {
            table.HasCheckConstraint(PositionCheckName, "position >= 0");

            // The floor and the ceiling in one constraint, rendered from the two constants that own them
            // rather than from literals: CiphertextEnvelope.MinimumLength is the shortest the framing can
            // be - a version, a nonce and a tag over an empty plaintext - and
            // NarrativeFieldLimits.NameBytes is the cap this column's field class carries. A hand-typed
            // 29 or 1024 here would be a second home for a rule the Domain already owns, and the copy
            // that drifted would still store, still read back and still open, differing only in what it
            // accepts from a client nobody exercised that day.
            //
            // A band and not a width: AES-GCM ciphertext is exactly the length of its plaintext, so a
            // name is as long as whatever somebody typed. Both bounds are inclusive, because both name a
            // length that is legal.
            //
            // The floor is also what refuses an empty name at the only level that can still refuse one -
            // CategoryGroup.ValidateOrThrow gave up every name rule it had. It is a floor on ENVELOPE
            // bytes and says nothing about the text underneath: an envelope over an empty string
            // satisfies it exactly. It is therefore not the blank-name rule the entity surrendered, and
            // must not be described as having restored it.
            table.HasCheckConstraint(
                NameLengthCheckName,
                $"length(name) between {CiphertextEnvelope.MinimumLength} "
                + $"and {NarrativeFieldLimits.NameBytes}");

            // substring rather than get_byte, and deliberately NOT the idiom
            // WrappedAccountKeysConfiguration uses. get_byte reads better - the leading byte is a number
            // and comparing it as one keeps the constraint reading the way the domain does - but it
            // RAISES on a zero-length bytea instead of answering false. Measured on PostgreSQL 17.10:
            // get_byte(''::bytea, 0) fails with SQLSTATE 2202E, "index 0 out of valid range, 0..-1", from
            // byteaGetByte. That is not a constraint violation at all: no constraint name, no failing
            // row, and nothing a `catch (PostgresException) when (... SqlState is 23514)` will ever see.
            //
            // WHAT THIS SPELLING BUYS ON THIS TABLE IS NOT THE SQLSTATE, AND SAYING OTHERWISE WOULD BE
            // FALSE. Measured on postgres:17.10 over a table carrying exactly these six constraints with
            // BOTH version checks spelled get_byte: no probe produced 2202E: every refusal came back
            // 23514 under a LENGTH constraint - name_length, name_key_length or description_length. The
            // alphabet is why. name_key_length and name_length both sort ahead of name_version, so a
            // zero-length name meets the length band first and is refused there; the wrong spelling on
            // this column is shielded by two neighbours whose names happen to sort first, which is not a
            // decision anybody took and not a property anybody should have to preserve. The description's
            // pair below is shielded the same way by description_length, and ciphertext-envelope.md says
            // so for that column already.
            //
            // So substring is still the right spelling, for the property rather than for the symptom: it
            // makes the predicate TOTAL over every length this column can hold, including zero, so the
            // check is false rather than fatal and the violation is 23514 under EVERY ordering - on
            // INSERT and on UPDATE alike, and whatever constraint some later slice adds beside it. A
            // get_byte spelling would be correct today and wrong the day a name-column check sorts ahead
            // of name_length. Nothing here is held by a test: through the schema as declared the wrong
            // spelling is unreachable, so measuring it needs a container probe over a table carrying the
            // version check alone, and this rule is held by review.
            //
            // The version is bounded here and not left to the client because the successor does not
            // exist - a row carrying version 2 is a client claiming a contract this deployment has never
            // implemented, and storing it would file bytes no version of this system can interpret,
            // discovered on the day somebody needs the name back. Rendered from the constant two hex
            // digits wide, for the reason the length check is rendered from its own.
            table.HasCheckConstraint(
                NameVersionCheckName,
                $"substring(name from 1 for 1) = '\\x{CiphertextEnvelope.Version:x2}'::bytea");

            // AN EQUALITY, NOT A BAND, and that is the difference between this column and the one above
            // rather than a stricter mood. HMAC-SHA-256 emits exactly 32 bytes and nothing truncates in
            // between, so there is no band of legal sizes to allow for; a bound written as a ceiling
            // would admit a short digest silently.
            //
            // What it bounds is what MAY BE STORED, and it restates nothing this server computed. The
            // digest is the CLIENT's: it is taken under the account's index key, which lives in a
            // browser, so this side cannot recompute it, cannot check it against the name beside it, and
            // cannot tell a correct value from a fabricated one of the right width. A wrong 32 bytes is
            // stable, never collides, keys perfectly and stands for a name this row does not hold, for
            // the life of the account. The width is therefore the whole of the defence, which is why it
            // is exact, and why the same equality is stated in IndexedName.Of as well: this one refuses a
            // row arriving by any other path, that one refuses a call.
            //
            // No version arm, and there is nothing to write one from: a blind index is a keyed digest,
            // not an envelope - no version byte, no nonce, no tag, nothing to open.
            table.HasCheckConstraint(
                NameKeyLengthCheckName,
                $"length(name_key) = {IndexedName.BlindIndexLength}");

            // THE DESCRIPTION'S BAND, AND ITS CEILING IS A DIFFERENT CONSTANT FROM THE NAME'S ON PURPOSE.
            // NarrativeFieldLimits carries two caps over field CLASSES - names and descriptions - so a
            // reviewer reading NameBytes onto this line would refuse values this column is meant to
            // accept, and one reading DescriptionBytes onto the name would widen a column that was never
            // asked to be wide.
            //
            // NOTHING HERE SAYS "OR NULL", and that is not an omission. A CHECK is satisfied by NULL -
            // length(null) is null, and a null predicate is not a violation - so a group with no
            // description passes both description checks vacuously. Measured on postgres:17.10 over
            // exactly these constraints: a NULL description is accepted, a 29-byte one is accepted, a
            // 2560-byte one is accepted, 2561 is 23514 under this name, and clearing a description back
            // to NULL by UPDATE succeeds. Adding a "or description is null" arm would be noise that reads
            // like a rule.
            //
            // The floor is what makes the empty-note case representable rather than refused: 29 bytes IS
            // a note somebody wrote and then emptied, and NULL is a note nobody wrote. The schema
            // distinguishes them, which is the whole reason CategoryGroup.NormalizeDescription had to go.
            table.HasCheckConstraint(
                DescriptionLengthCheckName,
                $"length(description) between {CiphertextEnvelope.MinimumLength} "
                + $"and {NarrativeFieldLimits.DescriptionBytes}");

            // substring here too, AND THE REASON A READER WILL GET BACKWARDS. The instinct is "the column
            // is nullable, so get_byte's zero-length trap does not apply". It applies. Measured on
            // postgres:17.10: get_byte(NULL::bytea, 0) answers NULL and does NOT raise, so a
            // get_byte-spelled version check would be green on every ordinary row - every row holding a
            // NULL and every row holding a valid envelope - and would bite only on the PRESENT,
            // ZERO-LENGTH value, which is exactly what a client sending an empty bytea produces and the
            // one value this check exists for. get_byte(''::bytea, 0) raises 2202E from inside a CHECK on
            // a nullable column just as it does on a NOT NULL one.
            //
            // AND THERE IS NO CASE THAT CATCHES IT, which is the sharper half. description_length sorts
            // ahead of description_version, so the length band reaches a present, zero-length value first
            // and answers 23514 - the neighbour SHIELDS the wrong spelling on every value this schema can
            // be handed, exactly as name_length and name_key_length shield the name's. Measured on
            // postgres:17.10 with both version checks spelled get_byte: nothing produced 2202E. So the
            // wrong predicate here is not merely quiet, it is unreachable through the schema as declared,
            // and measuring it would need a container probe over a table carrying this check alone -
            // which no test in this repository is. ciphertext-envelope.md states it the same way.
            table.HasCheckConstraint(
                DescriptionVersionCheckName,
                $"substring(description from 1 for 1) = '\\x{CiphertextEnvelope.Version:x2}'::bytea");
        });

        builder.HasKey(categoryGroup => categoryGroup.Id).HasName(PrimaryKeyName);
        builder.HasAlternateKey(categoryGroup => new { categoryGroup.Id, categoryGroup.BudgetId });

        builder.Property(categoryGroup => categoryGroup.Id).HasColumnName("id");
        builder.Property(categoryGroup => categoryGroup.BudgetId).HasColumnName("budget_id").IsRequired();

        // bytea, and the collation had to go: case_insensitive is a text collation and bytea is not a
        // collatable type, so UseCollation("case_insensitive") leaving this line is a forced consequence
        // of the column's type rather than a decision taken here. What it was doing - making "Essentials"
        // collide with "essentials" on the index below - did not disappear, it MOVED: case folding is now
        // part of the normalisation the client applies before it computes the HMAC. This server cannot
        // check that it happened, cannot fold anything itself, and no constraint here can be written to.
        //
        // HasMaxLength leaves with it. A character count is not a thing this column has: what bounds it
        // now is the byte band in the CHECK above, written from the type that owns the cap.
        //
        // NarrativeField is not a type the provider knows, so it is converted to the array bytea maps to;
        // both halves are declared on the fields above. The comparer is not optional decoration - see the
        // one it names for what change tracking does without one.
        builder.Property(categoryGroup => categoryGroup.Name)
            .HasConversion(EnvelopeConverter, EnvelopeContentComparer)
            .HasColumnName("name")
            .HasColumnType("bytea")
            .IsRequired();

        // The second half of the pair, and NOT NULL is the half of "a row cannot be half a name" that
        // this layer owns - IndexedName owns the other, which is that a CALL cannot be half. Neither
        // restates the other for error quality: this one refuses a row reaching the database by a path no
        // factory ran on, that one refuses a caller who meant to write both and wrote one.
        //
        // ReadOnlyMemory<byte> is not a type the provider knows either, converted the way
        // PayeeConfiguration converts the same column, and with a content comparer for the same reason.
        builder.Property(categoryGroup => categoryGroup.NameKey)
            .HasConversion(
                memory => memory.ToArray(),
                bytes => new ReadOnlyMemory<byte>(bytes),
                BlindIndexContentComparer)
            .HasColumnName("name_key")
            .HasColumnType("bytea")
            .IsRequired();

        // The nullable converter and NOT the name's, for the reason declared at the two fields. No
        // IsRequired: a group with no description is a legal row and NULL is how it says so. HasMaxLength
        // leaves this column too - the byte band in the CHECK above is the only length anything on this
        // side can measure.
        //
        // The comparer IS shared with the name, which is safe where the converter is not: a
        // ValueComparer's arms are typed the same whatever the property's nullability, and the one arm
        // that can be handed a null already copes.
        builder.Property(categoryGroup => categoryGroup.Description)
            .HasConversion(NullableEnvelopeConverter, EnvelopeContentComparer)
            .HasColumnName("description")
            .HasColumnType("bytea");

        builder.Property(categoryGroup => categoryGroup.Position)
            .HasColumnName("position")
            .IsRequired();
        builder.Property(categoryGroup => categoryGroup.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // THE RULE IS THE SAME RULE - one group name per budget - ENFORCED BY THE SAME MECHANISM OVER
        // BYTES THE DATABASE CANNOT READ. What changed is the column: uniqueness over `name` would
        // enforce nothing now, because every seal draws a fresh nonce and two rows holding one name hold
        // different bytes. The blind index is what survives that: it is deterministic under the account's
        // index key, so equality of names comes back as equality of digests, and this index refuses the
        // second one.
        //
        // Two things it can no longer do for itself. It cannot fold case - that moved to the client's
        // normalisation, above - and it cannot be read by anybody with the database open: which two
        // groups collided is a question only a browser holding the account's keys can answer.
        builder.HasIndex(categoryGroup => new { categoryGroup.BudgetId, categoryGroup.NameKey })
            .IsUnique()
            .HasDatabaseName(NameIndexName);

        // Untouched by the sealing, and worth saying so. Position is an int the server can still read and
        // order by, so every read path that orders category groups keeps working exactly as it did.
        builder.HasIndex(categoryGroup => new { categoryGroup.BudgetId, categoryGroup.Position });

        builder.HasOne<Budget>()
            .WithMany()
            .HasForeignKey(categoryGroup => categoryGroup.BudgetId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    // The write arm of the nullable converter, behind a call because an expression tree cannot hold a
    // throw statement. See that converter for why the null branch is unreachable and why it is loud
    // anyway.
    private static byte[] EnvelopeOf(NarrativeField? description) =>
        description?.Envelope.ToArray()
        ?? throw new InvalidOperationException(
            "The narrative converter was applied to a null category group description. A group with no "
            + "description reaches the column as NULL and never through this arm.");

    // Static methods rather than inline lambdas for the reason AccountConfiguration gives: the comparer's
    // arguments are expression trees, and a Span cannot appear in one - it is a ref struct, so the span
    // work has to sit behind a call.
    private static bool HasSameEnvelope(NarrativeField? left, NarrativeField? right) =>
        left is null || right is null
            ? ReferenceEquals(left, right)
            : left.Envelope.Span.SequenceEqual(right.Envelope.Span);

    private static int ComputeEnvelopeHashCode(NarrativeField field)
    {
        HashCode hash = new();
        hash.AddBytes(field.Envelope.Span);

        return hash.ToHashCode();
    }

    // Rebuilt through the unchecked door rather than returned as-is, so the snapshot is a copy: the
    // instance the tracker holds must not share a buffer with the one the entity holds, or the "old
    // value" changes whenever the new one does. FromStore copies on the way through, which is why there
    // is nothing to do here but call it.
    //
    // THIS ARM IS HELD BY REVIEW AND BY NO TEST, and its three neighbours are not - drop either comparer
    // or falsify HasSameEnvelope and CategoryGroupChangeTrackingTests reddens on the columns an UPDATE
    // names. Measured: returning the instance here - so the snapshot ALIASES the tracked value rather
    // than copying it - leaves every case in that class green, because for the two to differ somebody
    // would have to overwrite the bytes of an already-accepted envelope IN PLACE, and nothing can:
    // NarrativeField's buffer is private, Envelope is a window onto the copy the factory made - Sealed
    // and FromStore both ToArray() - and every write path replaces the whole field, so reaching it needs
    // MemoryMarshal and measures a hazard no route leads to. The copy stays anyway: what makes the
    // difference unobservable is NarrativeField's CURRENT shape and not a permanent property, and the
    // day a member rewrites an envelope in place this arm is the only thing keeping the tracker's "old
    // value" from moving with the new one.
    private static NarrativeField CopyEnvelope(NarrativeField field) =>
        NarrativeField.FromStore(field.Envelope);

    private static bool HasSameBytes(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right) =>
        left.Span.SequenceEqual(right.Span);

    private static int ComputeHashCode(ReadOnlyMemory<byte> memory)
    {
        HashCode hash = new();
        hash.AddBytes(memory.Span);

        return hash.ToHashCode();
    }

    private static ReadOnlyMemory<byte> Copy(ReadOnlyMemory<byte> memory) => memory.ToArray();
}
