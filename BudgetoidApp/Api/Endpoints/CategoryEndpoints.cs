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
        // One of the six groups allowed to bring an account into existence; why the permission is
        // opt-in is in ProvisionsUserAttribute, why it sits on the group in AccountEndpoints.
        RouteGroupBuilder group = endpoints.MapGroup("/api/categories")
            .WithMetadata(new ProvisionsUserAttribute());

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
                new UpdateCategoryCommand(id, request.Name, request.Description),
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

    private sealed record UpdateCategoryRequest(string Name, string? Description);
    private sealed record PlaceCategoryRequest(Guid CategoryGroupId, int Position);
}
