using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Budgetoid.Application.Accounts.Commands.CreateAccount;
using Budgetoid.Application.Accounts.Queries.GetAccount;
using Budgetoid.Application.Accounts.Queries.GetAccounts;
using Budgetoid.Application.Common;
using Budgetoid.Application.Transactions.Queries.GetTransaction;
using Budgetoid.Application.Transactions.Queries.GetTransactions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
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
        AccountDto result =
            await _mediator.Send(new GetAccountQuery(Guid.Parse(userId), Guid.Parse(id)));
        IEnumerable<TransactionDto> transactions =
            await _mediator.Send(new GetTransactionsQuery(Guid.Parse(id)));

        result = result with { Balance = transactions.Sum(t => t.Amount) };

        return new OkObjectResult(result);
    }

    [Authorize]
    [FunctionName("GetAccounts")]
    public async Task<ActionResult<IEnumerable<AccountDto>>> GetAccountsAsync(
        [HttpTrigger(AuthorizationLevel.User, "get", Route = "accounts/{userId}")]
        HttpRequest req,
        string userId,
        ClaimsPrincipal claimsPrincipal,
        ILogger log)
    {
        IEnumerable<AccountDto> result = await _mediator.Send(new GetAccountsQuery(Guid.Parse(userId)));

        return new OkObjectResult(result);
    }
}
