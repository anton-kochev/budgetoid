using Domain.Erasure;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class ErasureScheduleConfiguration : IEntityTypeConfiguration<ErasureSchedule>
{
    // Pinned rather than left to EF's naming convention, for the reason every sibling pins its own: a
    // constraint name is what PostgreSQL reports on a violation, so it has to outlive a rename of the
    // property it was derived from.
    //
    // Public because a 23505 under this name is the one collision the writer of a schedule models —
    // two racing requests from the same account, where the loser reads back the winner's instant
    // rather than failing — and a `catch ... when` can tell it from every other unique violation only
    // by this string.
    public const string PrimaryKeyName = "PK_erasure_schedules";

    private const string UserForeignKeyName = "FK_erasure_schedules_users";

    public void Configure(EntityTypeBuilder<ErasureSchedule> builder)
    {
        builder.ToTable("erasure_schedules");

        // THE ACCOUNT IS THE IDENTITY OF THE ROW, the shape key_rotations and factor_manifests already
        // have. One schedule per account is what lets a repeat request answer the instant it was first
        // told: a second INSERT collides here and the database refuses it, where a "check whether one
        // exists, then insert" in a handler is two statements with a window between them. A surrogate
        // id beside user_id would make the second schedule storable, and an account would then carry
        // two dates with nothing saying which one binds.
        builder.HasKey(schedule => schedule.UserId)
            .HasName(PrimaryKeyName);

        // NOT NULL is load-bearing rather than tidy: this is the column user_isolation decides tenancy
        // on, and a NULL owner fails closed and silently — the row would be invisible to every session,
        // including the one that wrote it, and the account would read as having nothing scheduled. Being
        // the primary key makes it NOT NULL anyway; the call keeps the requirement on the line.
        builder.Property(schedule => schedule.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        // timestamptz, and NOT NULL. A schedule with no instant is a date nobody can be told, and one
        // stored without a zone would be read back in whatever zone the server happened to run in.
        builder.Property(schedule => schedule.TakesEffectAtUtc)
            .HasColumnName("takes_effect_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // No index beside the key: user_id IS the primary key, so the key's own index already answers
        // the policy's predicate and every read this table has.

        // One foreign key, straight to users rather than through the credential whose locked session
        // asked for the schedule. A schedule belongs to the account: keyed to the credential, an email
        // change retiring that credential would take the schedule with it, and the date a person was
        // told would vanish because they moved address.
        //
        // CASCADE, and on this table it is the rule rather than the default. Once the account has gone,
        // a row still naming it is a record that this user existed and asked to be erased — the
        // deletion record docs/business-logic/erasure.md forbids. Restrict would be wrong in the other
        // direction too: the erasure the schedule is waiting for would be refused by its own schedule.
        // The role holds no DELETE here, so this referential action — run with the referencing table
        // owner's privileges rather than the role's — is how a row leaves on erasure.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(schedule => schedule.UserId)
            .HasConstraintName(UserForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
