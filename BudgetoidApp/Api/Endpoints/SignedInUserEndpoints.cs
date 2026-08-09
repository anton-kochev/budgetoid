using Application.Users.GetSignedInUser;

namespace Api.Endpoints;

public static class SignedInUserEndpoints
{
    public static IEndpointRouteBuilder MapSignedInUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // A third group over "/api/me", beside the erasure and export ones, which is the shape
        // PasskeyEndpoints already uses for one prefix and two groups. "/api/me" is the current
        // principal's namespace, and this is the principal describing itself.
        RouteGroupBuilder group = endpoints.MapGroup("/api/me");

        // No ProvisionsUserAttribute, and it must not gain one: a read that minted an account would
        // let a stale provider token — valid for up to an hour after the account it names is erased —
        // bring that account back as an empty shell. An authenticated subject with no account is
        // refused instead.
        //
        // No RequireAuthorization either, because the application's fallback policy already covers
        // every route that declares nothing, and restating it here would stop the one line that
        // defines the anonymous surface from being the only one. And never AllowAnonymous.
        group.MapGet("/", async (
            GetSignedInUserHandler handler,
            CancellationToken cancellationToken) =>
        {
            SignedInUser user = await handler.HandleAsync(
                new GetSignedInUserQuery(),
                cancellationToken);

            // TypedResults.Ok rather than hand-serialized JSON, so camelCase comes from
            // ConfigureHttpJsonOptions like every other response instead of from this call site.
            return TypedResults.Ok(user);
        });

        return endpoints;
    }
}
