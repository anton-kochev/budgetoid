using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CredentialConfiguration : IEntityTypeConfiguration<Credential>
{
    // Pinned rather than left to EF's naming convention (a property rename is enough to change it):
    // UserRepository matches it against PostgresException.ConstraintName to decide whether a 23505 is
    // the collision it models at all — an unmatched one propagates, and a rename would turn an
    // actionable 409 into an opaque 500.
    public const string ProviderSubjectIndexName = "IX_credentials_provider_subject";

    // Pinned for the same reason, and kept separate from the one above because a 23505 from each names
    // a different broken rule (see the index itself below). Nothing filters on this name yet; whatever
    // does will have to tell the two apart.
    public const string FederatedPerUserIndexName = "IX_credentials_user_id_federated";

    // Pinned for the same reason as the two above, and exposed for the same reason
    // FederatedPerUserIndexName is: a 23505 from this index names a rule of its own — one issued set
    // of recovery codes per account — and whatever comes to translate that collision into an answer
    // has to tell it apart from the other two by name.
    public const string RecoveryCodesPerUserIndexName = "IX_credentials_user_id_recovery_codes";

    private const string UserIdIndexName = "IX_credentials_user_id";

    // Pinned for the same reason as the index names above. This one names a unique constraint rather
    // than an index, and it exists solely so sessions can reference (id, user_id, type) as a unit —
    // see the alternate key below.
    private const string IdUserIdTypeAlternateKeyName = "AK_credentials_id_user_id_type";

    public void Configure(EntityTypeBuilder<Credential> builder)
    {
        builder.ToTable("credentials", table =>
        {
            // A CHECK rather than a native PostgreSQL enum type: the conversion below already stores
            // the member as text, so the check costs nothing extra, while a PG enum turns adding a
            // member into an ALTER TYPE dance. The price is that a new CredentialType member needs a
            // migration as well as a code change.
            table.HasCheckConstraint(
                "CK_credentials_type", "type in ('passkey', 'federated', 'recovery_codes')");

            // The same dictionary idiom, bounding the issuer vocabulary the way the one above bounds
            // the type vocabulary: without it 'Google' and 'google' are two accounts for one person,
            // since neither the column nor the lookup folds case. Widening the set is a one-line
            // migration, which is the right price for adding a provider. PostgreSQL renders a
            // single-element IN as '=', so this will not read like CK_credentials_type in the catalog.
            table.HasCheckConstraint("CK_credentials_provider", "provider is null or provider in ('google')");

            // This is what makes "a credential is exactly one type" a database rule rather than a
            // convention the application is trusted to keep: a federated row without an issuer, or a
            // passkey row carrying one, is rejected rather than stored. The passkey arm stays this
            // narrow: it bounds the shape of the identity row and nothing more, because a passkey's
            // verification material lives on its own tables — passkey_public_keys and
            // passkey_signature_counters — rather than on a column here. See ADR 0012.
            // The null test stays alongside the length test rather than being replaced by it: length(null)
            // is null and a check evaluating to null is satisfied, so the length test alone would let the
            // subject-less row through. Provider gets no length test — the dictionary above already
            // refuses an empty one, and one row breaching two checks makes the reported name an accident.
            //
            // The recovery_codes arm's predicate is IDENTICAL to the passkey arm's, and that is stated
            // rather than left to be discovered: from this constraint's point of view the two types are
            // now the same shape, so it no longer discriminates between them. That is acceptable and
            // deliberate. Both are self-contained credentials with no issuer and no provider subject,
            // and what tells them apart lives where it can: CK_credentials_type bounds the vocabulary,
            // and the child tables' composite foreign keys — passkey_public_keys, then
            // recovery_code_hashes — each compare their own credential_type copy against this column, so
            // a recovery-code row cannot hang off a passkey credential or the reverse. Collapsing the
            // two arms into one would say the same thing in less space and lose the record of which
            // types the schema has considered.
            table.HasCheckConstraint(
                "CK_credentials_type_shape",
                "(type = 'federated' and provider is not null and subject is not null and length(subject) > 0) "
                + "or (type = 'passkey' and provider is null and subject is null) "
                + "or (type = 'recovery_codes' and provider is null and subject is null)");
        });
        builder.HasKey(credential => credential.Id);

        builder.Property(credential => credential.Id).HasColumnName("id");
        builder.Property(credential => credential.UserId).HasColumnName("user_id").IsRequired();

        // The vocabulary is CredentialTypeSpelling's, written out once there rather than in each of
        // the places that needs it, so adding a CredentialType member is a single decision taken at
        // compile time rather than a spelling ToString() invents and a case-insensitive Enum.Parse then
        // accepts on the way back — that parse also took "PASSKEY" and the numeric "0", neither of
        // which CK_credentials_type allows. That HasConversion<string>() would store the PascalCase
        // member names remains true and remains a reason not to use it. This column is the one the API
        // surface has to agree with, which is why the spelling is owned above this layer instead of
        // being copied out of it. The lambdas call methods because these arguments are expression
        // trees; a method call is the one thing an expression tree can carry a switch behind.
        builder.Property(credential => credential.Type)
            .HasConversion(
                type => CredentialTypeSpelling.Of(type),
                value => FromColumnValue(value))
            .HasColumnName("type")
            .HasMaxLength(20)
            .IsRequired();

        // No collation on either column, unlike users.email: an OIDC `sub` is a case-sensitive opaque
        // string, so folding case here would merge two distinct provider identities into one.
        builder.Property(credential => credential.Provider)
            .HasColumnName("provider")
            .HasMaxLength(Credential.MaxProviderLength);
        builder.Property(credential => credential.Subject)
            .HasColumnName("subject")
            .HasMaxLength(Credential.MaxSubjectLength);

        builder.Property(credential => credential.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // The filter states the rule the index enforces — one account per provider identity — rather
        // than leaning on NULLS DISTINCT to make passkey rows fall out of it by accident.
        builder.HasIndex(credential => new { credential.Provider, credential.Subject })
            .IsUnique()
            .HasFilter("type = 'federated'")
            .HasDatabaseName(ProviderSubjectIndexName);

        // A different rule from the one above, and until now an unowned one: that index says one account
        // per provider identity, this one says one federated credential per account. Nothing was stopping
        // an account growing a second — not a feature anyone is adding, but exactly what a bug on a new
        // credential-insert path would do, and the account would then have two Google identities that both
        // resolve to it.
        builder.HasIndex(credential => credential.UserId, FederatedPerUserIndexName)
            .IsUnique()
            .HasFilter("type = 'federated'")
            .HasDatabaseName(FederatedPerUserIndexName);

        // The same rule shape as the index above, over the other self-contained credential type: one
        // issued set of recovery codes per account. Nothing else on the row refuses a second — the
        // provider-identity index names federated rows only, and every recovery-codes row carries
        // (NULL, NULL) — so without this an account can grow two sets, which is not a feature anyone
        // is adding but exactly what a bug on an issuing path would do. Two sets are two
        // remaining-counts with nothing saying which one binds: "you have three codes left" stops
        // being answerable and "revoke the set" stops naming anything, and reissuing can only
        // replace while there is one thing to replace. That is the argument
        // Credential.CreateRecoveryCodes' own remarks make, owned here by the layer that rejects.
        //
        // Partial, filtered to this type, and the filter is load-bearing rather than tidy: an
        // unfiltered unique index over user_id enforces this rule just as well and also refuses an
        // account a second passkey, which FR-043 allows.
        builder.HasIndex(credential => credential.UserId, RecoveryCodesPerUserIndexName)
            .IsUnique()
            .HasFilter("type = 'recovery_codes'")
            .HasDatabaseName(RecoveryCodesPerUserIndexName);

        // EF's foreign-key convention creates the plain user_id index only while nothing else covers the
        // column, and the partial index above covers federated rows alone. Declared explicitly so the
        // cascade from users, and every read of an account's credentials, keeps a full index.
        builder.HasIndex(credential => credential.UserId, UserIdIndexName)
            .HasDatabaseName(UserIdIndexName);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(credential => credential.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Redundant as a uniqueness claim — id is already the primary key, so (id, user_id, type)
        // cannot repeat — and that is not what it is for. It is the referencable target sessions
        // needs: PostgreSQL will only accept a foreign key pointing at a unique constraint covering
        // exactly the referenced columns, and sessions references (credential_id, user_id,
        // credential_type) together so that a session can neither name a credential belonging to a
        // different person nor disagree with it about which type of credential it was.
        //
        // Type joins the pair rather than being checked anywhere else because credentials.type is
        // immutable — no UPDATE grant of any shape exists on this table — so the copy sessions holds
        // cannot drift away from this source.
        //
        // This adds an INDEX, not a column. That matters here specifically: the credentials exemption
        // in RowLevelSecurityCoverage.Exemptions pins this table's exact column set, and a new column
        // would go red — correctly, because the exemption was argued about the columns read to
        // discover who is asking. An index is not part of that argument and does not widen what an
        // application session can read.
        builder.HasAlternateKey(credential => new { credential.Id, credential.UserId, credential.Type })
            .HasName(IdUserIdTypeAlternateKeyName);
    }

    // The read direction keeps a method of its own where the write direction needed none, and the
    // difference is the failure each reports. A member with no spelling is a bad argument, and
    // CredentialTypeSpelling.Of says so without naming a column, because by then the fault belongs to
    // no column in particular. A token this column holds that no member answers to is the opposite:
    // nothing was passed wrongly — the row itself holds a type CK_credentials_type should have refused,
    // which makes it state the model says cannot exist. That message has to name this column and that
    // constraint, so the refusal stays here rather than moving into the shared spelling, which has no
    // way to know which of the schema's four copies of this vocabulary it was asked about. Anyone
    // reading it needs the offending value, because finding the row is the only way to learn how it got
    // written.
    private static CredentialType FromColumnValue(string value) =>
        CredentialTypeSpelling.TryParse(value, out CredentialType type)
            ? type
            : throw new InvalidOperationException(
                $"The credentials.type column holds '{value}', a value CK_credentials_type should have "
                + "refused.");
}
