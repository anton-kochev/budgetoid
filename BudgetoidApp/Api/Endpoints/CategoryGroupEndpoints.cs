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

    // The body of the PUT, and the only thing this slice lands in the Api ring: the subject is the
    // request body, which Application already owns everywhere else.
    //
    // NameKey is required beside Name, because a group's name is two columns computed from one piece of
    // text by one client — see CategoryGroup.Update for what a half-written name costs. Description is
    // the only optional member, and a PUT with none CLEARS the note the group held; `null` and an absent
    // member mean the same thing, while `""` is a malformed envelope and a 400.
    //
    // NO [JsonUnmappedMemberHandling(Disallow)] HERE. That attribute went onto the two transaction shapes
    // because each had RETIRED a member a client was still sending, and refusing it visibly was better
    // than dropping it in silence. Nothing on this shape is retired — every member is added — so pasting
    // the attribute on would be a contract change with no argument behind it. The serializer options stay
    // Skip.
    private sealed record UpdateCategoryGroupRequest(
        string Name,
        string NameKey,
        string? Description);

    private sealed record MoveCategoryGroupRequest(int Position);
}
