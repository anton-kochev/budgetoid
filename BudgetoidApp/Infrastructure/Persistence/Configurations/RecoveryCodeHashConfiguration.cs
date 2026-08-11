using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class RecoveryCodeHashConfiguration : IEntityTypeConfiguration<RecoveryCodeHash>
{
    // Pinned rather than left to EF's naming convention, which derives a name from the property names
    // and so moves the moment a property is renamed. A constraint name is what PostgreSQL reports and
    // what a repository matches a PostgresException against to decide whether a violation is one it
    // models at all, so the name has to outlive a rename.
    public const string CredentialTypeCheckName = "CK_recovery_code_hashes_credential_type";

    public const string VerifierHashLengthCheckName = "CK_recovery_code_hashes_verifier_hash_length";

    private const string CredentialIndexName =
        "IX_recovery_code_hashes_credential_id_user_id_credential_type";

    private const string CredentialForeignKeyName = "FK_recovery_code_hashes_credentials";

    // SHA-256, so exactly 32 bytes. Stated here rather than on the domain type because nothing in the
    // domain computes or checks it yet — the hashing arrives with its own failing test — and a
    // constant with one reader is not yet a shared bound anything can drift from.
    private const int VerifierHashLength = 32;

    // The comparer PasskeyPublicKeyConfiguration declares, for the reason it declares one: change
    // tracking compares a property against the snapshot it took at load, and for a
    // ReadOnlyMemory<byte> the default comparison is the struct's own equality — pointer, offset and
    // length. That is wrong in both directions, and here it is wrong about a PRIMARY KEY: two equal
    // hashes in different arrays would be two different rows to identity resolution. The snapshot
    // copies rather than aliases, because a view over a buffer the caller still owns is not a record
    // of the old value at all.
    //
    // Spelled out here rather than shared with PasskeyPublicKeyConfiguration, following the habit the
    // neighbouring configurations already keep for their converters: each configuration owns the
    // statics its own mapping needs.
    private static readonly ValueComparer<ReadOnlyMemory<byte>> ByteContentComparer = new(
        (left, right) => HasSameBytes(left, right),
        memory => ComputeHashCode(memory),
        memory => Copy(memory));

    public void Configure(EntityTypeBuilder<RecoveryCodeHash> builder)
    {
        builder.ToTable("recovery_code_hashes", table =>
        {
            // Exactly 32, not a range, and the difference is what the constraint says about the
            // column. A range would read as "some hashes are longer than others", which is a claim
            // about input nobody makes here: the value is computed server-side by SHA-256 and is
            // therefore 32 bytes or it is not a hash this table can have produced. Any other length
            // is a bug in the code that wrote it, and the constraint is where that bug stops rather
            // than becomes a row nothing can redeem.
            table.HasCheckConstraint(
                VerifierHashLengthCheckName, $"length(verifier_hash) = {VerifierHashLength}");

            // The composite foreign key below already proves the referenced credential stands for a
            // set of recovery codes, because it compares this column against credentials.type. What
            // it does not do is bound what this column may say on its own, and the vocabulary matters
            // for the reason CK_passkey_public_keys_credential_type gives: this table exists only for
            // recovery codes, so 'passkey' here is not a value with a different meaning, it is a row
            // that should not exist. Stated as a check rather than inferred, so the refusal names a
            // rule instead of arriving as a foreign-key violation about a credential that is fine.
            table.HasCheckConstraint(CredentialTypeCheckName, "credential_type = 'recovery_codes'");
        });

        // The hash is the identity of the row, so a surrogate id would be a second name for the same
        // thing — and a worse one: the redemption request arrives carrying a code and nothing else,
        // so the hash is the only handle it has. Making it the primary key is also what makes two
        // codes hashing alike unstorable rather than a duplicate nothing would notice.
        builder.HasKey(recoveryCodeHash => recoveryCodeHash.VerifierHash);

        // ReadOnlyMemory<byte> is not a type the provider knows, so it is converted to the array
        // bytea maps to. The comparer is not optional decoration — see the field above.
        builder.Property(recoveryCodeHash => recoveryCodeHash.VerifierHash)
            .HasConversion(
                memory => memory.ToArray(),
                bytes => new ReadOnlyMemory<byte>(bytes),
                ByteContentComparer)
            .HasColumnName("verifier_hash")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(recoveryCodeHash => recoveryCodeHash.CredentialId)
            .HasColumnName("credential_id")
            .IsRequired();

        // NOT NULL is load-bearing rather than tidy, and its reason here is not the usual one. This
        // table is exempt from row-level security — the redemption read runs before anybody has said
        // who they are — so no policy predicate makes a NULL owner invisible. What the column carries
        // instead is the cascade from credentials and every scoped read the application makes of it
        // after redemption has answered whose account this is, and a NULL there is a row belonging to
        // nobody that every one of those reads would silently skip.
        builder.Property(recoveryCodeHash => recoveryCodeHash.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        // A copy of credentials.type, and the copy is the point: a CHECK sees only the row in front of
        // it, so the fact this table is recovery-codes-only has to be on this row for it to be
        // checkable at all. It cannot drift from its source — credentials.type is immutable, holding
        // no UPDATE grant of any shape — and the composite foreign key below is what ties the two
        // together. Spelled with the same two-direction converter idiom the neighbouring
        // configurations use, and the spellings must match, because the foreign key compares the two
        // columns directly.
        builder.Property(recoveryCodeHash => recoveryCodeHash.CredentialType)
            .HasConversion(
                credentialType => CredentialTypeSpelling.Of(credentialType),
                value => FromCredentialTypeColumnValue(value))
            .HasColumnName("credential_type")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(recoveryCodeHash => recoveryCodeHash.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // Covers exactly the columns of the composite foreign key below, for the reason
        // SessionConfiguration records: EF's foreign-key convention generates an index over the
        // foreign key's columns unless an existing one already starts with them, and the primary key
        // on verifier_hash is not a covering prefix of anything. So the index exists either way;
        // declaring it is what pins the name.
        builder.HasIndex(recoveryCodeHash => new
        {
            recoveryCodeHash.CredentialId,
            recoveryCodeHash.UserId,
            recoveryCodeHash.CredentialType,
        })
            .HasDatabaseName(CredentialIndexName);

        // Deliberately no user_id-only index, and the reflex it refuses is a specific one. On sessions
        // and passkey_signature_counters such an index pays for itself because user_isolation appends
        // user_id = current_setting(...) to EVERY statement against those tables, so an unindexed
        // owner column is a sequential scan on every read. This table is EXEMPT from row-level
        // security: no policy predicate is ever appended to a read of it, so the argument that
        // justifies the index next door does not exist here. What is left is the cascade from
        // credentials, which the composite index above serves by leading with credential_id. An index
        // added "because user_isolation filters on user_id" would be write amplification on every
        // issued code paying for a predicate that never runs.

        // One composite foreign key, and the composite is the point, exactly as on sessions and on
        // passkey_public_keys: all three columns must agree with the credential row. Referencing
        // credentials(id, user_id, type) through the AK_credentials_id_user_id_type alternate key
        // makes one person's code attached to another person's credential, and a code attached to a
        // passkey or a federated credential, both unstorable rather than merely unlikely. The first
        // matters more here than anywhere else on this schema: an anonymous redemption adopts the
        // user_id it finds on this row, so a row whose user_id disagreed with its credential's would
        // hand the redeemer somebody else's account, and no policy is watching — this table has none.
        //
        // Cascade rather than Restrict, for the reason the credentials -> users foreign key records:
        // Restrict would let an unredeemed code hold up the deletion of a credential, and through it
        // an account erasure — a row of recovery bookkeeping outranking a person's request to be
        // forgotten.
        builder.HasOne<Credential>()
            .WithMany()
            .HasForeignKey(recoveryCodeHash => new
            {
                recoveryCodeHash.CredentialId,
                recoveryCodeHash.UserId,
                recoveryCodeHash.CredentialType,
            })
            .HasPrincipalKey(credential => new { credential.Id, credential.UserId, credential.Type })
            .HasConstraintName(CredentialForeignKeyName)
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

    // The spellings must match credentials.type — the foreign key compares the two columns directly —
    // so this column reads CredentialTypeSpelling rather than repeating the vocabulary. Every member
    // remains spellable here, including the two this table's own CHECK refuses: the check is what makes
    // a row of the wrong type unstorable, and a converter that also refused them would move that refusal
    // into a place where it reports as a mapping failure instead of a named constraint.
    //
    // The message stays here where the spelling does not: a token this column holds that no member
    // answers to is a row THIS foreign key should have refused, and the shared spelling has no way to
    // know which of the schema's copies of this vocabulary it was asked about.
    private static CredentialType FromCredentialTypeColumnValue(string value) =>
        CredentialTypeSpelling.TryParse(value, out CredentialType credentialType)
            ? credentialType
            : throw new InvalidOperationException(
                $"The recovery_code_hashes.credential_type column holds '{value}', a value the foreign "
                + "key to credentials should have refused.");
}
