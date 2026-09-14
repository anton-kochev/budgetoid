using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class FactorManifestConfiguration : IEntityTypeConfiguration<FactorManifest>
{
    // Pinned rather than left to EF's naming convention, which derives a name from the property and so
    // moves the moment that property is renamed. A constraint name is what PostgreSQL reports on a
    // violation and what a repository matches a PostgresException against to decide whether a failure
    // is one it models at all, so the name has to outlive a rename.
    //
    // Public for the reason CredentialConfiguration's index names are public: a 23505 under this name
    // is the one collision a caller could be told something useful about — "this account already has a
    // manifest" — which is a different answer from every other unique violation the same INSERT could
    // raise, and a `catch ... when` can only filter on a name. Nothing filters on it yet, because
    // nothing writes a manifest yet; whatever comes to will have to tell it apart from the others by
    // name rather than by shape.
    public const string PrimaryKeyName = "PK_factor_manifests";

    // Pinned for the reason above, and public for the reason the sibling configurations' check names
    // are: a violation reports this string and nothing else, so it is the only handle a caller or a
    // reader of a failing statement has on which rule was broken.
    public const string RotationEpochCheckName = "CK_factor_manifests_rotation_epoch";

    public const string ManifestLengthCheckName = "CK_factor_manifests_manifest_length";

    private const string UserForeignKeyName = "FK_factor_manifests_users";

    // The comparer WrappedAccountKeysConfiguration and KeyRotationConfiguration both declare, for the
    // reason they declare one: change tracking compares a property against the snapshot taken at load,
    // and for a ReadOnlyMemory<byte> the default comparison is the struct's own equality — pointer,
    // offset and length — which is wrong in both directions. It reports two identical manifests held in
    // two buffers as different, and it reports a manifest rewritten in place inside the same buffer as
    // unchanged; on this column the second is the account's whole set of factor public keys silently
    // failing to persist. The snapshot copies rather than aliases, because a view over a buffer the
    // caller still owns is not a record of the old value at all.
    //
    // It is the correct mapping for the type rather than a feature waiting for a caller, which is why
    // it lands with the column and not with the first writer: without it this would be the one table in
    // the schema that maps ReadOnlyMemory<byte> by its struct identity.
    //
    // Spelled out here rather than shared with the siblings, following the habit the neighbouring
    // configurations already keep: each configuration owns the statics its own mapping needs.
    private static readonly ValueComparer<ReadOnlyMemory<byte>> ByteContentComparer = new(
        (left, right) => HasSameBytes(left, right),
        memory => ComputeHashCode(memory),
        memory => Copy(memory));

    public void Configure(EntityTypeBuilder<FactorManifest> builder)
    {
        builder.ToTable("factor_manifests", table =>
        {
            // Rendered from the entity's constant rather than typed out, the way the sibling
            // configurations render theirs from WrappedAccountKeys.EnvelopeLength. Both bounds describe
            // the SAME value — FactorManifest.For refuses an epoch below the floor, and this refuses
            // the identical row arriving by any other path — so a local literal would not be a second
            // fact, it would be one fact able to disagree with itself.
            //
            // A floor and not a ceiling: generations have no last one. The floor is where it is because
            // epoch 0 is the ABSENCE of a row — an account with no manifest answers 0, which is the
            // state of every account that exists today and is not an error — so a stored row claiming
            // epoch 0 would assert its own absence, and the one read that has to tell "never rotated"
            // from "rotated to generation zero" could not. The negative side is refused with it because
            // no generation has a number below the first.
            table.HasCheckConstraint(
                RotationEpochCheckName,
                $"rotation_epoch >= {FactorManifest.MinimumRotationEpoch}");

            // Both bounds, because the entity refuses both and the database restates both. The upper
            // one is rendered from MaximumBytes for the reason above; the lower one is written as 1,
            // as PasskeyPublicKeyConfiguration writes its own, because "not empty" is a fact about
            // bytea rather than a fact about a manifest that could ever be tuned.
            //
            // The empty side is the one worth reading twice. An empty bytea is exactly what an unset
            // member sends, and this blob is the sole carrier of every factor's public key — so a
            // caller that forgot to attach the manifest would otherwise file a row naming no factor at
            // all: an account with no way back in, stored as though it had one. The wide side is
            // refused rather than truncated for the reason the entity gives at MaximumBytes: cutting
            // the blob at the line silently drops whichever factor fell past it, and nothing about the
            // stored row would say so.
            table.HasCheckConstraint(
                ManifestLengthCheckName,
                $"length(manifest) between 1 and {FactorManifest.MaximumBytes}");
        });

        // THE ACCOUNT IS THE IDENTITY OF THE ROW, exactly as on key_rotations next door. One manifest
        // per account is what the design depends on: the manifest is authenticated as a SET, so a
        // second row would be a second claim about which factors exist, and a client choosing what to
        // encapsulate to would have no way to ask which of them it was looking at. Keyed on the user,
        // the second INSERT collides and the database refuses it; a "check whether one exists, then
        // insert" in a handler is two statements with a window between them, and the window is exactly
        // wide enough for the second browser tab. ADR 0002 applied literally.
        //
        // The cost is the same one key_rotations pays and a reader should meet it here rather than
        // discover it: a generation cannot be modelled as one row per epoch with the newest winning, so
        // promotion rewrites this row in place rather than appending beside it. A surrogate id added
        // beside user_id would demote this to an ordinary index and make the duplicate storable — the
        // shape every other table in this schema has, and therefore the shape somebody tidying reaches
        // for first.
        builder.HasKey(manifest => manifest.UserId)
            .HasName(PrimaryKeyName);

        // NOT NULL is load-bearing rather than tidy, for the reason SessionConfiguration,
        // WrappedAccountKeysConfiguration and KeyRotationConfiguration all record: this is the column
        // user_isolation decides tenancy on, and a NULL owner fails CLOSED — NULL = anything is NULL
        // and never true — so the row would be invisible to every session including the one that wrote
        // it. On this table that failure is silent in the worst available way: the write succeeds, the
        // account looks to have a manifest, and the one blob naming every factor's public key is
        // unreachable by the only person entitled to it. Being the primary key makes it NOT NULL
        // anyway; the call is kept so the requirement reads on the line rather than being a consequence
        // of another decision that could later move.
        builder.Property(manifest => manifest.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        // ReadOnlyMemory<byte> is not a type the provider knows, so it is converted to the array bytea
        // maps to. The comparer is not optional decoration — see the field above for what change
        // tracking does without it.
        //
        // bytea and nothing else. These bytes are an authenticated blob whose interior is the client's
        // business; a text type would invite PostgreSQL to collate, fold or validate an encoding over a
        // value whose authentication tag covers every byte exactly as written.
        builder.Property(manifest => manifest.Manifest)
            .HasConversion(
                memory => memory.ToArray(),
                bytes => new ReadOnlyMemory<byte>(bytes),
                ByteContentComparer)
            .HasColumnName("manifest")
            .HasColumnType("bytea")
            .IsRequired();

        // NO CONCURRENCY TOKEN HERE YET, AND THE ABSENCE IS THE DECISION — this is the property
        // somebody adds IsConcurrencyToken to, so what it would and would not buy is written down
        // rather than left to be rediscovered.
        //
        // What it would buy is the ATOMICITY half of the epoch rule: SaveChanges would emit
        // `WHERE rotation_epoch = @expected` on every UPDATE of this row and refuse a statement that
        // matched nothing, which is what stops two promotions started from the same generation from
        // both landing. It would NOT buy "a promotion writes an epoch exactly one greater than the one
        // it read" — N + 17 satisfies the predicate exactly as N + 1 does, and the CHECK above holds
        // only the floor.
        //
        // It is not here because nothing writes this table and the app role holds no write privilege on
        // it, so the predicate would guard a statement nobody can issue — and because the natural
        // promotion defeats it: FactorManifest.For returns a DETACHED instance, so
        // `For(user, bytes, epoch + 1)` followed by Update() gives EF original values taken from the
        // current ones and the predicate compares the new epoch against itself. Adding it costs nothing
        // later: a concurrency token on an int has no relational artifact, so it lands with the handler
        // that catches DbUpdateConcurrencyException and the test that reproduces the race, where both
        // its shape and its effect can be checked.
        builder.Property(manifest => manifest.RotationEpoch)
            .HasColumnName("rotation_epoch")
            .HasColumnType("integer")
            .IsRequired();

        // No user_id index, unlike wrapped_account_keys, and the difference is not a preference — it is
        // the argument KeyRotationConfiguration makes for the same shape. That table needs one because
        // its primary key is factor_id, so nothing on it leads with the column user_isolation appends a
        // predicate over. Here user_id IS the primary key, so the key's own index already answers every
        // policed read of this table, which is every read it has: the policy's predicate and the seek
        // are the same column. A second index over the same leading column would be write amplification
        // for a seek that already exists.

        // One foreign key, and it names users directly rather than reaching the account through a
        // credential. That is the whole point of the table: a manifest belongs to the ACCOUNT and names
        // every factor at once, so a key to any one credential would be a claim that the set belongs to
        // one member of it.
        //
        // Cascade rather than Restrict, the argument the credentials -> users and
        // wrapped_account_keys -> credentials keys both record. Restrict would let key bookkeeping hold
        // up an account erasure — a row listing public keys outranking a person's request to be
        // forgotten. Cascade is right on its own terms too: this row names the public keys of factors
        // that hang off the same account, so once the account is gone it is a list of public keys for
        // factors that no longer exist, kept against nobody.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(manifest => manifest.UserId)
            .HasConstraintName(UserForeignKeyName)
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
