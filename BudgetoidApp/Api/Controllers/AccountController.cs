using System.Net;
using Application.Transactions.Queries.GetAccounts;
using MediatR;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Api.Controllers;

public sealed class AccountController(ILoggerFactory loggerFactory, IMediator mediator)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<AccountController>();

    // For testing /api/accounts
    [Function("GetUserAccounts")]
    public async Task<HttpResponseData> GetUserAccounts(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "accounts")]
        HttpRequestData req,
        string userId)
    {
        _logger.LogInformation("GetUserAccountsAsync");

        IEnumerable<AccountDto> accounts =
            await mediator.Send(new GetAccountsQuery(Guid.Parse(userId)));

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(accounts);

        return response;
    }
}
