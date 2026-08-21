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

        // THIS READ MINTS NOTHING, and after this commit no metadata could make it. Accounts come
        // into existence in RegisterAccountHandler and nowhere else — it is the only caller of
        // User.CreateWithId, which is the only factory the domain offers — and it is reachable only
        // from POST /api/registration/registration, behind the provider scheme, a verified passkey
        // attestation and a challenge drawn from the AccountRegistration pool. The prohibition is a
        // compile error rather than a marker somebody must not add.
        //
        // Worth keeping the reason beside it: a read that minted would let a provider token outliving
        // an erasure by up to an hour bring the account back as an empty shell.
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
