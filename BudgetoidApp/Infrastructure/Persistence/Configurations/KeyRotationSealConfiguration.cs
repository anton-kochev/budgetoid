using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class KeyRotationSealConfiguration : IEntityTypeConfiguration<KeyRotationSeal>
{
    // Pinned rather than left to EF's naming convention, which derives a name from the property names
    // and so moves the moment a property is renamed. A constraint name is what PostgreSQL reports and
    // what a repository matches a PostgresException against to decide whether a violation is one it
    // models at all, so the name has to outlive a rename.
    //
    // Public for the reason PK_key_rotations is public: a 23505 under this name is "this run already
    // staged a value for that factor" — an answer with a resumable run behind it, which a caller has to
    // tell apart from every other unique violation the same INSERT could raise, and a `catch ... when`
    // can only filter on a name. KeyRotationRepository filters on it beside PK_key_rotations, so two
    // begins racing on one account converge on the winner's row rather than answering 500.
    public const string PrimaryKeyName = "PK_key_rotation_seals";

    // The two checks are wrapped_account_keys' pair over its own encapsulated column, and they are
    // BYTE-IDENTICAL IN MEANING BY CONSTRUCTION rather than by having been copied carefully: both are
    // rendered from the same two constants on WrappedAccountKeys. See the check block itself for why
    // agreement between these two tables is a correctness requirement and not tidiness.
    public const string EncapsulatedAccountKeysLengthCheckName =
        "CK_key_rotation_seals_encapsulated_account_keys_length";

    public const string EncapsulatedAccountKeysVersionCheckName =
        "CK_key_rotation_seals_encapsulated_account_keys_version";

    private const string RotationForeignKeyName = "FK_key_rotation_seals_key_rotations";

    private const string FactorForeignKeyName = "FK_key_rotation_seals_wrapped_account_keys";

    // The comparer WrappedAccountKeysConfiguration, KeyRotationConfiguration and
    // FactorManifestConfiguration all declare, for the reason they declare one: change tracking compares
    // a property against the snapshot taken at load, and for a ReadOnlyMemory<byte> the default
    // comparison is the struct's own equality — pointer, offset and length — which is wrong in both
    // directions. It reports two identical values held in two buffers as different, and it reports a
    // value rewritten in place inside the same buffer as unchanged; on this column the second is the
    // next generation of an account's keys silently failing to persist. The snapshot copies rather than
    // aliases, because a view over a buffer the caller still owns is not a record of the old value.
    //
    // Spelled out here rather than shared with the siblings, following the habit the neighbouring
    // configurations already keep: each configuration owns the statics its own mapping needs.
    private static readonly ValueComparer<ReadOnlyMemory<byte>> ByteContentComparer = new(
        (left, right) => HasSameBytes(left, right),
        memory => ComputeHashCode(memory),
        memory => Copy(memory));

    public void Configure(EntityTypeBuilder<KeyRotationSeal> builder)
    {
        builder.ToTable("key_rotation_seals", table =>
        {
            // THE TWO BOUNDS ARE wrapped_account_keys' BOUNDS, READ OFF THE ENTITY THAT OWNS THEM. This
            // value is exactly what a promotion copies into
            // wrapped_account_keys.encapsulated_account_keys, so a width or a version THIS table
            // accepted and THAT one refused is a row that stores here and fails at promotion — at the
            // one moment in the run where the old generation has already gone and the staged copy is
            // the only one of the new. The two tables therefore render their four constraints from two
            // shared constants: a local literal here would not be a second fact, it would be one fact
            // able to disagree with itself, in the one direction that destroys an account.
            //
            // The constants are the ENCAPSULATION suite's, and reading the AEAD ones instead is the
            // easy mistake to make, because the two differ by exactly the 65-byte ephemeral point and
            // the wrong expression is the shorter one. A value accepted at 93 bytes has no room for a
            // point at all. WrappedAccountKeys states the rule at length.
            //
            // Exactly equal rather than a range: the AEAD at the end of the key agreement produces
            // ciphertext exactly as long as its plaintext, and the plaintext is two fixed-width keys,
            // so this column has one legal size and both sides of the bound are refused.
            table.HasCheckConstraint(
                EncapsulatedAccountKeysLengthCheckName,
                "length(encapsulated_account_keys) = "
                + $"{WrappedAccountKeys.EncapsulatedAccountKeysLength}");

            // get_byte rather than substring: the leading byte is a number, and comparing it as one
            // keeps the constraint reading the way the entity does. The version is bounded here and not
            // left to the client because the successor does not exist — a row carrying version 2 is a
            // client claiming a contract this deployment has never implemented, and storing it would
            // file bytes no version of this system can interpret.
            //
            // It is the ENCAPSULATION version, and the column is the only thing in the world that says
            // so. Both framings this schema stores lead with 0x01 on DIFFERENT suites and nothing in
            // the bytes discriminates, so rendering this from the AEAD constant compiles, holds the
            // same number today, and renumbers this column the day the other suite bumps.
            table.HasCheckConstraint(
                EncapsulatedAccountKeysVersionCheckName,
                "get_byte(encapsulated_account_keys, 0) = "
                + $"{WrappedAccountKeys.EncapsulatedAccountKeysVersion}");
        });

        // ONE ROW PER FACTOR PER RUN, AND THE KEY SAYS SO WITHOUT NAMING THE RUN. A rotation draws one
        // new content key and one new index key and encapsulates that pair to every public key the
        // staged manifest names, so the value is per factor — and key_rotations is itself keyed on the
        // account, which holds at most one run. (user_id, factor_id) is therefore already unique per
        // run, and a rotation_id in the key would be a third column restating what the parent's own
        // primary key decides. Keyed on the factor alone would be wrong in the other direction: it
        // would make one account's run collide with another's across the whole table.
        builder.HasKey(seal => new { seal.UserId, seal.FactorId })
            .HasName(PrimaryKeyName);

        // NOT NULL is load-bearing rather than tidy, for the reason SessionConfiguration,
        // WrappedAccountKeysConfiguration, KeyRotationConfiguration and FactorManifestConfiguration all
        // record: this is the column user_isolation decides tenancy on, and a NULL owner fails CLOSED —
        // NULL = anything is NULL and never true — so the row would be invisible to every session
        // including the one that wrote it. On this table that is a staged generation the run that wrote
        // it can no longer read, which a completion step reads as a factor nobody staged. Being the
        // leading half of the primary key makes it NOT NULL anyway; the call is kept so the requirement
        // reads on the line rather than being a consequence of another decision that could later move.
        builder.Property(seal => seal.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        // The factor this copy was encapsulated to — the wrapped_account_keys row a promotion will write
        // it into. Client-minted over there and merely referenced here, so its unguessability and its
        // uniqueness are that table's primary key's job; what this column owes is that the pair it names
        // exists and belongs to this account, which is the composite foreign key below.
        builder.Property(seal => seal.FactorId)
            .HasColumnName("factor_id")
            .IsRequired();

        // ReadOnlyMemory<byte> is not a type the provider knows, so it is converted to the array bytea
        // maps to. The comparer is not optional decoration — see the field above for what change
        // tracking does without it.
        builder.Property(seal => seal.EncapsulatedAccountKeys)
            .HasConversion(
                memory => memory.ToArray(),
                bytes => new ReadOnlyMemory<byte>(bytes),
                ByteContentComparer)
            .HasColumnName("encapsulated_account_keys")
            .HasColumnType("bytea")
            .IsRequired();

        // TWO FOREIGN KEYS INTO ONE GRAPH, AND POSTGRESQL PERMITS THE TWO CASCADING PATHS THIS CREATES.
        // An erasure reaches this table twice — once through users → key_rotations and once through
        // users → credentials → wrapped_account_keys — and the standard's optional restriction on
        // multiple cascade paths is a SQL Server rule, not a PostgreSQL one. Verified against
        // PostgreSQL 17 when this pair was designed and re-confirmed against the migration; the server
        // simply queues both referential actions and the row is gone after the first.
        //
        // The first names key_rotations on user_id ALONE, and no alternate key is needed for it because
        // user_id is the WHOLE of PK_key_rotations. A composite here would have nothing to reference.
        // What it buys is that a seal cannot outlive the run that staged it: a second begin replaces
        // the staging row, and the copies encapsulated under the superseded generation must go with it
        // rather than being read by the completion step of a run that never produced them.
        builder.HasOne<KeyRotation>()
            .WithMany()
            .HasForeignKey(seal => seal.UserId)
            .HasConstraintName(RotationForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);

        // The second is composite, and the composite is the point, exactly as on wrapped_account_keys,
        // sessions and passkey_public_keys: both columns must agree with the referenced row. Referencing
        // wrapped_account_keys(factor_id, user_id) through AK_wrapped_account_keys_factor_id_user_id is
        // WHAT MAKES A SEAL STAGED AGAINST ANOTHER ACCOUNT'S FACTOR UNSTORABLE rather than merely
        // unlikely — the owner half is what user_isolation cannot check for itself, since the policy
        // reads user_id and never looks at the factor, and it is the half that decides whose row a
        // promotion overwrites. KeyRotationSeal.For makes the same refusal one ring up so the mistake
        // costs a caller one exception instead of a 23503 part-way through a run.
        //
        // THAT ALTERNATE KEY WAS CREATED FOR THE EDGE THIS CHANGE REMOVES AND IS REUSED BY THE EDGE IT
        // ADDS. It exists solely so another table can name (factor_id, user_id) as a unit; that table
        // used to be key_rotations, which no longer names a factor at all, and is now this one.
        //
        // Cascade rather than Restrict, the argument wrapped_account_keys → credentials records.
        // Restrict would let a staged copy hold up the revocation of a passkey, and through it an
        // account erasure — bookkeeping for an unfinished run outranking a person's request to be
        // forgotten. Cascade is right on its own terms too: this value was encapsulated to the public
        // half of a key pair whose private half lives on the row being deleted, so once the factor is
        // gone it is a blob nothing in the world can open.
        builder.HasOne<WrappedAccountKeys>()
            .WithMany()
            .HasForeignKey(seal => new { seal.FactorId, seal.UserId })
            .HasPrincipalKey(wrappedAccountKeys => new
            {
                wrappedAccountKeys.FactorId,
                wrappedAccountKeys.UserId,
            })
            .HasConstraintName(FactorForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);
    }

    // Static methods rather than inline lambdas because the comparer's arguments are expression trees,
    // and a Span cannot appear in one — it is a ref struct, so the span work has to sit behind a call.
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
