using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    // Pinned, not left to EF's naming convention (a property rename is enough to change it): UserRepository
    // matches these against PostgresException.ConstraintName to decide whether a 23505 is a failure it models
    // at all — an unmatched one propagates, and a rename would turn an actionable 409 into an opaque 500.
    public const string EmailIndexName = "IX_users_email";

    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(user => user.Id);

        builder.Property(user => user.Id).HasColumnName("id");
        builder.Property(user => user.Email)
            .HasConversion(email => email.Value, value => Email.Create(value))
            .HasColumnName("email")
            .HasMaxLength(Email.MaxLength)
            .IsRequired()
            // Carries the unique index below: without it Sam@x.com and sam@x.com both insert. Two
            // properties of case_insensitive (ICU und-u-ks-level2, nondeterministic), and only the
            // second holds on every server.
            //
            // Measured on postgres:17.10, the version this suite runs against: LIKE, position/strpos
            // and a regex match against this column all fail with SQLSTATE 0A000, so a search-by-email
            // would need an explicit COLLATE. Measured on postgres:18.3: 18 lifted the restriction for
            // LIKE and for substring search — both answer, and they answer case-insensitively — while
            // regular expressions still refuse with 0A000, and ILIKE refuses on both servers. Read that
            // as "a search has to say which server it is written for", not as "the COLLATE became
            // harmless on 18": measured, '%X.COM%' matches both seeded rows bare on 18 and none of them
            // under COLLATE "C", so the spelling that survives 17 is the case-sensitive one and 18's
            // bare form is not the same query.
            //
            // The second property is version-independent: it folds case but not accents, so josé@x.com
            // and jose@x.com are distinct rows.
            .UseCollation("case_insensitive");
        builder.Property(user => user.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        builder.HasIndex(user => user.Email).IsUnique().HasDatabaseName(EmailIndexName);
    }
}
