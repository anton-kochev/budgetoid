using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class PasskeySignatureCounterConfiguration : IEntityTypeConfiguration<PasskeySignatureCounter>
{
    // Pinned rather than left to EF's naming convention, which derives a name from the property names
    // and so moves the moment a property is renamed. A constraint name is what PostgreSQL reports and
    // what a repository matches a PostgresException against, so it has to outlive a rename.
    public const string CredentialTypeCheckName = "CK_passkey_signature_counters_credential_type";

    public const string ValueCheckName = "CK_passkey_signature_counters_value";

    private const string CredentialIndexName =
        "IX_passkey_signature_counters_credential_id_user_id_credential_type";

    private const string UserIdIndexName = "IX_passkey_signature_counters_user_id";

    private const string CredentialForeignKeyName = "FK_passkey_signature_counters_credentials";

    public void Configure(EntityTypeBuilder<PasskeySignatureCounter> builder)
    {
        builder.ToTable("passkey_signature_counters", table =>
        {
            // The composite foreign key below proves the referenced credential is a passkey; this
            // bounds what the column may say on its own. A counter against a federated credential
            // would be a clone check on a credential no authenticator ever signs with, which is a
            // check that can only ever pass — so it is a row that should not exist rather than a row
            // with a different meaning.
            table.HasCheckConstraint(CredentialTypeCheckName, "credential_type = 'passkey'");

            // The column is wider than the value, so the range has to be said out loud: bigint accepts
            // negatives and everything above 2^32-1, neither of which is a signCount. The floor is not
            // redundant with the uint on the way in — the database is what refuses a row written by any
            // other path, and a negative counter is one an authenticator could never advance past.
            table.HasCheckConstraint(
                ValueCheckName,
                $"signature_counter >= 0 and signature_counter <= {uint.MaxValue}");
        });

        // The credential is the identity of the counter: exactly one counter exists per passkey
        // credential, and making credential_id the primary key says so rather than inventing a second
        // number for the same thing.
        builder.HasKey(counter => counter.CredentialId);

        builder.Property(counter => counter.CredentialId).HasColumnName("credential_id").IsRequired();

        // NOT NULL is load-bearing rather than tidy, for the reason SessionConfiguration records: this
        // is the column user_isolation decides tenancy on, and a NULL owner fails CLOSED — NULL =
        // anything is NULL and never true — so the row would be invisible to every session including
        // the one that wrote it, with the write succeeding and nothing in the schema saying why it
        // cannot be read back. RowLevelSecurityCoverage.FindProblems fails a nullable one.
        builder.Property(counter => counter.UserId).HasColumnName("user_id").IsRequired();

        // A copy of credentials.type carried for the reason PasskeyPublicKey.CredentialType is carried:
        // a CHECK sees only the row in front of it. Same two-direction converter idiom, and the
        // spellings must match credentials.type because the foreign key compares the two columns
        // directly.
        builder.Property(counter => counter.CredentialType)
            .HasConversion(
                credentialType => ToCredentialTypeColumnValue(credentialType),
                value => FromCredentialTypeColumnValue(value))
            .HasColumnName("credential_type")
            .HasMaxLength(20)
            .IsRequired();

        // bigint rather than integer, and this is the whole reason the column is not the obvious type:
        // WebAuthn's signCount is an unsigned 32-bit value, so the top half of its range does not fit a
        // signed 32-bit column. Storing it in one would wrap a perfectly legitimate high counter into a
        // negative number, and the monotonic comparison that detects a cloned authenticator would then
        // read it as going backwards on a genuine sign-in.
        builder.Property(counter => counter.Value)
            .HasConversion(
                value => ToColumnValue(value),
                value => FromColumnValue(value))
            .HasColumnName("signature_counter")
            .HasColumnType("bigint")
            .IsRequired();

        // Covers exactly the columns of the composite foreign key below, for the reason
        // SessionConfiguration records: EF's foreign-key convention generates an index over the foreign
        // key's columns unless an existing one already starts with them, and the primary key on
        // credential_id alone is not a covering prefix. The index exists either way; declaring it is
        // what pins the name.
        builder.HasIndex(counter => new { counter.CredentialId, counter.UserId, counter.CredentialType })
            .HasDatabaseName(CredentialIndexName);

        // The reason sessions has its own: with row-level security on, user_isolation appends
        // user_id = current_setting(...) to every statement against this table, so an unindexed user_id
        // is a sequential scan on every read and on the counter update every assertion performs.
        builder.HasIndex(counter => counter.UserId, UserIdIndexName)
            .HasDatabaseName(UserIdIndexName);

        // One composite foreign key, and the composite is the point, exactly as on sessions and on
        // passkey_public_keys: all three columns must agree with the credential row. Referencing
        // credentials(id, user_id, type) through the AK_credentials_id_user_id_type alternate key makes
        // one person's counter attached to another person's credential, and a counter attached to a
        // federated credential, both unstorable rather than merely unlikely.
        //
        // Cascade rather than Restrict, for the reason the credentials -> users foreign key records:
        // Restrict would let a row of clone-detection bookkeeping hold up the deletion of a credential,
        // and through it an account erasure.
        builder.HasOne<Credential>()
            .WithMany()
            .HasForeignKey(counter => new { counter.CredentialId, counter.UserId, counter.CredentialType })
            .HasPrincipalKey(credential => new { credential.Id, credential.UserId, credential.Type })
            .HasConstraintName(CredentialForeignKeyName)
            .OnDelete(DeleteBehavior.Cascade);
    }

    // Widening, so it cannot fail: every uint fits a long. Written as a method rather than an inline
    // cast only so it sits beside the narrowing direction, which can.
    private static long ToColumnValue(uint value) => value;

    // The row itself holds a counter CK_passkey_signature_counters_value should have refused, which
    // makes it state the model says cannot exist. Checked rather than cast, because an unchecked
    // narrowing would silently wrap the offending value into a plausible-looking counter and hide the
    // one fact worth reporting.
    private static uint FromColumnValue(long value) => value is >= uint.MinValue and <= uint.MaxValue
        ? (uint)value
        : throw new InvalidOperationException(
            $"The passkey_signature_counters.signature_counter column holds '{value}', a value "
            + $"{ValueCheckName} should have refused.");

    // A method of this class rather than CredentialTypeSpelling.Of directly, and the reason is the
    // ACCEPTED SET, not the mechanics: an expression tree may call any static method, including one
    // another class owns — CredentialConfiguration passes the shared spelling straight into the same
    // kind of argument three files away — so nothing about a converter lambda forces a switch to live
    // here.
    //
    // What does live here is the restriction. This column may say 'passkey' or 'federated' and nothing
    // else: a counter against a set of recovery codes is a row that should not exist, and refusing it
    // on the way to the column is the application's half of the rule
    // CK_passkey_signature_counters_credential_type holds. The two members it does accept are spelled
    // by the shared definition rather than repeated, so the copy cannot drift from credentials.type,
    // which the foreign key compares it against directly. The restriction is stated as an enumerated
    // arm rather than as an exclusion of RecoveryCodes, so a fourth member is refused until somebody
    // decides otherwise here.
    private static string ToCredentialTypeColumnValue(CredentialType credentialType) => credentialType switch
    {
        CredentialType.Passkey or CredentialType.Federated => CredentialTypeSpelling.Of(credentialType),
        _ => throw new ArgumentOutOfRangeException(
            nameof(credentialType),
            credentialType,
            $"No passkey_signature_counters.credential_type spelling is defined for this "
            + $"{nameof(CredentialType)} member."),
    };

    // The same restriction on the way back, and it is not redundant with the one above: this direction
    // reads whatever the column holds, so a 'recovery_codes' row — which the CHECK and the foreign key
    // should both have refused — must not materialize as a counter that looks fine.
    private static CredentialType FromCredentialTypeColumnValue(string value) =>
        CredentialTypeSpelling.TryParse(value, out CredentialType credentialType)
        && credentialType is CredentialType.Passkey or CredentialType.Federated
            ? credentialType
            : throw new InvalidOperationException(
                $"The passkey_signature_counters.credential_type column holds '{value}', a value the "
                + "foreign key to credentials should have refused.");
}
