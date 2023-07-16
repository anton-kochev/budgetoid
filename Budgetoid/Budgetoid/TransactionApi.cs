using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Budgetoid.Application.Common;
using Budgetoid.Application.Payees.Commands.AddPayee;
using Budgetoid.Application.Transactions.Commands.CreateTransaction;
using Budgetoid.Application.Transactions.Commands.DeleteTransaction;
using Budgetoid.Application.Transactions.Commands.UpdateTransaction;
using Budgetoid.Application.Transactions.Queries.GetTransaction;
using Budgetoid.Application.Transactions.Queries.GetTransactions;
using Budgetoid.Dto;
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
            new GetTransactionsQuery(Guid.Parse(accountId)));

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
        TransactionDto result = await _mediator.Send(new GetTransactionQuery(Guid.Parse(id), Guid.Parse(accountId)));

        if (result == null) return new NotFoundResult();

        return new OkObjectResult(result);
    }

    [FunctionName("PostTransaction")]
    public async Task<IActionResult> CreateTransactionAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "transactions")]
        HttpRequest req,
        ILogger log)
    {
        log.LogInformation("Create transaction");
        CreateTransactionCommand command = await req.DeserializeBodyAsync<CreateTransactionCommand>();
        await _mediator.Send(command);
        await _mediator.Send(new AddPayeeCommand { Name = command.Payee, UserId = command.UserId });

        return new NoContentResult();
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
            Amount = update.Amount,
            CategoryId = update.CategoryId,
            Comment = update.Comment,
            Date = update.Date,
            Id = Guid.Parse(id),
            Payee = update.Payee,
            Tags = update.Tags
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
            await _mediator.Send(new DeleteTransactionCommand(Guid.Parse(id), Guid.Parse(accountId)));
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundResult();
        }

        return new OkResult();
    }
}
