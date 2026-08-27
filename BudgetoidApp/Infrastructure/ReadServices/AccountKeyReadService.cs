using Application.AccountKeys;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class AccountKeyReadService(BudgetoidDbContext dbContext) : IAccountKeyReadService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<FactorEnvelopes>> ListForCredentialAsync(
        Guid userId,
        Guid credentialId,
        CancellationToken cancellationToken = default) =>
        // A Select straight into FactorEnvelopes over AsNoTracking, never a read of the entity set
        // followed by a mapping step — and on this table that is a hazard rather than a preference.
        // The application role holds NO UPDATE and NO DELETE on wrapped_account_keys, so a row this
        // context has materialized and later decides to cascade into dies with 42501. That is the
        // never-materialise rule GenerateRecoveryCodesHandler carries, and IAccountKeyReadService's
        // own remarks say this is the shape that keeps a display read from being what trips it.
        // NOTHING GOES RED IF THIS IS GOT WRONG HERE: a read-only request has no cascade to walk, so
        // the failure surfaces on whichever later request removes a credential through this context.
        //
        // Both halves of the predicate are named, and the owner half is UNOBSERVABLE — no test can
        // hold it. wrapped_account_keys is policed by user_isolation, so PostgreSQL appends
        // user_id = current_setting('app.current_user_id') underneath this statement and a read
        // filtered on credential_id alone answers EMPTY rather than WRONG. It is written anyway for
        // the reason ExportReadService gives about budgets: the policy makes a wrong query answer
        // empty, not correct, and this second copy is the one that survives a policy missed on a
        // table added later. Do not delete it as redundant. It is also not a cost paid for defence in
        // depth — credential_id then user_id are the leading columns of
        // IX_wrapped_account_keys_credential_id_user_id_credential_type, so the pair is the index seek.
        //
        // Ordered by the primary key, which cannot tie, so two reads of unchanged rows agree. What is
        // promised is that determinism and nothing about the particular sequence — see the port.
        //
        // A list, never SingleOrDefault: a passkey files one row here and a set of recovery codes
        // files ten, one per code.
        await dbContext.WrappedAccountKeys
            .AsNoTracking()
            .Where(keys => keys.CredentialId == credentialId && keys.UserId == userId)
            .OrderBy(keys => keys.FactorId)
            .Select(keys => new FactorEnvelopes(
                keys.FactorId,
                keys.WrappedContentKey,
                keys.WrappedIndexKey))
            .ToListAsync(cancellationToken);
}
