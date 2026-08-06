using System.Text.Json;
using Application.Passkeys.BeginAssertion;
using Application.Passkeys.BeginRegistration;
using Application.Passkeys.CompleteAssertion;
using Application.Passkeys.CompleteRegistration;
using Application.Passkeys.Reauthentication;

namespace Api.Endpoints;

public static class PasskeyEndpoints
{
    public static IEndpointRouteBuilder MapPasskeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Two groups over one path prefix rather than one group with per-endpoint attributes, so the
        // anonymous surface of the whole application is a single line a reviewer can see. Anything
        // added to the first group inherits the application's fallback policy and stays authenticated;
        // anything added to the second is public, and has to be argued for where it is written.
        RouteGroupBuilder authenticated = endpoints.MapGroup("/api/passkeys");

        // POST, not GET, for both options legs. They mint a nonce and persist it, so they are neither
        // safe nor idempotent, and a GET would be cacheable and prefetchable — both of which spend
        // challenges nobody asked for.
        authenticated.MapPost("/registration/options", async (
            BeginRegistrationHandler handler,
            CancellationToken cancellationToken) =>
        {
            PasskeyCreationOptions options = await handler.HandleAsync(
                new BeginRegistrationCommand(),
                cancellationToken);
            return TypedResults.Ok(options);
        });

        authenticated.MapPost("/registration", async (
            RegistrationRequest request,
            CompleteRegistrationHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(
                new CompleteRegistrationCommand(
                    request.ClientDataJson,
                    request.AttestationObject,
                    request.ClientExtensionResults),
                cancellationToken);

            // 201 with no Location header and no body: the credential is a fact about the account,
            // not a resource this API exposes at an address. Nothing may read a stored passkey back,
            // so there is nowhere to point at — and nothing to report either, since the only thing
            // left to say is what the client told the server about its own device, and echoing that
            // back would read as the server having established it.
            return TypedResults.Created();
        });

        // The re-authentication options leg, and it belongs in THIS group. It mints the one nonce that
        // authorizes destroying an account, so an anonymous caller able to obtain one would make the
        // erasure endpoint's refusal of the other two pools worth nothing. There is no finish leg here:
        // the ceremony is completed by POST /api/me/erasure, which is the action it authorizes.
        authenticated.MapPost("/reauthentication/options", async (
            BeginReauthenticationHandler handler,
            CancellationToken cancellationToken) =>
        {
            PasskeyRequestOptions options = await handler.HandleAsync(
                new BeginReauthenticationCommand(),
                cancellationToken);
            return TypedResults.Ok(options);
        });

        // The sign-in legs. Anonymous on the group, because sign-in is the one exchange that by
        // definition runs before anyone is signed in. UserProvisioningMiddleware needs no exclusion
        // list for this — it no-ops on an unauthenticated principal, which is exactly the state the
        // discovery read needs, and an exclusion list would be a second place the anonymous surface is
        // defined.
        RouteGroupBuilder anonymous = endpoints.MapGroup("/api/passkeys").AllowAnonymous();

        anonymous.MapPost("/assertion/options", async (
            BeginAssertionHandler handler,
            CancellationToken cancellationToken) =>
        {
            PasskeyRequestOptions options = await handler.HandleAsync(
                new BeginAssertionCommand(),
                cancellationToken);
            return TypedResults.Ok(options);
        });

        anonymous.MapPost("/assertion", async (
            AssertionRequest request,
            CompleteAssertionHandler handler,
            CancellationToken cancellationToken) =>
        {
            EstablishedSession session = await handler.HandleAsync(
                new CompleteAssertionCommand(
                    request.CredentialId,
                    request.ClientDataJson,
                    request.AuthenticatorData,
                    request.Signature,
                    request.UserHandle),
                cancellationToken);

            // The kind and the expiry, and deliberately no session id. The body has to say something
            // the behaviour can be observed through, and the kind is exactly the fact that matters —
            // how much of the account this sign-in reaches. Returning the row's id would hand the
            // client a stable handle to a session, and the most likely way this design is broken later
            // is somebody deciding that handle is close enough to a token to start accepting it. Do
            // not add it.
            return TypedResults.Ok(new AssertionResponse(
                JsonNamingPolicy.CamelCase.ConvertName(session.Kind.ToString()),
                session.ExpiresAtUtc));
        });

        return endpoints;
    }

    private sealed record RegistrationRequest(
        string ClientDataJson,
        string AttestationObject,
        PasskeyClientExtensionResults? ClientExtensionResults);

    private sealed record AssertionRequest(
        string CredentialId,
        string ClientDataJson,
        string AuthenticatorData,
        string Signature,
        string? UserHandle);

    private sealed record AssertionResponse(string Kind, DateTime ExpiresAtUtc);
}
