using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Categories;
using Domain.CategoryGroups;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

public sealed class CategoryIntegrationTests
{
    [Test]
    public async Task CategoryHierarchy_CrudPlacementAndCustomOrderingWorkThroughApi()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid essentialsId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid lifestyleId = await CreateCategoryGroupAsync(client, "Lifestyle");
        Guid groceriesId = await CreateCategoryAsync(client, essentialsId, "Groceries");
        Guid utilitiesId = await CreateCategoryAsync(client, essentialsId, "Utilities");
        Guid diningId = await CreateCategoryAsync(client, lifestyleId, "Dining Out");

        // Act
        HttpResponseMessage moveGroup = await client.PatchAsJsonAsync(
            $"/api/category-groups/{lifestyleId}/position",
            new { position = 0 });
        HttpResponseMessage moveCategory = await client.PatchAsJsonAsync(
            $"/api/categories/{groceriesId}/placement",
            new { categoryGroupId = lifestyleId, position = 0 });
        HttpResponseMessage rename = await client.PutAsJsonAsync(
            $"/api/categories/{groceriesId}",
            new { name = "Food Shopping", description = "Weekly food" });
        JsonNode groups = await GetJsonAsync(client, "/api/category-groups");
        JsonNode categories = await GetJsonAsync(client, "/api/categories");

        // Assert
        await Assert.That(moveGroup.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(moveCategory.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(rename.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        JsonArray groupItems = groups["items"]!.AsArray();
        await Assert.That(groupItems[0]!["id"]!.GetValue<Guid>()).IsEqualTo(lifestyleId);
        await Assert.That(groupItems[0]!["position"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(groupItems[1]!["id"]!.GetValue<Guid>()).IsEqualTo(essentialsId);
        JsonArray categoryItems = categories["items"]!.AsArray();
        await Assert.That(categoryItems[0]!["id"]!.GetValue<Guid>()).IsEqualTo(groceriesId);
        await Assert.That(categoryItems[0]!["name"]!.GetValue<string>()).IsEqualTo("Food Shopping");
        await Assert.That(categoryItems[0]!["categoryGroupId"]!.GetValue<Guid>()).IsEqualTo(lifestyleId);
        await Assert.That(categoryItems[0]!["categoryGroupName"]!.GetValue<string>()).IsEqualTo("Lifestyle");
        await Assert.That(categoryItems[0]!["position"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(categoryItems[1]!["id"]!.GetValue<Guid>()).IsEqualTo(diningId);
        await Assert.That(categoryItems[1]!["position"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(categoryItems[2]!["id"]!.GetValue<Guid>()).IsEqualTo(utilitiesId);
        await Assert.That(categoryItems[2]!["position"]!.GetValue<int>()).IsEqualTo(0);
    }

    [Test]
    public async Task CreatedResources_AreRetrievableAtLocationHeader()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        HttpResponseMessage createGroup = await client.PostAsJsonAsync("/api/category-groups", new
        {
            name = "Essentials",
            description = (string?)null,
        });
        createGroup.EnsureSuccessStatusCode();
        JsonNode createdGroup = (await JsonNode.ParseAsync(
            await createGroup.Content.ReadAsStreamAsync()))!;
        Guid categoryGroupId = createdGroup["id"]!.GetValue<Guid>();
        HttpResponseMessage createCategory = await client.PostAsJsonAsync("/api/categories", new
        {
            name = "Groceries",
            description = (string?)null,
            categoryGroupId,
        });
        createCategory.EnsureSuccessStatusCode();

        // Act
        HttpResponseMessage getGroup = await client.GetAsync(createGroup.Headers.Location);
        HttpResponseMessage getCategory = await client.GetAsync(createCategory.Headers.Location);
        HttpResponseMessage unknownGroup = await client.GetAsync(
            $"/api/category-groups/{Guid.CreateVersion7()}");
        HttpResponseMessage unknownCategory = await client.GetAsync(
            $"/api/categories/{Guid.CreateVersion7()}");

        // Assert
        await Assert.That(getGroup.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(getCategory.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonNode group = (await JsonNode.ParseAsync(
            await getGroup.Content.ReadAsStreamAsync()))!;
        await Assert.That(group["id"]!.GetValue<Guid>()).IsEqualTo(categoryGroupId);
        await Assert.That(group["name"]!.GetValue<string>()).IsEqualTo("Essentials");
        JsonNode category = (await JsonNode.ParseAsync(
            await getCategory.Content.ReadAsStreamAsync()))!;
        await Assert.That(category["name"]!.GetValue<string>()).IsEqualTo("Groceries");
        await Assert.That(category["categoryGroupId"]!.GetValue<Guid>()).IsEqualTo(categoryGroupId);
        await Assert.That(category["categoryGroupName"]!.GetValue<string>()).IsEqualTo("Essentials");
        await Assert.That(unknownGroup.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownCategory.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task EmptyGroupAndUnreferencedCategory_CanBeDeleted()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid categoryGroupId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid categoryId = await CreateCategoryAsync(client, categoryGroupId, "Groceries");

        // Act
        HttpResponseMessage deleteCategory = await client.DeleteAsync($"/api/categories/{categoryId}");
        HttpResponseMessage deleteGroup = await client.DeleteAsync(
            $"/api/category-groups/{categoryGroupId}");
        JsonNode groups = await GetJsonAsync(client, "/api/category-groups");
        JsonNode categories = await GetJsonAsync(client, "/api/categories");

        // Assert
        await Assert.That(deleteCategory.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(deleteGroup.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(groups["items"]!.AsArray().Count).IsEqualTo(0);
        await Assert.That(categories["items"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task CategoryGroupNames_AreCaseInsensitivelyUniquePerUser()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        await CreateCategoryGroupAsync(client, "Essentials");

        // Act
        HttpResponseMessage duplicate = await client.PostAsJsonAsync("/api/category-groups", new
        {
            name = "essentials",
            description = (string?)null,
        });
        JsonNode problem = (await JsonNode.ParseAsync(
            await duplicate.Content.ReadAsStreamAsync()))!;

        // Assert
        await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem["errors"]!["Name"] is not null).IsTrue();
    }

    [Test]
    public async Task CategoryNames_AreCaseInsensitivelyUniqueAcrossGroups()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid essentialsId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid lifestyleId = await CreateCategoryGroupAsync(client, "Lifestyle");
        await CreateCategoryAsync(client, essentialsId, "Groceries");

        // Act
        HttpResponseMessage duplicate = await client.PostAsJsonAsync("/api/categories", new
        {
            name = "groceries",
            description = (string?)null,
            categoryGroupId = lifestyleId,
        });
        JsonNode problem = (await JsonNode.ParseAsync(
            await duplicate.Content.ReadAsStreamAsync()))!;

        // Assert
        await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem["errors"]!["Name"] is not null).IsTrue();
    }

    [Test]
    public async Task DeletionGuards_ProtectNonEmptyGroupsAndReferencedCategories()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        Guid categoryGroupId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid categoryId = await CreateCategoryAsync(client, categoryGroupId, "Groceries");
        HttpResponseMessage transaction = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -20m,
            date = "2026-07-14",
            accountId,
            description = "Food",
            categoryId,
        });
        transaction.EnsureSuccessStatusCode();

        // Act
        HttpResponseMessage deleteGroup = await client.DeleteAsync(
            $"/api/category-groups/{categoryGroupId}");
        HttpResponseMessage deleteCategory = await client.DeleteAsync(
            $"/api/categories/{categoryId}");

        // Assert
        await Assert.That(deleteGroup.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(deleteCategory.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task TransactionProjection_ReflectsCurrentCategoryAndGroupAfterRenameAndMove()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        Guid accountId = await CreateAccountAsync(client);
        Guid essentialsId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid lifestyleId = await CreateCategoryGroupAsync(client, "Lifestyle");
        Guid categoryId = await CreateCategoryAsync(client, essentialsId, "Groceries");
        HttpResponseMessage createTransaction = await client.PostAsJsonAsync("/api/transactions", new
        {
            amount = -20m,
            date = "2026-07-14",
            accountId,
            description = "Food",
            categoryId,
        });
        createTransaction.EnsureSuccessStatusCode();

        // Act
        await client.PutAsJsonAsync(
            $"/api/category-groups/{lifestyleId}",
            new { name = "Discretionary", description = (string?)null });
        await client.PutAsJsonAsync(
            $"/api/categories/{categoryId}",
            new { name = "Food Shopping", description = (string?)null });
        await client.PatchAsJsonAsync(
            $"/api/categories/{categoryId}/placement",
            new { categoryGroupId = lifestyleId, position = 0 });
        JsonNode transactions = await GetJsonAsync(client, "/api/transactions");
        JsonNode item = transactions["items"]!.AsArray().Single()!;

        // Assert
        await Assert.That(item["categoryId"]!.GetValue<Guid>()).IsEqualTo(categoryId);
        await Assert.That(item["categoryName"]!.GetValue<string>()).IsEqualTo("Food Shopping");
        await Assert.That(item["categoryGroupId"]!.GetValue<Guid>()).IsEqualTo(lifestyleId);
        await Assert.That(item["categoryGroupName"]!.GetValue<string>()).IsEqualTo("Discretionary");
    }

    [Test]
    public async Task CategoryResources_AreIsolatedByBudgetAndLegacyGroupsRouteIsGone()
    {
        // Arrange
        await using PostgresTestHost host = new();
        await host.StartAsync();
        await using ApiFactory factoryA = host.CreateFactory("google-a");
        await using ApiFactory factoryB = host.CreateFactory("google-b");
        HttpClient clientA = factoryA.CreateAuthenticatedClient();
        HttpClient clientB = factoryB.CreateAuthenticatedClient();
        Guid categoryGroupA = await CreateCategoryGroupAsync(clientA, "Essentials");
        await CreateCategoryAsync(clientA, categoryGroupA, "Groceries");

        // Act
        JsonNode groupsB = await GetJsonAsync(clientB, "/api/category-groups");
        JsonNode categoriesB = await GetJsonAsync(clientB, "/api/categories");
        HttpResponseMessage crossBudgetCreate = await clientB.PostAsJsonAsync("/api/categories", new
        {
            name = "Attempt",
            description = (string?)null,
            categoryGroupId = categoryGroupA,
        });
        HttpResponseMessage crossBudgetRead = await clientB.GetAsync(
            $"/api/category-groups/{categoryGroupA}");
        HttpResponseMessage crossBudgetUpdate = await clientB.PutAsJsonAsync(
            $"/api/category-groups/{categoryGroupA}",
            new { name = "Renamed", description = (string?)null });
        HttpResponseMessage legacy = await clientA.GetAsync("/api/groups");

        // Assert — a resource in another budget must be indistinguishable from one that does not
        // exist, so every cross-budget read is 404 and there is deliberately no 403 path to add.
        await Assert.That(groupsB["items"]!.AsArray().Count).IsEqualTo(0);
        await Assert.That(categoriesB["items"]!.AsArray().Count).IsEqualTo(0);
        await Assert.That(crossBudgetCreate.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(crossBudgetRead.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(crossBudgetUpdate.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(legacy.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Database_EnforcesRequiredSameBudgetCategoryMembership()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        Guid budgetA = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid budgetB = await host.SeedBudgetAsync("google-b", "b@example.com");
        var options = new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options;
        Guid categoryGroupId;
        await using (BudgetoidDbContext db = new(options, new TestBudgetContext(budgetA)))
        {
            CategoryGroup categoryGroup = CategoryGroup.Create(
                budgetA,
                "Essentials",
                null,
                0,
                UtcNow());
            db.CategoryGroups.Add(categoryGroup);
            await db.SaveChangesAsync();
            categoryGroupId = categoryGroup.Id;
        }

        // Act — the composite (CategoryGroupId, BudgetId) foreign key is what stops budget B from
        // adopting budget A's group; the query filter alone could not, since this is a write.
        await using BudgetoidDbContext crossBudgetDb = new(options, new TestBudgetContext(budgetB));
        crossBudgetDb.Categories.Add(Category.Create(
            budgetB,
            categoryGroupId,
            "Should Fail",
            null,
            0,
            UtcNow()));
        DbUpdateException? caught = null;
        try
        {
            await crossBudgetDb.SaveChangesAsync();
        }
        catch (DbUpdateException exception)
        {
            caught = exception;
        }

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That((caught!.InnerException as PostgresException)?.SqlState)
            .IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
    }

    private static async Task<Guid> CreateCategoryGroupAsync(HttpClient client, string name)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/category-groups", new
        {
            name,
            description = (string?)null,
        });
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
    }

    private static async Task<Guid> CreateCategoryAsync(
        HttpClient client,
        Guid categoryGroupId,
        string name)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/categories", new
        {
            name,
            description = (string?)null,
            categoryGroupId,
        });
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
    }

    private static async Task<Guid> CreateAccountAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts", new
        {
            name = $"Checking {Guid.CreateVersion7()}",
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "USD",
        });
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
    }

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string path) =>
        (await JsonNode.ParseAsync(await client.GetStreamAsync(path)))!;

    private static DateTime UtcNow() =>
        new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);

    private static async Task<PostgresTestHost> StartApiHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }

    private static async Task<RepositoryTestHost> StartRepositoryHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
