using Domain.Sessions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public sealed class SessionTokenRepository(BudgetoidDbContext dbContext) : ISessionTokenRepository
{
    /// <inheritdoc />
    public Task<SessionToken?> FindByTokenHashAsync(
        byte[] tokenHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        // Widened to the type the property carries so the comparison below binds to
        // ReadOnlyMemory<byte>.Equals(ReadOnlyMemory<byte>) rather than to the object overload, which
        // the provider would not translate. A local rather than an inline cast because it has to be
        // the captured value the expression tree reads.
        ReadOnlyMemory<byte> hash = tokenHash;

        // THE DISCOVERY LOOKUP, the shape PasskeyRepository.FindByWebAuthnCredentialIdAsync,
        // RecoveryCodeRepository.FindByVerifierHashAsync and DbWebAuthnChallengeStore.ConsumeAsync all
        // have. It names no owner because there is no owner to name: the request arrives carrying a
        // cookie, on a connection whose app.current_user_id is still '', and whose account it belongs
        // to is what this answer establishes. That is the whole reason session_tokens is exempt from
        // row-level security — a user_isolation policy here would compare against ''::uuid and raise
        // 22P02 on every authenticated request in the product — and it is why this statement may touch
        // no second table. A join to sessions or to users would put a policed relation on the same
        // identity-less connection and fail for exactly that reason, which is also why whether the
        // session is live is read afterwards rather than here.
        //
        // SingleOrDefault rather than FirstOrDefault: token_hash is the PRIMARY KEY, so a second row
        // is not a case to choose between, it is a database that has lost the rule making two sessions
        // sharing a token unstorable.
        //
        // Equals rather than ==, for the reason FindByWebAuthnCredentialIdAsync gives:
        // ReadOnlyMemory<byte> declares no equality operator, and what reaches PostgreSQL through the
        // property's value converter is a bytea comparison of content rather than of buffer identity.
        return dbContext.SessionTokens
            .SingleOrDefaultAsync(
                sessionToken => sessionToken.TokenHash.Equals(hash), cancellationToken);
    }
}
