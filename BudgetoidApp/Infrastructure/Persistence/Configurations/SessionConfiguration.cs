using Domain.Sessions;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    // Pinned rather than left to EF's naming convention, which derives a name from the property names
    // and so moves the moment a property is renamed. Nothing verifies these two names today:
    // SchemaConstraintSnapshotTests filters on indisunique, and neither index below is unique, so no
    // snapshot covers a non-unique index. Pinning is what keeps the name a reader can search for —
    // in an EXPLAIN, a catalog query, or a future snapshot — stable across a rename.
    private const string CredentialIndexName = "IX_sessions_credential_id_user_id_credential_type";

    private const string UserIdIndexName = "IX_sessions_user_id";

    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("sessions", table =>
        {
            // A CHECK rather than a native PostgreSQL enum type, exactly as on credentials.type: the
            // conversion below already stores the member as text, so the check costs nothing extra,
            // while a PG enum turns adding a member into an ALTER TYPE dance. The price is that a new
            // SessionKind member needs a migration as well as a code change.
            table.HasCheckConstraint("CK_sessions_kind", "kind in ('full', 'locked')");

            // A session whose expiry is at or before its creation was never live for an instant, so it
            // is not a session at all. Session.Establish already refuses one; this is the same rule
            // owned by the layer that can enforce it declaratively, and it is what stops a row arriving
            // by any other path.
            //
            // A separate constraint from the one above rather than an AND of both, because the repo
            // pins constraint attribution by name and one defect must report exactly one name.
            //
            // Deliberately no ordering check on revoked_at_utc. Exactly one writer sets it —
            // Session.Revoke — and a row could breach such a check and this one at the same time, which
            // would make the constraint name PostgreSQL reports an accident of evaluation order. That
            // is the counterexample docs/business-logic/users-and-ownership.md already records about
            // length(provider) > 0.
            table.HasCheckConstraint("CK_sessions_lifetime", "expires_at_utc > created_at_utc");

            // The rule itself, stated once in the layer that rejects rather than coerces: an
            // authorization exchange with an identity provider returns claims, not a secret a client
            // can turn into a key, so a session a federated credential opened can never unlock the
            // account's narrative. A passkey's authenticator holds the account's keys and a set of
            // recovery codes is what those keys are wrapped under, so both open a full session.
            // CK_sessions_kind bounds only the vocabulary and the composite foreign key proves only
            // whose the two rows are, so without this check
            // (credential_id = <a federated credential>, kind = 'full') is a row the application
            // role's table-wide INSERT can write.
            //
            // The full side is ENUMERATED, and it stays enumerated. The prettier inversion —
            //   (kind = 'locked') = (credential_type = 'federated')
            // says the same thing about every row this schema can hold today and is what a later
            // reader will propose. It fails OPEN: a fourth credential type added to the vocabulary
            // is not federated, so it satisfies the right-hand side and is granted a full session by
            // default, with nobody having decided that. The form below fails closed — an
            // unenumerated type gets no full session until someone adds it here, which is the same
            // decision Session.KindFor forces by writing out every arm. No test in the suite can
            // tell the two spellings apart until that fourth type exists, which is why this comment
            // is the only thing carrying the difference.
            table.HasCheckConstraint(
                "CK_sessions_kind_matches_credential",
                "(kind = 'full') = (credential_type in ('passkey', 'recovery_codes'))");
        });
        builder.HasKey(session => session.Id);

        builder.Property(session => session.Id).HasColumnName("id");

        // NOT NULL is load-bearing rather than tidy: this is the column the user_isolation policy
        // decides tenancy on, and RowLevelSecurityCoverage.FindProblems fails a nullable one. A NULL
        // owner fails CLOSED — NULL = anything is NULL and never true — so the row would be invisible
        // to every session including the one that wrote it, with the write succeeding and nothing in
        // the schema saying why it cannot be read back.
        builder.Property(session => session.UserId).HasColumnName("user_id").IsRequired();

        builder.Property(session => session.CredentialId).HasColumnName("credential_id").IsRequired();

        // A copy of credentials.type, and the copy is the point: a CHECK sees only the row in front
        // of it, so the fact kind is derived from has to be on this row for the derivation to be
        // checkable at all. It cannot drift from its source — credentials.type is immutable, holding
        // no UPDATE grant of any shape — and the composite foreign key below is what ties the two
        // together. Spelled with the same two-direction converter idiom as kind, for the same
        // reasons.
        builder.Property(session => session.CredentialType)
            .HasConversion(
                credentialType => CredentialTypeSpelling.Of(credentialType),
                value => FromCredentialTypeColumnValue(value))
            .HasColumnName("credential_type")
            .HasMaxLength(20)
            .IsRequired();

        // The vocabulary is written out once per direction, for the reasons recorded on
        // CredentialConfiguration.Type: HasConversion<string>() would store the PascalCase member
        // names, and the case-insensitive Enum.Parse on the way back would accept "FULL" and the
        // numeric "0", neither of which CK_sessions_kind allows. The lowercase spelling is the
        // vocabulary the CHECK above and the API surface both read. The switches sit in methods
        // because these arguments are expression trees, which cannot contain a switch expression.
        builder.Property(session => session.Kind)
            .HasConversion(
                kind => ToColumnValue(kind),
                value => FromColumnValue(value))
            .HasColumnName("kind")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(session => session.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(session => session.ExpiresAtUtc)
            .HasColumnName("expires_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // The one nullable column on the table, and the null means something: not revoked. An
        // already-revoked session keeps the instant access actually ended, which is why Session.Revoke
        // is idempotent rather than overwriting it.
        //
        // Session.Revoke's idempotence is a property of one object in memory, not of the table. As a
        // concurrency token the column joins the WHERE clause, so a revocation reads
        // "... where id = @id and revoked_at_utc is null" and two concurrent sweeps of the same
        // credential can no longer both stamp the row and let the later commit overwrite the instant
        // access actually ended. What it buys is that the loser is told: it affects zero rows and EF
        // raises DbUpdateConcurrencyException instead of silently rewriting history. What it costs is
        // that every write path touching a session now has to answer that exception —
        // SessionRepository.RevokeForCredentialAsync does. A token in the WHERE clause needs only
        // SELECT, so the GRANT UPDATE (revoked_at_utc) column list is untouched by this.
        builder.Property(session => session.RevokedAtUtc)
            .HasColumnName("revoked_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsConcurrencyToken();

        // The index the read that revokes every session a credential established runs on, and it
        // covers exactly the columns of the composite foreign key below. That is deliberate: EF's
        // foreign-key convention generates an index over the foreign key's columns unless one already
        // starts with them, so a narrower declaration here would not replace the convention's index
        // — it would sit beside it, and credential_id is a strict prefix of both. One index over all
        // three serves every read a narrower one would, and the surplus is pure write amplification
        // on every insert and revocation. Do not add a credential_id-only index for the revocation
        // query; this is that query's index.
        //
        // Declared here rather than left to the convention only so the name is pinned, for the
        // reason recorded on the constant above.
        builder.HasIndex(session => new { session.CredentialId, session.UserId, session.CredentialType })
            .HasDatabaseName(CredentialIndexName);

        // Every read of one person's sessions runs through this, and so does the user_isolation
        // predicate: with row-level security on, user_id = current_setting(...) is appended to every
        // statement against this table, so an unindexed user_id is a sequential scan on every query.
        builder.HasIndex(session => session.UserId, UserIdIndexName)
            .HasDatabaseName(UserIdIndexName);

        // One composite foreign key, and the composite is the point. sessions carries user_id,
        // credential_id and credential_type, and all three must agree with the credential row: a
        // session naming a credential that belongs to somebody else is a row user_isolation would
        // happily show to the wrong person, because the policy reads user_id and never looks at the
        // credential; a session claiming a passkey against a federated credential would satisfy
        // CK_sessions_kind_matches_credential while lying about what opened it. Referencing
        // credentials(id, user_id, type) makes both disagreements unstorable rather than merely
        // unlikely.
        //
        // No second foreign key to users. The credential's own user_id -> users.id cascade already
        // reaches sessions transitively through this one, so a direct one would add nothing but
        // another constraint name for the pinned snapshots to carry.
        //
        // Cascade rather than Restrict, for the reason the credentials -> users foreign key already
        // records: Restrict would let a session hold up the deletion of a credential, and through it an
        // account erasure — a row of access bookkeeping outranking a person's request to be forgotten.
        //
        // The trap that cascade creates, for whoever lands credential revocation next: because the
        // cascade exists, deleting the credential row satisfies "revoking a credential ends its
        // sessions" BY ACCIDENT and invisibly — no session row is left to show when access ended, and
        // a test asserting the sessions are gone passes without the revocation path existing. That
        // path must revoke explicitly and then delete, or the fact is unobservable.
        builder.HasOne<Credential>()
            .WithMany()
            .HasForeignKey(session => new { session.CredentialId, session.UserId, session.CredentialType })
            .HasPrincipalKey(credential => new { credential.Id, credential.UserId, credential.Type })
            .OnDelete(DeleteBehavior.Cascade);
    }

    // The discard arm is unreachable from anything the domain can produce: it means a member was added
    // to SessionKind and nobody chose a spelling for it here, or an undeclared value was cast into the
    // enum. That is a caller handing the converter a value outside its declared range, and the
    // exception says so — the enum member, not the column, is what is wrong.
    private static string ToColumnValue(SessionKind kind) => kind switch
    {
        SessionKind.Full => "full",
        SessionKind.Locked => "locked",
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            $"No sessions.kind spelling is defined for this {nameof(SessionKind)} member."),
    };

    // A different failure from the one above, so a different exception: nothing was passed wrongly here
    // — the row itself holds a kind CK_sessions_kind should have refused, which makes it state the
    // model says cannot exist rather than a bad argument. Anyone reading the message needs the
    // offending value, because finding the row is the only way to learn how it got written.
    private static SessionKind FromColumnValue(string value) => value switch
    {
        "full" => SessionKind.Full,
        "locked" => SessionKind.Locked,
        _ => throw new InvalidOperationException(
            $"The sessions.kind column holds '{value}', a value CK_sessions_kind should have refused."),
    };

    // The spellings must match credentials.type — the foreign key compares the two columns directly —
    // so this column does not spell the vocabulary for itself. It reads CredentialTypeSpelling, the one
    // definition credentials.type reads too, which is what makes "the copy agrees with its source" a
    // fact rather than two lists somebody has to keep in step. What stays here is the message: a token
    // this column holds that no member answers to is a row THIS foreign key should have refused, and
    // the shared spelling has no way to know which of the schema's copies it was asked about.
    private static CredentialType FromCredentialTypeColumnValue(string value) =>
        CredentialTypeSpelling.TryParse(value, out CredentialType credentialType)
            ? credentialType
            : throw new InvalidOperationException(
                $"The sessions.credential_type column holds '{value}', a value the foreign key to "
                + "credentials should have refused.");
}
