using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class PasskeyPublicKeyConfiguration : IEntityTypeConfiguration<PasskeyPublicKey>
{
    // Pinned rather than left to EF's naming convention, which derives a name from the property names
    // and so moves the moment a property is renamed. A constraint name is what PostgreSQL reports and
    // what a repository matches a PostgresException against to decide whether a violation is one it
    // models at all, so the name has to outlive a rename.
    public const string CredentialTypeCheckName = "CK_passkey_public_keys_credential_type";

    public const string WebAuthnCredentialIdLengthCheckName =
        "CK_passkey_public_keys_webauthn_credential_id_length";

    public const string CoseAlgorithmCheckName = "CK_passkey_public_keys_cose_algorithm";

    public const string PublicKeyLengthCheckName = "CK_passkey_public_keys_public_key_length";

    // A 23505 from this index is the collision that says one authenticator credential already resolves
    // to an account, which is the one duplicate a caller can be told something useful about.
    public const string WebAuthnCredentialIdIndexName = "IX_passkey_public_keys_webauthn_credential_id";

    private const string CredentialIndexName = "IX_passkey_public_keys_credential_id_user_id_credential_type";

    private const string CredentialForeignKeyName = "FK_passkey_public_keys_credentials";

    // Change tracking compares a property's value with the snapshot it took when the entity was loaded,
    // and for a ReadOnlyMemory<byte> the default comparison is the struct's own equality — the pointer,
    // the offset and the length. That is wrong in both directions: rewriting the bytes of the same
    // buffer in place is a real edit the tracker would miss, and handing over an equal copy in a
    // different array is not an edit but would be reported as one. Comparing and snapshotting the bytes
    // is what makes the tracker agree with what the column holds. The snapshot copies rather than
    // aliases, for the reason PasskeyPublicKey.Register copies: a snapshot that is a view over a buffer
    // the caller still owns is not a record of the old value at all.
    private static readonly ValueComparer<ReadOnlyMemory<byte>> ByteContentComparer = new(
        (left, right) => HasSameBytes(left, right),
        memory => ComputeHashCode(memory),
        memory => Copy(memory));

    public void Configure(EntityTypeBuilder<PasskeyPublicKey> builder)
    {
        builder.ToTable("passkey_public_keys", table =>
        {
            // The composite foreign key below already proves the referenced credential is a passkey,
            // because it compares this column against credentials.type. What it does not do is bound
            // what this column may say on its own, and the vocabulary matters here: this table exists
            // only for passkeys, so 'federated' is not a value with a different meaning, it is a row
            // that should not exist. Stated as a check rather than inferred, so the refusal names a
            // rule instead of arriving as a foreign-key violation about a credential that is fine.
            table.HasCheckConstraint(CredentialTypeCheckName, "credential_type = 'passkey'");

            // The same bounds PasskeyPublicKey.Register applies, owned by the layer that rejects
            // rather than coerces. Below the floor the handle could not have come from a conforming
            // authenticator and would be a guessable value filed as a credential id; above the ceiling
            // it is not a WebAuthn credential id at all. The numbers are read off the domain constants
            // so the two bounds cannot drift apart.
            table.HasCheckConstraint(
                WebAuthnCredentialIdLengthCheckName,
                $"length(webauthn_credential_id) between {PasskeyPublicKey.MinWebAuthnCredentialIdLength} "
                + $"and {PasskeyPublicKey.MaxWebAuthnCredentialIdLength}");

            // The dictionary idiom the type and provider columns already use, bounding the algorithm
            // vocabulary to the two the product verifies. Enum.IsDefined in Register refuses the rest
            // on the way in; this is what stops a row arriving by any other path and moving the failure
            // from registration to sign-in. Rendered from the enum members rather than typed out, so
            // adding a member is one edit and a migration rather than two places to keep in step.
            table.HasCheckConstraint(
                CoseAlgorithmCheckName,
                $"cose_algorithm in ({(int)CoseAlgorithm.Es256}, {(int)CoseAlgorithm.Rs256})");

            // A zero-length COSE key is a key that verifies nothing, and the ceiling is what keeps a
            // column nobody reads in a loop from becoming somewhere to park a megabyte.
            table.HasCheckConstraint(
                PublicKeyLengthCheckName,
                $"length(public_key_cose) between 1 and {PasskeyPublicKey.MaxCoseKeyLength}");
        });

        // The credential is the identity of the key, so the key's own surrogate id would be a second
        // number for the same thing: exactly one public key exists per passkey credential, and making
        // credential_id the primary key is what says so rather than a unique index saying it twice.
        builder.HasKey(passkeyPublicKey => passkeyPublicKey.CredentialId);

        builder.Property(passkeyPublicKey => passkeyPublicKey.CredentialId)
            .HasColumnName("credential_id")
            .IsRequired();

        // NOT NULL is load-bearing rather than tidy, for the reason SessionConfiguration records: this
        // is the column user_isolation decides tenancy on, and a NULL owner fails CLOSED — NULL =
        // anything is NULL and never true — so the row would be invisible to every session including
        // the one that wrote it. RowLevelSecurityCoverage.FindProblems fails a nullable one.
        builder.Property(passkeyPublicKey => passkeyPublicKey.UserId).HasColumnName("user_id").IsRequired();

        // A copy of credentials.type, and the copy is the point: a CHECK sees only the row in front of
        // it, so the fact this table is passkey-only has to be on this row for it to be checkable at
        // all. It cannot drift from its source — credentials.type is immutable, holding no UPDATE grant
        // of any shape — and the composite foreign key below is what ties the two together. Spelled
        // with the same two-direction converter idiom sessions uses, and the spellings must match,
        // because the foreign key compares the two columns directly.
        builder.Property(passkeyPublicKey => passkeyPublicKey.CredentialType)
            .HasConversion(
                credentialType => ToCredentialTypeColumnValue(credentialType),
                value => FromCredentialTypeColumnValue(value))
            .HasColumnName("credential_type")
            .HasMaxLength(20)
            .IsRequired();

        // ReadOnlyMemory<byte> is not a type the provider knows, so it is converted to the array
        // bytea maps to. The comparer is not optional decoration — see the field above for what change
        // tracking does without it.
        builder.Property(passkeyPublicKey => passkeyPublicKey.WebAuthnCredentialId)
            .HasConversion(
                memory => memory.ToArray(),
                bytes => new ReadOnlyMemory<byte>(bytes),
                ByteContentComparer)
            .HasColumnName("webauthn_credential_id")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(passkeyPublicKey => passkeyPublicKey.CoseKey)
            .HasConversion(
                memory => memory.ToArray(),
                bytes => new ReadOnlyMemory<byte>(bytes),
                ByteContentComparer)
            .HasColumnName("public_key_cose")
            .HasColumnType("bytea")
            .IsRequired();

        // A deliberate departure from the lowercase-text enum idiom the neighbouring configurations
        // use. These are IANA-assigned COSE identifiers: they arrive as numbers in the credential
        // creation options and they are what the COSE key itself carries, so a parallel textual
        // vocabulary would be one more thing to keep in step with the protocol for no reader's benefit
        // — and the numbers are already negative, which no reader mistakes for an ordinal EF invented.
        // Still written out once per direction rather than reached for with HasConversion<int>(), so a
        // new CoseAlgorithm member is a decision somebody makes here at compile time.
        builder.Property(passkeyPublicKey => passkeyPublicKey.Algorithm)
            .HasConversion(
                algorithm => ToColumnValue(algorithm),
                value => FromColumnValue(value))
            .HasColumnName("cose_algorithm")
            .HasColumnType("integer")
            .IsRequired();

        // Unique, and the uniqueness is the rule rather than a lookup optimisation that happens to
        // hold: this is the index the discovery read runs on — an assertion arrives naming only a
        // WebAuthn credential id, before anybody has said who they are — and if two rows could carry
        // the same handle, that read would resolve one authenticator credential to two accounts and
        // have nothing to choose between them.
        builder.HasIndex(passkeyPublicKey => passkeyPublicKey.WebAuthnCredentialId)
            .IsUnique()
            .HasDatabaseName(WebAuthnCredentialIdIndexName);

        // Covers exactly the columns of the composite foreign key below, for the reason
        // SessionConfiguration records: EF's foreign-key convention generates an index over the foreign
        // key's columns unless an existing one already starts with them, and the primary key on
        // credential_id alone is not a covering prefix of (credential_id, user_id, credential_type).
        // So the index exists either way; declaring it is what pins the name.
        builder.HasIndex(passkeyPublicKey => new
        {
            passkeyPublicKey.CredentialId,
            passkeyPublicKey.UserId,
            passkeyPublicKey.CredentialType,
        })
            .HasDatabaseName(CredentialIndexName);

        // Deliberately no user_id-only index, unlike sessions. Nothing reads this table by owner on a
        // hot path — a key is fetched by the handle an assertion names, or by the credential it hangs
        // off — and the composite index above leads with credential_id, so the cascade from credentials
        // is served. An index added reflexively "because user_isolation filters on user_id" would be
        // write amplification on every registration paying for a read nobody makes.

        // One composite foreign key, and the composite is the point, exactly as on sessions: all three
        // columns must agree with the credential row. Referencing credentials(id, user_id, type)
        // through the AK_credentials_id_user_id_type alternate key makes one person's key attached to
        // another person's credential, and a key attached to a federated credential, both unstorable
        // rather than merely unlikely — the first is a row user_isolation would happily show to the
        // wrong person, because the policy reads user_id and never looks at the credential.
        //
        // Cascade rather than Restrict, for the reason the credentials -> users foreign key records:
        // Restrict would let key material hold up the deletion of a credential, and through it an
        // account erasure — a row of key bookkeeping outranking a person's request to be forgotten.
        builder.HasOne<Credential>()
            .WithMany()
            .HasForeignKey(passkeyPublicKey => new
            {
                passkeyPublicKey.CredentialId,
                passkeyPublicKey.UserId,
                passkeyPublicKey.CredentialType,
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

    // The discard arm is unreachable from anything the domain can produce: it means a member was added
    // to CoseAlgorithm and nobody chose a column value for it here, or an undeclared value was cast
    // into the enum. That is a caller handing the converter a value outside its declared range, and the
    // exception says so — the enum member, not the column, is what is wrong.
    private static int ToColumnValue(CoseAlgorithm algorithm) => algorithm switch
    {
        CoseAlgorithm.Es256 => (int)CoseAlgorithm.Es256,
        CoseAlgorithm.Rs256 => (int)CoseAlgorithm.Rs256,
        _ => throw new ArgumentOutOfRangeException(
            nameof(algorithm),
            algorithm,
            $"No passkey_public_keys.cose_algorithm value is defined for this {nameof(CoseAlgorithm)} member."),
    };

    // A different failure from the one above, so a different exception: nothing was passed wrongly here
    // — the row itself holds an algorithm CK_passkey_public_keys_cose_algorithm should have refused,
    // which makes it state the model says cannot exist rather than a bad argument. Anyone reading the
    // message needs the offending value, because finding the row is the only way to learn how it got
    // written.
    private static CoseAlgorithm FromColumnValue(int value) => value switch
    {
        (int)CoseAlgorithm.Es256 => CoseAlgorithm.Es256,
        (int)CoseAlgorithm.Rs256 => CoseAlgorithm.Rs256,
        _ => throw new InvalidOperationException(
            $"The passkey_public_keys.cose_algorithm column holds '{value}', a value "
            + $"{CoseAlgorithmCheckName} should have refused."),
    };

    // Spelled out here rather than reached for on CredentialConfiguration: these arguments are
    // expression trees, which cannot contain a switch expression, so the switch has to sit in a method
    // this class owns.
    private static string ToCredentialTypeColumnValue(CredentialType credentialType) => credentialType switch
    {
        CredentialType.Passkey => "passkey",
        CredentialType.Federated => "federated",
        _ => throw new ArgumentOutOfRangeException(
            nameof(credentialType),
            credentialType,
            $"No passkey_public_keys.credential_type spelling is defined for this "
            + $"{nameof(CredentialType)} member."),
    };

    private static CredentialType FromCredentialTypeColumnValue(string value) => value switch
    {
        "passkey" => CredentialType.Passkey,
        "federated" => CredentialType.Federated,
        _ => throw new InvalidOperationException(
            $"The passkey_public_keys.credential_type column holds '{value}', a value the foreign key "
            + "to credentials should have refused."),
    };
}
