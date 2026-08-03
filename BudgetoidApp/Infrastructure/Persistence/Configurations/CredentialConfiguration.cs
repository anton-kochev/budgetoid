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

    private const string UserIdIndexName = "IX_credentials_user_id";

    public void Configure(EntityTypeBuilder<Credential> builder)
    {
        builder.ToTable("credentials", table =>
        {
            // A CHECK rather than a native PostgreSQL enum type: the conversion below already stores
            // the member as text, so the check costs nothing extra, while a PG enum turns adding a
            // member into an ALTER TYPE dance. The price is that a new CredentialType member needs a
            // migration as well as a code change.
            table.HasCheckConstraint("CK_credentials_type", "type in ('passkey', 'federated')");

            // The same dictionary idiom, bounding the issuer vocabulary the way the one above bounds
            // the type vocabulary: without it 'Google' and 'google' are two accounts for one person,
            // since neither the column nor the lookup folds case. Widening the set is a one-line
            // migration, which is the right price for adding a provider. PostgreSQL renders a
            // single-element IN as '=', so this will not read like CK_credentials_type in the catalog.
            table.HasCheckConstraint("CK_credentials_provider", "provider is null or provider in ('google')");

            // This is what makes "a credential is exactly one type" a database rule rather than a
            // convention the application is trusted to keep: a federated row without an issuer, or a
            // passkey row carrying one, is rejected rather than stored. Story 11.3 widens the passkey
            // arm when the passkey columns arrive.
            // The null test stays alongside the length test rather than being replaced by it: length(null)
            // is null and a check evaluating to null is satisfied, so the length test alone would let the
            // subject-less row through. Provider gets no length test — the dictionary above already
            // refuses an empty one, and one row breaching two checks makes the reported name an accident.
            table.HasCheckConstraint(
                "CK_credentials_type_shape",
                "(type = 'federated' and provider is not null and subject is not null and length(subject) > 0) "
                + "or (type = 'passkey' and provider is null and subject is null)");
        });
        builder.HasKey(credential => credential.Id);

        builder.Property(credential => credential.Id).HasColumnName("id");
        builder.Property(credential => credential.UserId).HasColumnName("user_id").IsRequired();

        // An explicit pair of lambdas rather than HasConversion<string>(): that would store the
        // PascalCase member names. The lowercase spelling is deliberate — it is the vocabulary the
        // requirement uses, and it is what both the CHECK above and the API surface read.
        builder.Property(credential => credential.Type)
            .HasConversion(
                type => type.ToString().ToLowerInvariant(),
                value => Enum.Parse<CredentialType>(value, ignoreCase: true))
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

        // EF's foreign-key convention creates the plain user_id index only while nothing else covers the
        // column, and the partial index above covers federated rows alone. Declared explicitly so the
        // cascade from users, and every read of an account's credentials, keeps a full index.
        builder.HasIndex(credential => credential.UserId, UserIdIndexName)
            .HasDatabaseName(UserIdIndexName);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(credential => credential.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
