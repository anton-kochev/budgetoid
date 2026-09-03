using System.Text.Json.Serialization;
using Api.Infrastructure;
using Application.Categories;
using Application.Categories.CreateCategory;
using Application.Categories.DeleteCategory;
using Application.Categories.GetCategories;
using Application.Categories.GetCategory;
using Application.Categories.PlaceCategory;
using Application.Categories.UpdateCategory;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Api.Endpoints;

public static class CategoryEndpoints
{
    public static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Brings no account into existence, and cannot: RegisterAccountHandler is the only code that
        // does, behind /api/registration. AccountEndpoints carries the argument.
        RouteGroupBuilder group = endpoints.MapGroup("/api/categories");

        group.MapPost("/", async (
            CreateCategoryCommand command,
            CreateCategoryHandler handler,
            CancellationToken cancellationToken) =>
        {
            CategoryDto dto = await handler.HandleAsync(command, cancellationToken);
            return TypedResults.Created($"/api/categories/{dto.Id}", dto);
        });

        group.MapGet("/", async (
            GetCategoriesHandler handler,
            CancellationToken cancellationToken) =>
        {
            CategoryListResponse response = await handler.HandleAsync(
                new GetCategoriesQuery(),
                cancellationToken);
            return TypedResults.Ok(response);
        });

        group.MapGet("/{id:guid}", async Task<Results<Ok<CategoryDto>, NotFound>> (
            Guid id,
            GetCategoryHandler handler,
            CancellationToken cancellationToken) =>
        {
            CategoryDto? dto = await handler.HandleAsync(
                new GetCategoryQuery(id),
                cancellationToken);
            return dto is null ? TypedResults.NotFound() : TypedResults.Ok(dto);
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateCategoryRequest request,
            UpdateCategoryHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(
                new UpdateCategoryCommand(id, request.Name, request.NameKey, request.Description),
                cancellationToken);
            return TypedResults.NoContent();
        });

        group.MapPatch("/{id:guid}/placement", async (
            Guid id,
            PlaceCategoryRequest request,
            PlaceCategoryHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(
                new PlaceCategoryCommand(id, request.CategoryGroupId, request.Position),
                cancellationToken);
            return TypedResults.NoContent();
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DeleteCategoryHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(new DeleteCategoryCommand(id), cancellationToken);
            return TypedResults.NoContent();
        });

        return endpoints;
    }

    // The body of the PUT, and one of the two request bodies this file declares: PlaceCategoryRequest is
    // the other, below. POST binds CreateCategoryCommand directly, so the attribute on that command is
    // that route's whole contract.
    //
    // NameKey is required beside Name, because a category's name is two columns computed from one piece
    // of text by one client — see Category.Update for what a half-written name costs. Description is the
    // only optional member, and a PUT with none CLEARS the note the category held; `null` and an absent
    // member mean the same thing, while `""` is a malformed envelope and a 400.
    //
    // [JsonUnmappedMemberHandling(Disallow)] IS HERE, AND NOT FOR THE REASON THE TRANSACTION SHAPES CARRY
    // IT. There the attribute answers wire DRIFT: CreateTransactionCommand and UpdateTransactionRequest
    // each RETIRED payeeName, a member a released client still sends, and refusing it visibly beat
    // dropping it in silence. Nothing on this shape is retired — every member is new — so that argument
    // does not reach here and must not be copied onto this line.
    //
    // What earns it here is Description, which binds a NULLABLE narrative column. Under
    // JsonSerializerDefaults.Web with the options Api/Program.cs registers, whose UnmappedMemberHandling
    // is the default Skip, a misspelled member binds IDENTICALLY to no member at all: `descriptionn`
    // leaves Description null, and so does an absent description. On this route that is a 204 and a note
    // the caller never asked to remove; on POST it is a 201 and a note that never arrives. Nothing
    // downstream can tell either from an operation — a category with no description is a legal row and
    // NULL is how it says so, both description CHECKs pass vacuously, and every later read agrees.
    // CategoryGroupEndpoints argues the same pair at length; this is the second table to make the
    // decision and it makes it for itself.
    //
    // PER TYPE, AND THE TWO IT REACHES ARE THIS SHAPE AND CreateCategoryCommand — the two that bind a
    // description. PlaceCategoryRequest below deliberately does NOT get it and is this file's negative
    // control: it binds no nullable narrative member, so it has not made this contract decision. Do not
    // widen it into Api/Program.cs, whose options every route in the product shares —
    // PatchCategoryGroupPosition_WithAnUnknownMember_StillIgnoresIt is what reddens if somebody does.
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record UpdateCategoryRequest(string Name, string NameKey, string? Description);

    private sealed record PlaceCategoryRequest(Guid CategoryGroupId, int Position);
}
