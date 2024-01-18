using Microsoft.Azure.Cosmos;

namespace Infrastructure.Repositories;

public abstract class Repository<T>(CosmosClient client)
{
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
