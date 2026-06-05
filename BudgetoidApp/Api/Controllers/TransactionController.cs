using System.Net;
using Api.Common;
using Application.Transactions.Commands.CreateTransaction;
using Application.Transactions.Commands.DeleteTransaction;
using Application.Transactions.Commands.UpdateTransaction;
using Application.Transactions.Queries.GetAccountTransactions;
using Application.Transactions.Queries.GetTransaction;
using Application.Transactions.Queries.GetUserTransactions;
using MediatR;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson.Exceptions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Api.Controllers;

public sealed class TransactionController(ILoggerFactory loggerFactory, IMediator mediator)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<TransactionController>();

    // For testing /api/user/00000000-0000-0000-0000-000000000002/account/2253cb57-e71f-4022-a799-4b4a8553e4ba/transactions
    [Function("GetAccountTransactions")]
    public async Task<HttpResponseData> GetAccountTransactions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "user/{userId}/account/{accountId}/transactions")]
        HttpRequestData req,
        string userId,
        string accountId)
    {
        _logger.LogInformation("GetAccountTransactionsAsync");

        IList<TransactionDto> transactions = await mediator.Send(
            new GetAccountTransactionsQuery(Guid.Parse(userId), Guid.Parse(accountId)));

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(transactions);

        return response;
    }

    // For testing /api/user/00000000-0000-0000-0000-000000000002/transactions
    [Function("GetUserTransactions")]
    public async Task<HttpResponseData> GetUserTransactions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "user/{userId}/transactions")]
        HttpRequestData req,
        string userId)
    {
        _logger.LogInformation("GetUserTransactionsAsync");

        IList<TransactionDto> transactions = await mediator.Send(new GetUserTransactionsQuery(Guid.Parse(userId)));

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(transactions);

        return response;
    }

    // For testing /api/user/00000000-0000-0000-0000-000000000002/transactions/a48ddc97-f29e-4ea4-aeff-a1784b8f93f7
    [Function("GetTransaction")]
    public async Task<HttpResponseData> GetTransaction(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "user/{userId}/transactions/{transactionId}")]
        HttpRequestData req,
        string userId,
        string transactionId)
    {
        _logger.LogInformation("GetTransactionAsync");

        TransactionDto? transaction = await mediator.Send(
            new GetTransactionQuery(Guid.Parse(userId), Guid.Parse(transactionId)));

        if (transaction is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        HttpResponseData response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(transaction);

        return response;
    }

    [Function("PostTransaction")]
    public async Task<HttpResponseData> CreateTransaction(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "transactions")]
        HttpRequestData req)
    {
        _logger.LogInformation("CreateTransaction");

        CreateTransactionCommand? command = await req.DeserializeBodyAsync<CreateTransactionCommand>();
        await mediator.Send(command!);
        // await mediator.Send(new AddPayeeCommand { Name = command.Payee, UserId = command.UserId });

        HttpResponseData response = req.CreateResponse(HttpStatusCode.NoContent);

        return response;
    }

    [Function("DeleteTransaction")]
    public async Task<HttpResponseData> DeleteTransaction(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "user/{userId}/transactions/{transactionId}")]
        HttpRequestData req,
        string userId,
        string transactionId)
    {
        _logger.LogInformation("DeleteTransaction");

        await mediator.Send(new DeleteTransactionCommand(Guid.Parse(userId), Guid.Parse(transactionId)));

        HttpResponseData response = req.CreateResponse(HttpStatusCode.NoContent);

        return response;
    }

    [Function("UpdateTransaction")]
    public async Task<HttpResponseData> UpdateTransaction(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "user/{userId}/transactions/{transactionId}")]
        HttpRequestData req,
        string userId,
        string transactionId)
    {
        _logger.LogInformation("UpdateTransaction");

        if (!req.TryDeserializeBody(out JsonPatchDocument<TransactionDto>? patch) || patch is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        TransactionDto? transaction = await mediator.Send(
            new GetTransactionQuery(Guid.Parse(userId), Guid.Parse(transactionId)));
        if (transaction is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        try
        {
            patch.ApplyTo(transaction);
        }
        catch (JsonPatchException)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        await mediator.Send(new UpdateTransactionCommand(Guid.Parse(userId), transaction));

        HttpResponseData response = req.CreateResponse(HttpStatusCode.NoContent);

        return response;
    }
}
