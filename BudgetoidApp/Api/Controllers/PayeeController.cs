using MediatR;
using Microsoft.Extensions.Logging;

namespace Api.Controllers;

public sealed class PayeeController(ILoggerFactory loggerFactory, IMediator mediator)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<PayeeController>();
}