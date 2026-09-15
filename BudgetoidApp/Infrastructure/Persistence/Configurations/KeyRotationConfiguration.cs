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
    public const string StagedManifestLengthCheckName = "CK_key_rotations_staged_manifest_length";

    public const string StagedRotationEpochCheckName = "CK_key_rotations_staged_rotation_epoch";

    // THIS TABLE'S ONLY FOREIGN KEY, AND IT IS THE LAST LINK OF THE ERASURE CHAIN. The edge that used
    // to hold this row on the graph was the composite key to wrapped_account_keys, which went with
    // factor_id when a rotation stopped being performed under one factor. Nothing replaced it
    // structurally, and a table on no edge at all is a table the cascade from users never reaches: an
    // erased account would leave its staging row behind, holding its account identifier, which
    // docs/business-logic/erasure.md forbids outright — no row in any table may reference the erased
    // user. So the edge is restated where the row's own column already points, at users, and the
    // erasure argument the old edge carried moves here rather than vanishing with it.
    private const string UserForeignKeyName = "FK_key_rotations_users";

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
            // BOTH BOUNDS ARE factor_manifests' BOUNDS, READ OFF FactorManifest RATHER THAN RESTATED.
            // The staged manifest is the manifest a promotion writes into that row, so a width or an
            // epoch this table accepted and that one refused is a row that stores here and fails at
            // promotion — at the one moment in the run where the old generation has already gone. It is
            // the rule this table already kept for the envelope bounds it read off WrappedAccountKeys,
            // and it is the same argument the sibling configurations make about their own: a local copy
            // of 4096 or of 1 would not be a second fact, it would be one fact able to disagree with
            // itself.
            //
            // Both sides of the length band, because the entity refuses both and the database restates
            // both, and the empty side is the one worth reading twice. An empty bytea is exactly what an
            // unset member sends, and a manifest is the sole carrier of every factor's public key — so a
            // caller that forgot to attach one would stage a generation naming no factor at all, and the
            // promotion would file it: an account with no way back in, stored as though it had one. The
            // wide side is refused rather than truncated, because cutting the blob at the line silently
            // drops whichever factor fell past it. The lower bound is written as 1, as
            // FactorManifestConfiguration writes its own, because "not empty" is a fact about bytea
            // rather than a fact about a manifest that could ever be tuned.
            //
            // NOTHING HERE READS INTO THE BYTES, AND THAT IS A DECISION. The manifest is authenticated
            // as a SET by a key this server does not hold, so any structural check would be a second,
            // unverifiable grammar sitting where a client's is authoritative. Presence and the cap are
            // the whole of what this side may say about it.
            table.HasCheckConstraint(
                StagedManifestLengthCheckName,
                $"length(staged_manifest) between 1 and {FactorManifest.MaximumBytes}");

            // A floor and not a ceiling: generations have no last one. The floor is where it is because
            // epoch 0 is the ABSENCE of a manifest row — an account with no manifest answers 0, which is
            // the state of every account that exists today and is not an error — so a staged row
            // claiming epoch 0 would stage a generation asserting its own absence. The negative side is
            // refused with it because no generation has a number below the first.
            table.HasCheckConstraint(
                StagedRotationEpochCheckName,
                $"staged_rotation_epoch >= {FactorManifest.MinimumRotationEpoch}");
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

        // NO factor_id, AND THE ABSENCE IS THE SHAPE OF THE TABLE RATHER THAN A COLUMN SOMEBODY FORGOT.
        // Under a key-encryption key there was exactly one factor a run could have been begun under,
        // because re-wrapping the account's keys needed the secret that factor derives. Under ECDH there
        // is no such thing: encapsulating the next generation takes public halves only, so a run
        // produces ONE VALUE PER SURVIVING FACTOR — those are key_rotation_seals rows hanging off this
        // one — and "the factor this rotation was performed under" has stopped being a question with an
        // answer. It did not become plural either: a factor id array here would be the seals' own key
        // set restated on the parent, which is the copy that drifts.
        //
        // ReadOnlyMemory<byte> is not a type the provider knows, so it is converted to the array bytea
        // maps to. The comparer is not optional decoration — see the field above for what change
        // tracking does without it.
        //
        // bytea and nothing else, the call FactorManifestConfiguration makes over the same bytes: this
        // blob is authenticated by a key the client holds and its tag covers every byte exactly as
        // written, so a text type would invite PostgreSQL to collate, fold or validate an encoding over
        // it.
        builder.Property(keyRotation => keyRotation.StagedManifest)
            .HasConversion(
                memory => memory.ToArray(),
                bytes => new ReadOnlyMemory<byte>(bytes),
                ByteContentComparer)
            .HasColumnName("staged_manifest")
            .HasColumnType("bytea")
            .IsRequired();

        // The generation the staged manifest will be filed at when the run completes. The epoch is bound
        // in the manifest and nowhere else — not in any encapsulated value's KDF info and not in any
        // associated data — because binding it into a value would make every encapsulation of a
        // generation unopenable the moment the epoch it was produced under stopped being current, which
        // turns a resumable run into a disposable one.
        builder.Property(keyRotation => keyRotation.StagedRotationEpoch)
            .HasColumnName("staged_rotation_epoch")
            .HasColumnType("integer")
            .IsRequired();

        builder.Property(keyRotation => keyRotation.StartedAtUtc)
            .HasColumnName("started_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // No index declaration at all, and both halves of that are decisions.
        //
        // No user_id index, unlike wrapped_account_keys next door, and the difference is not a
        // preference. That table needs one because its primary key is factor_id, so nothing on it leads
        // with the column user_isolation appends a predicate over. Here user_id IS the primary key, so
        // the key's own index already answers every policed read of this table. A second index over the
        // same leading column would be write amplification for a seek that already exists — and the
        // foreign key below names that same column, so EF's convention generates nothing either.
        //
        // No (factor_id, user_id) index, because there is no such pair on this table any more. The one
        // that stood here covered the composite key to wrapped_account_keys and was the index that
        // key's ON DELETE CASCADE used; both left with factor_id.

        // ONE FOREIGN KEY, AND IT IS WHAT KEEPS THIS TABLE ON THE ERASURE GRAPH. Dropping factor_id
        // dropped the composite key to wrapped_account_keys, which was this table's ONLY edge and the
        // last link of users → credentials → wrapped_account_keys → key_rotations. Without a
        // replacement an erased account leaves a staging row behind carrying its own user id — a row
        // referencing the erased user, which docs/business-logic/erasure.md forbids outright, and which
        // no grant could clean up either, because this role holds no DELETE here of any shape.
        //
        // It names users directly rather than reaching the account through a credential, which is the
        // call FactorManifestConfiguration makes for the same reason: a rotation belongs to the ACCOUNT
        // — it is keyed on the account, and it stages a generation for every factor the account holds —
        // so a key to any one credential would be a claim that the run belongs to one member of the set.
        // The chain that reaches it is now users → key_rotations, one edge shorter than the one it
        // replaced, and app-role-grants.sql's cascade rendering is updated to say so.
        //
        // Cascade rather than Restrict, the argument the credentials → users, wrapped_account_keys →
        // credentials and factor_manifests → users keys all record. Restrict would let bookkeeping for
        // an unfinished run hold up an account erasure — a person's request to be forgotten refused by a
        // rotation they abandoned. Cascade is right on its own terms too: the staged manifest lists the
        // public keys of factors that hang off the same account, so once the account is gone it is a
        // list of public keys for factors that no longer exist, kept against nobody.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(keyRotation => keyRotation.UserId)
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
