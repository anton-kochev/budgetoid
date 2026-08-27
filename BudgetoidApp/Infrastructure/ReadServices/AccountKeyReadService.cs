using Application.AccountKeys;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class AccountKeyReadService(BudgetoidDbContext dbContext) : IAccountKeyReadService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<FactorEnvelopes>> ListForAccountAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        // THE SELECT IS THE MECHANISM AND AsNoTracking IS A SIGNAL. A Select into FactorEnvelopes — a
        // type that is not an entity — is already an untracked query, so the AsNoTracking call below
        // changes nothing about this statement and would change nothing if it were deleted. It is kept
        // as a statement of intent beside the rule it serves, and NOT as the thing that enforces it:
        // what enforces it is that no line here reads the entity set into memory.
        //
        // On this table that rule is a hazard rather than a preference. The application role holds NO
        // UPDATE and NO DELETE on wrapped_account_keys, so a row this context has materialized and later
        // decides to cascade into dies with 42501. That is the never-materialise rule
        // GenerateRecoveryCodesHandler carries, and IAccountKeyReadService's own remarks say the
        // projection is what keeps a display read from being what trips it. NOTHING GOES RED IF THIS IS
        // GOT WRONG HERE: a read-only request has no cascade to walk, so the failure surfaces on
        // whichever later request removes a credential through this context.
        //
        // ONE PREDICATE, ON THE OWNER, AND IT IS UNOBSERVABLE — no test can hold it.
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
        // Ordered by the primary key, which cannot tie, so two reads of unchanged rows agree. What is
        // promised is that determinism and nothing about the particular sequence — see the port.
        //
        // A list, never SingleOrDefault: a passkey files one row here and a set of recovery codes
        // files ten, so an ordinary account is eleven.
        await dbContext.WrappedAccountKeys
            .AsNoTracking()
            .Where(keys => keys.UserId == userId)
            .OrderBy(keys => keys.FactorId)
            .Select(keys => new FactorEnvelopes(
                keys.FactorId,
                keys.WrappedContentKey,
                keys.WrappedIndexKey))
            .ToListAsync(cancellationToken);
}
