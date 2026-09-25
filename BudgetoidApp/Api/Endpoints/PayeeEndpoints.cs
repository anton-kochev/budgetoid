using Api.Infrastructure;
using Application.Payees;
using Application.Payees.CreatePayee;
using Application.Payees.GetPayee;
using Application.Payees.GetPayees;
using Application.Payees.RenamePayee;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Api.Endpoints;

public static class PayeeEndpoints
{
    public static IEndpointRouteBuilder MapPayeeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Brings no account into existence, and cannot: RegisterAccountHandler is the only code that
        // does, behind /api/registration. AccountEndpoints carries the argument.
        //
        // THAT SENTENCE IS ABOUT ACCOUNTS AND STAYS TRUE, but the group below it does create something:
        // POST / creates a payee, and it is the first payee-creating route this product has ever had.
        // It reverses a rule the docs argue at length, so the reason belongs here rather
        // than only there: payees used to exist solely as a side effect of writing a transaction, found
        // or created by name on the server. The server can no longer look a name up. payees.name is an
        // AEAD envelope drawn under a fresh nonce every time, so two seals of one name are different
        // bytes; the digest that IS stable is taken under the account's index key, which lives in a
        // browser; and the case folding the old lookup leant on left with the column's case_insensitive
        // collation, because bytea is not collatable. Creation therefore has to be asked for.
        //
        // What survives and what is lost, so neither is re-derived later. What survives is that the client
        // resolves the counterparty against the list it already holds and can decrypt, so one counterparty
        // is still one row, now enforced by IX_payees_budget_id_name_key rather than by a re-read. What is
        // lost is that a payee row implied a transaction: a create here followed by a failed
        // POST /api/transactions leaves a payee nothing names, on a table the app role holds no DELETE on.
        // That orphan is accepted — see docs/business-logic/payees.md for why every alternative is worse.
        RouteGroupBuilder group = endpoints.MapGroup("/api/payees");

        group.MapPost("/", async (
            CreatePayeeCommand command,
            CreatePayeeHandler handler,
            CancellationToken cancellationToken) =>
        {
            PayeeDto dto = await handler.HandleAsync(command, cancellationToken);

            // The Location is why GET /{id:guid} below exists at all: without it the header would name an
            // address answering 404. dto.Id is the Guid this API renders, not the text the caller sent —
            // the handler has already refused every spelling that would differ.
            return TypedResults.Created($"/api/payees/{dto.Id}", dto);
        });

        group.MapGet("/", async (GetPayeesHandler handler, CancellationToken cancellationToken) =>
        {
            PayeeListResponse response = await handler.HandleAsync(new GetPayeesQuery(), cancellationToken);
            return TypedResults.Ok(response);
        });

        group.MapGet("/{id:guid}", async Task<Results<Ok<PayeeDto>, NotFound>> (
            Guid id,
            GetPayeeHandler handler,
            CancellationToken cancellationToken) =>
        {
            PayeeDto? dto = await handler.HandleAsync(new GetPayeeQuery(id), cancellationToken);
            return dto is null ? TypedResults.NotFound() : TypedResults.Ok(dto);
        });

        group.MapPatch("/{id:guid}", async (
            Guid id,
            RenamePayeeRequest request,
            RenamePayeeHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(
                new RenamePayeeCommand(id, request.Name, request.NameKey),
                cancellationToken);
            return TypedResults.NoContent();
        });

        return endpoints;
    }

    /// <summary>
    /// The body of <c>PATCH /api/payees/{id}</c> — the command without the identifier, which the route
    /// carries.
    /// </summary>
    /// <param name="Name">The re-sealed name as unpadded base64url.</param>
    /// <param name="NameKey">The blind index over the same name as unpadded base64url.</param>
    /// <remarks>
    /// <para>
    /// <b>Both members are required rather than <c>Optional&lt;string&gt;</c>, and there are two of them
    /// for one field.</b> A payee has one mutable thing about it, so a body omitting either half is a
    /// malformed request and not a no-op the caller could have meant — and a body carrying only
    /// <paramref name="Name"/> is worse than malformed: it is the half-rename
    /// <see cref="Domain.Security.IndexedName"/> exists to make unspellable, arriving as a shape the
    /// binder would have accepted.
    /// </para>
    /// <para>
    /// <b>The route parameter stays <c>{id:guid}</c> and gets no canonical check, unlike the id in the
    /// create body.</b> On a rename the client re-seals against the row's existing identifier, which it
    /// read back from this API in the one spelling <see cref="Guid"/> renders; the text in the URL is
    /// never the text anything was sealed under, so there is no spelling here to preserve.
    /// </para>
    /// </remarks>
    private sealed record RenamePayeeRequest(string Name, string NameKey);
}
