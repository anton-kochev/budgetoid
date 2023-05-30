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
using Budgetoid.Dto;
using Budgetoid.Models;
using Microsoft.Azure.Cosmos;

namespace Budgetoid;

public sealed class TransactionApi
{
    private readonly Container _container;
    
    public TransactionApi(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("Budgetoid", "Transactions");
    }

    [FunctionName("GetTransactions")]
    public async Task<IActionResult> GetTransactionsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "transactions")] HttpRequest req,
        ILogger log)
    {
        FeedIterator<Transaction> transactions = _container.GetItemQueryIterator<Transaction>();
        IEnumerable<TransactionDto> result = (await transactions.ReadNextAsync())
            .Select(t => new TransactionDto
            {
                Id = Guid.Parse(t.Id),
                Amount = t.Amount,
                Comment = t.Comment,
                Date = t.Date.ToDateOnly(),
                PayeeId = t.PayeeId,
                Tags = t.Tags,
                AccountId = t.AccountId,
            });

        return new OkObjectResult(result);
    }

    [FunctionName("GetTransaction")]
    public async Task<IActionResult> GetTransactionAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "transaction/{userId}/{id}")] HttpRequest req,
        ILogger log,
        string userId,
        string id)
    {
        Transaction t;
        try
        {
            t = await _container.ReadItemAsync<Transaction>(id, new PartitionKey(userId));
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new NotFoundResult();
        }

        TransactionDto result = new()
        {
            Id = Guid.Parse(t.Id),
            AccountId = t.AccountId,
            Amount = t.Amount,
            Comment = t.Comment,
            CategoryId = t.CategoryId,
            Date = t.Date.ToDateOnly(),
            PayeeId = t.PayeeId,
            Tags = t.Tags,
        };
        
        return new OkObjectResult(result);
    }

    [FunctionName("PostTransaction")]
    public async Task<IActionResult> CreateTransactionAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "transaction")] HttpRequest req,
        ILogger log)
    {
        log.LogInformation("Create transaction");
        CreateTransactionCommand command = await req.DeserializeBodyAsync<CreateTransactionCommand>();
        Transaction transaction = new()
        {
            AccountId = command.AccountId,
            Amount = command.Amount,
            CategoryId = command.CategoryId,
            Comment = command.Comment,
            CreatedBy = command.CreatedBy.ToString(),
            CreatedOn = DateTime.UtcNow,
            Date = command.Date,
            PayeeId = command.PayeeId,
            Tags = command.Tags,
        };

        await _container.CreateItemAsync(transaction, new PartitionKey(transaction.CreatedBy));
        
        return new OkObjectResult(true);
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
        await _container.DeleteItemAsync<Transaction>(id, new PartitionKey(userId));
        
        return new OkResult();
    }
}

