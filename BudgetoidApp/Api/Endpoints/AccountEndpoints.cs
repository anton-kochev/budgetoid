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
        // The marker that allows an authenticated request on this group to bring an account into
        // existence. Six groups carry it — accounts, transactions, categories, category groups, payees,
        // currencies — and the reason is written out here once, with the other five pointing back at
        // this comment.
        //
        // On the GROUP rather than on each route, so the whole minting surface of the application is six
        // greppable lines a reviewer can hold in their head at once. That is the same argument
        // PasskeyEndpoints already makes for keeping the anonymous surface in one visible place, and it
        // is worth more here: what a route may create is harder to infer from reading its handler than
        // what it may read.
        //
        // Provisioning is opt-in: everything unmarked resolves an account that already exists and is
        // refused if there is none (see UserProvisioningMiddleware). Opt-out would leave a browser
        // holding a still-valid provider token for an erased account able to resurrect it through any
        // route nobody thought to exempt, which is exactly the defect this shape closes.
        //
        // These six are the groups a client's first authenticated request may legitimately be: they are
        // the data surface of a signed-in person's own budget, and the client is free to open on any of
        // them. Currencies is in the list even though its handler reads a reference table and needs no
        // budget — the contract being declared is "a first authenticated request may land here", and a
        // currencies-shaped exception would be a distinction every future reader has to re-derive.
        //
        // What this does NOT fix, stated rather than implied: a client that calls one of these six on
        // app boot still resurrects an erased account for as long as the provider's id token remains
        // valid — up to an hour. That hole is pre-existing, and it closes when registration becomes a
        // consented act and these six markers collapse into one.
        RouteGroupBuilder group = endpoints.MapGroup("/api/accounts")
            .WithMetadata(new ProvisionsUserAttribute());

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
                new UpdateAccountCommand(id, request.Name, request.Type, request.OpeningBalance),
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

    private sealed record UpdateAccountRequest(string Name, AccountType Type, decimal OpeningBalance);
}
