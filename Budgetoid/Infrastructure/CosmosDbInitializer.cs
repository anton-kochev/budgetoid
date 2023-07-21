using Microsoft.Azure.Cosmos;

namespace Infrastructure;

public static class CosmosDbInitializer
{
    private static string DatabaseName => "Budgetoid";

    public static async Task InitializeCosmosDbStructure(CosmosClient client)
    {
        await CreateDatabaseIfNotExistsAsync(client);
        await CreateContainerIfNotExistsAsync(client, "Accounts", "/userId");
        await CreateContainerIfNotExistsAsync(client, "Budgets", "/userId");
        await CreateContainerIfNotExistsAsync(client, "Payees", "/userId", "/userId", "/name");
        await CreateContainerIfNotExistsAsync(client, "Transactions", "/accountId");
    }

    private static async Task CreateDatabaseIfNotExistsAsync(CosmosClient client)
    {
        await client.CreateDatabaseIfNotExistsAsync(DatabaseName);
    }

    private static async Task CreateContainerIfNotExistsAsync(
        CosmosClient client,
        string containerName,
        string partitionKeyPath)
    {
        Database? database = client.GetDatabase(DatabaseName);
        await database.CreateContainerIfNotExistsAsync(containerName, partitionKeyPath);
    }

    private static async Task CreateContainerIfNotExistsAsync(
        CosmosClient client,
        string containerName,
        string partitionKeyPath,
        params string[] uniqueKeyPaths)
    {
        Database? database = client.GetDatabase(DatabaseName);

        UniqueKey uniqueKey = new()
        {
            Paths = { uniqueKeyPaths.ToString() }
        };

        UniqueKeyPolicy uniqueKeyPolicy = new()
        {
            UniqueKeys = { uniqueKey }
        };

        ContainerProperties containerProperties = new(containerName, partitionKeyPath)
        {
            UniqueKeyPolicy = uniqueKeyPolicy
        };

        await database.CreateContainerIfNotExistsAsync(containerProperties);
    }
}
