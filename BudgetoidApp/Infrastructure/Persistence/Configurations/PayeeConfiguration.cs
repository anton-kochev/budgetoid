using Domain.Budgets;
using Domain.Payees;
using Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.Persistence.Configurations;

public sealed class PayeeConfiguration : IEntityTypeConfiguration<Payee>
{
    // Pinned to the name EF's convention already produces, so the schema does not move: PayeeRepository
    // matches it against PostgresException.ConstraintName in two places, and a name only the schema knows
    // about stops both matches.
    //
    // THE REASON FOR PINNING IT CHANGED WITH THE SLICE, and the old one must not be read back into this
    // line. It used to protect a swallow-and-re-read inside GetOrCreateAsync, where an unnamed 23505 sent
    // a stranger's collision down a recovery path that then reported no matching payee. That method is
    // gone — this server cannot look a payee up by name at all. What the name holds now is ATTRIBUTION for
    // the two answers that replaced it: AddAsync turns this index's 23505 into a 409 telling the client to
    // re-read its list, and UpdateAsync turns the same index's 23505 into a 400 keyed on Name. Both are
    // sentences about a duplicate payee name; matching on SQLSTATE alone would put either sentence on a
    // violation of some other rule flushed by the same SaveChanges.
    //
    // THE VALUE MOVED AND THE IDENTIFIER DID NOT, exactly as on accounts. The index is over name_key now,
    // so the convention's own name for it ends in _name_key. The C# name still says NameIndexName because
    // that is what the index is for — finding the row a name is already taken by.
    public const string NameIndexName = "IX_payees_budget_id_name_key";

    // Pinned for the reason AccountConfiguration pins its three: a constraint name is what PostgreSQL
    // reports and what a repository would have to match a PostgresException against, so it has to outlive
    // a property rename. Spelled the way that file spells its own — the column, then what is being
    // bounded — so the tables' narrative checks read as one family.
    public const string NameLengthCheckName = "CK_payees_name_length";

    public const string NameVersionCheckName = "CK_payees_name_version";

    public const string NameKeyLengthCheckName = "CK_payees_name_key_length";

    // The comparer AccountConfiguration declares over the same value type, for the same reason and with
    // the same two arms. Change tracking compares a property against the snapshot it took at load;
    // NarrativeField is a class with no Equals of its own, so the default comparison is reference
    // equality — wrong in both directions. A field rebuilt from identical bytes would read as an edit, and
    // an envelope rewritten inside the instance's own buffer would not. The snapshot copies rather than
    // aliases, because a value sharing the tracked instance's buffer is not a record of the old value.
    //
    // The equality arm copes with a null and the other two do not, and that is ValueComparer<T>'s
    // signature speaking rather than this column: equality is Func<T?, T?, bool>, the hash and snapshot
    // arms are Func<T, …>. This column is NOT NULL and Payee.Name is non-nullable, so the null branch is
    // unreachable in practice; it is written because the delegate type asks for it.
    private static readonly ValueComparer<NarrativeField> EnvelopeContentComparer = new(
        (left, right) => HasSameEnvelope(left, right),
        field => ComputeEnvelopeHashCode(field),
        field => CopyEnvelope(field));

    // For a ReadOnlyMemory<byte> the default comparison is the struct's own equality — pointer, offset and
    // length — which reads a digest rebuilt from identical bytes as an edit and misses one rewritten in
    // place inside the same buffer. On this column the second is the one that bites, and it bites harder
    // here than on accounts: an index the tracker does not notice changing leaves a payee whose uniqueness
    // value describes a name the row no longer holds, and the payee list IS this domain's deduplication
    // mechanism — see Payee.Rename for the two failures that follow.
    private static readonly ValueComparer<ReadOnlyMemory<byte>> BlindIndexContentComparer = new(
        (left, right) => HasSameBytes(left, right),
        memory => ComputeHashCode(memory),
        memory => Copy(memory));

    // FromStore on the way in — the unchecked door, which is why Domain grants InternalsVisibleTo to this
    // assembly and why that grant is argued in Domain.csproj — and Envelope.ToArray() on the way out. The
    // read side deliberately does not re-validate: see NarrativeField.FromStore for why a validating read
    // turns a cap change into silent data loss.
    private static readonly ValueConverter<NarrativeField, byte[]> EnvelopeConverter = new(
        name => name.Envelope.ToArray(),
        bytes => NarrativeField.FromStore(bytes));

    public void Configure(EntityTypeBuilder<Payee> builder)
    {
        // The three checks are declared inside the ToTable lambda rather than beside the properties so
        // they land on the entity type's table facet, which is what the regenerated baseline and
        // SchemaConstraintSnapshotTests both read.
        builder.ToTable("payees", table =>
        {
            // The floor and the ceiling in one constraint, rendered from the two constants that own them
            // rather than from literals: CiphertextEnvelope.MinimumLength is the shortest the framing can
            // be — a version, a nonce and a tag over an empty plaintext — and NarrativeFieldLimits.
            // NameBytes is the cap this column's field class carries. A hand-typed 29 or 1024 here would
            // be a second home for a rule the Domain already owns, and the copy that drifted would still
            // store, still read back and still open, differing only in what it accepts from a client
            // nobody exercised that day.
            //
            // A band and not a width: AES-GCM ciphertext is exactly the length of its plaintext, so a name
            // is as long as whatever somebody typed. Both bounds are inclusive, because both name a length
            // that is legal.
            //
            // The floor is also what refuses an empty name at the only level that can still refuse one —
            // Payee.ValidateOrThrow gave up every name rule it had. It is a floor on ENVELOPE bytes and
            // says nothing about the text underneath: an envelope over an empty string satisfies it
            // exactly. It is therefore not the blank-name rule the entity surrendered, and must not be
            // described as having restored it.
            table.HasCheckConstraint(
                NameLengthCheckName,
                $"length(name) between {CiphertextEnvelope.MinimumLength} "
                + $"and {NarrativeFieldLimits.NameBytes}");

            // substring rather than get_byte, and deliberately NOT the idiom
            // WrappedAccountKeysConfiguration uses. get_byte reads better — the leading byte is a number
            // and comparing it as one keeps the constraint reading the way the domain does — but it
            // RAISES on a zero-length bytea instead of answering false. Measured on PostgreSQL 17.10 for
            // this table's own constraint shape: an INSERT of ''::bytea against
            // `check (get_byte(name, 0) = 1)` fails with SQLSTATE 2202E, "index 0 out of valid range,
            // 0..-1", from byteaGetByte — no constraint name, no failing row, and nothing a
            // `catch (PostgresException) when (… SqlState is 23514)` will ever see. The identical INSERT
            // against `check (substring(name from 1 for 1) = '\x01'::bytea)` answered 23514 naming the
            // constraint, and so did the same value arriving by UPDATE.
            //
            // The length check next door does not save it, and believing it does is the trap. Which of a
            // column's CHECKs runs first is decided by the CONSTRAINT NAME and not by the order they are
            // declared in — the finding AccountConfiguration measured, and this table inherits its
            // consequence without having chosen it: the names sort CK_payees_name_key_length, then
            // CK_payees_name_length, then CK_payees_name_version, so a zero-length name happens to answer
            // 23514 from the length check (measured: it did). That is held by nothing but the word
            // "length" sorting before "version", which is not a decision anybody took — and the names are
            // forced by the column names anyway. Folding two checks into one AND-joined constraint only
            // moves the same coin flip inside the expression, since PostgreSQL does not promise it
            // evaluates AND left to right either.
            //
            // substring carries no such dependency. It answers a zero-length bytea for a zero-length
            // input, that is not the version byte, the check is false rather than fatal, and the violation
            // is 23514 under every ordering, on INSERT and on UPDATE alike.
            //
            // The version is bounded here and not left to the client because the successor does not
            // exist — a row carrying version 2 is a client claiming a contract this deployment has never
            // implemented, and storing it would file bytes no version of this system can interpret,
            // discovered on the day somebody needs the name back. Rendered from the constant two hex
            // digits wide, for the reason the length check is rendered from its own.
            table.HasCheckConstraint(
                NameVersionCheckName,
                $"substring(name from 1 for 1) = '\\x{CiphertextEnvelope.Version:x2}'::bytea");

            // AN EQUALITY, NOT A BAND, and that is the difference between this column and the one above
            // rather than a stricter mood. HMAC-SHA-256 emits exactly 32 bytes and nothing truncates in
            // between, so there is no band of legal sizes to allow for; a bound written as a ceiling would
            // admit a short digest silently.
            //
            // What it bounds is what MAY BE STORED, and it restates nothing this server computed. The
            // digest is the CLIENT's: it is taken under the account's index key, which lives in a browser,
            // so this side cannot recompute it, cannot check it against the name beside it, and cannot
            // tell a correct value from a fabricated one of the right width. A wrong 32 bytes is stable,
            // never collides, keys perfectly and matches nothing for the life of the account. On this
            // table that also means it matches no payee the client can find, while the name it should have
            // indexed stays creatable — the width is the whole of the defence, which is why it is exact,
            // and why the same equality is stated in IndexedName.Of as well: this one refuses a row
            // arriving by any other path, that one refuses a call.
            //
            // No version arm, and there is nothing to write one from: a blind index is a keyed digest, not
            // an envelope — no version byte, no nonce, no tag, nothing to open.
            table.HasCheckConstraint(
                NameKeyLengthCheckName,
                $"length(name_key) = {IndexedName.BlindIndexLength}");
        });

        builder.HasKey(payee => payee.Id);
        builder.HasAlternateKey(payee => new { payee.Id, payee.BudgetId });

        builder.Property(payee => payee.Id).HasColumnName("id");
        builder.Property(payee => payee.BudgetId).HasColumnName("budget_id").IsRequired();

        // bytea, and the collation had to go: case_insensitive is a text collation and bytea is not a
        // collatable type, so UseCollation("case_insensitive") leaving this line is a forced consequence
        // of the column's type rather than a decision taken here. What it was doing — making "Starbucks"
        // collide with "starbucks" on the index below — did not disappear with it, it MOVED, and on this
        // table that move is the largest single thing the slice gives up. Case folding is now part of the
        // normalisation the client applies before it computes the HMAC; this server cannot check that it
        // happened, cannot fold anything itself, and no constraint here can be written to.
        //
        // NarrativeField is not a type the provider knows, so it is converted to the array bytea maps to;
        // both halves are declared on the fields above. The comparer is not optional decoration — see the
        // one it names for what change tracking does without one.
        builder.Property(payee => payee.Name)
            .HasConversion(EnvelopeConverter, EnvelopeContentComparer)
            .HasColumnName("name")
            .HasColumnType("bytea")
            .IsRequired();

        // The second half of the pair, and NOT NULL is the half of "a row cannot be half a name" that this
        // layer owns — IndexedName owns the other, which is that a CALL cannot be half. Neither restates
        // the other for error quality: this one refuses a row reaching the database by a path no factory
        // ran on, that one refuses a caller who meant to write both and wrote one.
        //
        // ReadOnlyMemory<byte> is not a type the provider knows either, converted the way
        // AccountConfiguration converts the same column, and with a content comparer for the same reason.
        builder.Property(payee => payee.NameKey)
            .HasConversion(
                memory => memory.ToArray(),
                bytes => new ReadOnlyMemory<byte>(bytes),
                BlindIndexContentComparer)
            .HasColumnName("name_key")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(payee => payee.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        // THE RULE IS THE SAME RULE — one payee name per budget — ENFORCED BY THE SAME MECHANISM OVER
        // BYTES THE DATABASE CANNOT READ. What changed is the column: uniqueness over `name` would enforce
        // nothing now, because every seal draws a fresh nonce and two rows holding one name hold different
        // bytes. The blind index is what survives that: it is deterministic under the account's index key,
        // so equality of names comes back as equality of digests, and this index refuses the second one.
        //
        // What this index carries is more than it carries on accounts, and that is the one thing worth
        // saying twice. There, two accounts sharing a name is a confusion. Here, this index is the WHOLE of
        // counterparty deduplication: the server used to hold that property itself by folding case and
        // re-reading the table, and it can do neither, so "one counterparty is one row" now rests on this
        // index plus a client that computes the same digest for the same name every time.
        //
        // Two things it can no longer do for itself. It cannot fold case — that moved to the client's
        // normalisation, above — and it cannot be read by anybody with the database open: which two payees
        // collided is a question only a browser holding the account's keys can answer.
        builder.HasIndex(payee => new { payee.BudgetId, payee.NameKey }).IsUnique().HasDatabaseName(NameIndexName);

        builder.HasOne<Budget>()
            .WithMany()
            .HasForeignKey(payee => payee.BudgetId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    // Static methods rather than inline lambdas for the reason AccountConfiguration gives: the comparer's
    // arguments are expression trees, and a Span cannot appear in one — it is a ref struct, so the span
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
    // instance the tracker holds must not share a buffer with the one the entity holds, or the "old value"
    // changes whenever the new one does. FromStore copies on the way through, which is why there is
    // nothing to do here but call it.
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
