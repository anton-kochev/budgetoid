using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Application.Accounts.Commands.CreateAccount;
using Application.Accounts.Queries.GetAccount;
using Application.Accounts.Queries.GetAccounts;
using Application.Common;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Budgetoid;

public sealed class AccountApi
{
    private readonly IMediator _mediator;

    public AccountApi(IMediator mediator)
    {
        _mediator = mediator;
    }

    [FunctionName("PostAccount")]
    public async Task<ActionResult<Guid>> CreateAccountAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "accounts")]
        HttpRequest req,
        ILogger log)
    {
        CreateAccountCommand command = await req.DeserializeBodyAsync<CreateAccountCommand>();
        Guid id = await _mediator.Send(command);

        return new OkObjectResult(id);
    }

    [FunctionName("GetAccount")]
    public async Task<ActionResult<AccountDto>> GetAccountAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "accounts/{userId}/{id}")]
        HttpRequest req,
        ILogger log,
        string userId,
        string id)
    {
        AccountDto result = await _mediator.Send(new GetAccountQuery
        {
            Id = Guid.Parse(id),
            UserId = Guid.Parse(userId)
        });

        if (result == null) return new NotFoundResult();

        return new OkObjectResult(result);
    }

    [FunctionName("GetAccounts")]
    public async Task<ActionResult<IEnumerable<AccountDto>>> GetAccountsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "accounts/{userId}")]
        HttpRequest req,
        string userId,
        ILogger log)
    {
        IEnumerable<AccountDto> result = await _mediator.Send(new GetAccountsQuery { UserId = Guid.Parse(userId) });

        return new OkObjectResult(result);
    }
}
