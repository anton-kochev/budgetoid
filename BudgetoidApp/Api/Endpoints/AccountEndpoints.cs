using Api.Infrastructure;
using Application.Accounts;
using Application.Accounts.CreateAccount;
using Application.Accounts.DeleteAccount;
using Application.Accounts.GetAccount;
using Application.Accounts.GetAccounts;
using Application.Accounts.UpdateAccount;
using Domain.Accounts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Api.Endpoints;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // NOTHING ON THIS GROUP MAY BRING AN ACCOUNT INTO EXISTENCE. RegisterAccountHandler is the
        // only code in the application that creates one, User.CreateWithId is the only factory it can
        // reach, and both are behind POST /api/registration/registration — a route that needs the
        // provider scheme, a verified attestation, and a challenge drawn from the AccountRegistration
        // pool. There is no metadata a group could gain that would put minting back here.
        //
        // That is a property of what is written, not one the compiler holds. User.CreateWithId takes a
        // plain Guid, so a second creating path is one line and nothing would redden. What the deleted
        // minting factory buys is that such a path has to name the identifier it invents, in the diff a
        // reviewer reads — see the remarks on User.CreateWithId, which spell the same correction.
        //
        // Six groups used to carry a ProvisionsUser marker so that a first authenticated request could
        // mint. It is gone with the middleware that read it: the session cookie is issued only over a
        // row, so an authenticated request naming an account that does not exist is not a state this
        // product can reach.
        RouteGroupBuilder group = endpoints.MapGroup("/api/accounts");

        group.MapPost("/", async (
            CreateAccountCommand command,
            CreateAccountHandler handler,
            CancellationToken cancellationToken) =>
        {
            AccountDto dto = await handler.HandleAsync(command, cancellationToken);
            return TypedResults.Created($"/api/accounts/{dto.Id}", dto);
        });

        group.MapGet("/", async (GetAccountsHandler handler, CancellationToken cancellationToken) =>
        {
            AccountListResponse response = await handler.HandleAsync(new GetAccountsQuery(), cancellationToken);
            return TypedResults.Ok(response);
        });

        group.MapGet("/{id:guid}", async Task<Results<Ok<AccountDto>, NotFound>> (
            Guid id,
            GetAccountHandler handler,
            CancellationToken cancellationToken) =>
        {
            AccountDto? dto = await handler.HandleAsync(new GetAccountQuery(id), cancellationToken);
            return dto is null ? TypedResults.NotFound() : TypedResults.Ok(dto);
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateAccountRequest request,
            UpdateAccountHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(
                new UpdateAccountCommand(
                    id,
                    request.Name,
                    request.NameKey,
                    request.Type,
                    request.OpeningBalance),
                cancellationToken);
            return TypedResults.NoContent();
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DeleteAccountHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(new DeleteAccountCommand(id), cancellationToken);
            return TypedResults.NoContent();
        });

        return endpoints;
    }

    /// <summary>
    /// The body of <c>PUT /api/accounts/{id}</c> — the command without the identifier, which the route
    /// carries.
    /// </summary>
    /// <param name="Name">The re-sealed name as unpadded base64url.</param>
    /// <param name="NameKey">The blind index over the same name as unpadded base64url.</param>
    /// <param name="Type">The kind of account.</param>
    /// <param name="OpeningBalance">The balance the ledger starts from.</param>
    /// <remarks>
    /// <b>The route parameter stays <c>{id:guid}</c> and gets no canonical check, unlike the id in the
    /// create body.</b> On an update the client re-seals against the row's existing identifier, which it
    /// read back from this API in the one spelling <see cref="Guid"/> renders; the text in the URL is
    /// never the text anything was sealed under, so there is no spelling here to preserve. The rule lives
    /// where an identifier is <em>chosen</em>, and that is <c>POST /api/accounts</c> alone.
    /// </remarks>
    private sealed record UpdateAccountRequest(
        string Name,
        string NameKey,
        AccountType Type,
        decimal OpeningBalance);
}
