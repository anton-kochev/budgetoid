using Domain.Accounts;
using Domain.Budgets;
using Domain.Currencies;
using Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.Persistence.Configurations;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    // Pinned to the name EF's convention already produces, so the schema does not move: AccountRepository
    // matches it against PostgresException.ConstraintName to decide whether a 23505 is the collision it
    // models. Declaring it here rather than as a literal in the repository keeps the two from drifting —
    // a name only the schema knows about stops the match and costs that 400 outright.
    //
    // THE VALUE MOVED AND THE IDENTIFIER DID NOT, on purpose. The index is over name_key now, so the
    // convention's own name for it ends in _name_key and the schema follows the convention rather than
    // carrying a hand-pinned exception to it. The C# name still says NameIndexName because that is what
    // the index is for — finding the row a name is already taken by — and because the repository's two
    // `catch ... when` clauses read as sentences about a duplicate name, not about a digest.
    public const string NameIndexName = "IX_accounts_budget_id_name_key";

    // The same constant PayeeConfiguration declares, for the same reason and matched by the same shape of
    // `catch ... when`: the id in this key arrives MINTED BY THE CLIENT, so a 23505 under this name says a
    // caller's identifier is already spoken for — a retried POST after a network timeout sends a
    // byte-identical body and collides here and nowhere else.
    //
    // The ordering measurement that makes this the name a duplicate id actually arrives under is written
    // out once, on PayeeConfiguration.PrimaryKeyName, and holds identically here: this table has the same
    // three constraints in the same creation order, so a row violating both the key and NameIndexName is
    // reported under "PK_accounts", and AK_accounts_id_budget_id is unreachable as a reported name.
    public const string PrimaryKeyName = "PK_accounts";

    // Pinned for the reason BudgetConfiguration pins its pair and WrappedAccountKeysConfiguration its
    // five: a constraint name is what PostgreSQL reports and what a repository would have to match a
    // PostgresException against, so it has to outlive a property rename. Spelled the way those files
    // spell theirs — the column, then what is being bounded — so the three tables' narrative checks read
    // as one family.
    public const string NameLengthCheckName = "CK_accounts_name_length";

    public const string NameVersionCheckName = "CK_accounts_name_version";

    public const string NameKeyLengthCheckName = "CK_accounts_name_key_length";

    // The comparer BudgetConfiguration declares over the same value type, for the same reason. Change
    // tracking compares a property against the snapshot it took at load; NarrativeField is a class with
    // no Equals of its own, so the default comparison is reference equality — wrong in both directions. A
    // field rebuilt from identical bytes would read as an edit, and an envelope rewritten inside the
    // instance's own buffer would not. The snapshot copies rather than aliases, because a value sharing
    // the tracked instance's buffer is not a record of the old value at all.
    //
    // The equality arm copes with a null and the other two do not, and that is the signature speaking
    // rather than this column: ValueComparer<T> declares equality as Func<T?, T?, bool> and the hash and
    // snapshot arms as Func<T, …>. Unlike budgets.name, this column is NOT NULL and the property is
    // non-nullable, so the null branch below is unreachable in practice; it is written because the
    // delegate type asks for it, and an arm narrower than its own signature is a nullability warning
    // rather than a guarantee.
    //
    // NONE OF THE THREE ARMS IS HELD BY A TEST, AND THIS TABLE IS WORSE OFF THAN CATEGORY GROUPS, WHERE
    // ONLY THE SNAPSHOT ARM IS UNHELD. CategoryGroupChangeTrackingTests composes a context over a
    // statement-recording interceptor and asserts which columns an UPDATE names; it is scoped to
    // category_groups and accounts have no equivalent. A wrong arm here is quiet rather than loud: EF
    // restates a column with the bytes the row already holds, so the value reads back exactly as
    // expected, and name and name_key both sit inside this table's GRANT UPDATE, so nothing answers
    // 42501 either. The symptom is a statement that should not have been sent, nothing in this
    // repository reads one for this table, and no case anywhere reads a SaveChangesAsync count. The same
    // is true arm for arm of the blind index comparer below.
    private static readonly ValueComparer<NarrativeField> EnvelopeContentComparer = new(
        (left, right) => HasSameEnvelope(left, right),
        field => ComputeEnvelopeHashCode(field),
        field => CopyEnvelope(field));

    // The comparer WrappedAccountKeysConfiguration declares over its two envelope columns, restated here
    // for name_key. For a ReadOnlyMemory<byte> the default comparison is the struct's own equality —
    // pointer, offset and length — which reads a digest rebuilt from identical bytes as an edit and
    // misses one rewritten in place inside the same buffer. On this column the second is the one that
    // bites: an index the tracker does not notice changing is a row whose uniqueness value stops
    // describing its own name.
    private static readonly ValueComparer<ReadOnlyMemory<byte>> BlindIndexContentComparer = new(
        (left, right) => HasSameBytes(left, right),
        memory => ComputeHashCode(memory),
        memory => Copy(memory));

    // FromStore on the way in — the unchecked door, which is why Domain grants InternalsVisibleTo to this
    // assembly and why that grant is argued in Domain.csproj — and Envelope.ToArray() on the way out. The
    // read side deliberately does not re-validate: see NarrativeField.FromStore for why a validating read
    // turns a cap change into silent data loss.
    //
    // Both type arguments are non-nullable, unlike budgets.name, so neither arm has to say anything about
    // a null it will never be handed. An account has a name or it is not an account.
    private static readonly ValueConverter<NarrativeField, byte[]> EnvelopeConverter = new(
        name => name.Envelope.ToArray(),
        bytes => NarrativeField.FromStore(bytes));

    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts", table =>
        {
            // A CHECK rather than a native PostgreSQL enum type: HasConversion<string>() already
            // stores the member name, so the check costs nothing extra, while a PG enum turns adding
            // a member into an ALTER TYPE dance. The honest price is that a new AccountType member
            // now needs a migration as well as a code change. That is acceptable because the enum is
            // fixed at compile time anyway, but the next person to add one deserves to learn it here
            // rather than from a failing deploy.
            table.HasCheckConstraint("CK_accounts_type", "type in ('Checking', 'Savings', 'Cash', 'CreditCard')");

            // Magnitude only, deliberately half of the domain rule. The column scale does not
            // enforce the decimal-places half: PostgreSQL rounds an over-precise value to the column
            // scale instead of rejecting it, storing 0.005 as 0.01 without raising anything.
            // Enforcement at the database means rejects, not coerces, so decimal places stay
            // domain-owned and no constraint here pretends to cover them.
            table.HasCheckConstraint("CK_accounts_opening_balance", "abs(opening_balance) <= 1000000000");

            // The floor and the ceiling in one constraint, rendered from the two constants that own them
            // rather than from literals: CiphertextEnvelope.MinimumLength is the shortest the framing can
            // be — a version, a nonce and a tag over an empty plaintext — and NarrativeFieldLimits.
            // NameBytes is the cap this column's field class carries. A hand-typed 29 or 1024 here would
            // be a second home for a rule the Domain already owns, and the copy that drifted would still
            // store, still read back and still open, differing only in what it accepts from a client
            // nobody exercised that day.
            //
            // A band and not a width, unlike wrapped_account_keys and unlike name_key below: AES-GCM
            // ciphertext is exactly the length of its plaintext, so a name is as long as whatever
            // somebody typed. Both bounds are inclusive, because both name a length that is legal.
            //
            // The floor is also what refuses an empty name at the only level that can still refuse one.
            // It is a floor on ENVELOPE bytes and says nothing about the text underneath — an envelope
            // over an empty string satisfies it exactly — so it is not the blank-name rule the entity
            // gave up, and must not be described as having restored it.
            table.HasCheckConstraint(
                NameLengthCheckName,
                $"length(name) between {CiphertextEnvelope.MinimumLength} "
                + $"and {NarrativeFieldLimits.NameBytes}");

            // substring rather than get_byte, and deliberately NOT the idiom
            // WrappedAccountKeysConfiguration uses. get_byte reads better — the leading byte is a number
            // and comparing it as one keeps the constraint reading the way the domain does — but it
            // RAISES on a zero-length bytea instead of answering false. Measured on PostgreSQL 17.10,
            // `get_byte(''::bytea, 0)` fails with SQLSTATE 2202E, "index 0 out of valid range, 0..-1".
            // That is not a constraint violation at all: no constraint name, no failing row, and nothing
            // a `catch (PostgresException) when (… SqlState is 23514)` will ever see.
            //
            // THE LENGTH CHECKS NEXT DOOR SAVE IT ONLY BY ACCIDENT, AND READING THAT ACCIDENT AS A
            // GUARANTEE IS THE TRAP. Which of a column's CHECKs runs first is decided by the CONSTRAINT
            // NAME and not by the order they are declared in — measured: a table declaring the version
            // check first still reported the length violation, while renaming the version check so it
            // sorts ahead produced 2202E from an identical pair of predicates. This table sorts
            // CK_accounts_name_key_length, then CK_accounts_name_length, then CK_accounts_name_version,
            // so a zero-length name meets a length band BEFORE the version predicate is evaluated at all
            // and comes back 23514. The wrong spelling on this column is shielded by two neighbours whose
            // names happen to sort first — held by nothing but the word "length" sorting before
            // "version", which is not a decision anybody took and not a property anybody should have to
            // preserve. Measured on postgres:17.10 over the six-constraint narrative shape
            // category_groups declares, with BOTH version checks spelled get_byte: no probe produced
            // 2202E, and every refusal came back 23514 under a length constraint. The shielding here is
            // read off the same alphabet rather than probed on this table. Folding two checks into one
            // AND-joined constraint only moves the same coin flip inside the expression, since PostgreSQL
            // does not promise it evaluates AND left to right either.
            //
            // So substring is still the right spelling, for the PROPERTY rather than for the symptom: it
            // makes the predicate TOTAL over every length this column can hold, including zero, so the
            // check is false rather than fatal and the violation is 23514 under EVERY ordering — on
            // INSERT and on UPDATE alike, and whatever constraint some later slice adds beside it. What
            // it buys is not the SQLSTATE, which the length band already earns; it is that the ordering
            // stops mattering. Nothing here is held by a test: through the schema as declared the wrong
            // spelling is unreachable, so measuring it needs a container probe over a table carrying the
            // version check alone, and this rule is held by review.
            //
            // The version is bounded here and not left to the client because the successor does not
            // exist — a row carrying version 2 is a client claiming a contract this deployment has never
            // implemented, and storing it would file bytes no version of this system can interpret,
            // discovered on the day somebody needs the name back. Rendered from the constant two hex
            // digits wide, for the reason the length check is rendered from its own: a typed '\x01' would
            // be a second home for a version the Domain already owns.
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
            // stable, never collides, keys perfectly and matches nothing for the life of the account. The
            // width is the whole of the defence here, which is why it is exact — and why the same
            // equality is stated in IndexedName.Of as well: this one refuses a row arriving by any other
            // path, that one refuses a call.
            //
            // No version arm, and there is nothing to write one from: a blind index is a keyed digest,
            // not an envelope — no version byte, no nonce, no tag, nothing to open. IndexedName says so
            // out loud for the reader who expects the pair to be two envelopes.
            table.HasCheckConstraint(
                NameKeyLengthCheckName,
                $"length(name_key) = {IndexedName.BlindIndexLength}");
        });

        builder.HasKey(account => account.Id).HasName(PrimaryKeyName);
        builder.HasAlternateKey(account => new { account.Id, account.BudgetId });

        builder.Property(account => account.Id).HasColumnName("id");
        builder.Property(account => account.BudgetId).HasColumnName("budget_id").IsRequired();

        // bytea, and the collation had to go: case_insensitive is a text collation and bytea is not a
        // collatable type, so this is a forced consequence of the column's type rather than a decision
        // taken here. What it was doing — making "Groceries" collide with "groceries" on the index below
        // — did not disappear with it, it MOVED: the index is over name_key now, and case folding is part
        // of the normalisation the client applies before it computes the HMAC. This server cannot check
        // that it happened, and no constraint here can be written to.
        //
        // NarrativeField is not a type the provider knows, so it is converted to the array bytea maps to;
        // both halves are declared on the fields above. The comparer is not optional decoration — see the
        // one it names for what change tracking does without one.
        builder.Property(account => account.Name)
            .HasConversion(EnvelopeConverter, EnvelopeContentComparer)
            .HasColumnName("name")
            .HasColumnType("bytea")
            .IsRequired();

        // The second half of the pair, and NOT NULL is the half of "a row cannot be half a name" that
        // this layer owns — IndexedName owns the other, which is that a CALL cannot be half. Neither
        // restates the other for error quality: this one refuses a row reaching the database by a path
        // no factory ran on, that one refuses a caller who meant to write both and wrote one.
        //
        // ReadOnlyMemory<byte> is not a type the provider knows either, converted the way
        // WrappedAccountKeysConfiguration converts its envelopes, and with a content comparer for the
        // same reason.
        builder.Property(account => account.NameKey)
            .HasConversion(
                memory => memory.ToArray(),
                bytes => new ReadOnlyMemory<byte>(bytes),
                BlindIndexContentComparer)
            .HasColumnName("name_key")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(account => account.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(account => account.OpeningBalance).HasColumnName("opening_balance").HasColumnType("numeric(14,4)").IsRequired();
        builder.Property(account => account.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
        builder.Property(account => account.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        // THE RULE IS THE SAME RULE — one name per budget — ENFORCED BY THE SAME MECHANISM OVER BYTES THE
        // DATABASE CANNOT READ. What changed is the column: uniqueness over `name` would enforce nothing
        // now, because every seal draws a fresh nonce and two rows holding one name hold different bytes.
        // The blind index is what survives that: it is deterministic under the account's index key, so
        // equality of names comes back as equality of digests, and this index refuses the second one.
        //
        // What did NOT change, and a reader comparing this against budgets.name will expect it to have:
        // the rule is still enforced, still by a unique index, still reported as a 23505 under the name
        // AccountRepository matches, and still scoped per budget. Budgets gave their name uniqueness up
        // because that column has no blind index and the requirement excludes one; this column has one,
        // which is the entire difference.
        //
        // Two things this index can no longer do for itself. It cannot fold case — that moved to the
        // client's normalisation, above — and it cannot be read by anybody with the database open: which
        // two accounts collided is a question only a browser holding the account's keys can answer.
        builder.HasIndex(account => new { account.BudgetId, account.NameKey }).IsUnique().HasDatabaseName(NameIndexName);
        builder.HasIndex(account => account.CurrencyCode);

        builder.HasOne<Budget>()
            .WithMany()
            .HasForeignKey(account => account.BudgetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Currency>()
            .WithMany()
            .HasForeignKey(account => account.CurrencyCode)
            .OnDelete(DeleteBehavior.Restrict);
    }

    // Static methods rather than inline lambdas for the reason WrappedAccountKeysConfiguration gives: the
    // comparer's arguments are expression trees, and a Span cannot appear in one — it is a ref struct, so
    // the span work has to sit behind a call.
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
    // HELD BY REVIEW, like every other arm on this table — the comparer naming this one says why that is
    // wider here than on category groups. What is additionally true of THIS arm is that no test could
    // hold it: aliasing instead of copying only becomes visible if an accepted envelope's bytes are
    // overwritten in place, which NarrativeField's shape does not allow.
    // CategoryGroupConfiguration.CopyEnvelope makes that argument in full over the same value type and
    // it transfers unchanged. The copy stays for the reason given there — the unobservability is a
    // property of that type as it stands today rather than a permanent one.
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
