using Application.Transactions.CreateTransaction;
using Application.Transactions.GetTransactions;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class GetTransactionsHandlerTests
{
    [Test]
    public async Task HandleAsync_ReturnsTransactionsNewestFirst()
    {
        // Arrange
        var repository = new InMemoryTransactionRepository();
        var budgetId = Guid.CreateVersion7();
        var olderTime = new FakeTimeProvider(
            new DateTimeOffset(2026, 6, 11, 13, 14, 15, TimeSpan.Zero));
        var newerTime = new FakeTimeProvider(
            new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero));
        var accounts = new InMemoryAccountRepository(budgetId, olderTime);
        Account account = await accounts.CreateAsync();
        var categoryGroups = new InMemoryCategoryGroupRepository(budgetId, olderTime);
        var categories = new InMemoryCategoryRepository(budgetId, olderTime, categoryGroups);

        await CreateAsync(repository, accounts, categoryGroups, categories, olderTime, account, "Older");
        await CreateAsync(repository, accounts, categoryGroups, categories, newerTime, account, "Newest");

        // Act
        var response = await new GetTransactionsHandler(repository)
            .HandleAsync(new GetTransactionsQuery());

        // Assert — the note crosses this DTO as the base64url envelope the column holds, not as text.
        // The labels are still what tells the two rows apart, because the fixture's filler is
        // deterministic in the label; what they no longer are is readable, which is the product working.
        await Assert.That(response.Items.Count).IsEqualTo(2);
        await Assert.That(response.Items[0].Description)
            .IsEqualTo(SealedNarrative.EncodedDescription("Newest"));
        await Assert.That(response.Items[1].Description)
            .IsEqualTo(SealedNarrative.EncodedDescription("Older"));
    }

    [Test]
    public async Task HandleAsync_RendersTheNoteTheCreateHandlerSealed()
    {
        // Arrange — SPLIT OUT OF HandleAsync_ReturnsTransactionsNewestFirst, WHERE IT WAS CARRIED BY
        // ACCIDENT. That case's two description comparisons are the only thing in the whole unit suite
        // that catches CreateTransactionHandler passing null in place of the decoded note — a silent
        // NULL in somebody's description column, on a nullable column no constraint speaks for. But its
        // NAME promises ordering, and the two claims are worse than co-located: those same two
        // comparisons are also how that case tells the rows apart, because the note is the only member
        // its two fixtures differ in. So the day somebody gives those rows distinct amounts and
        // "clarifies" the ordering assertion to compare amounts, the ordering case gets BETTER and the
        // round-trip guard disappears with nothing named for it going red.
        //
        // One transaction, because ordering is not this case's business.
        var repository = new InMemoryTransactionRepository();
        var budgetId = Guid.CreateVersion7();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero));
        var accounts = new InMemoryAccountRepository(budgetId, timeProvider);
        Account account = await accounts.CreateAsync();
        var categoryGroups = new InMemoryCategoryGroupRepository(budgetId, timeProvider);
        var categories = new InMemoryCategoryRepository(budgetId, timeProvider, categoryGroups);

        await CreateAsync(
            repository, accounts, categoryGroups, categories, timeProvider, account, "Corner shop");

        // Act
        var response = await new GetTransactionsHandler(repository)
            .HandleAsync(new GetTransactionsQuery());

        // Assert — the note the command carried, decoded by the handler into a NarrativeField, stored on
        // the entity, and encoded back by TransactionDto.FromTransaction. Every one of those four steps
        // is between the caller and this value, and a handler that dropped the parameter answers 201 with
        // a row whose note is gone and nothing anywhere reporting it.
        //
        // "Corner shop" is measured to spell DIFFERENTLY under padded standard base64 and unpadded
        // base64url, so this assertion also catches a read path reaching for the wrong encoder. A blind
        // label would make that half decoration; SealedNarrative's remarks enumerate the ones that are.
        await Assert.That(response.Items.Single().Description)
            .IsEqualTo(SealedNarrative.EncodedDescription("Corner shop"));
    }

    [Test]
    public async Task HandleAsync_WithNoNote_RendersTheDescriptionAsNull()
    {
        // Arrange — a transaction created with NO note, which is the ordinary state of a quickly typed
        // or imported row and a state no other case in this file reaches: every fixture here seals one.
        var repository = new InMemoryTransactionRepository();
        var budgetId = Guid.CreateVersion7();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero));
        var accounts = new InMemoryAccountRepository(budgetId, timeProvider);
        Account account = await accounts.CreateAsync();
        var categoryGroups = new InMemoryCategoryGroupRepository(budgetId, timeProvider);
        var categories = new InMemoryCategoryRepository(budgetId, timeProvider, categoryGroups);

        await CreateAsync(
            repository, accounts, categoryGroups, categories, timeProvider, account, descriptionLabel: null);

        // Act
        var response = await new GetTransactionsHandler(repository)
            .HandleAsync(new GetTransactionsQuery());

        // Assert — THE ONE THING BETWEEN A CLIENT AND AN UNOPENABLE EMPTY STRING. TransactionDto.Description
        // is string? and its remarks forbid the `?? string.Empty` this record used to carry; restoring
        // that coercion passes every other case in the suite, because every other case sends a note. What
        // it would do is hand the browser "" where the row holds NULL — and "" is not a legal envelope, so
        // a client that tried to open it gets a decode failure on a transaction whose owner simply never
        // wrote a note.
        //
        // `IsNull` and never IsNullOrEmpty: "no note" and "a note somebody emptied" are two different
        // rows, and an assertion that accepted either would be blind to exactly the fold being guarded
        // against.
        await Assert.That(response.Items.Single().Description).IsNull();
    }

    [Test]
    public async Task HandleAsync_ReturnsCurrentCategoryAndCategoryGroupProjection()
    {
        // Arrange
        var repository = new InMemoryTransactionRepository();
        var budgetId = Guid.CreateVersion7();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero));
        var accounts = new InMemoryAccountRepository(budgetId, timeProvider);
        Account account = await accounts.CreateAsync();
        var categoryGroups = new InMemoryCategoryGroupRepository(budgetId, timeProvider);
        var categories = new InMemoryCategoryRepository(budgetId, timeProvider, categoryGroups);
        CategoryGroup categoryGroup =
            await categoryGroups.CreateAsync(SealedNarrative.Indexed("Essential Obligations"));
        Category category = await categories.CreateAsync(categoryGroup.Id, "Groceries");
        await CreateAsync(
            repository,
            accounts,
            categoryGroups,
            categories,
            timeProvider,
            account,
            "Food",
            category.Id);
        // BOTH names cross the wire as envelopes now, so the projection is fed the encoded values
        // rather than the entities' NarrativeFields: what this fake stands in for is the read service's
        // shaped row, not the entity. The category's name joined the group's in the same slice its own
        // description was sealed, so the two arguments finally take one treatment.
        repository.SetCategoryProjection(
            category.Id,
            SealedNarrative.EncodedName("Groceries"),
            categoryGroup.Id,
            SealedNarrative.EncodedName("Essential Obligations"));

        // Act
        var response = await new GetTransactionsHandler(repository)
            .HandleAsync(new GetTransactionsQuery());

        // Assert
        var dto = response.Items.Single();
        await Assert.That(dto.CategoryId).IsEqualTo(category.Id);
        await Assert.That(dto.CategoryName).IsEqualTo(SealedNarrative.EncodedName("Groceries"));
        await Assert.That(dto.CategoryGroupId).IsEqualTo(categoryGroup.Id);
        await Assert.That(dto.CategoryGroupName)
            .IsEqualTo(SealedNarrative.EncodedName("Essential Obligations"));
    }

    [Test]
    public async Task HandleAsync_WhenNoTransactions_ReturnsEmptyList()
    {
        // Act
        var response = await new GetTransactionsHandler(new InMemoryTransactionRepository())
            .HandleAsync(new GetTransactionsQuery());

        // Assert
        await Assert.That(response.Items.Count).IsEqualTo(0);
    }

    private static Task CreateAsync(
        InMemoryTransactionRepository transactions,
        InMemoryAccountRepository accounts,
        InMemoryCategoryGroupRepository categoryGroups,
        InMemoryCategoryRepository categories,
        TimeProvider timeProvider,
        Account account,
        string? descriptionLabel,
        Guid? categoryId = null)
    {
        var handler = new CreateTransactionHandler(
            transactions,
            accounts,
            new InMemoryCurrencyReadService(),
            new InMemoryPayeeRepository(account.BudgetId, timeProvider),
            categories,
            categoryGroups,
            new StubBudgetContext(account.BudgetId),
            timeProvider);
        // The id is minted HERE and threaded in, because the command carries one now: it is the
        // associated data the note was sealed against, so the handler takes it and never invents it. It
        // travels as a string in the canonical spelling, because binding it as a Guid would fold the
        // spellings before any handler saw text.
        //
        // The description is a SEALED envelope in base64url rather than the label itself; the label
        // survives as what distinguishes one row's note from the next's.
        //
        // A NULL LABEL MEANS NO NOTE AND IS NOT THE SAME AS AN EMPTY ONE. SealedNarrative.Description("")
        // is a legal twenty-nine-byte envelope — a note somebody wrote and then emptied — while null
        // reaches the command as null and the column as NULL. The two are different rows, so this helper
        // maps null through rather than sealing it.
        return handler.HandleAsync(new CreateTransactionCommand(
            Guid.CreateVersion7().ToString("D"),
            20m,
            DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime),
            account.Id,
            descriptionLabel is null ? null : SealedNarrative.EncodedDescription(descriptionLabel),
            CategoryId: categoryId));
    }
}
