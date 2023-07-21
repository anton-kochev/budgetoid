using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Budgetoid.Application.Accounts.Queries.GetAccount;
using Budgetoid.Application.Budgets.Queries.GetBudgets;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Budgetoid;

public sealed class BudgetApi
{
    private readonly IMediator _mediator;

    public BudgetApi(IMediator mediator)
    {
        _mediator = mediator;
    }

    [FunctionName("GetBudgets")]
    public async Task<ActionResult<IEnumerable<AccountDto>>> GetBudgetsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "budgets/{userId}")]
        HttpRequest req,
        string userId,
        ILogger log)
    {
        IEnumerable<BudgetBriefDto> result = await _mediator.Send(new GetBudgetsQuery(Guid.Parse(userId)));

        return new OkObjectResult(result);
    }
}
