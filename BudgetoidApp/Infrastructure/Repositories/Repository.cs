using Microsoft.Azure.Cosmos;

namespace Infrastructure.Repositories;

public abstract class Repository<T>(CosmosClient client) : IDisposable
{
    private bool _disposed;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                client.Dispose();
            }
        }

        _disposed = true;
    }

    protected Container GetContainer()
    {
        return client.GetContainer("Budgetoid", ContainerName());
    }

    protected async Task<FeedResponse<T>> GetItemsAsync(CancellationToken cancellationToken)
    {
        return await GetContainer().GetItemQueryIterator<T>().ReadNextAsync(cancellationToken);
    }

    protected abstract string ContainerName();
}
