using Application.Abstractions;
using Domain.Entities;
using MediatR;

namespace Application.Transactions.Commands.CreateTransaction;

public record CreateTransactionCommand : IRequest<Unit>
{
    public Guid AccountId { get; init; } = Guid.Empty;
    public decimal Amount { get; init; }
    public Guid CategoryId { get; init; } = Guid.Empty;
    public string Comment { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public string Payee { get; init; } = string.Empty;
    public string[] Tags { get; init; } = Array.Empty<string>();
    public Guid UserId { get; init; } = Guid.Empty;
}

public sealed class CreateTransactionHandler(ITransactionsRepository transactionsRepository)
    : IRequestHandler<CreateTransactionCommand, Unit>
{
    public async Task<Unit> Handle(CreateTransactionCommand request, CancellationToken cancellationToken)
    {
        Transaction transaction = new()
        {
            AccountId = request.AccountId,
            Amount = request.Amount,
            CategoryId = request.CategoryId,
            Comment = request.Comment,
            Date = request.Date,
            Payee = request.Payee,
            Tags = request.Tags,
            UserId = request.UserId.ToString()
        };

        await transactionsRepository.CreateAsync(transaction, cancellationToken);

        return Unit.Value;
    }
}
