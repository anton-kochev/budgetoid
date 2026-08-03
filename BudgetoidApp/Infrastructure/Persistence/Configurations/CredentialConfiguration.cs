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

    public void Configure(EntityTypeBuilder<Credential> builder)
    {
        builder.ToTable("credentials", table =>
        {
            // A CHECK rather than a native PostgreSQL enum type: the conversion below already stores
            // the member as text, so the check costs nothing extra, while a PG enum turns adding a
            // member into an ALTER TYPE dance. The price is that a new CredentialType member needs a
            // migration as well as a code change.
            table.HasCheckConstraint("CK_credentials_type", "type in ('passkey', 'federated')");

            // This is what makes "a credential is exactly one type" a database rule rather than a
            // convention the application is trusted to keep: a federated row without an issuer, or a
            // passkey row carrying one, is rejected rather than stored. Story 11.3 widens the passkey
            // arm when the passkey columns arrive.
            table.HasCheckConstraint(
                "CK_credentials_type_shape",
                "(type = 'federated' and provider is not null and subject is not null) "
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

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(credential => credential.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
