using System.Text.Json.Serialization;
using Api.Infrastructure;
using Application.CategoryGroups;
using Application.CategoryGroups.CreateCategoryGroup;
using Application.CategoryGroups.DeleteCategoryGroup;
using Application.CategoryGroups.GetCategoryGroup;
using Application.CategoryGroups.GetCategoryGroups;
using Application.CategoryGroups.MoveCategoryGroup;
using Application.CategoryGroups.UpdateCategoryGroup;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Api.Endpoints;

public static class CategoryGroupEndpoints
{
    public static IEndpointRouteBuilder MapCategoryGroupEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        // Brings no account into existence, and cannot: RegisterAccountHandler is the only code that
        // does, behind /api/registration. AccountEndpoints carries the argument.
        RouteGroupBuilder group = endpoints.MapGroup("/api/category-groups");

        group.MapPost("/", async (
            CreateCategoryGroupCommand command,
            CreateCategoryGroupHandler handler,
            CancellationToken cancellationToken) =>
        {
            CategoryGroupDto dto = await handler.HandleAsync(command, cancellationToken);
            return TypedResults.Created($"/api/category-groups/{dto.Id}", dto);
        });

        group.MapGet("/", async (
            GetCategoryGroupsHandler handler,
            CancellationToken cancellationToken) =>
        {
            CategoryGroupListResponse response = await handler.HandleAsync(
                new GetCategoryGroupsQuery(),
                cancellationToken);
            return TypedResults.Ok(response);
        });

        group.MapGet("/{id:guid}", async Task<Results<Ok<CategoryGroupDto>, NotFound>> (
            Guid id,
            GetCategoryGroupHandler handler,
            CancellationToken cancellationToken) =>
        {
            CategoryGroupDto? dto = await handler.HandleAsync(
                new GetCategoryGroupQuery(id),
                cancellationToken);
            return dto is null ? TypedResults.NotFound() : TypedResults.Ok(dto);
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateCategoryGroupRequest request,
            UpdateCategoryGroupHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(
                new UpdateCategoryGroupCommand(
                    id, request.Name, request.NameKey, request.Description),
                cancellationToken);
            return TypedResults.NoContent();
        });

        group.MapPatch("/{id:guid}/position", async (
            Guid id,
            MoveCategoryGroupRequest request,
            MoveCategoryGroupHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(
                new MoveCategoryGroupCommand(id, request.Position),
                cancellationToken);
            return TypedResults.NoContent();
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DeleteCategoryGroupHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(new DeleteCategoryGroupCommand(id), cancellationToken);
            return TypedResults.NoContent();
        });

        return endpoints;
    }

    // The body of the PUT, and one of the two request bodies this file declares: MoveCategoryGroupRequest
    // is the other, two lines down. Api-ring request bodies are ordinary here — RenamePayeeRequest,
    // UpdateAccountRequest, UpdateCategoryRequest, PlaceCategoryRequest and UpdateTransactionRequest are
    // all declared in their own endpoint files. What is true of this one is narrower: it is the only shape
    // in this slice that Application does not own, because the other five routes bind a command directly.
    //
    // NameKey is required beside Name, because a group's name is two columns computed from one piece of
    // text by one client — see CategoryGroup.Update for what a half-written name costs. Description is
    // the only optional member, and a PUT with none CLEARS the note the group held; `null` and an absent
    // member mean the same thing, while `""` is a malformed envelope and a 400.
    //
    // [JsonUnmappedMemberHandling(Disallow)] IS HERE, AND NOT FOR THE REASON THE TRANSACTION SHAPES CARRY
    // IT. There the attribute answers wire DRIFT: CreateTransactionCommand and UpdateTransactionRequest
    // each RETIRED payeeName, a member a released client still sends, and refusing it visibly beat
    // dropping it in silence. Nothing on this shape is retired — every member is new — so that argument
    // does not reach here and must not be copied onto this line.
    //
    // What earns it here is Description, which binds a NULLABLE narrative column. Measured against this
    // shape under JsonSerializerDefaults.Web with the options Api/Program.cs registers, whose
    // UnmappedMemberHandling is the default Skip:
    //   {"name":…,"nameKey":…,"descriptionn":"AQIDBA"}  -> Description is null
    //   {"name":…,"nameKey":…}                          -> Description is null
    //   {"name":…,"nameKey":…,"description":"AQIDBA"}   -> Description = "AQIDBA"
    // A misspelled member binds IDENTICALLY to no member at all. On this route that is a 204 and a note
    // the caller never asked to remove; on POST it is a 201 and a note that never arrives.
    //
    // NOTHING DOWNSTREAM CAN TELL THAT FROM AN OPERATION, which is what makes it worth a contract change
    // rather than a lint. A group with no description is a legal row and NULL is how it says so, so the
    // write succeeds, both description CHECKs pass vacuously, and every later read agrees the group has no
    // note. Clearing a description is a real thing this route does, so no status, no constraint and no
    // reader can separate "the caller asked" from "the caller typed the member wrong" — which is the exact
    // hazard the null-versus-"" care above this line exists to close, reached by a path outside every
    // member it names. UpdateCategoryGroupCommand's remarks argue the null/"" half.
    //
    // PER TYPE, AND THE TWO IT REACHES ARE THIS SHAPE AND CreateCategoryGroupCommand — the two that bind
    // description. Do not widen it: no UnmappedMemberHandling belongs in Api/Program.cs, whose options
    // every route in the product shares, and neither MoveCategoryGroupRequest below nor the category
    // shapes get it for company. Whether a shape refuses what it was not asked for is a contract decision
    // that shape makes for itself, and the shapes that bind no nullable narrative member have not made it.
    //
    // What it costs, stated rather than left to be discovered: any client sending an unknown member to
    // these two routes now gets a 400, dressed as application/problem+json by this product's pipeline,
    // with the unmappable member named in the JsonException that reaches the server log and not the
    // response. Nothing sends to them today — the Angular category-group service still posts a plaintext
    // name, so that screen already cannot write, exactly as /app/accounts and the transaction form cannot.
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record UpdateCategoryGroupRequest(
        string Name,
        string NameKey,
        string? Description);

    private sealed record MoveCategoryGroupRequest(int Position);
}
