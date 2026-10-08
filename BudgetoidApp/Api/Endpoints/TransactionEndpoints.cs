using System.Text.Json.Serialization;
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
        // Brings no account into existence, and cannot: RegisterAccountHandler is the only code that
        // does, behind /api/registration. AccountEndpoints carries the argument.
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
                    request.PayeeId,
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
    //
    // THE Disallow BELOW IS WHAT MAKES A BODY STILL CARRYING payeeName AUDIBLE. This member was
    // Optional<string?> PayeeName until the payee became a row the caller creates for itself, and with
    // nothing declared System.Text.Json drops a property matching no parameter without a word: the
    // released Angular client sends exactly the old shape, so its POSTs answered 201 and its PATCHes
    // 204 while attaching no payee at all — a person's counterparty lost with nothing on either side
    // seeing it. The same body now answers 400 — measured, and it arrives as application/problem+json
    // because this product's pipeline dresses the bare 400 the binder writes. The member it could not
    // map is named in the JsonException, so it reaches the server log and not the response; that is
    // the right way round, since the audience for this refusal is whoever wires the client.
    //
    // The attribute is per-type: its blast radius is the shape it sits on and no other, so the DRIFT
    // argument above covers exactly two shapes — this record and CreateTransactionCommand, which the
    // create route binds a body straight onto. Those are the two that carried payeeName. Do not widen
    // it: no UnmappedMemberHandling belongs in Api/Program.cs, whose options every route in the product
    // shares.
    //
    // THIS IS NOT A CENSUS OF THE ATTRIBUTE, and reading it as one is the mistake to avoid. Four other
    // shapes declare it — the category-group pair and the category pair — and they earn it a DIFFERENT
    // way: each binds a nullable narrative column, where a misspelled member binds identically to an
    // absent one, so a PUT answers 204 having cleared a note nobody asked to remove. Those files argue
    // that at their own shapes. It does not reach this pair and must not be added here for symmetry:
    // Optional<T> makes an absent member mean "leave this alone", so a member dropped here loses an
    // edit rather than performing one — measured, not assumed. Whether a shape refuses what it was not
    // asked for stays a contract decision that shape makes for itself.
    //
    // What it costs, stated rather than left to be discovered: the transaction form stops working
    // until the client is wired to POST /api/payees and send payeeId, because every write it makes now
    // answers 400. That is chosen, not overlooked — /app/accounts is already in the same state, unable
    // to create or rename since its name became an envelope. Losing the counterparty invisibly is
    // worse than failing visibly, which is the polarity this repository takes everywhere else: the
    // mistake that is audible beats the one that is not.
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record UpdateTransactionRequest(
        Optional<decimal> Amount,
        Optional<DateOnly> Date,
        Optional<Guid> AccountId,
        Optional<string?> Description,
        Optional<Guid?> PayeeId,
        Optional<Guid?> CategoryId);
}
