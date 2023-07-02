using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Budgetoid.Dto;
using Budgetoid.Transactions.Commands.CreateTransaction;
using Budgetoid.Transactions.Commands.DeleteTransaction;
using Budgetoid.Transactions.Commands.UpdateTransaction;
using Budgetoid.Transactions.Queries.GetTransaction;
using Budgetoid.Transactions.Queries.GetTransactions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Budgetoid;

public sealed class TransactionApi
{
    private readonly IMediator _mediator;

    public TransactionApi(IMediator mediator)
    {
        _mediator = mediator;
    }

    [FunctionName("GetTransactions")]
    public async Task<ActionResult<IEnumerable<TransactionDto>>> GetTransactionsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "transactions/{accountId}")]
        HttpRequest req,
        ILogger log,
        string accountId)
    {
        IEnumerable<TransactionDto> result = await _mediator.Send(
            new GetTransactionsQuery { AccountId = Guid.Parse(accountId) });

        return new OkObjectResult(result);
    }

    [FunctionName("GetTransaction")]
    public async Task<ActionResult<TransactionDto>> GetTransactionAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "transactions/{accountId}/{id}")]
        HttpRequest req,
        ILogger log,
        string accountId,
        string id)
    {
        TransactionDto result = await _mediator.Send(new GetTransactionQuery
        {
            Id = Guid.Parse(id),
            AccountId = Guid.Parse(accountId)
        });

        if (result == null) return new NotFoundResult();

        return new OkObjectResult(result);
    }

    [FunctionName("PostTransaction")]
    public async Task<ActionResult<Guid>> CreateTransactionAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "transactions")]
        HttpRequest req,
        ILogger log)
    {
        log.LogInformation("Create transaction");
        CreateTransactionCommand command = await req.DeserializeBodyAsync<CreateTransactionCommand>();
        Guid id = await _mediator.Send(command);

        return new OkObjectResult(id);
    }

    [FunctionName("PutTransaction")]
    public async Task<IActionResult> UpdateTransactionAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "transaction/{accountId}/{id}")]
        HttpRequest req,
        ILogger log,
        string accountId,
        string id)
    {
        log.LogInformation("Create transaction");
        UpdateTransactionDto update = await req.DeserializeBodyAsync<UpdateTransactionDto>();
        await _mediator.Send(new UpdateTransactionCommand
        {
            AccountId = Guid.Parse(accountId),
            Id = Guid.Parse(id),
            Update = update
        });

        return new NoContentResult();
    }

    [FunctionName("DeleteTransaction")]
    public async Task<IActionResult> DeleteTransactionAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "transactions/{accountId}/{id}")]
        HttpRequest req,
        ILogger log,
        string accountId,
        string id)
    {
        try
        {
            await _mediator.Send(new DeleteTransactionCommand
            {
                Id = Guid.Parse(id),
                AccountId = Guid.Parse(accountId)
            });
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundResult();
        }

        return new OkResult();
    }
}
