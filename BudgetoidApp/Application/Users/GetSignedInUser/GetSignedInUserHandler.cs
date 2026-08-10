using Application.Abstractions;

namespace Application.Users.GetSignedInUser;

/// <summary>
/// Answers the signed-in account's own email address.
/// </summary>
/// <remarks>
/// <para>
/// <b>It takes no <c>ILogger</c> and no <c>ILoggerFactory</c>, and must never take one.</b> The value
/// it holds is an email address, and a single log line of it copies a person's identifier into a sink
/// with a different retention policy and a different audience from the database it came from. No gate
/// anywhere reads a log line from this path, so there is nothing to trade against.
/// </para>
/// <para>
/// A <see langword="null" /> address is not a state the product can be left in <em>at rest</em>: the
/// account, its first credential and its default budget go in one save, and <c>users</c> →
/// <c>credentials</c> cascades on delete, so a live credential standing over a missing user row is not
/// a shape the schema holds. What produces one here is <b>read skew</b> rather than a stored state.
/// <c>UserProvisioningMiddleware</c> resolves the identity out of <c>credentials</c> and then reads
/// <c>budgets</c>, and only then does this handler read <c>users</c> — three round trips sharing no
/// transaction with each other, so an erasure committing inside that window leaves the earlier reads
/// valid and this one empty. It throws, the same policy <c>ExportDataHandler</c> states, and the caller
/// sees the 500 <c>GlobalExceptionHandler</c> writes rather than a defensive branch or <b>a 404</b>:
/// the account genuinely no longer exists, the request is already authenticated <em>as that
/// account</em>, and <c>ResolveUserHandler</c> answers the same race one step earlier the same way.
/// Answering "no such account" would file it as an ordinary missing resource — the one shape nobody
/// investigates — while the honest reading is that the request was authenticated against a row that
/// has since been erased.
/// </para>
/// </remarks>
public sealed class GetSignedInUserHandler(
    IUserContext userContext,
    IUserAccountReadService readService)
    : IQueryHandler<GetSignedInUserQuery, SignedInUser>
{
    public async Task<SignedInUser> HandleAsync(
        GetSignedInUserQuery query,
        CancellationToken cancellationToken = default)
    {
        Guid userId = userContext.UserId;

        string email = await readService.FindEmailAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException(
                "The resolved identity for this request answers to no user row.");

        return new SignedInUser(email);
    }
}
