using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Budgetoid.Application.Accounts.Queries.GetAccount;
using Budgetoid.Application.Payees.Queries.GetPayees;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Budgetoid;

public sealed class PayeeApi
{
    private readonly IMediator _mediator;

    public PayeeApi(IMediator mediator)
    {
        _mediator = mediator;
    }

    [FunctionName("GetPayees")]
    public async Task<ActionResult<IEnumerable<AccountDto>>> GetPayeesAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "payees/{userId}")]
        HttpRequest req,
        string userId,
        ILogger log)
    {
        IEnumerable<PayeeDto> result = await _mediator.Send(new GetPayeesQuery(Guid.Parse(userId)));

        return new OkObjectResult(result);
    }
}
