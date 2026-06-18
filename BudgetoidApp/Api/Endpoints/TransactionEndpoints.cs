using Application.Transactions;
using Application.Transactions.CreateTransaction;
using Application.Transactions.GetTransactions;

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

        return endpoints;
    }
}
