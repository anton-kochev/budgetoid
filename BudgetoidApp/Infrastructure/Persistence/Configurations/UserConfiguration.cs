using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    // Pinned, not left to EF's naming convention (a property rename is enough to change it): UserRepository
    // matches these against PostgresException.ConstraintName to decide whether a 23505 is a failure it models
    // at all — an unmatched one propagates, and a rename would turn an actionable 409 into an opaque 500.
    public const string GoogleSubjectIndexName = "IX_users_google_subject";
    public const string EmailIndexName = "IX_users_email";

    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(user => user.Id);

        builder.Property(user => user.Id).HasColumnName("id");
        builder.Property(user => user.GoogleSubject).HasColumnName("google_subject").HasMaxLength(User.MaxGoogleSubjectLength).IsRequired();
        builder.Property(user => user.Email)
            .HasConversion(email => email.Value, value => Email.Create(value))
            .HasColumnName("email")
            .HasMaxLength(Email.MaxLength)
            .IsRequired()
            // Carries the unique index below: without it Sam@x.com and sam@x.com both insert. Two
            // properties of case_insensitive (ICU und-u-ks-level2, nondeterministic): LIKE against
            // this column fails with SQLSTATE 0A000, so search-by-email needs an explicit COLLATE;
            // and it folds case but not accents, so josé@x.com and jose@x.com are distinct rows.
            .UseCollation("case_insensitive");
        builder.Property(user => user.DisplayName).HasColumnName("display_name").HasMaxLength(User.MaxDisplayNameLength);
        builder.Property(user => user.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        builder.HasIndex(user => user.GoogleSubject).IsUnique().HasDatabaseName(GoogleSubjectIndexName);

        builder.HasIndex(user => user.Email).IsUnique().HasDatabaseName(EmailIndexName);
    }
}
