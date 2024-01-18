using Domain.Entities;
using MediatR;

namespace Application.Abstractions;

public interface ITransactionsRepository
{
    Task<Transaction?> GetAsync(Guid id, Guid userId, CancellationToken cancellationToken);

    Task<IEnumerable<Transaction>> GetUserTransactionsAsync(Guid userId, CancellationToken cancellationToken);

    Task<IEnumerable<Transaction>> GetAccountTransactionsAsync(Guid userId, Guid accountId,
        CancellationToken cancellationToken);

    Task<Unit> CreateAsync(Transaction transaction, CancellationToken cancellationToken);

    Task<Unit> DeleteAsync(Guid id, Guid userId, CancellationToken cancellationToken);

    Task<Unit> UpdateAsync(Transaction transaction, CancellationToken cancellationToken);
}
