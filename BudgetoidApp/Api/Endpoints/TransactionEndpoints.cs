using Application.Transactions;
using Application.Transactions.CreateTransaction;
using Application.Transactions.DeleteTransaction;
using Application.Transactions.GetTransaction;
using Application.Transactions.GetTransactions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Api.Endpoints;

public static class TransactionEndpoints
{
    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/transactions");

        group.MapPost("/", async (
            CreateTransactionCommand command,
            CreateTransactionHandler handler,
            CancellationToken cancellationToken) =>
        {
            TransactionDto dto = await handler.HandleAsync(command, cancellationToken);
            return TypedResults.Created($"/api/transactions/{dto.Id}", dto);
        });

        group.MapGet("/", async (GetTransactionsHandler handler, CancellationToken cancellationToken) =>
        {
            TransactionListResponse response = await handler.HandleAsync(new GetTransactionsQuery(), cancellationToken);
            return TypedResults.Ok(response);
        });

        group.MapGet("/{id:guid}", async Task<Results<Ok<TransactionDto>, NotFound>> (
            Guid id,
            GetTransactionHandler handler,
            CancellationToken cancellationToken) =>
        {
            TransactionDto? dto = await handler.HandleAsync(
                new GetTransactionQuery(id),
                cancellationToken);
            return dto is null ? TypedResults.NotFound() : TypedResults.Ok(dto);
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DeleteTransactionHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(new DeleteTransactionCommand(id), cancellationToken);
            return TypedResults.NoContent();
        });

        return endpoints;
    }
}
