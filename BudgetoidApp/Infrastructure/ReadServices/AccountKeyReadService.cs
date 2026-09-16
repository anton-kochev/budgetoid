using Application.AccountKeys;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class AccountKeyReadService(BudgetoidDbContext dbContext) : IAccountKeyReadService
{
    /// <inheritdoc />
    public async Task<AccountKeyCustody> ListForAccountAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // THE SELECT IS THE MECHANISM AND AsNoTracking IS A SIGNAL. A Select into FactorEnvelopes — a
        // type that is not an entity — is already an untracked query, so the AsNoTracking call below
        // changes nothing about this statement and would change nothing if it were deleted. It is kept
        // as a statement of intent beside the rule it serves, and NOT as the thing that enforces it:
        // what enforces it is that no line here reads the entity set into memory.
        //
        // On this table that rule is a hazard rather than a preference. The application role holds NO
        // DELETE on wrapped_account_keys, so a row this context has materialized and later decides to
        // cascade into dies with 42501. That is the never-materialise rule GenerateRecoveryCodesHandler
        // carries, and IAccountKeyReadService's own remarks say the projection is what keeps a display
        // read from being what trips it. NOTHING GOES RED IF THIS IS GOT WRONG HERE: a read-only request
        // has no cascade to walk, so the failure surfaces on whichever later request removes a credential
        // through this context.
        //
        // THE DELETE IS THE WHOLE OF THAT PREMISE NOW. The role does hold
        // UPDATE (encapsulated_account_keys), granted for a content-key rotation's promotion, so a
        // tracked row whose encapsulated value something assigned would be committed by the next
        // SaveChanges on this context with no SQLSTATE to say it happened — the silent half of the same
        // mistake, on the one column a promotion rewrites. wrapped_private_key, factor_id,
        // credential_id, user_id, credential_type and created_at_utc are all absent from that list and
        // still answer 42501; the private key is immutable by that omission, because a rotation changes
        // the account's keys and never the factor's key-encryption key. Nothing assigns either value
        // today — the entity exposes no mutator — so this is what a materialized row would cost, not a
        // path the product has. Projecting keeps this read out of both halves.
        //
        // THE OWNER IS NAMED ON EVERY ARM, AND ON THIS TABLE IT IS UNOBSERVABLE — no test can hold it.
        // wrapped_account_keys is policed by user_isolation, so PostgreSQL appends
        // user_id = current_setting('app.current_user_id') underneath this statement and a read with no
        // predicate at all answers the same rows. It is written anyway for the reason ExportReadService
        // gives about budgets: the policy makes a wrong query answer empty, not correct, and this second
        // copy is the one that survives a policy missed on a table added later. Do not delete it as
        // redundant. It is also the index seek — IX_wrapped_account_keys_user_id exists for exactly this
        // predicate, and WrappedAccountKeysConfiguration argues for it where it is declared.
        //
        // NO CREDENTIAL PREDICATE, AND THE ABSENCE IS THE DECISION. The keys belong to the account, and
        // a ceremony can present any of the account's factors — re-authentication looks a passkey up by
        // account and the assertion options carry no allowCredentials, so the authenticator chooses.
        // Narrowing here to the session's credential is what this read used to do, and it refused a
        // factor that had just been verified. GetAccountKeysHandler carries the argument.
        //
        // BOTH LEVELS COME OFF ONE SQL STATEMENT, AND THAT IS WHAT MAKES AccountKeyCustody'S CLAIM
        // TRUE RATHER THAN HOPEFUL. That type argues the two levels arrive together because the
        // client compares them — it refuses a rotation whose served factor set and manifest disagree
        // — and a comparison is only meaningful if both halves describe one instant. One port member
        // does not buy that on its own: two awaited queries on this connection are two autocommitted
        // statements, each taking its own READ COMMITTED snapshot, so an enrolment committing between
        // them would make a correct manifest and a correct factor list disagree and the client would
        // report tampering to somebody who was merely enrolling a factor. A single statement takes
        // ONE snapshot at statement start and every lateral below it is evaluated against that
        // snapshot. Splitting this back into two awaits is the edit that quietly unmakes the
        // guarantee, and nothing would go red if somebody did.
        //
        // MEASURED, NOT ASSUMED. The shape below renders as one SELECT over users with two
        // LEFT JOIN LATERALs — the manifest lateral carrying LIMIT 1 and a marker column, the factors
        // lateral carrying none — with no query splitting configured anywhere in this solution.
        //
        // AsSingleQuery DEFENDS THAT AGAINST A GLOBAL SETTING, AND THE SPLIT WAS OBSERVED RATHER THAN
        // FEARED. Adding UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery) to the options
        // this context is built from — one line on the AddDbContext in Api/Program.cs, the ordinary way
        // a solution adopts split queries — makes EF 10 issue this read as TWO commands against a
        // seeded account: the first carrying users and the manifest lateral, the second re-selecting
        // the same user row and joining wrapped_account_keys laterally. That is two autocommitted
        // statements taking two READ COMMITTED snapshots, which is exactly the window the paragraph
        // above says the single statement closes. AsSingleQuery is the per-query override that survives
        // the global setting; deleting it puts the guarantee back at the mercy of a line in Program.cs.
        //
        // WHAT THE ONE STATEMENT COSTS, WHICH THE ARGUMENT ABOVE HAD NOT NAMED: the manifest is in the
        // OUTER projection, so PostgreSQL repeats it once per factor row — an account holding eleven
        // factors and a manifest near the 4096-byte cap transfers roughly 45 KB where two statements
        // would transfer roughly 4 KB. That cartesian cost is accepted deliberately, because the
        // snapshot the second statement would lose is the thing the client's mismatch check is about.
        //
        // ROOTED ON users, AND THE ROOT IS FORCED. The statement has to answer for an account holding
        // no manifest AND no factor — the state of every account in the product — so neither of the
        // two tables it is really about can be the root: rooted on factor_manifests the epoch case
        // loses its factors, rooted on wrapped_account_keys the manifest disappears for an account
        // with no factor, which is exactly the arrangement the epoch test seeds. users is the account,
        // and one row of it is what both levels hang off. It is policed by user_isolation on the same
        // id as the two tables below it, so the root is scoped by the same comparison and adds no
        // reach. An account with no users row answers the same empty custody a missing row on either
        // lateral would.
        //
        // NOT TWO SCALAR SUBQUERIES FOR THE MANIFEST. Projecting manifest and rotation_epoch as
        // separate correlated subqueries is also one statement, and it is wrong: the provider wraps a
        // missing bytea in a COALESCE onto an EMPTY BYTEA, which is the empty buffer AccountKeyCustody
        // spends a paragraph refusing. Projected as one row, the whole subobject is null when there is
        // no row, and null is the only spelling that survives to the wire distinguishable from an
        // absent value.
        //
        // FirstOrDefault ON THE MANIFEST AND A LIST BELOW IT, AND THE ASYMMETRY IS THE SCHEMA'S.
        // factor_manifests is keyed on user_id alone, so an account has one manifest row or none and
        // taking the first truncates nothing. wrapped_account_keys is keyed on factor_id: a passkey
        // files one row and a set of recovery codes files ten, so an ordinary account is eleven and
        // First or Single there would be correct for every passkey in the product and would drop nine
        // of every ten recovery-code envelopes.
        //
        // Ordered by the primary key, which cannot tie, so two reads of unchanged rows agree. What is
        // promised is that determinism and nothing about the particular sequence — see the port.
        var custody = await dbContext.Users
            .AsNoTracking()
            .AsSingleQuery()
            .Where(user => user.Id == userId)
            .Select(user => new
            {
                // THE SAME OWNER, NAMED AGAIN, AND IT IS NOT REDUNDANT HERE EITHER. factor_manifests
                // is policed by user_isolation on user_id — the same policy over the same column as
                // the table beside it — so a lateral naming no owner would still never reach another
                // account's manifest. It is written for the reason the factors predicate is written:
                // the policy makes a wrong query answer EMPTY rather than correct, and the copy in
                // the statement is the one that survives a policy missed on a table added later. The
                // app role holds SELECT on this table and no write privilege of any shape.
                Manifest = dbContext.FactorManifests
                    .Where(row => row.UserId == userId)
                    .Select(row => new { row.Manifest, row.RotationEpoch })
                    .FirstOrDefault(),
                Factors = dbContext.WrappedAccountKeys
                    .Where(keys => keys.UserId == userId)
                    .OrderBy(keys => keys.FactorId)
                    .Select(keys => new FactorEnvelopes(
                        keys.FactorId,
                        keys.WrappedPrivateKey,
                        keys.EncapsulatedAccountKeys))
                    .ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Null at zero rather than an empty buffer at zero: FactorManifest.MinimumRotationEpoch is 1
        // and the column refuses anything below it, so 0 cannot collide with a stored generation, and
        // FactorManifest.For refuses an empty manifest, so an empty buffer would spend a spelling the
        // domain has declared impossible on the one state that is normal. AccountKeyCustody argues both.
        //
        // THE ABSENT MANIFEST IS SPELLED WithNoManifest AND NEVER WITH A CONDITIONAL.
        // `custody.Manifest is null ? null : custody.Manifest.Manifest` looks identical to a ?. and is
        // not: ReadOnlyMemory<byte> carries an implicit conversion from byte[], so the null literal
        // converts THROUGH it and the conditional's natural type becomes the non-nullable memory.
        // Lifted, that is HasValue = true over ZERO bytes — the empty buffer the paragraph above
        // refuses — and it compiles with no warning. Measured, not reasoned: written that way, the two
        // absent-manifest cases fail on a manifest of "" rather than null. The factory removes the
        // second half of the same hazard, the literal 0 that used to be written out here twice: it is
        // the only thing that decides what epoch an absent manifest answers, and the record's guard
        // refuses the pairing rather than leaving it to whoever edits these lines next.
        if (custody is null)
        {
            return AccountKeyCustody.WithNoManifest([]);
        }

        // Read into a local first: the null test and the two reads then run against one value the
        // compiler can track, rather than three accesses to a property it may re-evaluate.
        var manifest = custody.Manifest;

        return manifest is null
            ? AccountKeyCustody.WithNoManifest(custody.Factors)
            : new AccountKeyCustody(manifest.Manifest, manifest.RotationEpoch, custody.Factors);
    }
}
