using Domain.Accounts;
using Domain.Budgets;
using Domain.Categories;
using Domain.Payees;
using Domain.Security;
using Domain.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Infrastructure.Persistence.Configurations;

public sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    // Pinned to the names EF's convention already produces, so the schema does not move. These are the
    // only inbound foreign keys on accounts and categories, so naming them is what turns "it has
    // transactions" from something AccountRepository and CategoryRepository assume of any 23503 into
    // something they can prove — and keeps that guarantee stated once a second referencing table
    // appears.
    public const string AccountForeignKeyName = "FK_transactions_accounts_account_id_budget_id";
    public const string CategoryForeignKeyName = "FK_transactions_categories_category_id_budget_id";

    // Public, and applied by HasName below, for the reason the four sibling configurations made their own
    // primary keys' names public: the id in this key arrives MINTED BY THE CLIENT, so a 23505 under this
    // name is a caller's identifier already being spoken for, which is a thing the caller can be told
    // something useful about. TransactionRepository.AddAsync matches it, and it is the constraint a
    // retried POST hits - the same request body sent twice after a network timeout collides here.
    //
    // It is the ONLY unique constraint on this table, so unlike its siblings there is no OID ordering to
    // reason about: nothing else can raise a 23505 here.
    public const string PrimaryKeyName = "PK_transactions";

    public const string AmountCheckName = "CK_transactions_amount";

    // The description's pair. This table's only narrative column, and its only sealed one.
    public const string DescriptionLengthCheckName = "CK_transactions_description_length";

    public const string DescriptionVersionCheckName = "CK_transactions_description_version";

    // ONE COMPARER AND ONE CONVERTER, because there is one narrative column. Its siblings hold two of
    // each, which is not a shape to copy here for symmetry: the second converter on those tables exists
    // solely because a NOT NULL name sits beside a nullable description, and this table has no name.
    //
    // Change tracking compares a property against the snapshot it took at load; NarrativeField is a class
    // with no Equals of its own, so the default comparison is reference equality - wrong in both
    // directions. A note rebuilt from identical bytes would read as an edit, and an envelope rewritten
    // inside the instance's own buffer would not. The snapshot copies rather than aliases, because a
    // value sharing the tracked instance's buffer is not a record of the old value. That copy arm is held
    // by review and by no test, for the reason argued at CopyEnvelope below - in this file, because a
    // reader who deletes the copy is standing here and not in a sibling configuration.
    private static readonly ValueComparer<NarrativeField> EnvelopeContentComparer = new(
        (left, right) => HasSameEnvelope(left, right),
        field => ComputeEnvelopeHashCode(field),
        field => CopyEnvelope(field));

    // FromStore on the way in - the unchecked door, which is why Domain grants InternalsVisibleTo to this
    // assembly - and Envelope.ToArray() on the way out. The read side deliberately does not re-validate:
    // see NarrativeField.FromStore for why a validating read turns a cap change into silent data loss.
    //
    // The model type is the nullable NarrativeField because the property is one, so the write arm has to
    // say something about a null it will never be handed: EF does not apply a converter to a null value,
    // and a transaction with no note reaches the column as NULL without either arm running. It says so by
    // throwing rather than with a quiet fallback - the day the guarantee stops holding the symptom is a
    // loud one instead of an empty buffer filed as somebody's note.
    private static readonly ValueConverter<NarrativeField?, byte[]> NullableEnvelopeConverter = new(
        description => EnvelopeOf(description),
        bytes => NarrativeField.FromStore(bytes));

    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        // WHICH CHECK FIRES FIRST IS DECIDED BY THE CONSTRAINT NAME, ALPHABETICALLY, AND ON THIS TABLE
        // THAT PUTS A NON-NARRATIVE RULE AT THE FRONT - which has no precedent on any of the four sealed
        // tables before it. Measured on postgres:17.10 over exactly these three: amount <
        // description_length < description_version, so a row carrying an out-of-range amount BESIDE a
        // malformed note is reported under CK_transactions_amount.
        //
        // The consequence for anybody writing a case against this table: every description probe must
        // carry a LEGAL amount, or it reports the amount's constraint and passes for the wrong reason.
        // On its siblings the habit is "the name beside it is well-formed"; here it is "the amount beside
        // it is legal".
        builder.ToTable("transactions", table =>
        {
            // Magnitude only: a numeric scale rounds an over-precise amount rather than rejecting
            // it, so the decimal-places half of the domain rule stays domain-owned.
            table.HasCheckConstraint(AmountCheckName, "abs(amount) <= 1000000000");

            // THE DESCRIPTION'S BAND, under the DESCRIPTION's cap and not the name's. NarrativeFieldLimits
            // carries two ceilings over field CLASSES; this column is a description, and reading NameBytes
            // onto this line would refuse values it is meant to accept. Rendered from the two constants
            // that own them rather than from literals, so a hand-typed 29 or 2560 cannot drift.
            //
            // NOTHING HERE SAYS "OR NULL", and that is not an omission: length(null) is null, and a null
            // predicate is not a violation, so a transaction with no note passes both checks vacuously.
            //
            // The floor is what makes the empty-note case representable rather than refused: 29 bytes IS a
            // note somebody wrote and then emptied, and NULL is a note nobody wrote. The schema
            // distinguishes them, which is the whole reason Transaction's description normalisation had to
            // go.
            table.HasCheckConstraint(
                DescriptionLengthCheckName,
                $"length(description) between {CiphertextEnvelope.MinimumLength} "
                + $"and {NarrativeFieldLimits.DescriptionBytes}");

            // substring rather than get_byte, for the property and not for the symptom. get_byte reads
            // better but RAISES on a zero-length bytea instead of answering false - measured on
            // PostgreSQL 17.10, get_byte(''::bytea, 0) fails with SQLSTATE 2202E, which is not a
            // constraint violation at all: no constraint name, no failing row. substring is TOTAL over
            // every length this column can hold, so the check is false rather than fatal and the
            // violation is 23514 under every ordering.
            //
            // AND NO CASE IN THIS REPOSITORY CATCHES A get_byte SPELLING HERE. description_length sorts
            // ahead of description_version, so the length band reaches a present, zero-length value first
            // and answers 23514 - the neighbour shields the wrong spelling on every value this schema can
            // be handed. Measured on postgres:17.10 over a transactions-shaped table with the version
            // check spelled get_byte: nothing produced 2202E. The rule is held by review and by a
            // container probe over a table carrying this check alone, never by a test here.
            //
            // The version is bounded here and not left to the client because the successor does not
            // exist - a row carrying version 2 is a client claiming a contract this deployment has never
            // implemented.
            table.HasCheckConstraint(
                DescriptionVersionCheckName,
                $"substring(description from 1 for 1) = '\\x{CiphertextEnvelope.Version:x2}'::bytea");
        });

        builder.HasKey(transaction => transaction.Id).HasName(PrimaryKeyName);

        // THERE IS NO AK_transactions_id_budget_id, AND THE ABSENCE IS THE SHAPE OF THE REFERENCE GRAPH
        // RATHER THAN AN OVERSIGHT. A budget-owned table carries (id, budget_id) as an alternate key when
        // something references it compositely, because a composite foreign key needs a principal key to
        // point at - and the referrer is not always this table. The three HasPrincipalKey calls below
        // consume AK_accounts_id_budget_id, AK_payees_id_budget_id and AK_categories_id_budget_id, and
        // those three alone. AK_category_groups_id_budget_id is NOT one of them: its single consumer is
        // in CategoryConfiguration, because a CATEGORY is what points at a group compositely. Said
        // explicitly, because the shorter story - "they exist for transactions" - is the one a reader
        // reconstructs from this file alone, and acting on it would retire the group key the day
        // transactions stopped naming categories, taking the categories-to-groups foreign key with it.
        //
        // transactions is the LEAF of that graph: nothing in the schema references it at all, which is
        // also why DeleteAsync has no 23503 to translate. An alternate key added here for symmetry would
        // be a constraint no statement can violate and a name no code could ever match. Said at the
        // element because an absence cannot be found by grep, and a reader comparing the five
        // configurations will read this one as incomplete.
        builder.Property(transaction => transaction.Id).HasColumnName("id");
        builder.Property(transaction => transaction.BudgetId).HasColumnName("budget_id").IsRequired();
        builder.Property(transaction => transaction.AccountId).HasColumnName("account_id").IsRequired();
        builder.Property(transaction => transaction.Amount)
            .HasColumnName("amount")
            .HasColumnType("numeric(14,4)")
            .IsRequired();
        builder.Property(transaction => transaction.Date)
            .HasColumnName("date")
            .HasColumnType("date")
            .IsRequired();
        // bytea, and HasMaxLength(500) leaves by force: a character count is not a thing this column has
        // any more. What bounds it is the byte band in the CHECK above, written from the type that owns
        // the cap. No IsRequired - a transaction with no note is a legal row and NULL is how it says so.
        //
        // NO name_key, NO blind index and NO unique index, and none of that is missing. A blind index
        // answers "which row holds this name"; a description is not looked up, is not unique and is not a
        // name, so an index over one would publish a deterministic per-account fingerprint of somebody's
        // free text with nothing on the other side asking for it. This is the first sealed table in the
        // product with no name column at all, so IndexedName has nothing here to be half of.
        //
        // NarrativeField is not a type the provider knows, so it is converted to the array bytea maps to;
        // both halves are declared on the fields above. The comparer is not optional decoration - see the
        // one it names for what change tracking does without one.
        builder.Property(transaction => transaction.Description)
            .HasConversion(NullableEnvelopeConverter, EnvelopeContentComparer)
            .HasColumnName("description")
            .HasColumnType("bytea");
        builder.Property(transaction => transaction.PayeeId).HasColumnName("payee_id");
        builder.Property(transaction => transaction.CategoryId).HasColumnName("category_id");
        builder.Property(transaction => transaction.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(transaction => new
        {
            transaction.BudgetId,
            transaction.Date,
            transaction.CreatedAtUtc,
        })
            .IsDescending(false, true, true);

        // Restrict here while accounts, category groups, categories and payees cascade from the same
        // budget_id: the asymmetry is the rule, not an oversight. Money movement is discarded only by
        // explicit intent, never as a side effect, so a delete aimed at the budget may not carry the
        // history out with it; a delete aimed at one transaction is a different act this key does not
        // speak to. A budget holding no movement was created by mistake and its structure follows it out.
        builder.HasOne<Budget>()
            .WithMany()
            .HasForeignKey(transaction => transaction.BudgetId)
            .OnDelete(DeleteBehavior.Restrict);

        // The three references below are composite on purpose: a single-column foreign key lets the
        // database accept a transaction pointing at another budget's row, and no query filter can
        // enforce tenancy on a write. The reference id leads so the foreign-key index EF derives is
        // also the more selective one. Optionality is left to property nullability - calling
        // IsRequired(false) on a composite relationship turns the whole reference optional, so the
        // budget half stops being an enforced part of it. PostgreSQL already skips a multi-column
        // foreign key check when any column is NULL (MATCH SIMPLE), which is what makes an absent
        // payee or category work without any extra configuration.
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(transaction => new { transaction.AccountId, transaction.BudgetId })
            .HasPrincipalKey(account => new { account.Id, account.BudgetId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(AccountForeignKeyName);

        // Restrict, not SetNull: ON DELETE SET NULL cannot coexist with a composite key containing
        // the NOT NULL budget_id column - PostgreSQL would attempt budget_id = NULL, and EF's
        // change tracker fails the same fixup on the non-nullable Guid. A referenced payee cannot be
        // deleted at all - the same guard accounts and categories have. Refusing forces an explicit
        // decision about historical rows instead of silently erasing the counterparty from past
        // transactions. No application code path deletes a payee — IPayeeRepository offers AddAsync,
        // GetByIdAsync and UpdateAsync and nothing that removes a row, and the app role is granted
        // SELECT, INSERT and UPDATE (name, name_key) on payees and no DELETE of any shape — so nothing
        // in the app depends on the delete succeeding.
        builder.HasOne<Payee>()
            .WithMany()
            .HasForeignKey(transaction => new { transaction.PayeeId, transaction.BudgetId })
            .HasPrincipalKey(payee => new { payee.Id, payee.BudgetId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(transaction => new { transaction.CategoryId, transaction.BudgetId })
            .HasPrincipalKey(category => new { category.Id, category.BudgetId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(CategoryForeignKeyName);
    }

    // The write arm of the converter, behind a call because an expression tree cannot hold a throw
    // statement. See that converter for why the null branch is unreachable and why it is loud anyway.
    private static byte[] EnvelopeOf(NarrativeField? description) =>
        description?.Envelope.ToArray()
        ?? throw new InvalidOperationException(
            "The narrative converter was applied to a null transaction description. A transaction with "
            + "no description reaches the column as NULL and never through this arm.");

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

    // Rebuilt through the unchecked door rather than handed back, so what the tracker keeps is a COPY: a
    // snapshot sharing the tracked instance's buffer is not a record of the old value, it is a second name
    // for the new one. FromStore copies on the way through, so calling it is the whole of the work here.
    //
    // THIS ARM IS HELD BY REVIEW AND BY NO TEST, and the equality arm beside it is not: take the comparer
    // off the description property above and TransactionChangeTrackingTests reddens on the restatement,
    // reporting the UPDATE that should never have been sent. Measured on THIS table and not inherited from
    // the sibling configurations, which argue the same asymmetry over three comparers instead of one:
    // returning the argument, so the snapshot ALIASES the live value rather than copying it, left every
    // case in both suites green.
    //
    // Nothing can see it, because for the two to diverge something would have to overwrite an
    // already-accepted envelope's bytes IN PLACE - and no path does. The buffer behind NarrativeField is
    // private, Envelope only ever hands out a window over the copy its factory took, and a write replaces
    // the field whole rather than editing it, so reaching those bytes needs MemoryMarshal and would be
    // measuring a hazard this product has no route to.
    //
    // Keep the copy regardless. The silence is a fact about how NarrativeField is built TODAY and not a
    // guarantee it owes anybody, and this file is where a reader deleting the copy will be standing. One
    // comparer here means two arms and no more - the equality one covered, this one argued - which is the
    // whole of the change-tracking coverage story for transactions.
    private static NarrativeField CopyEnvelope(NarrativeField field) =>
        NarrativeField.FromStore(field.Envelope);
}
