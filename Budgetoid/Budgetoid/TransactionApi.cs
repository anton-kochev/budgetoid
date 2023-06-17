using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Web.Http;
using Budgetoid.Dto;
using Budgetoid.Models;
using Budgetoid.Transactions.Commands.CreateTransaction;
using Budgetoid.Transactions.Commands.DeleteTransaction;
using Budgetoid.Transactions.Queries.GetTransaction;
using Budgetoid.Transactions.Queries.GetTransactions;
using MediatR;
using Microsoft.Azure.Cosmos;

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
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "transactions")] HttpRequest req,
        ILogger log)
    {
        IEnumerable<TransactionDto> result = await _mediator.Send(new GetTransactionsQuery());

        return new OkObjectResult(result);
    }

    [FunctionName("GetTransaction")]
    public async Task<ActionResult<TransactionDto>> GetTransactionAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "transaction/{userId}/{id}")] HttpRequest req,
        ILogger log,
        string userId,
        string id)
    {
        TransactionDto result = await _mediator.Send(new GetTransactionQuery
        {
            Id = Guid.Parse(id),
            UserId = Guid.Parse(userId)
        });
        
        if (result == null)
        {
            return new NotFoundResult();
        }
        
        return new OkObjectResult(result);
    }

    [FunctionName("PostTransaction")]
    public async Task<ActionResult<Guid>> CreateTransactionAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "transaction")] HttpRequest req,
        ILogger log)
    {
        log.LogInformation("Create transaction");
        CreateTransactionCommand command = await req.DeserializeBodyAsync<CreateTransactionCommand>();
        Guid id = await _mediator.Send(command);
        
        return new OkObjectResult(id);
    }

    // [FunctionName("PutTransaction")]
    // public async Task<IActionResult> UpdateTransactionAsync(
    //     [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "transaction")] HttpRequest req,
    //     ILogger log)
    // {
    //     TransactionDto transaction = await req.DeserializeBodyAsync<TransactionDto>();
    //     
    //     return new OkObjectResult(transaction);
    // }
    
    [FunctionName("DeleteTransaction")]
    public async Task<IActionResult> DeleteTransactionAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "transaction/{userId}/{id}")] HttpRequest req,
        ILogger log,
        string userId,
        string id)
    {
        try
        {
            await _mediator.Send(new DeleteTransactionCommand
            {
                Id = Guid.Parse(id),
                UserId = Guid.Parse(userId)
            });
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new NotFoundResult();
        }

        return new OkResult();
    }
}

