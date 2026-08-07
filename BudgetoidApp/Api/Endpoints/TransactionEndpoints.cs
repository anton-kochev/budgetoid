using Api.Infrastructure;
using Application.Abstractions;
using Application.Transactions;
using Application.Transactions.CreateTransaction;
using Application.Transactions.DeleteTransaction;
using Application.Transactions.GetTransaction;
using Application.Transactions.GetTransactions;
using Application.Transactions.UpdateTransaction;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Api.Endpoints;

public static class TransactionEndpoints
{
    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // One of the six groups allowed to bring an account into existence; the reason the marker is
        // opt-in and sits on the group is written out in AccountEndpoints.
        RouteGroupBuilder group = endpoints.MapGroup("/api/transactions")
            .WithMetadata(new ProvisionsUserAttribute());

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

        group.MapPatch("/{id:guid}", async (
            Guid id,
            UpdateTransactionRequest request,
            UpdateTransactionHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(
                new UpdateTransactionCommand(
                    id,
                    request.Amount,
                    request.Date,
                    request.AccountId,
                    request.Description,
                    request.PayeeName,
                    request.CategoryId),
                cancellationToken);
            return TypedResults.NoContent();
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

    // Every field is optional in the three-state sense: a property the caller omitted arrives as
    // default(Optional<T>), which is the absent state. The budget, the id and the creation time are
    // not listed because they are not the caller's to rewrite.
    private sealed record UpdateTransactionRequest(
        Optional<decimal> Amount,
        Optional<DateOnly> Date,
        Optional<Guid> AccountId,
        Optional<string?> Description,
        Optional<string?> PayeeName,
        Optional<Guid?> CategoryId);
}
