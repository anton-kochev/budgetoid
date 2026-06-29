using Application.Accounts;
using Application.Accounts.CreateAccount;
using Application.Accounts.DeleteAccount;
using Application.Accounts.GetAccounts;
using Application.Accounts.UpdateAccount;
using Domain.Accounts;

namespace Api.Endpoints;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
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
