using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class KeyRotationRepository(BudgetoidDbContext dbContext) : IKeyRotationRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, WrappedAccountKeys>> ListFactorsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // THE OWNER PREDICATE IS WRITTEN RATHER THAN LEFT TO THE POLICY, for the reason
        // ExportReadService gives about its own budgets predicate: user_isolation makes a query that
        // lost its scoping answer EMPTY, not correct, and empty is the dangerous answer here. It is
        // worse than it was: an empty listing used to refuse a begin the account was entitled to make,
        // and now it also COMPARES EQUAL to a client that submitted no seals at all, because two empty
        // sets satisfy set equality vacuously. BeginKeyRotationHandler refuses an empty seal list on
        // its own before it compares anything, for exactly that reason; this predicate is the half that
        // stops the listing being empty in the first place.
        //
        // THE TYPE PREDICATE IS GONE, AND ITS REMOVAL IS THE POINT OF THIS MEMBER RATHER THAN A FILTER
        // SOMEBODY DROPPED. A run stages one seal per factor the account holds, and a set of recovery
        // codes is ten factors under one credential — so narrowing to passkeys would hand the handler a
        // set of one, let a begin sealing only the passkey pass a comparison against it, and leave ten
        // codes holding a copy of a content key the promotion has just replaced. The credentials join
        // goes with it: nothing here needs the credential any more, since KeyRotation.Begin is handed
        // the one the re-authentication gate verified.
        //
        // A wrapped_account_keys ENTITY IS MATERIALISED HERE, AND AsNoTracking IS WHAT MAKES THAT SAFE.
        // The hazard the never-materialise rule names is a TRACKED row, not a materialised one: the
        // application role holds NO DELETE on this table, so a tracked row EF later decides to cascade
        // into dies with 42501 — the rule GenerateRecoveryCodesHandler carries at length and
        // IAccountKeyReadService was shaped around. An untracked entity is never cascaded into, is
        // never in a save at all, and is the same reason the credential half of this read already asked
        // for no tracking.
        //
        // WHAT WOULD BRING THE HAZARD BACK, said plainly because the projection that used to stand here
        // said it by construction: dropping AsNoTracking, and handing one of these entities to anything
        // that saves. They exist to be read — KeyRotationSeal.For takes one and copies two ids off it —
        // and a caller that put one in a DbSet, or mutated one, is outside what this member offers.
        List<WrappedAccountKeys> factors = await dbContext.WrappedAccountKeys
            .AsNoTracking()
            .Where(keys => keys.UserId == userId)
            .ToListAsync(cancellationToken);

        // No duplicate key is reachable: factor_id is the primary key of wrapped_account_keys, so the
        // read cannot answer the same factor twice. ToDictionary would throw rather than overwrite if
        // it ever did, which is the right direction — two rows claiming one factor is a database that
        // has lost that key, not a case to pick a winner in.
        return factors.ToDictionary(factor => factor.FactorId);
    }

    /// <inheritdoc />
    public Task<KeyRotation?> FindStagedRotationAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        // SingleOrDefault rather than FirstOrDefault: user_id is the PRIMARY KEY of key_rotations, so a
        // second row is not a case to choose between — it is a database that has lost the rule making
        // two concurrent rotations of one account unstorable. Find/FindAsync is a banned symbol, so the
        // primary-key read is spelled as a predicate like every other one in this assembly.
        dbContext.KeyRotations
            .SingleOrDefaultAsync(rotation => rotation.UserId == userId, cancellationToken);

    /// <inheritdoc />
    public async Task StageAsync(
        KeyRotation rotation,
        IReadOnlyList<KeyRotationSeal> seals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        ArgumentNullException.ThrowIfNull(seals);

        try
        {
            await ConvergeAsync(rotation, seals, cancellationToken);
        }
        // THE RACE THE UPSERT BELOW CANNOT WIN ON ITS OWN, AND IT CONVERGES RATHER THAN CONFLICTING.
        // Two begins of one account from two requests read the staged row at READ COMMITTED, both find
        // nothing, and both Add. The loser's INSERT blocks on the winner's index entry and meets 23505
        // the moment the winner commits. Nothing stood between that and GlobalExceptionHandler, which
        // has no case for a DbUpdateException, so it was a 500 titled about an unexpected error for a
        // person who did nothing but press the button in two tabs.
        //
        // A 409 IS THE WRONG ANSWER AND IS NOT AVAILABLE HERE. This port promises replacement because
        // begin is the repair path — a completion that refuses because the account's live factor set
        // moved is answered by a begin carrying the corrected set — and a conflict raised at the one
        // moment the path is under load would leave a client holding a staged row it cannot replace and
        // a run it cannot finish, with no route that removes either. So the answer is the same answer a
        // second begin gets when it is not racing: re-read, copy the values over, save again.
        //
        // BOTH PRIMARY KEYS ARE NAMED, AND A FILTER WRITTEN AGAINST ONE OF THEM TRANSLATES HALF THESE
        // RACES AND PASSES THE REST THROUGH AS A 500. The parent and its children go in ONE batch, so
        // which of the two constraints reports the violation depends on statement order inside that
        // batch and is not a thing a caller — or this method — can predict.
        //
        // NARROWED ON THE CONSTRAINT NAMES RATHER THAN ON THE SQLSTATE ALONE, the rule every translated
        // exception in this folder follows: this save carries two unique rules and a composite foreign
        // key, so a bare 23505 catch would retry a violation this method does not model. In particular
        // FK_key_rotation_seals_wrapped_account_keys raises 23503 for a factor revoked mid-request, and
        // that must stay loud rather than be re-attempted against a set that really is wrong.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: KeyRotationConfiguration.PrimaryKeyName
            or KeyRotationSealConfiguration.PrimaryKeyName,
        })
        {
            // ONE BOUNDED RETRY AND NEVER A LOOP. There is exactly one state the re-read can find that
            // the first attempt did not: a row committed by the request that beat this one. A second
            // 23505 after that would mean the row vanished between the re-read and the save, which
            // needs a DELETE on key_rotations that the application role does not hold — so a loop would
            // spin on something that cannot happen while a lost race stays a 500 either way.
            //
            // THE ROLLED-BACK ATTEMPT'S INSERTS ARE DETACHED FIRST, AND WITHOUT THIS THE RETRY IS A
            // SECOND COPY OF THE SAME FAILURE. EF leaves an Added entity Added after a failed save, and
            // the re-read resolves to the tracked instance by identity rather than to the row it just
            // materialised — so the converge below would find its own Added object, take it for the
            // staged row, change nothing, and re-issue the very INSERT that raised. Only the Added ones
            // go: anything this attempt loaded and marked Modified is rewritten idempotently on the
            // second pass, and the two type arguments keep this to the tables a begin writes.
            //
            // The save that raised was rolled back to the savepoint EF takes before SaveChanges when a
            // transaction is already open — ITransactionalExecutor has opened one by the time this runs
            // — so the enclosing transaction is usable rather than aborted, and the re-read below sees
            // the winner's committed row.
            foreach (EntityEntry entry in dbContext.ChangeTracker
                         .Entries<KeyRotation>()
                         .Cast<EntityEntry>()
                         .Concat(dbContext.ChangeTracker.Entries<KeyRotationSeal>())
                         .Where(tracked => tracked.State == EntityState.Added)
                         .ToList())
            {
                entry.State = EntityState.Detached;
            }

            await ConvergeAsync(rotation, seals, cancellationToken);
        }
    }

    /// <summary>
    /// One pass of the upsert: the staged row added or copied onto, one seal per submitted factor added
    /// or rewritten, and a single save covering both.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="StageAsync" /> so the convergence above re-runs the whole of it rather
    /// than a repair written a second time — a retry that re-issued only the statements its own attempt
    /// had not reached would leave the surviving generation a blend of two runs, which is the one
    /// outcome worse than the 500 it replaces.
    /// </remarks>
    private async Task ConvergeAsync(
        KeyRotation rotation,
        IReadOnlyList<KeyRotationSeal> seals,
        CancellationToken cancellationToken)
    {
        // AN UPSERT, BECAUSE THE PORT PROMISES REPLACEMENT. Begin is the repair path — a completion
        // that refuses because the live factor set moved is answered by a begin carrying the corrected
        // set — so a second begin has to go through. A blind Add passes every case in
        // BeginKeyRotationHandlerTests over an in-memory dictionary and meets 23505 on PK_key_rotations
        // the first time a person begins twice.
        //
        // FOUND AND UPDATED RATHER THAN DELETED AND RE-INSERTED, and the alternative fails two ways
        // rather than one. EF batches a delete and an insert of the same primary key without a
        // guaranteed order, so the pair is a coin flip on 23505; and key_rotations is deliberately
        // granted no DELETE, so the tidier-looking spelling would also need a privilege nothing else on
        // this path needs. What it does need is INSERT and UPDATE, the latter over rotation_id,
        // staged_manifest, staged_rotation_epoch and started_at_utc — user_id is the primary key and is
        // never in the SET list. (That list moved with the table: factor_id and the two wrapped
        // envelopes it used to name are columns key_rotations no longer has.)
        KeyRotation? staged = await dbContext.KeyRotations
            .SingleOrDefaultAsync(existing => existing.UserId == rotation.UserId, cancellationToken);

        if (staged is null)
        {
            // Add on an instance the tracker already holds as Unchanged — which is what an attempt the
            // database rolled back leaves behind — moves it back to Added rather than throwing, so the
            // replayed attempt re-issues the INSERT. That is why the port asks a caller for ONE
            // instance per begin: a fresh object here would be a second entity carrying the same
            // primary key while the first is still tracked, and EF refuses that by name.
            dbContext.KeyRotations.Add(rotation);
        }
        else if (!ReferenceEquals(staged, rotation))
        {
            EntityEntry<KeyRotation> entry = dbContext.Entry(staged);

            // SetValues copies every mapped scalar from the detached instance onto the tracked one,
            // which is how a row with private setters and no mutator is replaced without giving
            // KeyRotation a second way to be built. The key is copied too and is the same value, so
            // nothing about the row's identity moves.
            entry.CurrentValues.SetValues(rotation);

            // MARKED MODIFIED RATHER THAN LEFT TO CHANGE DETECTION, and this line is what makes a
            // replayed replacement converge. On the second attempt the tracked entity ALREADY carries
            // the new values — it was updated by the abandoned attempt, and EF does not refresh a
            // tracked entity from a later query — so change detection sees nothing to write, no
            // statement is emitted, and the row the database rolled back keeps the OLD generation's
            // envelopes with the request answering 200. Forcing the state writes every column again,
            // which is idempotent and correct on every attempt.
            entry.State = EntityState.Modified;
        }

        // THE CHILDREN ARE LOADED TRACKED, WHICH IS THE ONE PLACE IN THIS ASSEMBLY THAT READS
        // key_rotation_seals TO WRITE IT. Tracking is what an UPDATE is issued from, and the role holds
        // exactly INSERT and UPDATE (encapsulated_account_keys) here — the two statements below and no
        // third.
        //
        // The never-materialise rule the listing above states applies to these rows in the same shape,
        // and is satisfied rather than exempted: this table is granted no DELETE either, so an EF
        // cascade into a tracked seal would die with 42501. Nothing on this path can produce one — the
        // only principals of these rows are the KeyRotation above, which is added or updated and never
        // deleted, and wrapped_account_keys rows, which this repository reads untracked.
        //
        // The owner predicate is written rather than left to user_isolation, the rule this file keeps
        // everywhere: a query that lost its scoping answers EMPTY, and empty here reads as "the
        // previous run staged nothing" — every replacement becomes an insert, which meets 23505 on
        // PK_key_rotation_seals against the rows that are really there.
        List<KeyRotationSeal> existing = await dbContext.KeyRotationSeals
            .Where(seal => seal.UserId == rotation.UserId)
            .ToListAsync(cancellationToken);

        Dictionary<Guid, KeyRotationSeal> sealedFactors = existing.ToDictionary(seal => seal.FactorId);

        // PER KEY THE STATEMENT IS AN UPDATE OR AN INSERT — NEVER A DELETE AND AN INSERT OF THE SAME
        // KEY. That pair is the coin flip on 23505 the parent's own comment argues against: EF batches a
        // delete and an insert of one primary key in no guaranteed order, and here it would be a whole
        // set of them rather than one row. Matching the submitted seals against the tracked ones first
        // is what keeps every key to a single statement.
        foreach (KeyRotationSeal seal in seals)
        {
            if (!sealedFactors.TryGetValue(seal.FactorId, out KeyRotationSeal? tracked))
            {
                // Add on an instance the tracker already holds as Unchanged — which is what an attempt
                // the database rolled back leaves behind — moves it back to Added rather than throwing,
                // so the replayed attempt re-issues the INSERT. That is why the port asks a caller for
                // ONE instance set per begin.
                dbContext.KeyRotationSeals.Add(seal);

                continue;
            }

            if (ReferenceEquals(tracked, seal))
            {
                continue;
            }

            EntityEntry<KeyRotationSeal> entry = dbContext.Entry(tracked);

            // SetValues copies every mapped scalar from the detached instance onto the tracked one, as
            // on the parent. Both key columns are copied too and are the same values, so nothing about
            // the row's identity moves — only encapsulated_account_keys can actually differ, which is
            // the one column the GRANT UPDATE on this table names.
            entry.CurrentValues.SetValues(seal);

            // MARKED MODIFIED RATHER THAN LEFT TO CHANGE DETECTION, for the parent's replay reason read
            // one table down. On the second attempt the tracked child ALREADY carries the new value —
            // the abandoned attempt wrote it, and EF does not refresh a tracked entity from a later
            // query — so change detection sees nothing, no statement is emitted, and the row the
            // database rolled back keeps the SUPERSEDED generation's value while the request answers
            // 200. Forcing the state writes the column again, which is idempotent on every attempt.
            entry.State = EntityState.Modified;
        }

        // NOTHING IS DELETED HERE, AND THE ABSENT THIRD ARM IS THE PART OF THIS METHOD WORTH READING.
        // The loop above leaves a seal the submitted set did not claim exactly where it was — and there
        // can be no such seal, which is a fact about the schema rather than about this code.
        //
        // TWO CASCADES REACH THIS TABLE AND EXACTLY ONE OF THEM FIRES ON THIS PATH. Do not collapse
        // them:
        //
        //   FK_key_rotation_seals_key_rotations (user_id → key_rotations) DOES NOT FIRE. It runs when
        //   the PARENT ROW IS DELETED, and a second begin UPDATES that row in place — key_rotations is
        //   keyed on user_id and is granted no DELETE of any shape. That is precisely why the two arms
        //   above exist: nothing clears the previous run's seals, so they are rewritten one by one
        //   instead of being replaced wholesale.
        //
        //   FK_key_rotation_seals_wrapped_account_keys ((factor_id, user_id) → wrapped_account_keys,
        //   ON DELETE CASCADE) DOES FIRE, on the only event that can take a factor out of the submitted
        //   set. BeginKeyRotationHandler's gate has already established that the submitted set EQUALS
        //   the account's live wrapped_account_keys rows, so a seal here for a factor NOT in that set
        //   would be a seal whose own factor row is gone — and the deletion that removed that row took
        //   the seal with it, before this method read anything. The role holds no DELETE on
        //   wrapped_account_keys at all, so rows leave it only by the cascade from credentials or from
        //   users, both of which reach this table through this same edge.
        //
        // The two windows that look like exceptions and are not: a factor revoked between the handler's
        // listing and this read leaves `existing` SHORT rather than long, and its insert meets 23503 on
        // that composite key, loudly; a factor registered between two begins is in the submitted set and
        // not in `existing`, which is the Add branch.
        //
        // SO A DELETE IS NOT OWED, and granting one would be a privilege on a table holding key
        // material for a statement nothing can issue — the case app-role-grants.sql's own rule about
        // withholding writes exists to refuse. A defensive throw was considered and refused for a
        // smaller reason: the condition cannot be produced, so the branch would be an untestable second
        // and weaker statement of a foreign key. WHAT WOULD MAKE THE DELETE OWED is a gate that
        // admitted a subset of the account's factors, or a path that removed a factor without deleting
        // its wrapped_account_keys row.
        //
        // ONE SAVE FOR THE ROW AND ITS SEALS. A begin writes this account's staged generation and
        // nothing else, so there is no second repository to stay in step with — and the parent and the
        // children have to move together or the account holds a staged run that cannot be completed.
        // The transaction the caller opened is what holds this write together with the rest of its unit
        // of work; this save is what makes the write happen inside it.
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
