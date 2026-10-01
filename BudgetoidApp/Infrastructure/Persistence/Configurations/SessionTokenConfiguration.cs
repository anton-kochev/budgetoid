using Domain.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class SessionTokenConfiguration : IEntityTypeConfiguration<SessionToken>
{
    // Pinned rather than left to EF's naming convention, which derives a name from the property names
    // and so moves the moment a property is renamed. A constraint name is what PostgreSQL reports and
    // what a repository matches a PostgresException against to decide whether a violation is one it
    // models at all, so the name has to outlive a rename.
    public const string TokenHashLengthCheckName = "CK_session_tokens_token_hash_length";

    private const string SessionIndexName = "IX_session_tokens_session_id_user_id";

    private const string SessionForeignKeyName = "FK_session_tokens_sessions";

    // The comparer PasskeyPublicKeyConfiguration declares, for the reason it declares one: change
    // tracking compares a property against the snapshot it took at load, and for a
    // ReadOnlyMemory<byte> the default comparison is the struct's own equality — pointer, offset and
    // length. That is wrong in both directions, and here it is wrong about a PRIMARY KEY: two equal
    // digests in different arrays would be two different rows to identity resolution. The snapshot
    // copies rather than aliases, because a view over a buffer the caller still owns is not a record
    // of the old value at all.
    //
    // Spelled out here rather than shared with the neighbouring configurations, following the habit
    // they already keep: each configuration owns the statics its own mapping needs.
    private static readonly ValueComparer<ReadOnlyMemory<byte>> ByteContentComparer = new(
        (left, right) => HasSameBytes(left, right),
        memory => ComputeHashCode(memory),
        memory => Copy(memory));

    public void Configure(EntityTypeBuilder<SessionToken> builder)
    {
        builder.ToTable("session_tokens", table =>
            // Exactly 32, not a range, and the difference is what the constraint says about the
            // column. A range would read as "some digests are longer than others", which is a claim
            // about input nobody makes here: the value is computed server-side by SHA-256 and is
            // therefore 32 bytes or it is not a hash this table can have produced. Any other length is
            // a bug in the code that wrote it, and the constraint is where that bug stops rather than
            // becomes a row no cookie can ever match.
            //
            // The bound reads SessionToken.HashLength rather than a local copy, and that is
            // deliberately NOT the case RecoveryCodeHashConfiguration records for its own separate
            // constant. There the column's 32 bytes and the Domain's verifier width are numerically
            // equal and mean two different things — the width of the DIGEST versus the width of the
            // VERIFIER that was digested. Here both describe the same value, so a local 32 would not
            // be a second fact, it would be the same fact able to disagree with itself. The token's
            // own width is SessionToken.TokenLength, which this constraint cannot see and deliberately
            // does not try to: a short token digests to a perfectly well-formed 32 bytes.
            table.HasCheckConstraint(
                TokenHashLengthCheckName, $"length(token_hash) = {SessionToken.HashLength}"));

        // The hash is the identity of the row, so a surrogate id would be a second name for the same
        // thing — and a worse one: the request arrives carrying a token and nothing else, so the hash
        // is the only handle it has. Making it the primary key is also what makes two sessions sharing
        // a token unstorable rather than a duplicate nothing would notice — and a duplicate here is
        // two accounts reachable by one cookie, resolved by whichever row the read happened to return.
        builder.HasKey(sessionToken => sessionToken.TokenHash);

        // ReadOnlyMemory<byte> is not a type the provider knows, so it is converted to the array
        // bytea maps to. The comparer is not optional decoration — see the field above.
        builder.Property(sessionToken => sessionToken.TokenHash)
            .HasConversion(
                memory => memory.ToArray(),
                bytes => new ReadOnlyMemory<byte>(bytes),
                ByteContentComparer)
            .HasColumnName("token_hash")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(sessionToken => sessionToken.SessionId)
            .HasColumnName("session_id")
            .IsRequired();

        // NOT NULL is load-bearing rather than tidy, and its reason here is the recovery_code_hashes
        // reason rather than the sessions one. This table is EXEMPT from row-level security — the
        // lookup runs before anybody has said who they are — so no policy predicate makes a NULL owner
        // invisible. What the column carries instead is the composite foreign key below, which is what
        // stops a token naming a session that belongs to somebody else, and the identity every request
        // adopts from this row. A NULL there would be a token belonging to nobody, presented by a
        // browser that would then be signed in as nobody.
        builder.Property(sessionToken => sessionToken.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        // Covers exactly the columns of the composite foreign key below, for the reason
        // SessionConfiguration and RecoveryCodeHashConfiguration both record: EF's foreign-key
        // convention generates an index over the foreign key's columns unless an existing one already
        // starts with them, and the primary key on token_hash is not a covering prefix of anything. So
        // the index exists either way; declaring it is what pins the name. It is also the only index
        // the ON DELETE CASCADE below can use when a session's row goes with its credential.
        builder.HasIndex(sessionToken => new { sessionToken.SessionId, sessionToken.UserId })
            .HasDatabaseName(SessionIndexName);

        // Deliberately no user_id-only index, and the reflex it refuses is the one
        // RecoveryCodeHashConfiguration refuses next door. On sessions, passkey_signature_counters and
        // wrapped_account_keys such an index pays for itself because user_isolation appends
        // user_id = current_setting(...) to EVERY statement against those tables, so an unindexed owner
        // column is a sequential scan on every read. This table is EXEMPT: no policy predicate is ever
        // appended to a read of it, and the one read it has is by primary key. An index added "because
        // user_isolation filters on user_id" would be write amplification on every sign-in paying for
        // a predicate that never runs.

        // One composite foreign key, and the composite is the point, exactly as on sessions,
        // passkey_public_keys and wrapped_account_keys: both columns must agree with the session row.
        // Referencing sessions(id, user_id) through the AK_sessions_id_user_id alternate key makes a
        // token naming another person's session unstorable rather than merely unlikely. The owner half
        // matters more here than on any of them: this lookup runs ANONYMOUS and the request adopts the
        // user_id it finds on this row, and no policy is watching — this table has none. A row whose
        // owner disagreed with its session's would sign a caller into somebody else's account.
        //
        // Cascade rather than Restrict, for the reason the sessions -> credentials foreign key
        // records: Restrict would let a stored handle hold up the deletion of a credential, and
        // through it an account erasure — a row of access bookkeeping outranking a person's request to
        // be forgotten. It is also the only correct answer on its own terms: a token whose session is
        // gone names nothing, and the row it would leave behind is a handle that resolves to a
        // dangling id.
        //
        // Note what the cascade does NOT do, because it is the trap sessions already documents one
        // level up: removing a session's token row is not revocation, and a path that deleted the
        // token instead of stamping revoked_at_utc would sign the browser out while leaving nothing
        // that says when access ended.
        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(sessionToken => new { sessionToken.SessionId, sessionToken.UserId })
            .HasPrincipalKey(session => new { session.Id, session.UserId })
            .HasConstraintName(SessionForeignKeyName)
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
