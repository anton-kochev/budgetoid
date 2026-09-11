using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class KeyRotationConfiguration : IEntityTypeConfiguration<KeyRotation>
{
    // Public, and it is the one name on this table a caller has to be able to filter on. A 23505 under
    // this name is "you already have a rotation in flight" — an answer with a resumable run behind it,
    // which the handler that begins a rotation has to tell apart from every other unique violation the
    // same INSERT could raise. It can only do that by name, so the name is pinned here rather than left
    // to EF's convention, which derives one from the property and moves on a rename.
    //
    // KeyRotationSchemaTests writes the same string out as a literal, deliberately: a test taking its
    // expectation from the thing under test agrees with whatever that thing later decides.
    public const string PrimaryKeyName = "PK_key_rotations";

    // Pinned for the reason the sibling configurations pin theirs: a constraint name is what PostgreSQL
    // reports on a violation, so it has to outlive a property rename.
    public const string ContentKeyLengthCheckName = "CK_key_rotations_wrapped_content_key_length";

    public const string ContentKeyVersionCheckName = "CK_key_rotations_wrapped_content_key_version";

    public const string IndexKeyLengthCheckName = "CK_key_rotations_wrapped_index_key_length";

    public const string IndexKeyVersionCheckName = "CK_key_rotations_wrapped_index_key_version";

    private const string FactorForeignKeyName = "FK_key_rotations_wrapped_account_keys";

    private const string FactorIndexName = "IX_key_rotations_factor_id_user_id";

    // The comparer WrappedAccountKeysConfiguration declares, for the reason it declares one: change
    // tracking compares a property against the snapshot taken at load, and for a ReadOnlyMemory<byte>
    // the default comparison is the struct's own equality — pointer, offset and length — which is wrong
    // in both directions. The snapshot copies rather than aliases, because a view over a buffer the
    // caller still owns is not a record of the old value.
    //
    // Spelled out here rather than shared with the sibling, following the habit the neighbouring
    // configurations already keep: each configuration owns the statics its own mapping needs.
    private static readonly ValueComparer<ReadOnlyMemory<byte>> ByteContentComparer = new(
        (left, right) => HasSameBytes(left, right),
        memory => ComputeHashCode(memory),
        memory => Copy(memory));

    public void Configure(EntityTypeBuilder<KeyRotation> builder)
    {
        builder.ToTable("key_rotations", table =>
        {
            // The four checks are wrapped_account_keys' four, restated over this table's two columns and
            // rendered from the same two constants rather than from literals. That is deliberate and it
            // is the same argument the sibling makes about its own: both tables hold the SAME kind of
            // value — an AEAD envelope over one 32-byte key — so a local copy of 61 or of the version
            // byte would not be a second fact, it would be one fact able to disagree with itself. The
            // staged generation is the generation the completion step promotes, so a width this table
            // accepted and the other refused would be a row that stores here and fails there, after the
            // old keys have already been overwritten.
            //
            // Exactly equal rather than a range, for the reason the entity gives: AES-GCM ciphertext is
            // the length of its plaintext and the plaintext is a 32-byte key, so an envelope over a
            // wrapped account key has one legal size and both sides of the bound are refused. Stated per
            // column rather than once over both, so a violation names which envelope was malformed;
            // nothing else can tell them apart, since the two columns are indistinguishable by every
            // check here.
            table.HasCheckConstraint(
                ContentKeyLengthCheckName,
                $"length(wrapped_content_key) = {WrappedAccountKeys.EnvelopeLength}");

            // get_byte rather than substring: the leading byte is a number, and comparing it as one
            // keeps the constraint reading the way the entity does. The version is bounded here and not
            // left to the client because the successor does not exist — a row carrying version 2 is a
            // client claiming a contract this deployment has never implemented.
            table.HasCheckConstraint(
                ContentKeyVersionCheckName,
                $"get_byte(wrapped_content_key, 0) = {WrappedAccountKeys.EnvelopeVersion}");

            table.HasCheckConstraint(
                IndexKeyLengthCheckName,
                $"length(wrapped_index_key) = {WrappedAccountKeys.EnvelopeLength}");

            table.HasCheckConstraint(
                IndexKeyVersionCheckName,
                $"get_byte(wrapped_index_key, 0) = {WrappedAccountKeys.EnvelopeVersion}");
        });

        // THE ACCOUNT IS THE IDENTITY OF THE ROW, and this is the whole reason the table has the shape
        // it has. "At most one rotation in flight per account" is what the chunking depends on: a
        // rotation re-seals every narrative column across several requests, and two concurrent runs
        // would each re-wrap a subset of the same rows under a DIFFERENT new content key, leaving the
        // account holding columns sealed under two keys with nothing recording which got which. Neither
        // generation is distinguishable from the other by looking — both are well-formed envelopes of
        // the one legal width carrying the one legal version, and the server can open neither.
        //
        // Keyed on the user, the second INSERT collides and is refused by the database. A "check whether
        // one is running, then insert" in a handler is two statements with a window between them, and
        // the window is exactly wide enough for the second browser tab. That is ADR 0002 applied
        // literally: the lowest layer that can hold the rule declaratively holds it.
        //
        // The cost is deliberate and a reader should meet it here rather than discover it: a rotation
        // cannot be modelled as one row per attempt with a status column, so an abandoned run has to be
        // DELETED rather than marked, or the account can never begin another. A surrogate id added
        // beside user_id would demote this to an ordinary index and make the duplicate storable — the
        // shape every other table in this schema has, and therefore the shape somebody tidying reaches
        // for first.
        builder.HasKey(keyRotation => keyRotation.UserId)
            .HasName(PrimaryKeyName);

        // NOT NULL is load-bearing rather than tidy, for the reason SessionConfiguration and
        // WrappedAccountKeysConfiguration both record: this is the column user_isolation decides tenancy
        // on, and a NULL owner fails CLOSED — NULL = anything is NULL and never true — so the row would
        // be invisible to every session including the one that wrote it. Being the primary key makes it
        // NOT NULL anyway; the call is kept so the requirement reads on the line rather than being a
        // consequence of another decision that could later move.
        builder.Property(keyRotation => keyRotation.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        // Client-minted, and NOT unique. It is the value each later chunk quotes to say which run it is
        // continuing, and the value each stamped row carries in its own rotation_id column — but the
        // uniqueness that matters is already held one column over: at most one row exists per account,
        // so at most one rotation id is in flight per account. A unique index here would state that
        // again under a second name the same duplicate could arrive under, and a `catch ... when` can
        // only filter on one.
        builder.Property(keyRotation => keyRotation.RotationId)
            .HasColumnName("rotation_id")
            .IsRequired();

        // The factor the staged envelopes were wrapped under, which is also their associated data. Not
        // unique here for the same reason rotation_id is not: the primary key already allows only one
        // row per account, and this column's real guard is the composite foreign key below.
        builder.Property(keyRotation => keyRotation.FactorId)
            .HasColumnName("factor_id")
            .IsRequired();

        // ReadOnlyMemory<byte> is not a type the provider knows, so it is converted to the array bytea
        // maps to. The comparer is not optional decoration — see the field above for what change
        // tracking does without it.
        builder.Property(keyRotation => keyRotation.WrappedContentKey)
            .HasConversion(
                memory => memory.ToArray(),
                bytes => new ReadOnlyMemory<byte>(bytes),
                ByteContentComparer)
            .HasColumnName("wrapped_content_key")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(keyRotation => keyRotation.WrappedIndexKey)
            .HasConversion(
                memory => memory.ToArray(),
                bytes => new ReadOnlyMemory<byte>(bytes),
                ByteContentComparer)
            .HasColumnName("wrapped_index_key")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(keyRotation => keyRotation.StartedAtUtc)
            .HasColumnName("started_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // No user_id index, unlike wrapped_account_keys next door, and the difference is not a
        // preference. That table needs one because its primary key is factor_id, so nothing on it leads
        // with the column user_isolation appends a predicate over. Here user_id IS the primary key, so
        // the key's own index already answers every policed read of this table. A second index over the
        // same leading column would be write amplification for a seek that already exists.
        //
        // This index covers the composite foreign key below instead. EF's convention would generate one
        // over those columns anyway — the primary key leads with user_id, not factor_id, so nothing else
        // here starts with the pair — and declaring it pins the name. It is the index the ON DELETE
        // CASCADE uses when a factor is revoked mid-rotation.
        //
        // Non-unique, deliberately. A factor belongs to exactly one account and a row exists per
        // account, so the pair happens to be unique; saying so here would be a third constraint
        // restating what the primary key and the foreign key already decide between them.
        builder.HasIndex(keyRotation => new
        {
            keyRotation.FactorId,
            keyRotation.UserId,
        })
            .HasDatabaseName(FactorIndexName);

        // One composite foreign key, and the composite is the point, exactly as on wrapped_account_keys,
        // sessions and passkey_public_keys: both columns must agree with the referenced row.
        // Referencing wrapped_account_keys(factor_id, user_id) through the
        // AK_wrapped_account_keys_factor_id_user_id alternate key makes a rotation staged against
        // another account's factor unstorable rather than merely unlikely. The owner half is what
        // user_isolation cannot check for itself — the policy reads user_id and never looks at the
        // factor — and it is the half that decides whose envelopes the completion step overwrites.
        //
        // Referencing wrapped_account_keys rather than credentials is the narrower of the two available
        // choices and the correct one: a rotation is staged under a FACTOR, and a set of recovery codes
        // is ten factors under one credential, so a key to the credential would leave "which factor" a
        // value nothing checks. It also makes the promotion target exist by construction — the row this
        // rotation will overwrite is the row the key points at.
        //
        // Cascade rather than Restrict, and on this table it is the only answer that is not actively
        // harmful. Restrict would let a staging row hold up the revocation of a passkey, and through it
        // an account erasure — bookkeeping for an unfinished run outranking a person's request to be
        // forgotten. Cascade is right on its own terms too: the staged envelopes were sealed under the
        // key-encryption key that factor derives, so once the factor is gone they are two blobs nothing
        // in the world can open, and a rotation that cannot be completed must not be resumable either.
        builder.HasOne<WrappedAccountKeys>()
            .WithMany()
            .HasForeignKey(keyRotation => new
            {
                keyRotation.FactorId,
                keyRotation.UserId,
            })
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
