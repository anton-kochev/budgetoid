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
/// A <see langword="null" /> address on a resolved identity is a broken invariant rather than a state
/// the product can produce — the account, its first credential and its default budget go in one save,
/// and <c>users</c> → <c>credentials</c> cascades on delete, so a live credential over a missing user
/// row cannot exist — so it throws, the same policy <c>ExportDataHandler</c> states. The caller sees
/// the 500 <c>GlobalExceptionHandler</c> writes, and <b>not a 404</b>: answering "no such account" to
/// a request the pipeline has just authenticated <em>as that account</em> would report a broken
/// invariant as an ordinary missing resource, which reads as a state the product produces and is the
/// one shape nobody investigates.
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
