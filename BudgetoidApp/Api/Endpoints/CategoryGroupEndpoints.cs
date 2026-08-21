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
                new UpdateCategoryGroupCommand(id, request.Name, request.Description),
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

    private sealed record UpdateCategoryGroupRequest(string Name, string? Description);
    private sealed record MoveCategoryGroupRequest(int Position);
}
