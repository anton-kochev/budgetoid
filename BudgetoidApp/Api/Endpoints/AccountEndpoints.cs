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
        // currencies. Why the permission is opt-in is in ProvisionsUserAttribute; why the set is
        // exactly six, including the one member that reads no tenant data, is in CurrencyEndpoints.
        //
        // On the GROUP rather than on each route, so the whole minting surface of the application is six
        // greppable lines a reviewer can hold in their head at once. That is the same argument
        // PasskeyEndpoints already makes for keeping the anonymous surface in one visible place, and it
        // is worth more here: what a route may create is harder to infer from reading its handler than
        // what it may read.
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
