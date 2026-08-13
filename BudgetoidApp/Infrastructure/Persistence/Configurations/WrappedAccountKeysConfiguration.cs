using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class WrappedAccountKeysConfiguration : IEntityTypeConfiguration<WrappedAccountKeys>
{
    // Pinned rather than left to EF's naming convention, which derives a name from the property names
    // and so moves the moment a property is renamed. A constraint name is what PostgreSQL reports and
    // what a repository matches a PostgresException against to decide whether a violation is one it
    // models at all, so the name has to outlive a rename.
    public const string CredentialTypeCheckName = "CK_wrapped_account_keys_credential_type";

    public const string ContentKeyLengthCheckName = "CK_wrapped_account_keys_wrapped_content_key_length";

    public const string ContentKeyVersionCheckName = "CK_wrapped_account_keys_wrapped_content_key_version";

    public const string IndexKeyLengthCheckName = "CK_wrapped_account_keys_wrapped_index_key_length";

    public const string IndexKeyVersionCheckName = "CK_wrapped_account_keys_wrapped_index_key_version";

    // Public for the reason IX_passkey_public_keys_webauthn_credential_id is public: a 23505 reported
    // under this name is the one duplicate a caller can be told something useful about — the factor id
    // arrives minted by the client, so a collision is a claim on a factor that already exists rather
    // than an internal accident, and a repository has to be able to tell it apart from every other
    // unique violation the same statement could raise.
    //
    // It is the PRIMARY KEY's name that carries that job now, and it used to be a separate unique index
    // over the same column. The uniqueness did not move because the key's name is prettier: it moved
    // because the key moved to factor_id, and one constraint stating one rule is the whole point — a
    // second constraint saying the same thing is a second name the same duplicate could arrive under,
    // and a `catch ... when` can only filter on one. See HasKey below.
    public const string PrimaryKeyName = "PK_wrapped_account_keys";

    private const string CredentialIndexName =
        "IX_wrapped_account_keys_credential_id_user_id_credential_type";

    private const string UserIdIndexName = "IX_wrapped_account_keys_user_id";

    private const string CredentialForeignKeyName = "FK_wrapped_account_keys_credentials";

    // The comparer PasskeyPublicKeyConfiguration declares, for the reason it declares one: change
    // tracking compares a property against the snapshot it took at load, and for a ReadOnlyMemory<byte>
    // the default comparison is the struct's own equality — pointer, offset and length. That is wrong in
    // both directions, and on these two columns it is wrong about the account's only way back in: an
    // envelope rewritten in place inside the same buffer is a real edit the tracker would miss. The
    // snapshot copies rather than aliases, because a view over a buffer the caller still owns is not a
    // record of the old value at all.
    //
    // Spelled out here rather than shared with PasskeyPublicKeyConfiguration and
    // RecoveryCodeHashConfiguration, following the habit the neighbouring configurations already keep:
    // each configuration owns the statics its own mapping needs.
    private static readonly ValueComparer<ReadOnlyMemory<byte>> ByteContentComparer = new(
        (left, right) => HasSameBytes(left, right),
        memory => ComputeHashCode(memory),
        memory => Copy(memory));

    public void Configure(EntityTypeBuilder<WrappedAccountKeys> builder)
    {
        builder.ToTable("wrapped_account_keys", table =>
        {
            // Two spellings, not one, and that is what makes this table's vocabulary check different
            // from CK_passkey_public_keys_credential_type: those tables hold rows for exactly one
            // credential type, while account keys are wrapped under whichever factors have a
            // key-encryption key — a passkey through its PRF output, a set of recovery codes through
            // the code the client still holds. 'federated' is the value that must not appear: OAuth has
            // no PRF equivalent, so a row filed against the federated credential would be two envelopes
            // nothing in the world can open, presented as a way back into the account.
            //
            // Rendered through CredentialTypeSpelling rather than typed out, because these tokens are a
            // copy of credentials.type that the composite foreign key below compares directly against
            // its source. A literal here would be a second, independent spelling of the same
            // vocabulary, and "the column agrees with credentials.type" would go back to being a
            // coincidence that holds until somebody edits one side.
            table.HasCheckConstraint(
                CredentialTypeCheckName,
                $"credential_type in ('{CredentialTypeSpelling.Of(CredentialType.Passkey)}', "
                + $"'{CredentialTypeSpelling.Of(CredentialType.RecoveryCodes)}')");

            // The width and the version constants are read off the entity, and this is deliberately NOT
            // the case RecoveryCodeHashConfiguration records for its own separate constant. There, the
            // column's 32 bytes and the Domain's verifier width are numerically equal and mean two
            // different things — the width of the DIGEST versus the width of the VERIFIER that was
            // digested — so folding them together would let a change to either silently move the
            // other's check. Here both bounds describe the SAME value: WrappedAccountKeys.For refuses
            // an envelope that is not exactly EnvelopeLength bytes carrying EnvelopeVersion, and these
            // constraints refuse the identical row arriving by any other path. A local copy of 61 would
            // not be a second fact, it would be the same fact able to disagree with itself.
            //
            // Exactly equal rather than a range, for the reason the entity gives: AES-GCM ciphertext is
            // the length of its plaintext and the plaintext is a 32-byte key, so an envelope over a
            // wrapped account key has one legal size and both sides of the bound are refused. Stated
            // per column rather than once over both, so a violation names which envelope was malformed;
            // nothing else can tell them apart, since the two columns are indistinguishable by every
            // check here.
            table.HasCheckConstraint(
                ContentKeyLengthCheckName,
                $"length(wrapped_content_key) = {WrappedAccountKeys.EnvelopeLength}");

            // get_byte rather than substring: the leading byte is a number, and comparing it as one
            // keeps the constraint reading the way the entity does. The version is bounded here and not
            // left to the client because the successor does not exist — a row carrying version 2 is a
            // client claiming a contract this deployment has never implemented, and storing it would
            // file bytes no version of this system can interpret.
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

        // THE FACTOR IS THE IDENTITY OF THE ROW. The key used to be credential_id, on the argument that
        // exactly one pair of envelopes exists per recovery factor — true, and it quietly assumed that a
        // factor IS a credential. A passkey is: one credential, one PRF output, one key-encryption key,
        // one pair of envelopes. A set of recovery codes is not. A set is ten separate secrets filed
        // under a single credentials row, because a set is issued, counted and revoked as a unit — and
        // the client derives a key-encryption key from each CODE. Ten codes are ten key-encryption keys
        // and ten pairs of envelopes, no one of which can stand for the others. Keyed on the credential
        // the table stored the first pair and refused the other nine, so nine codes of every set opened
        // nothing at all, and the holder would learn it by redeeming one, being handed a session, and
        // finding the account still locked. So credential_id is now an ordinary, NON-UNIQUE column and a
        // credential carries as many rows as it has factors.
        //
        // Being client-minted is the reason factor_id NEEDS this key, not a reason it cannot hold it.
        // Nothing else in the system stops two rows claiming one factor identifier, and it is the
        // associated data both of a row's envelopes were sealed with — so a shared value would let one
        // factor's keys be opened against another's, and the second registration is the only place
        // anybody would ever learn of the collision. What a client-minted value needs is therefore a
        // constraint whose violation a repository can NAME, and the key is that constraint; it carries
        // the uniqueness alone, with no second unique index over the same column. Table-wide rather than
        // per owner, for the reason it always was: scoping it to an account would make the duplicate
        // storable and leave the associated data ambiguous exactly where it is trusted.
        builder.HasKey(wrappedAccountKeys => wrappedAccountKeys.FactorId)
            .HasName(PrimaryKeyName);

        builder.Property(wrappedAccountKeys => wrappedAccountKeys.CredentialId)
            .HasColumnName("credential_id")
            .IsRequired();

        // NOT NULL is load-bearing rather than tidy, for the reason SessionConfiguration records: this
        // is the column user_isolation decides tenancy on, and a NULL owner fails CLOSED — NULL =
        // anything is NULL and never true — so the row would be invisible to every session including
        // the one that wrote it. On this table that is the worst shape the failure could take: the write
        // succeeds, registration looks complete, and the envelopes are unreachable by the only person
        // entitled to them. RowLevelSecurityCoverage.FindProblems fails a nullable one.
        builder.Property(wrappedAccountKeys => wrappedAccountKeys.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        // Client-minted, so NOT NULL is the least of what it needs; see the key above. It is
        // also the associated data both envelopes were sealed with, which is why it is stored rather
        // than derived: the browser needs back the exact value it bound, and credentials.id is
        // deliberately not that value — WrappedAccountKeys.FactorId records why.
        builder.Property(wrappedAccountKeys => wrappedAccountKeys.FactorId)
            .HasColumnName("factor_id")
            .IsRequired();

        // A copy of credentials.type, and the copy is the point: a CHECK sees only the row in front of
        // it, so the fact this table refuses federated credentials has to be on this row for it to be
        // checkable at all. It cannot drift from its source — credentials.type is immutable, holding no
        // UPDATE grant of any shape — and the composite foreign key below is what ties the two together.
        // Spelled with the same two-direction converter idiom the neighbouring configurations use, and
        // the spellings must match, because the foreign key compares the two columns directly.
        builder.Property(wrappedAccountKeys => wrappedAccountKeys.CredentialType)
            .HasConversion(
                credentialType => ToCredentialTypeColumnValue(credentialType),
                value => FromCredentialTypeColumnValue(value))
            .HasColumnName("credential_type")
            .HasMaxLength(20)
            .IsRequired();

        // ReadOnlyMemory<byte> is not a type the provider knows, so it is converted to the array bytea
        // maps to. The comparer is not optional decoration — see the field above for what change
        // tracking does without it.
        builder.Property(wrappedAccountKeys => wrappedAccountKeys.WrappedContentKey)
            .HasConversion(
                memory => memory.ToArray(),
                bytes => new ReadOnlyMemory<byte>(bytes),
                ByteContentComparer)
            .HasColumnName("wrapped_content_key")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(wrappedAccountKeys => wrappedAccountKeys.WrappedIndexKey)
            .HasConversion(
                memory => memory.ToArray(),
                bytes => new ReadOnlyMemory<byte>(bytes),
                ByteContentComparer)
            .HasColumnName("wrapped_index_key")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(wrappedAccountKeys => wrappedAccountKeys.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // Covers exactly the columns of the composite foreign key below, and this line stopped being
        // incidental the day the key moved. EF's foreign-key convention generates an index over the
        // foreign key's columns unless an existing one already starts with them; the key used to lead
        // with credential_id and merely fell short of covering all three, so the index existed either
        // way and declaring it did nothing but pin the name. The key now names factor_id, a column the
        // foreign key does not contain at all, so nothing else in this table leads with credential_id.
        //
        // That makes this the only index answering "which rows belong to this credential" — the question
        // a set of recovery codes asks by existing, since a set is ten rows under one credentials row —
        // and the only one the ON DELETE CASCADE below can use when a factor is revoked or an account
        // erased. Declaring it still pins the name; that is no longer all it is doing.
        //
        // Non-unique, and deliberately so: uniqueness over credential_id is the exact rule the key move
        // removed, and restoring it here in any form would put every set of recovery codes back to one
        // stored pair of envelopes and nine codes that open nothing.
        builder.HasIndex(wrappedAccountKeys => new
        {
            wrappedAccountKeys.CredentialId,
            wrappedAccountKeys.UserId,
            wrappedAccountKeys.CredentialType,
        })
            .HasDatabaseName(CredentialIndexName);

        // A user_id index, unlike recovery_code_hashes next door, and the difference is the whole
        // argument rather than a preference. That table is EXEMPT from row-level security — its
        // discovery read runs before anybody has said who they are — so no policy predicate is ever
        // appended to a read of it, and an index there would be write amplification paying for a
        // predicate that never runs. This table is POLICED: it carries user_id, so user_isolation
        // appends user_id = current_setting(...) to every statement against it, exactly as on sessions
        // and passkey_signature_counters. An unindexed owner column is then a sequential scan on every
        // read, including the one that lists a person's factors to hand the browser its envelopes.
        builder.HasIndex(wrappedAccountKeys => wrappedAccountKeys.UserId, UserIdIndexName)
            .HasDatabaseName(UserIdIndexName);

        // One composite foreign key, and the composite is the point, exactly as on sessions and on
        // passkey_public_keys: all three columns must agree with the credential row. Referencing
        // credentials(id, user_id, type) through the AK_credentials_id_user_id_type alternate key makes
        // one account's wrapped keys attached to another account's factor, and keys attached to a
        // federated credential, both unstorable rather than merely unlikely. The owner half is what
        // user_isolation cannot check for itself — the policy reads user_id and never looks at the
        // credential — and it is the half that decides who is handed these envelopes.
        //
        // Cascade rather than Restrict, for the reason the credentials -> users foreign key records:
        // Restrict would let a row of wrapped key material hold up the deletion of a credential, and
        // through it an account erasure — key bookkeeping outranking a person's request to be forgotten.
        // Cascade is also the only correct answer on its own terms: a factor that no longer exists
        // cannot derive the key-encryption key that opens these envelopes, so the row it leaves behind
        // is ciphertext nothing can open, kept against an account that asked for neither.
        builder.HasOne<Credential>()
            .WithMany()
            .HasForeignKey(wrappedAccountKeys => new
            {
                wrappedAccountKeys.CredentialId,
                wrappedAccountKeys.UserId,
                wrappedAccountKeys.CredentialType,
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

    // A method of this class rather than CredentialTypeSpelling.Of directly, for the reason
    // PasskeySignatureCounterConfiguration states at the same place: not the mechanics of an expression
    // tree — which may call a static method any class owns — but the ACCEPTED SET. This column may say
    // 'passkey' or 'recovery_codes' and nothing else, because those are the two factors that have a
    // key-encryption key, and the two members it does accept are spelled by the shared definition rather
    // than repeated so the copy cannot drift from credentials.type, which the foreign key compares it
    // against directly. Stated as an enumerated arm rather than as an exclusion of Federated, so a
    // fourth credential type is refused until somebody decides here that it can hold the account's keys
    // — which is the same enumeration WrappedAccountKeys.For makes, for the same reason.
    private static string ToCredentialTypeColumnValue(CredentialType credentialType) => credentialType switch
    {
        CredentialType.Passkey or CredentialType.RecoveryCodes => CredentialTypeSpelling.Of(credentialType),
        _ => throw new ArgumentOutOfRangeException(
            nameof(credentialType),
            credentialType,
            $"No wrapped_account_keys.credential_type spelling is defined for this "
            + $"{nameof(CredentialType)} member."),
    };

    // The same restriction on the way back, and it is not redundant with the one above: this direction
    // reads whatever the column holds, so a 'federated' row — which the credential_type check and the
    // foreign key should both have refused — must not materialize as a pair of envelopes that looks
    // fine. Looking fine is the specific danger here: the row would be presented as a way back into the
    // account, and the discovery that it is not happens in the browser, on the day somebody needs it.
    private static CredentialType FromCredentialTypeColumnValue(string value) =>
        CredentialTypeSpelling.TryParse(value, out CredentialType credentialType)
        && credentialType is CredentialType.Passkey or CredentialType.RecoveryCodes
            ? credentialType
            : throw new InvalidOperationException(
                $"The wrapped_account_keys.credential_type column holds '{value}', a value the foreign "
                + "key to credentials should have refused.");
}
