using Api.Infrastructure;
using Application.Erasure.ScheduleErasure;
using Application.Passkeys.Reauthentication;
using Application.Users.EraseAccount;

namespace Api.Endpoints;

public static class AccountErasureEndpoints
{
    public static IEndpointRouteBuilder MapAccountErasureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // "/api/me" rather than "/api/account": account is already this codebase's word for
        // Domain.Accounts.Account, served at /api/accounts/{id} with the same verb and the same
        // status, and a client aiming at the wrong one would be indistinguishable in the logs.
        RouteGroupBuilder group = endpoints.MapGroup("/api/me");

        // POST to a named sub-resource rather than DELETE on the group, and the old DELETE /api/me is
        // removed rather than kept beside it: leaving it routed would leave the token-only erasure path
        // open, and every rule below would describe a door standing next to an open one. The verb is
        // POST because the request carries a body that has to be verified — a DELETE with a proof in
        // its body is a shape intermediaries are free to strip.
        //
        // Still no id in the route, and the body still names no account: the account erased is
        // whichever one the request is authenticated as, and the assertion in the body names a
        // credential handle that the owner-scoped lookup makes incapable of selecting anybody.
        // Authentication comes from the fallback policy, so this endpoint declares no metadata of its
        // own and must never declare AllowAnonymous.
        group.MapPost("/erasure", async (
            ErasureRequest request,
            EraseAccountHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(
                new EraseAccountCommand(new ReauthenticationAssertion(
                    request.CredentialId,
                    request.ClientDataJson,
                    request.AuthenticatorData,
                    request.Signature,
                    request.UserHandle)),
                cancellationToken);

            // 204: the post-condition is stated, and there is nothing left to describe. A body would
            // have to be assembled from an account that no longer exists.
            return TypedResults.NoContent();
        });

        // The release valve: a locked session files the account's erasure for seven days out, and that is
        // the one act it may perform. Under the erasure resource on purpose — it is the same erasure,
        // deferred — and ErasureIrreversibilityTests pins the resource's two routes as an exact list
        // (ErasureResource_MapsExactlyTheDestructiveRouteAndItsSchedule).
        //
        // No body and no id, for the immediate erasure's reason: the account scheduled is whichever one
        // the request is authenticated as. No passkey either, and that is not a gap: the caller is
        // somebody who has none left, which is why the erasure waits instead of happening.
        //
        // Both markers, and they are read by two requirements on the same fallback policy. The first lets
        // a locked session past FullSessionRequirement; the second refuses a full one, which has the
        // better door above and must not file an erasure no assertion authorized. Neither alone is the
        // rule: the second without the first refuses every session there is. The cookie is not touched
        // — scheduling ends nothing and begins nothing.
        group.MapPost("/erasure/schedule", async (
            ScheduleErasureHandler handler,
            CancellationToken cancellationToken) =>
        {
            ScheduledErasure scheduled = await handler.HandleAsync(new ScheduleErasureCommand(), cancellationToken);

            // 200 with the instant, the same one on every repeat: the person is told when the account
            // goes, and a second press tells them the same date rather than a later one.
            return TypedResults.Ok(new ScheduledErasureResponse(scheduled.TakesEffectAtUtc));
        })
            .WithMetadata(new AllowsLockedSessionAttribute())
            .WithMetadata(new RequiresLockedSessionAttribute());

        return endpoints;
    }

    /// <summary>
    /// When the account's scheduled erasure takes effect, and nothing else about the account.
    /// </summary>
    /// <param name="TakesEffectAtUtc">The stored instant the erasure takes effect, in UTC.</param>
    private sealed record ScheduledErasureResponse(DateTime TakesEffectAtUtc);

    /// <summary>
    /// The assertion the erasure is authorized by, in the shape the sign-in leg's own request record
    /// already uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The members are not <c>required</c>, deliberately and identically to that leg: every member
    /// bound to <see langword="null" /> — a body of <c>{}</c>, or one naming only some of them —
    /// reaches the gate's own decode, which answers the same 401 every other refusal on this endpoint
    /// answers. Marking them required would buy a framework 400 that tells a caller holding a stolen
    /// bearer token that its proof was the thing found wanting.
    /// </para>
    /// <para>
    /// That envelope covers what binds, not what fails to. No body at all, a literal <c>null</c>, or a
    /// member of the wrong JSON type is a framework 400 raised before this handler is entered, and the
    /// gap is accepted: a deserialization failure is a fact about the caller's own request and says
    /// nothing about what credentials exist. Closing it would mean a custom model binder on the one
    /// endpoint that must never grow one.
    /// </para>
    /// </remarks>
    private sealed record ErasureRequest(
        string CredentialId,
        string ClientDataJson,
        string AuthenticatorData,
        string Signature,
        string? UserHandle);
}
