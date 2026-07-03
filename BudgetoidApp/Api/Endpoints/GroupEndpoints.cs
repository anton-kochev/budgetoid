using Application.Groups;
using Application.Groups.CreateGroup;
using Application.Groups.DeleteGroup;
using Application.Groups.GetGroups;
using Application.Groups.UpdateGroup;

namespace Api.Endpoints;

public static class GroupEndpoints
{
    public static IEndpointRouteBuilder MapGroupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/groups");

        group.MapPost("/", async (
            CreateGroupCommand command,
            CreateGroupHandler handler,
            CancellationToken cancellationToken) =>
        {
            GroupDto dto = await handler.HandleAsync(command, cancellationToken);
            return TypedResults.Created($"/api/groups/{dto.Id}", dto);
        });

        group.MapGet("/", async (GetGroupsHandler handler, CancellationToken cancellationToken) =>
        {
            GroupListResponse response = await handler.HandleAsync(new GetGroupsQuery(), cancellationToken);
            return TypedResults.Ok(response);
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateGroupRequest request,
            UpdateGroupHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(
                new UpdateGroupCommand(id, request.Name, request.Description),
                cancellationToken);
            return TypedResults.NoContent();
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DeleteGroupHandler handler,
            CancellationToken cancellationToken) =>
        {
            await handler.HandleAsync(new DeleteGroupCommand(id), cancellationToken);
            return TypedResults.NoContent();
        });

        return endpoints;
    }

    private sealed record UpdateGroupRequest(string Name, string? Description);
}
