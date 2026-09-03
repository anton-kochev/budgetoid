using Domain.Budgets;
using Domain.Currencies;
using Domain.Security;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.Persistence.Configurations;

public sealed class BudgetConfiguration : IEntityTypeConfiguration<Budget>
{
    // Pinned to the name EF's convention already produces, so the schema does not move: this index is
    // the only 23505 BudgetRepository.TryAddAsync is allowed to report as a lost race, because that is
    // the only one whose winner left a budget behind for the loser to re-read.
    public const string UserNameIndexName = "IX_budgets_user_id_name";

    // Pinned for the reason WrappedAccountKeysConfiguration pins its own five: a constraint name is
    // what PostgreSQL reports and what a repository would have to match a PostgresException against, so
    // it has to outlive a property rename. Spelled the way that file spells its pair — the column, then
    // what is being bounded — so the two tables' narrative checks read as one family.
    public const string NameLengthCheckName = "CK_budgets_name_length";

    public const string NameVersionCheckName = "CK_budgets_name_version";

    // The comparer WrappedAccountKeysConfiguration declares, for the reason it declares one, restated
    // here over the value type rather than over raw bytes. Change tracking compares a property against
    // the snapshot it took at load; NarrativeField is a class with no Equals of its own, so the default
    // comparison is reference equality — wrong in both directions. A field rebuilt from identical bytes
    // would read as an edit, and an envelope rewritten inside the instance's own buffer would not. The
    // snapshot copies rather than aliases, because a value sharing the tracked instance's buffer is not
    // a record of the old value at all.
    //
    // The equality arm is null-tolerant and the other two are not, unlike WrappedAccountKeysConfiguration
    // where the question does not arise — this column is nullable, and the nameless budget is the common
    // row rather than an edge case. Which arms have to cope is not a guess: ValueComparer<T> declares
    // equality as Func<T?, T?, bool> and the hash and snapshot arms as Func<T, …>, so EF is stating that
    // it may compare a null against a value and will never ask for the hash or the snapshot of one. An
    // arm written wider than its own signature would be dead code that reads like a rule.
    //
    // NONE OF THE THREE ARMS IS HELD BY A TEST, AND BUDGETS SIT FURTHEST FROM ONE. The suite's single
    // change-tracking class reads the statements a save composes for category_groups; this table has
    // nothing of the kind, so the equality arm is as unheld as the snapshot arm below. What separates it
    // from accounts and payees is that a failure here could not be quiet: the role holds no UPDATE grant
    // on budgets of any shape — a budgets row is never updated at all, which app-role-grants.sql states
    // as rule B2 — so a restatement this comparer failed to suppress would be refused for want of
    // privilege, the fail-closed 42501 that file argues for, rather than committing like an edit nobody
    // asked for. Unreachable, though, and not covered: no path modifies a tracked budget, so this arm
    // runs on every save with one in scope and is never handed two different values. The comparer is
    // depth for a path the product does not have, and review is all that holds it.
    private static readonly ValueComparer<NarrativeField> EnvelopeContentComparer = new(
        (left, right) => HasSameBytes(left, right),
        field => ComputeHashCode(field),
        field => Copy(field));

    // FromStore on the way in — the unchecked door, which is why Domain grants InternalsVisibleTo to this
    // assembly and why that grant is argued in Domain.csproj — and Envelope.ToArray() on the way out. The
    // read side deliberately does not re-validate: see NarrativeField.FromStore for why a validating read
    // turns a cap change into silent data loss.
    //
    // The model type is the NULLABLE NarrativeField because the property is one and the builder's
    // signature follows it, so the write arm has to say something about a null it will never be handed:
    // EF does not apply a converter to a null value, and the nameless budget reaches the column as NULL
    // without either arm running. It says so by throwing rather than with a null-forgiving operator or a
    // quiet fallback — the guarantee is stated where it is relied on, and the day it stops holding the
    // symptom is a loud one instead of an empty buffer filed as somebody's budget name.
    private static readonly ValueConverter<NarrativeField?, byte[]> EnvelopeConverter = new(
        name => EnvelopeOf(name),
        bytes => NarrativeField.FromStore(bytes));

    public void Configure(EntityTypeBuilder<Budget> builder)
    {
        builder.ToTable("budgets", table =>
        {
            // The floor and the ceiling in one constraint, rendered from the two constants that own
            // them rather than from literals: CiphertextEnvelope.MinimumLength is the shortest the
            // framing can be — a version, a nonce and a tag over an empty plaintext — and
            // NarrativeFieldLimits.NameBytes is the cap this column's field class carries. A hand-typed
            // 29 or 1024 here would be a second home for a rule the Domain already owns, and the copy
            // that drifted would still store, still read back and still open, differing only in what it
            // accepts from a client nobody exercised that day.
            //
            // A band and not a width, unlike wrapped_account_keys: AES-GCM ciphertext is exactly the
            // length of its plaintext, so a name is as long as whatever somebody typed. Both bounds are
            // inclusive, because both name a length that is legal.
            //
            // Nothing here says "or null". A CHECK is satisfied by NULL — length(null) is null, and a
            // null predicate is not a violation — so the nameless budget passes both of these without
            // an arm written for it. Adding one would be noise that reads like a rule.
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
            // THE LENGTH CHECK NEXT DOOR SAVES IT ONLY BY ACCIDENT, AND READING THAT ACCIDENT AS A
            // GUARANTEE IS THE TRAP. Which of two CHECKs on one column runs first is decided by the
            // CONSTRAINT NAME and not by the order they are declared in: measured, a table declaring the
            // version check first still reported the length violation, while renaming the version check
            // so it sorts ahead of the length one produced 2202E from an identical pair of predicates.
            // Today the sort is CK_budgets_name_length before CK_budgets_name_version, so a zero-length
            // name meets the band BEFORE the version predicate is evaluated at all and comes back 23514.
            // That is held by nothing but the word "length" sorting before "version", which is not a
            // decision anybody took and not a property anybody should have to preserve. Measured on
            // postgres:17.10 over the six-constraint narrative shape category_groups declares, with BOTH
            // version checks spelled get_byte: no probe produced 2202E, and every refusal came back 23514
            // under a length constraint — so a get_byte spelling on this column would be unreachable
            // through the schema as declared rather than merely quiet. Folding the two into one
            // AND-joined constraint only moves the same coin flip inside the expression, since PostgreSQL
            // does not promise it evaluates AND left to right either.
            //
            // So substring is still the right spelling, for the PROPERTY rather than for the symptom: it
            // makes the predicate TOTAL over every length this column can hold, including zero, so the
            // check is false rather than fatal and the violation is 23514 under EVERY ordering — on
            // INSERT and on UPDATE alike, measured on all four, and whatever constraint some later slice
            // adds beside it. What it buys is not the SQLSTATE, which the length band already earns; it
            // is that the ordering stops mattering. NULL still satisfies it, also measured, so the
            // nameless budget is unaffected by the spelling. Nothing here is held by a test: measuring
            // the wrong spelling needs a container probe over a table carrying the version check alone,
            // and this rule is held by review.
            //
            // The version is bounded here and not left to the client because the successor does not
            // exist — a row carrying version 2 is a client claiming a contract this deployment has
            // never implemented, and storing it would file bytes no version of this system can
            // interpret, discovered on the day somebody needs the name back. Rendered from the constant
            // two hex digits wide, for the reason the length check is rendered from its own: a typed
            // '\x01' would be a second home for a version the Domain already owns.
            table.HasCheckConstraint(
                NameVersionCheckName,
                $"substring(name from 1 for 1) = '\\x{CiphertextEnvelope.Version:x2}'::bytea");
        });

        builder.HasKey(budget => budget.Id);

        builder.Property(budget => budget.Id).HasColumnName("id");
        builder.Property(budget => budget.UserId).HasColumnName("user_id").IsRequired();

        // bytea, and the collation had to go: case_insensitive is a text collation and bytea is not a
        // collatable type, so this is a forced consequence of the column's type rather than a decision
        // taken here. What it cost is stated at the index below, which is the only thing that was
        // reading it.
        //
        // NarrativeField is not a type the provider knows, so it is converted to the array bytea maps
        // to; both halves are declared on the fields above. The comparer is not optional decoration —
        // see the one it names for what change tracking does without one. No IsRequired: the nameless
        // budget is a legal row and NULL is how it says so.
        builder.Property(budget => budget.Name)
            .HasConversion(EnvelopeConverter, EnvelopeContentComparer)
            .HasColumnName("name")
            .HasColumnType("bytea");

        builder.Property(budget => budget.BaseCurrencyCode).HasColumnName("base_currency_code").HasMaxLength(3);
        builder.Property(budget => budget.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        // Deliberately no unique index or key over user_id alone: the schema is multi-budget-ready
        // from day one, and "exactly one budget per user" is a release-scope property (no code path
        // creates a second one), not a schema invariant. The leading column of the composite index
        // below also serves WHERE user_id = ?, so no separate index on user_id is needed.
        //
        // NULLS NOT DISTINCT (PG15+) is what states the invariant: at most one unnamed budget per
        // user, plus any number of named ones. Without it PostgreSQL treats each NULL as distinct, two
        // racing writers both insert (userId, NULL), and a user silently ends up owning two
        // budgets. Keying race safety on the absence of a name is stronger than the literal it
        // replaces: collision used to require both racers to write the same string, and a constant
        // two callers must agree on can drift; nothing about "no name" can.
        //
        // IT SURVIVES THE COLUMN BECOMING CIPHERTEXT, AND A REVIEWER WILL PROPOSE DELETING IT. The
        // proposal is right about the named half and it is not the half that is load-bearing. Two rows
        // holding the same name now hold different bytes — every seal draws a fresh nonce — so the
        // uniqueness no longer refuses a duplicate name, and the case_insensitive collation that made
        // it refuse "Groceries" against "groceries" is gone with the text type. What is unchanged is
        // the NULLS NOT DISTINCT half: "no name" is NULL before and after, so this index is still the
        // only thing standing between two racing provisioners and an account owning two default
        // budgets, and it is still the 23505 BudgetRepository.TryAddAsync names. Refusing duplicate
        // names is a blind index's job and this column has none — that is a later slice, not a hole
        // this line ever filled after today.
        builder.HasIndex(budget => new { budget.UserId, budget.Name }).IsUnique().AreNullsDistinct(false)
            .HasDatabaseName(UserNameIndexName);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(budget => budget.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Currency>()
            .WithMany()
            .HasForeignKey(budget => budget.BaseCurrencyCode)
            .OnDelete(DeleteBehavior.Restrict);
    }

    // The write arm of the converter, behind a call because an expression tree cannot hold a throw
    // statement. See the converter for why the null branch is unreachable and why it is loud anyway.
    private static byte[] EnvelopeOf(NarrativeField? name) =>
        name?.Envelope.ToArray()
        ?? throw new InvalidOperationException(
            "The narrative converter was applied to a null budget name. A budget with no name reaches "
            + "the column as NULL and never through this arm.");

    // Static methods rather than inline lambdas for the reason WrappedAccountKeysConfiguration gives:
    // the comparer's arguments are expression trees, and a Span cannot appear in one — it is a ref
    // struct, so the span work has to sit behind a call.
    private static bool HasSameBytes(NarrativeField? left, NarrativeField? right) =>
        left is null || right is null
            ? ReferenceEquals(left, right)
            : left.Envelope.Span.SequenceEqual(right.Envelope.Span);

    private static int ComputeHashCode(NarrativeField field)
    {
        HashCode hash = new();
        hash.AddBytes(field.Envelope.Span);

        return hash.ToHashCode();
    }

    // Rebuilt through the unchecked door rather than returned as-is, so the snapshot is a copy: the
    // instance the tracker holds must not share a buffer with the one the entity holds, or the
    // "old value" changes whenever the new one does. FromStore copies on the way through, which is why
    // there is nothing to do here but call it.
    //
    // HELD BY REVIEW, like the two arms beside it — the comparer says why that is the whole comparer on
    // this table. This one could not be held by a test in any case: an aliased snapshot diverges from
    // the tracked value only if an accepted envelope's bytes are overwritten in place, and
    // NarrativeField's shape leaves nothing able to do that. CategoryGroupConfiguration.CopyEnvelope
    // argues it in full over the same value type; the copy stays for the reason given there.
    private static NarrativeField Copy(NarrativeField field) =>
        NarrativeField.FromStore(field.Envelope);
}
