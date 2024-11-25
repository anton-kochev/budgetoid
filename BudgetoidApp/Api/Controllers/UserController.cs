using System.Net;
using Application.Users.Queries.GetUser;
using MediatR;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Api.Controllers;

public sealed class UserController(ILoggerFactory loggerFactory, IMediator mediator)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<UserController>();

    [Function("GetUser")]
    public async Task<HttpResponseData> GetUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "user/{email}")]
        HttpRequestData req,
        string email)
    {
        _logger.LogInformation("GetUserId for {email}", email);

        Guid userId = await mediator.Send(new GetUserQuery(email));

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(userId);

        return response;
    }
}
