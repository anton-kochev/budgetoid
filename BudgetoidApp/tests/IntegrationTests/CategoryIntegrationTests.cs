using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Security;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TestSupport;
using TUnit.Assertions.Enums;

namespace IntegrationTests;

public sealed class CategoryIntegrationTests
{
    [Test]
    public async Task CategoryHierarchy_CrudPlacementAndCustomOrderingWorkThroughApi()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
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
        // A GENUINE RENAME: both halves of the name change together, which is the only edit that makes
        // EF emit `name` and `name_key` in one UPDATE. A description-only edit re-sends the same name,
        // and because every seal draws a fresh nonce the envelope differs while the index is
        // byte-identical - so the comparer reports name_key unchanged and a ONE-COLUMN update goes out.
        // That is why this case, and not any of the description edits, is what reaches the grant.
        HttpResponseMessage rename = await client.PutAsJsonAsync(
            $"/api/categories/{groceriesId}",
            new
            {
                name = SealedNarrative.EncodedName("Food Shopping"),
                nameKey = SealedNarrative.EncodedIndex("Food Shopping"),
                description = SealedNarrative.EncodedDescription("Weekly food"),
            });
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
        await Assert.That(categoryItems[0]!["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Food Shopping"));
        await Assert.That(categoryItems[0]!["categoryGroupId"]!.GetValue<Guid>()).IsEqualTo(lifestyleId);
        await Assert.That(categoryItems[0]!["categoryGroupName"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Lifestyle"));
        await Assert.That(categoryItems[0]!["position"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(categoryItems[1]!["id"]!.GetValue<Guid>()).IsEqualTo(diningId);
        await Assert.That(categoryItems[1]!["position"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(categoryItems[2]!["id"]!.GetValue<Guid>()).IsEqualTo(utilitiesId);
        await Assert.That(categoryItems[2]!["position"]!.GetValue<int>()).IsEqualTo(0);
    }

    /// <summary>
    /// Positions are contiguous and zero-based — budget-wide across category groups, group-scoped
    /// across categories. The database only checks <c>position &gt;= 0</c>; contiguity and
    /// uniqueness are pure domain, owned by <see cref="CategoryOrdering" /> and
    /// <see cref="CategoryGroupOrdering" />, and nothing below them would notice their loss.
    /// </summary>
    [Test]
    public async Task Ordering_StaysContiguousAndZeroBasedAcrossASequenceOfMovesPlacementsAndDeletes()
    {
        // Arrange — three groups with several categories each, so every reindex below has siblings
        // to shift. One group per operation would leave the reindex loops running over lists of one,
        // where an off-by-one and a correct result are the same answer.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid essentialsId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid lifestyleId = await CreateCategoryGroupAsync(client, "Lifestyle");
        Guid sinkingId = await CreateCategoryGroupAsync(client, "Sinking Funds");
        Guid groceriesId = await CreateCategoryAsync(client, essentialsId, "Groceries");
        Guid utilitiesId = await CreateCategoryAsync(client, essentialsId, "Utilities");
        Guid rentId = await CreateCategoryAsync(client, essentialsId, "Rent");
        Guid diningId = await CreateCategoryAsync(client, lifestyleId, "Dining Out");
        Guid hobbiesId = await CreateCategoryAsync(client, lifestyleId, "Hobbies");
        Guid travelId = await CreateCategoryAsync(client, sinkingId, "Travel");

        // Act — a scripted sequence rather than a single operation, because the bug this guards
        // against lives in the reindex that follows an operation, not in the operation. The order is
        // chosen so each step is the *last* write to the list it reindexes: a step whose damage a
        // later step would repair proves nothing about the step. Moving Groceries out is the final
        // write to Essentials, deleting Dining Out is the final write to Lifestyle, and deleting the
        // emptied group is the final write to the budget's group list. Sinking Funds is moved to the
        // front first for the same reason — deleting the last group would leave its siblings already
        // contiguous, and the group-level gap close would be free.
        HttpResponseMessage moveGroup = await client.PatchAsJsonAsync(
            $"/api/category-groups/{sinkingId}/position",
            new { position = 0 });
        HttpResponseMessage placeAcrossGroups = await client.PatchAsJsonAsync(
            $"/api/categories/{groceriesId}/placement",
            new { categoryGroupId = lifestyleId, position = 1 });
        HttpResponseMessage emptyAGroup = await client.PatchAsJsonAsync(
            $"/api/categories/{travelId}/placement",
            new { categoryGroupId = lifestyleId, position = 0 });
        HttpResponseMessage deleteCategory = await client.DeleteAsync($"/api/categories/{diningId}");
        HttpResponseMessage deleteEmptiedGroup = await client.DeleteAsync(
            $"/api/category-groups/{sinkingId}");

        // Assert
        await Assert.That(moveGroup.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(placeAcrossGroups.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(emptyAGroup.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(deleteCategory.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(deleteEmptiedGroup.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // Read the rows, not /api/categories. The invariant is a property of stored state, and both
        // read services tie-break on Id (`orderby categoryGroup.Position, category.Position,
        // category.Id`), so a duplicate position is deterministic: it never surfaces as flakiness,
        // only as an item sitting quietly in the wrong place that the projection re-sorts into a
        // plausible order. CategoryHierarchy_CrudPlacementAndCustomOrderingWorkThroughApi asserts
        // positions through that projection and would not see a broken reindex at all.
        IReadOnlyList<CategoryGroupRow> groupRows = await ReadCategoryGroupRowsAsync(host);
        IReadOnlyList<CategoryRow> categoryRows = await ReadCategoryRowsAsync(host);

        // Set equality over the whole list, not a position per item: gaps and duplicates are both
        // failures of the set, and a list of individual position checks catches neither reliably.
        // Membership is asserted alongside it because a move that reindexed correctly but carried
        // the wrong row would satisfy the position set on its own.
        await AssertGroupPositionsAreContiguousAsync(groupRows, essentialsId, lifestyleId);
        await AssertGroupContentsAreContiguousAsync(categoryRows, essentialsId, utilitiesId, rentId);
        await AssertGroupContentsAreContiguousAsync(
            categoryRows, lifestyleId, travelId, groceriesId, hobbiesId);
        await Assert.That(categoryRows.Count).IsEqualTo(5);
    }

    [Test]
    public async Task EmptyGroupAndUnreferencedCategory_CanBeDeleted()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
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

    // CategoryGroupNames_AreCaseInsensitivelyUniquePerUser WAS HERE AND IS DELETED WITH NO
    // REPLACEMENT, because the rule it named no longer exists on this side. It posted "Essentials" and
    // then "essentials" and expected a 400: that worked because category_groups.name carried the
    // case_insensitive collation, and the collation left the column BY FORCE when it became bytea,
    // which is not a collatable type. Case folding did not disappear - it MOVED into the normalisation
    // the client applies before it computes the blind index - but it moved somewhere this server cannot
    // observe, cannot perform and cannot write a constraint against. There is no server behaviour left
    // to assert, so a weakened version of this case would be a test of the fixture rather than of the
    // product. The payees slice deleted two cases for the identical reason and also without
    // replacement; the surviving rule - one name per budget, over the blind index - is held by
    // RepositoryConstraintAttributionTests.AddCategoryGroup_WithADuplicateGroupName_TranslatesItsOwnUniqueIndex,
    // which now collides two rows on the SAME index value rather than on two spellings of one name.
    //
    // CategoryNames_AreCaseInsensitivelyUniqueAcrossGroups WAS HERE AND IS NOW DELETED TOO, WITH NO
    // REPLACEMENT AND FOR THE SAME REASON ONE PARAGRAPH UP. It posted "Groceries" into one group and
    // "groceries" into another and expected a 400 keyed on Name. categories.name has now made the move
    // category_groups.name made: it is bytea, bytea is not collatable, so the case_insensitive
    // collation left the column by force and this server can no longer tell two spellings of one name
    // apart. THE FOLDING DID NOT VANISH - it is trim, NFKC, full case fold, UTF-8, applied in the
    // browser before the HMAC - but it now happens where no case here can reach it, and its executable
    // statement is the client's known-answer table rather than anything in this suite. Uniqueness
    // itself survives, as a collision on identical blind-index bytes.
    //
    // THIS IS THE THIRD INSTANCE OF THIS DELETION AND THE PRECEDENT IS RECORDED: the payees slice
    // deleted two such cases with no replacement, for the identical reason, and CLAUDE.md says so.
    // A reviewer meeting a deleted assertion with nothing put in its place should find the answer
    // here rather than having to ask for it.

    /// <summary>
    /// A category's note survives <c>POST</c> and comes back on BOTH read routes — the list and the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>GET /api/categories</c> could stop returning <c>description</c> entirely and this suite
    /// stayed green.</b> The only non-null category note anywhere in the tree was created in
    /// <c>DataExportCompletenessTests</c> and read back THROUGH THE EXPORT — a different projection in a
    /// different service. <c>CategoryReadService</c>'s own remarks describe exactly this trap, and until
    /// this case existed nothing held it: the column is nullable, so a projection that dropped the member
    /// writes no error, violates no constraint, and answers 200 with a note nobody can tell from a note
    /// that was never filed.
    /// </para>
    /// <para>
    /// <b>Both routes, because they are two projections.</b> The list and the by-id read are separate
    /// query shapes; one may carry the member while the other does not, and a case reading only one would
    /// certify half a surface.
    /// </para>
    /// <para>
    /// <b>The note is asserted as the ENVELOPE, and non-null.</b> An assertion that the member is present
    /// passes on JSON null; one that reads it back byte-for-byte cannot.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PostThenGetCategory_CarriesTheNoteThroughTheListAndTheRow()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid groupId = await CreateCategoryGroupAsync(client, "Essentials");
        var id = Guid.CreateVersion7();

        // Act
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/categories", new
        {
            id = id.ToString("D"),
            name = SealedNarrative.EncodedName("Groceries"),
            nameKey = SealedNarrative.EncodedIndex("Groceries"),
            description = SealedNarrative.EncodedDescription("Weekly food shop"),
            categoryGroupId = groupId,
        });
        JsonNode body = (await JsonNode.ParseAsync(await created.Content.ReadAsStreamAsync()))!;
        JsonNode listed = await GetJsonAsync(client, "/api/categories");
        JsonNode row = await GetJsonAsync(client, $"/api/categories/{id}");

        // Assert — the 201 body first, then the two reads. The 201 is built from the entity the handler
        // just wrote, so on its own it is a picture of the request rather than evidence about the column;
        // the two GETs are what go back to the database.
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(body["description"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedDescription("Weekly food shop"));
        JsonNode item = listed["items"]!.AsArray().Single()!;
        await Assert.That(item["description"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedDescription("Weekly food shop"));
        await Assert.That(row["description"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedDescription("Weekly food shop"));
    }

    /// <summary>
    /// A <c>PUT</c> that replaces the note writes it, and a <c>PUT</c> that omits one clears it.
    /// </summary>
    /// <remarks>
    /// <b>The write path the slice's own brief named, and nothing read it back.</b> One existing case
    /// sends a <c>description</c> and never reads it; another sends null. That is defect six from the
    /// brief — "a lost description is invisible where a lost name is <c>23502</c>" — alive on a route that
    /// was named in the plan. Both legs are in ONE case deliberately: split apart, a handler that ignored
    /// the member entirely passes the clearing leg (the seed had a note, the PUT omits one, the column
    /// ends null either way) while only the replacing leg reddens, and the pair is what tells "wrote the
    /// new note" from "did nothing".
    /// </remarks>
    [Test]
    public async Task PutCategory_ReplacesTheNoteAndThenClearsIt()
    {
        // Arrange — a category that already HOLDS a note, so a replacement is a change rather than a
        // first write.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid groupId = await CreateCategoryGroupAsync(client, "Essentials");
        var id = Guid.CreateVersion7();
        (await client.PostAsJsonAsync("/api/categories", new
        {
            id = id.ToString("D"),
            name = SealedNarrative.EncodedName("Groceries"),
            nameKey = SealedNarrative.EncodedIndex("Groceries"),
            description = SealedNarrative.EncodedDescription("Weekly food shop"),
            categoryGroupId = groupId,
        })).EnsureSuccessStatusCode();

        // Act — replace, read, then clear and read again.
        HttpResponseMessage replaced = await client.PutAsJsonAsync($"/api/categories/{id}", new
        {
            name = SealedNarrative.EncodedName("Groceries"),
            nameKey = SealedNarrative.EncodedIndex("Groceries"),
            description = SealedNarrative.EncodedDescription("Corner shop and the market"),
        });
        JsonNode afterReplace = await GetJsonAsync(client, $"/api/categories/{id}");
        HttpResponseMessage cleared = await client.PutAsJsonAsync($"/api/categories/{id}", new
        {
            name = SealedNarrative.EncodedName("Groceries"),
            nameKey = SealedNarrative.EncodedIndex("Groceries"),
            description = (string?)null,
        });
        JsonNode afterClear = await GetJsonAsync(client, $"/api/categories/{id}");

        // Assert — the replacement is read back BY VALUE, which is the half that was missing; the clear
        // is read back as JSON null, which is what an absent member means on a PUT because a PUT is a
        // full replacement.
        await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(afterReplace["description"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedDescription("Corner shop and the market"));
        await Assert.That(cleared.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(afterClear["description"] is null || afterClear["description"]!.GetValueKind()
            == System.Text.Json.JsonValueKind.Null).IsTrue();
    }

    /// <summary>
    /// <c>POST /api/categories</c> refuses a body carrying a member the shape does not declare, and
    /// writes nothing.
    /// </summary>
    /// <remarks>
    /// <b>The attribute is on <c>CreateCategoryCommand</c> and nothing sent it a misspelled member.</b>
    /// Every <c>descriptionn</c> in this tree goes to a category GROUP route, and the attribute is
    /// per-type — one declaration says nothing about another. The argument the group's own cases make
    /// transfers verbatim and is not restated: under the global <c>Skip</c> a misspelled
    /// <c>descriptionn</c> binds identically to an absent member, so this route answered 201 with a note
    /// that never arrived.
    /// </remarks>
    [Test]
    public async Task PostCategory_WithAnUnknownMember_IsRefusedAndWritesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid groupId = await CreateCategoryGroupAsync(client, "Essentials");

        // Act — every declared member is correct and well-formed; the ONLY fault is the extra one.
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/categories", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Groceries"),
            nameKey = SealedNarrative.EncodedIndex("Groceries"),
            descriptionn = SealedNarrative.EncodedDescription("Weekly food shop"),
            categoryGroupId = groupId,
        });
        JsonNode listed = await GetJsonAsync(client, "/api/categories");

        // Assert — refused, and nothing filed. A refusal that still wrote the row is worse than the
        // silent 201 it replaces: the caller is told the write failed while the note-less category lands.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(listed["items"]!.AsArray().Count).IsEqualTo(0);
    }

    /// <summary>
    /// <c>PUT /api/categories/{id}</c> refuses the same, and the note the category held is still there.
    /// </summary>
    /// <remarks>
    /// <b>The destructive half, and it is a separate declaration.</b>
    /// <c>CategoryEndpoints.UpdateCategoryRequest</c> is a different type in a different project from
    /// <c>CreateCategoryCommand</c>, so the case above says nothing about it. On a PUT an unmapped
    /// <c>description</c> is not a note that fails to arrive — it is a note that is DELETED, because the
    /// route reads an absent member as "this category has none". The surviving description is therefore
    /// the assertion that matters; the status alone is satisfied by a refusal that already cleared the
    /// column.
    /// </remarks>
    [Test]
    public async Task PutCategory_WithAnUnknownMember_IsRefusedAndLeavesTheNoteStanding()
    {
        // Arrange — a category that HOLDS a note, or there is nothing for the refusal to fail to destroy.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid groupId = await CreateCategoryGroupAsync(client, "Essentials");
        var id = Guid.CreateVersion7();
        (await client.PostAsJsonAsync("/api/categories", new
        {
            id = id.ToString("D"),
            name = SealedNarrative.EncodedName("Groceries"),
            nameKey = SealedNarrative.EncodedIndex("Groceries"),
            description = SealedNarrative.EncodedDescription("Weekly food shop"),
            categoryGroupId = groupId,
        })).EnsureSuccessStatusCode();

        // Act — a rename that also means to change the note, with the note's member misspelled.
        HttpResponseMessage update = await client.PutAsJsonAsync($"/api/categories/{id}", new
        {
            name = SealedNarrative.EncodedName("Food Shopping"),
            nameKey = SealedNarrative.EncodedIndex("Food Shopping"),
            descriptionn = SealedNarrative.EncodedDescription("Corner shop and the market"),
        });
        JsonNode after = await GetJsonAsync(client, $"/api/categories/{id}");

        // Assert — refused, and BOTH columns exactly as they were.
        await Assert.That(update.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(after["description"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedDescription("Weekly food shop"));
        await Assert.That(after["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Groceries"));
    }

    /// <summary>
    /// A retried <c>POST /api/categories</c> carrying an identifier the budget already holds answers
    /// <b>409</b>, not 500.
    /// </summary>
    /// <remarks>
    /// <b>The repository-level attribution case is not this claim.</b> That one proves
    /// <c>CategoryRepository.AddAsync</c> raises a <c>ConflictException</c>; this one proves the wire
    /// carries it as a 409. Measured by the code owner on the sibling arm: an unreachable translation
    /// degrades not into a neighbouring status but into a <b>500</b>, so the wire half is not decoration
    /// — a client retrying after a network timeout is the most ordinary thing an HTTP client does, and it
    /// is exactly the request that produces this collision.
    /// </remarks>
    [Test]
    public async Task PostCategory_WithADuplicateIdentifier_AnswersConflict()
    {
        // Arrange — a category, then the byte-identical body again, which is what a retry sends.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid groupId = await CreateCategoryGroupAsync(client, "Essentials");
        var id = Guid.CreateVersion7();
        object body = new
        {
            id = id.ToString("D"),
            name = SealedNarrative.EncodedName("Groceries"),
            nameKey = SealedNarrative.EncodedIndex("Groceries"),
            description = (string?)null,
            categoryGroupId = groupId,
        };
        (await client.PostAsJsonAsync("/api/categories", body)).EnsureSuccessStatusCode();

        // Act
        HttpResponseMessage retried = await client.PostAsJsonAsync("/api/categories", body);

        // Assert — the status AND the sentence. A retry and a reused identifier are two readings this
        // server cannot tell apart, so the detail is what gives each its own next step; a 409 carrying
        // the duplicate-NAME sentence would send somebody to change a name that is not in dispute.
        await Assert.That(retried.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        JsonNode problem = (await JsonNode.ParseAsync(await retried.Content.ReadAsStreamAsync()))!;
        await Assert.That(problem["detail"]!.GetValue<string>()).IsEqualTo(
            "A category already exists with this identifier. If this request is a retry, read that "
            + "category back by its identifier instead of posting it again; otherwise mint a fresh "
            + "identifier and post again.");
    }

    [Test]
    public async Task DeletionGuards_ProtectNonEmptyGroupsAndReferencedCategories()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid accountId = await CreateAccountAsync(client);
        Guid categoryGroupId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid categoryId = await CreateCategoryAsync(client, categoryGroupId, "Groceries");
        HttpResponseMessage transaction = await client.PostAsJsonAsync("/api/transactions", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            amount = -20m,
            date = "2026-07-14",
            accountId,
            description = SealedNarrative.EncodedDescription("Food"),
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
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid accountId = await CreateAccountAsync(client);
        Guid essentialsId = await CreateCategoryGroupAsync(client, "Essentials");
        Guid lifestyleId = await CreateCategoryGroupAsync(client, "Lifestyle");
        Guid categoryId = await CreateCategoryAsync(client, essentialsId, "Groceries");
        HttpResponseMessage createTransaction = await client.PostAsJsonAsync("/api/transactions", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            amount = -20m,
            date = "2026-07-14",
            accountId,
            description = SealedNarrative.EncodedDescription("Food"),
            categoryId,
        });
        createTransaction.EnsureSuccessStatusCode();

        // Act
        // The PUT names two of the four mutable columns and NOT the description, which is why this
        // case is not the grant control for this table: EF emits only what changed, so this statement
        // succeeds under a grant that has forgotten `description`. The four-column control lives in
        // TenancySchemaTests and the route-level one is its own case.
        //
        // "Sinking Funds" AND NOT "Discretionary", and this is the label choice the whole encoding
        // class turns on. TransactionDto.CategoryGroupName is asserted in EXACTLY ONE PLACE — the last
        // line of this case — so whatever label it renames to is the entire evidence that
        // TransactionReadService encodes that member as base64url. Name("Discretionary") is 42 bytes
        // that spell IDENTICALLY under padded standard base64 and unpadded base64url, so swapping
        // PasskeyEncoding.Encode for Convert.ToBase64String on that line reddened nothing anywhere in
        // the product. "Sinking Funds" is 42 bytes too and carries a 62/63 byte, so the two alphabets
        // disagree about it. SealedNarrative.EncodedName holds the measurement and the list of the four
        // blind labels; the property is not readable off the length, which is what this pair proves.
        await client.PutAsJsonAsync(
            $"/api/category-groups/{lifestyleId}",
            new
            {
                name = SealedNarrative.EncodedName("Sinking Funds"),
                nameKey = SealedNarrative.EncodedIndex("Sinking Funds"),
                description = (string?)null,
            });
        // The second genuine rename in the file, for the reason stated on the first.
        await client.PutAsJsonAsync(
            $"/api/categories/{categoryId}",
            new
            {
                name = SealedNarrative.EncodedName("Food Shopping"),
                nameKey = SealedNarrative.EncodedIndex("Food Shopping"),
                description = (string?)null,
            });
        await client.PatchAsJsonAsync(
            $"/api/categories/{categoryId}/placement",
            new { categoryGroupId = lifestyleId, position = 0 });
        JsonNode transactions = await GetJsonAsync(client, "/api/transactions");
        JsonNode item = transactions["items"]!.AsArray().Single()!;

        // Assert
        await Assert.That(item["categoryId"]!.GetValue<Guid>()).IsEqualTo(categoryId);
        await Assert.That(item["categoryName"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Food Shopping"));
        await Assert.That(item["categoryGroupId"]!.GetValue<Guid>()).IsEqualTo(lifestyleId);
        await Assert.That(item["categoryGroupName"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Sinking Funds"));

        // The precondition for the line above, stated rather than trusted, and it belongs to this
        // assertion and not to the fixture: this is the ONLY assertion of TransactionDto's group name
        // in either suite, so if the label were blind nothing in the product would be pinning
        // TransactionReadService's alphabet at all. It failed against the label this case shipped with.
        await Assert.That(Convert.ToBase64String(
                SealedNarrative.Name("Sinking Funds").Envelope.ToArray()))
            .IsNotEqualTo(SealedNarrative.EncodedName("Sinking Funds"));
    }

    [Test]
    public async Task CategoryResources_AreIsolatedByBudgetAndLegacyGroupsRouteIsGone()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient clientA = (await host.Factory.CreateSignedInClientAsync("google-a")).Client;
        HttpClient clientB = (await host.Factory.CreateSignedInClientAsync("google-b")).Client;
        Guid categoryGroupA = await CreateCategoryGroupAsync(clientA, "Essentials");
        await CreateCategoryAsync(clientA, categoryGroupA, "Groceries");

        // Act
        JsonNode groupsB = await GetJsonAsync(clientB, "/api/category-groups");
        JsonNode categoriesB = await GetJsonAsync(clientB, "/api/categories");
        HttpResponseMessage crossBudgetCreate = await clientB.PostAsJsonAsync("/api/categories", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Attempt"),
            nameKey = SealedNarrative.EncodedIndex("Attempt"),
            description = (string?)null,
            categoryGroupId = categoryGroupA,
        });
        HttpResponseMessage crossBudgetRead = await clientB.GetAsync(
            $"/api/category-groups/{categoryGroupA}");
        // THE BODY HAS TO BE WELL-FORMED OR THIS ASSERTION STOPS BEING ABOUT ISOLATION. The update
        // handler runs its whole decode block ABOVE GetByIdAsync — its own remarks argue why — so a
        // plaintext name answers 400 on the shape of the request and the query filter is never
        // consulted. That is a correct 400 and a worthless test: it would stay green with the budget
        // filter deleted. Sealed and indexed here, the request is admissible, the lookup runs, the
        // filter finds nothing in client B's budget, and the 404 below is the isolation verdict.
        HttpResponseMessage crossBudgetUpdate = await clientB.PutAsJsonAsync(
            $"/api/category-groups/{categoryGroupA}",
            new
            {
                name = SealedNarrative.EncodedName("Renamed"),
                nameKey = SealedNarrative.EncodedIndex("Renamed"),
                description = (string?)null,
            });
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
                Guid.CreateVersion7(),
                budgetA,
                SealedNarrative.Indexed("Essentials"),
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
            Guid.CreateVersion7(),
            budgetB,
            categoryGroupId,
            SealedNarrative.Indexed("Should Fail"),
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

    /// <summary>
    /// <c>category_groups.name</c> and <c>category_groups.description</c> are <c>bytea</c> carrying no
    /// collation, and the unique index moved to <c>(budget_id, name_key)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The payee and account cases argue the shape; what this table adds is a THIRD column to check and
    /// a nullability to check with it. <c>description</c> is <c>bytea</c> like its neighbour and
    /// <c>IS NULLABLE</c> unlike it, and both halves of that are load-bearing: a description typed
    /// <c>text</c> would take plaintext straight from a handler that forgot to seal, and a description
    /// made <c>NOT NULL</c> would refuse every group nobody annotated — the ordinary case.
    /// </para>
    /// <para>
    /// <b>The index definition is compared WHOLE and never with Contains.</b> <c>pg_get_indexdef</c>
    /// prints the index name inside the text it returns, and this index is called
    /// <c>IX_category_groups_budget_id_name_key</c> — so <c>Contains("name_key")</c> is satisfied by the
    /// name alone and passes over an index whose columns are <c>(budget_id, name)</c>.
    /// </para>
    /// </remarks>
    [Test]
    public async Task CategoryGroupsTable_HasNoCollationAndAUniqueIndexOverTheBlindIndex()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act
        await using NpgsqlCommand columnsCommand = new(
            """
            select column_name, data_type, is_nullable, collation_name
            from information_schema.columns
            where table_name = 'category_groups' and column_name in ('name', 'name_key', 'description')
            order by column_name
            """, connection);
        Dictionary<string, (string Type, string Nullable, object? Collation)> columns = [];
        await using (NpgsqlDataReader reader = await columnsCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns[reader.GetString(0)] =
                    (reader.GetString(1), reader.GetString(2), reader.GetValue(3));
            }
        }

        await using NpgsqlCommand indexCommand = new(
            """
            select indexdef
            from pg_indexes
            where tablename = 'category_groups'
              and indexname = 'IX_category_groups_budget_id_name_key'
            """, connection);
        string? indexDef = (string?)await indexCommand.ExecuteScalarAsync();

        await using NpgsqlCommand retiredIndexCommand = new(
            """
            select indexdef
            from pg_indexes
            where tablename = 'category_groups' and indexname = 'IX_category_groups_budget_id_name'
            """, connection);
        object? retiredIndexDef = await retiredIndexCommand.ExecuteScalarAsync();

        // Assert — all three columns are opaque bytes. The collation is not a different one, it is
        // GONE: bytea is not collatable, so case_insensitive left this column by force rather than by
        // choice, and what it used to do — make "Essentials" and "essentials" one group — is now the
        // client's, which folds before it computes the index.
        await Assert.That(columns["name"].Type).IsEqualTo("bytea");
        await Assert.That(columns["name_key"].Type).IsEqualTo("bytea");
        await Assert.That(columns["description"].Type).IsEqualTo("bytea");
        await Assert.That(columns["name"].Collation is null or DBNull).IsTrue();

        // The nullability split IS the description's whole shape, asserted in both directions so that
        // neither half can move alone. name and name_key together are "a row cannot hold half a name";
        // description being nullable is what represents a group nobody annotated.
        await Assert.That(columns["name"].Nullable).IsEqualTo("NO");
        await Assert.That(columns["name_key"].Nullable).IsEqualTo("NO");
        await Assert.That(columns["description"].Nullable).IsEqualTo("YES");

        await Assert.That(indexDef).IsEqualTo(
            """CREATE UNIQUE INDEX "IX_category_groups_budget_id_name_key" ON public.category_groups USING btree (budget_id, name_key)""");

        // And the old one is not still standing beside it. Without this line a baseline that added the
        // new index and kept the old one would pass everything above while leaving a unique constraint
        // over ciphertext — one that refuses nothing, because every seal draws a fresh nonce.
        await Assert.That(retiredIndexDef is null or DBNull).IsTrue();
    }

    /// <summary>
    /// A zero-length <c>name</c> is refused as <c>23514</c> naming
    /// <c>CK_category_groups_name_length</c>, never as <c>2202E</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE SEEDED ROW'S DESCRIPTION IS NULL AND THAT IS THE CASE, NOT AN ACCIDENT.</b> Measured on
    /// postgres:17.10: PostgreSQL reports a multiply-violating row under whichever constraint sorts
    /// first ALPHABETICALLY, and that ordering crosses columns on this table —
    /// <c>description_length</c> &lt; <c>description_version</c> &lt; <c>name_key_length</c> &lt;
    /// <c>name_length</c> &lt; <c>name_version</c> &lt; <c>position</c>. So a row bad in the name AND
    /// bad in the description comes back naming the DESCRIPTION, and this case would assert the wrong
    /// constraint while looking like it passed. A NULL description satisfies both description checks
    /// vacuously, which is what leaves the name's the first one that can fire.
    /// </para>
    /// <para>
    /// <b>THE SAME ORDERING IS WHY THE CONSTRAINT NAME IS ASSERTED AND NOT MERELY BOUNDED.</b> A
    /// zero-length name violates the floor and the version rule together, so the reported name is
    /// decided by that alphabet and by nothing else: <c>name_length</c> sorts before
    /// <c>name_version</c>, and the answer is <c>CK_category_groups_name_length</c> every time. The
    /// assertion at the bottom of this case says so. A membership test over the pair would be this file
    /// claiming, forty-five lines apart, that the ordering both is and is not knowable.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Database_RefusesAZeroLengthName_WithACheckViolation()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Non-vacuity first: the same statement with a well-formed envelope goes through, so a refusal
        // below cannot be the insert shape being wrong.
        Guid seeded = await InsertCategoryGroupAsync(
            connection,
            budgetId,
            SealedNarrative.Name("Everyday").Envelope.ToArray(),
            SealedNarrative.BlindIndex("Everyday").ToArray(),
            description: null,
            position: 0);

        // Act
        PostgresException onInsert = await ThrowsPostgresExceptionAsync(() => InsertCategoryGroupAsync(
            connection,
            budgetId,
            [],
            SealedNarrative.BlindIndex("Blank name").ToArray(),
            description: null,
            position: 1));

        PostgresException onUpdate = await ThrowsPostgresExceptionAsync(async () =>
        {
            await using NpgsqlCommand blank = new(
                "update category_groups set name = @name where id = @id", connection);
            blank.Parameters.AddWithValue("name", Array.Empty<byte>());
            blank.Parameters.AddWithValue("id", seeded);
            await blank.ExecuteNonQueryAsync();
        });

        // Assert — 23514 is the whole claim, and it is what 2202E is not. WHAT EARNS IT IS THE LENGTH
        // BAND AND NOT THE SPELLING, which corrects the obvious reading: name_key_length and
        // name_length both sort ahead of name_version, so a zero-length name is refused as a length
        // violation before CK_category_groups_name_version is evaluated at all. Measured on
        // postgres:17.10 over a table carrying all six shipped constraints with BOTH version checks
        // spelled get_byte: no probe produced 2202E, and every refusal came back 23514 under a length
        // constraint. So this case would NOT redden on a get_byte spelling, and no other case in this
        // repository would either — the wrong predicate is unreachable through the schema as declared,
        // and measuring it needs a container probe over a table carrying a version check alone.
        // substring is still the right spelling, for a reason that owes nothing to the alphabet: it is
        // TOTAL over every length the column can hold, answering false where get_byte raises. That rule
        // is held by review and by that probe, not by this case. What this case does hold is the length
        // floor: delete it and the version predicate reaches the value, answers false rather than
        // raising, and the refusal arrives under CK_category_groups_name_version — reddening the
        // constraint-name assertion below, which is the only line here that can see the difference.
        await Assert.That(onInsert.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(onUpdate.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);

        // THE EXACT NAME, because the ordering is deterministic and this case already depends on that.
        // A zero-length value violates the floor AND the version rule, and PostgreSQL reports a
        // multiply-violating row under whichever constraint sorts first ALPHABETICALLY - which is the
        // same fact the remarks above lean on to explain why the seeded description must be NULL. Only
        // one reading can be true: either the alphabet decides, in which case `length` < `version` and
        // the answer is knowable, or it does not, in which case the NULL description buys nothing. It
        // decides, measured on postgres:17.10 - a zero-length name beside a NULL description reports
        // CK_category_groups_name_length - so the name is asserted rather than admitted as one of two.
        //
        // PayeeIntegrationTests.Database_RefusesAZeroLengthPayeeName_WithACheckViolationRatherThanAFatal
        // faces the identical pair and CHOOSES MEMBERSHIP, arguing that pinning the name turns a rename
        // into a failure. That is a real argument and this is not a correction of it: what differs is
        // that THIS file has already spent the determinism. Its remarks explain the seeded NULL
        // description by the alphabet crossing columns on this table, so a membership test 45 lines
        // later says the ordering is unknowable while the case above it only works because it is not.
        // One of the two had to go, and the assertion is the cheaper one to keep honest.
        await Assert.That(onInsert.ConstraintName).IsEqualTo("CK_category_groups_name_length");
        await Assert.That(onUpdate.ConstraintName).IsEqualTo("CK_category_groups_name_length");
        await Assert.That(onInsert.TableName).IsEqualTo("category_groups");
    }

    /// <summary>
    /// A blind index of any width but exactly 32 bytes is refused by
    /// <c>CK_category_groups_name_key_length</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>31 AND 33, because one of them alone measures a ceiling rather than a width.</b> The
    /// constraint is written <c>= 32</c> and both <c>SchemaConstraintSnapshotTests</c> and
    /// <c>BudgetoidDbContextConstructionTests</c> pin that as text — but a pin is not a firing. Until
    /// this case existed, <c>&lt;= 32</c> would have shipped green in every sense that matters: it
    /// refuses 33 exactly as the equality does, and admits a 31-byte digest that stores, reads back,
    /// keys perfectly, never collides and matches no group the client will ever look for. Nothing on
    /// this side can recompute it to notice, because the index key lives in a browser.
    /// </para>
    /// <para>
    /// The description is <see langword="null" /> and the name is a well-formed envelope for the
    /// reason the zero-length name case above states at length: PostgreSQL reports a multiply-violating
    /// row under whichever constraint sorts first alphabetically, and <c>description_length</c> and
    /// <c>description_version</c> both sort ahead of <c>name_key_length</c>. A row bad in the width AND
    /// bad in anything else would come back naming the other thing, and this case would assert the
    /// wrong constraint while looking like it passed.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(31)]
    [Arguments(33)]
    public async Task Database_RefusesABlindIndexThatIsNotExactlyThirtyTwoBytes(int width)
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Non-vacuity, and it has to come first: the same insert with the one legal width goes
        // through, so the refusal below is about the width and not about the statement.
        await InsertCategoryGroupAsync(
            connection,
            budgetId,
            SealedNarrative.Name("Everyday").Envelope.ToArray(),
            SealedNarrative.BlindIndex("Everyday").ToArray(),
            description: null,
            position: 0);

        // Act
        PostgresException refusal = await ThrowsPostgresExceptionAsync(() => InsertCategoryGroupAsync(
            connection,
            budgetId,
            SealedNarrative.Name("Wrong width").Envelope.ToArray(),
            new byte[width],
            description: null,
            position: 1));

        // Assert — the constraint is named beside the SQLSTATE because every other check on this table
        // raises 23514 as well, and the case would otherwise pass on the wrong rejection.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo("CK_category_groups_name_key_length");
        await Assert.That(refusal.TableName).IsEqualTo("category_groups");
    }

    /// <summary>
    /// The other two rules on <c>category_groups.name</c> fired, rather than merely rendered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The twin of <c>PayeeIntegrationTests.Database_RefusesANameTheEnvelopeRulesForbid</c>, and it
    /// is owed here rather than inherited.</b> A constraint is a property of a table; the payees case
    /// fires <c>CK_payees_name_*</c> and can say nothing about a second table's copy of the same rule.
    /// Before this case, <c>CiphertextEnvelope.MinimumLength</c> was named exactly once in this file —
    /// in a description case — and neither <c>CK_category_groups_name_length</c> nor
    /// <c>CK_category_groups_name_version</c> had ever fired.
    /// </para>
    /// <para>
    /// <b>Neither shape is reachable through the API, which is why they are written raw.</b>
    /// <c>CiphertextEnvelopeText</c> refuses an over-cap envelope and an unimplemented version before
    /// either reaches a row, so a route-level case measures the Application ring's copy of the rule and
    /// says nothing about the column's. MEASURED on postgres:17.10, by altering the constraint on a live
    /// container: rewrite the floor as <c>length(name) between 1 and 1024</c> and the twenty-eight-byte
    /// value below STORES, while <c>Database_RefusesAZeroLengthName_WithACheckViolation</c> beside it
    /// stays green — zero is still outside a floor of one, so that case answers <c>23514</c> naming
    /// <c>CK_category_groups_name_length</c> exactly as before and cannot see the widening. Drop the
    /// version check outright and the version value below stores while every other case in this file
    /// stays green. The 1024 ceiling had never fired at all.
    /// </para>
    /// <para>
    /// The floor argument is the one a reader will think the zero-length case already covers, and it
    /// does not — the measurement above is what says so. Only a well-versioned envelope ONE BYTE short
    /// can tell 29 from any smaller number, and 29 is where
    /// <see cref="CiphertextEnvelope.MinimumLength" /> puts it because a version, a nonce and a tag over
    /// an empty plaintext is the shortest thing the framing can produce.
    /// </para>
    /// <para>
    /// The constraint names ARE asserted here, unlike on the zero-length case: each of these values
    /// violates exactly one check, so which one PostgreSQL reports is decided by the value rather than
    /// by the alphabet.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("one byte under the framing floor", "CK_category_groups_name_length")]
    [Arguments("one byte over the column's cap", "CK_category_groups_name_length")]
    [Arguments("a version this deployment has never implemented", "CK_category_groups_name_version")]
    public async Task Database_RefusesANameTheEnvelopeRulesForbid(string shape, string constraint)
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Non-vacuity: the same statement with a well-formed envelope goes through.
        await InsertCategoryGroupAsync(
            connection,
            budgetId,
            SealedNarrative.Name("Everyday").Envelope.ToArray(),
            SealedNarrative.BlindIndex("Everyday").ToArray(),
            description: null,
            position: 0);

        // Act — the index is exactly 32 bytes and the description is NULL, or the alphabet would report
        // name_key_length, description_length or description_version and this case would pass on the
        // wrong refusal: the five sort description_length < description_version < name_key_length <
        // name_length < name_version.
        PostgresException refusal = await ThrowsPostgresExceptionAsync(() => InsertCategoryGroupAsync(
            connection,
            budgetId,
            MalformedNameEnvelope(shape),
            SealedNarrative.BlindIndex(shape).ToArray(),
            description: null,
            position: 1));

        // Assert
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo(constraint);
        await Assert.That(refusal.TableName).IsEqualTo("category_groups");
    }

    /// <summary>
    /// A description of a legal LENGTH but leading with a version this deployment has never implemented
    /// is refused by <c>CK_category_groups_description_version</c>.
    /// </summary>
    /// <remarks>
    /// <b>The one description rule nothing fired.</b> Its neighbour above catches a zero-length value
    /// and the cap case catches one byte too many — both land on
    /// <c>CK_category_groups_description_length</c>, so delete the version check outright and every
    /// description case in this file stays green while the column starts accepting bytes no version of
    /// this deployment can interpret. The value is exactly
    /// <see cref="CiphertextEnvelope.MinimumLength" /> bytes so the length check passes and the version
    /// check is the only one left that can fire, and its leading byte is derived from
    /// <see cref="CiphertextEnvelope.Version" /> rather than written as <c>2</c>, so a deployment that
    /// implemented a second version moves this with it.
    /// </remarks>
    [Test]
    public async Task Database_RefusesADescriptionVersionThisDeploymentHasNeverImplemented()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        byte[] wrongVersion = new byte[CiphertextEnvelope.MinimumLength];
        wrongVersion[0] = CiphertextEnvelope.Version + 1;

        // Non-vacuity: the same statement carrying a well-versioned description of the SAME length is
        // accepted, so the refusal below is about the leading byte and not about the length.
        await InsertCategoryGroupAsync(
            connection,
            budgetId,
            SealedNarrative.Name("Everyday").Envelope.ToArray(),
            SealedNarrative.BlindIndex("Everyday").ToArray(),
            SealedNarrative.Description().Envelope.ToArray(),
            position: 0);

        // Act
        PostgresException refusal = await ThrowsPostgresExceptionAsync(() => InsertCategoryGroupAsync(
            connection,
            budgetId,
            SealedNarrative.Name("Emptied note").Envelope.ToArray(),
            SealedNarrative.BlindIndex("Emptied note").ToArray(),
            wrongVersion,
            position: 1));

        // Assert
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName)
            .IsEqualTo("CK_category_groups_description_version");
        await Assert.That(refusal.TableName).IsEqualTo("category_groups");
    }

    /// <summary>
    /// A PRESENT, zero-length <c>description</c> is refused as <c>23514</c> naming
    /// <c>CK_category_groups_description_length</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not the previous case with a different column, and a reviewer will say that it is.</b>
    /// The name is <c>NOT NULL</c>, so absent and empty are one thing there; here they are two, and only
    /// one of them is refused. A NULL description is the ordinary row — a group nobody annotated —
    /// while a present, zero-length one is what a client sending an empty <c>bytea</c> produces, and
    /// this is the only place in the file that hands the column that value on an <c>INSERT</c> and on
    /// an <c>UPDATE</c>.
    /// </para>
    /// <para>
    /// <b>WHAT IT DEFENDS IS THE LENGTH FLOOR, and an earlier reading of it claimed more.</b> Remove
    /// the floor from <c>CK_category_groups_description_length</c> — leaving the ceiling, which
    /// <c>Database_AcceptsAnAbsentDescriptionAndOneAtTheCap</c> holds — and this case reddens on the
    /// constraint NAME rather than on the SQLSTATE: measured on postgres:17.10, the version predicate
    /// then reaches the value, answers false rather than raising, and the refusal arrives as
    /// <c>23514</c> under <c>CK_category_groups_description_version</c>.
    /// </para>
    /// <para>
    /// <b>It does NOT catch a version check spelled with <c>get_byte</c>, and nothing in this suite
    /// does.</b> Measured on postgres:17.10: <c>get_byte(NULL::bytea, 0)</c> answers <c>NULL</c> and
    /// does not raise, while <c>get_byte(''::bytea, 0)</c> raises <c>2202E</c> —
    /// <c>index 0 out of valid range, 0..-1</c> — from inside a CHECK on a NULLABLE column, so the
    /// wrong spelling bites on exactly the value below and on no other. But <c>description_length</c>
    /// sorts ahead of <c>description_version</c>, and a multiply-violating row is reported under
    /// whichever constraint sorts first, so the floor reaches that value first and the version
    /// predicate is never evaluated. Measured over a table carrying all six shipped constraints with
    /// both version checks spelled <c>get_byte</c>: no probe produced <c>2202E</c>, and every refusal
    /// came back <c>23514</c> under a length constraint. The wrong spelling is unreachable through the
    /// schema as declared; <c>substring</c> is right because it is <i>total</i> over every length the
    /// column can hold, which no ordering can take away, and that rule is held by review and by a
    /// container probe over a table carrying a version check alone — not by this case.
    /// </para>
    /// <para>
    /// Insert and update are both asserted because the constraint has to bite on both, and the update
    /// leg carries a second claim of its own: a row that legitimately held a description can be
    /// emptied to <c>''</c> by any statement the app role is allowed to issue.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Database_RefusesAZeroLengthDescription_WithACheckViolation()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Non-vacuity: the same statement carrying a well-formed description is accepted.
        Guid seeded = await InsertCategoryGroupAsync(
            connection,
            budgetId,
            SealedNarrative.Name("Everyday").Envelope.ToArray(),
            SealedNarrative.BlindIndex("Everyday").ToArray(),
            SealedNarrative.Description("Rent, food and the bus").Envelope.ToArray(),
            position: 0);

        // Act — the name beside it is well-formed, or the alphabet would report a name constraint and
        // this case would pass on the wrong refusal.
        PostgresException onInsert = await ThrowsPostgresExceptionAsync(() => InsertCategoryGroupAsync(
            connection,
            budgetId,
            SealedNarrative.Name("Empty note").Envelope.ToArray(),
            SealedNarrative.BlindIndex("Empty note").ToArray(),
            description: [],
            position: 1));

        PostgresException onUpdate = await ThrowsPostgresExceptionAsync(async () =>
        {
            await using NpgsqlCommand blank = new(
                "update category_groups set description = @description where id = @id", connection);
            blank.Parameters.AddWithValue("description", Array.Empty<byte>());
            blank.Parameters.AddWithValue("id", seeded);
            await blank.ExecuteNonQueryAsync();
        });

        // Assert — 23514 AND the constraint name, and the name is the half that can move. The SQLSTATE
        // is what a length band earns on any spelling; naming description_length is what says the FLOOR
        // did the refusing rather than the version check. description_length sorts ahead of
        // description_version, so the alphabet has no choice to make here and a report naming the
        // version check is a real disagreement — it is what a deleted floor produces, measured.
        // The name case above pins its constraint too, for the reason its own remarks give; the payee
        // twin chooses membership, which is an argument about renames and not about this ordering.
        await Assert.That(onInsert.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(onInsert.ConstraintName)
            .IsEqualTo("CK_category_groups_description_length");
        await Assert.That(onInsert.TableName).IsEqualTo("category_groups");
        await Assert.That(onUpdate.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(onUpdate.ConstraintName)
            .IsEqualTo("CK_category_groups_description_length");
    }

    /// <summary>
    /// A <see langword="null" /> description and one of exactly
    /// <see cref="NarrativeFieldLimits.DescriptionBytes" /> bytes are both stored; one byte more is
    /// refused.
    /// </summary>
    /// <remarks>
    /// <b>The at-cap value cannot come from <c>SealedNarrative.Description</c> and the over-cap one
    /// certainly cannot</b> — that helper judges against the same ceiling, so it would refuse the one
    /// value that catches a widened one. Both are built inline from
    /// <see cref="NarrativeFieldLimits.DescriptionBytes" /> and
    /// <see cref="CiphertextEnvelope.Version" />, never from literals, so a cap that moves moves these
    /// with it instead of leaving the case measuring a number nobody uses.
    /// </remarks>
    [Test]
    public async Task Database_AcceptsAnAbsentDescriptionAndOneAtTheCap()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        byte[] atCap = new byte[NarrativeFieldLimits.DescriptionBytes];
        atCap[0] = CiphertextEnvelope.Version;
        byte[] overCap = new byte[NarrativeFieldLimits.DescriptionBytes + 1];
        overCap[0] = CiphertextEnvelope.Version;

        // Act — three rows, three descriptions: absent, at the floor the framing sets, and at the cap.
        Guid absent = await InsertCategoryGroupAsync(
            connection,
            budgetId,
            SealedNarrative.Name("No note").Envelope.ToArray(),
            SealedNarrative.BlindIndex("No note").ToArray(),
            description: null,
            position: 0);
        Guid atFloor = await InsertCategoryGroupAsync(
            connection,
            budgetId,
            SealedNarrative.Name("Emptied note").Envelope.ToArray(),
            SealedNarrative.BlindIndex("Emptied note").ToArray(),
            SealedNarrative.Description().Envelope.ToArray(),
            position: 1);
        Guid atCeiling = await InsertCategoryGroupAsync(
            connection,
            budgetId,
            SealedNarrative.Name("Long note").Envelope.ToArray(),
            SealedNarrative.BlindIndex("Long note").ToArray(),
            atCap,
            position: 2);
        PostgresException refusal = await ThrowsPostgresExceptionAsync(() => InsertCategoryGroupAsync(
            connection,
            budgetId,
            SealedNarrative.Name("Too long").Envelope.ToArray(),
            SealedNarrative.BlindIndex("Too long").ToArray(),
            overCap,
            position: 3));

        // Assert — the NULL row is stored AS NULL and not as anything shorter. Reading the length back
        // is what tells "the column accepted an absent value" from "the column coerced it", which a
        // successful insert alone cannot.
        await Assert.That(await DescriptionLengthAsync(connection, absent)).IsNull();

        // A NOT NULL on this column would answer 23502 on the row above and this line would never run;
        // an at-cap value written as DescriptionBytes - 1 would redden here. The floor row is the
        // twenty-nine bytes an empty plaintext seals to — a note somebody emptied, which the schema
        // distinguishes from one nobody wrote, and this pair of assertions is where it is distinguished.
        await Assert.That(await DescriptionLengthAsync(connection, atFloor))
            .IsEqualTo(CiphertextEnvelope.MinimumLength);
        await Assert.That(await DescriptionLengthAsync(connection, atCeiling))
            .IsEqualTo(NarrativeFieldLimits.DescriptionBytes);

        // And one byte past it is refused by the length check rather than truncated by the column,
        // which has no width to truncate to.
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(refusal.ConstraintName).IsEqualTo("CK_category_groups_description_length");
    }

    /// <summary>
    /// <c>POST /api/category-groups</c> stores a non-null description and hands it back byte for byte.
    /// </summary>
    /// <remarks>
    /// <b>This is the only guard on a hazard the schema cannot see.</b> On the <c>NOT NULL</c> name, a
    /// write path that decoded a member and then forgot to assign it is <c>23502</c> — loud, immediate,
    /// unmissable. On this column the same bug is a legal row, byte-identical to a person who
    /// legitimately has no note, answered with a 201. Nothing in PostgreSQL, EF or the domain can tell
    /// the two apart. Round-tripping a NON-NULL description through the route and back is the whole of
    /// the defence, which is why this case may not be weakened to "the member is present".
    /// </remarks>
    [Test]
    public async Task PostCategoryGroup_WithADescription_RoundTripsTheEnvelope()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        var id = Guid.CreateVersion7();

        // Act
        HttpResponseMessage create = await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = id.ToString("D"),
            name = SealedNarrative.EncodedName("Essentials"),
            nameKey = SealedNarrative.EncodedIndex("Essentials"),
            description = SealedNarrative.EncodedDescription("Rent, food and the bus"),
        });
        JsonNode created = (await JsonNode.ParseAsync(await create.Content.ReadAsStreamAsync()))!;
        JsonNode fetched = await GetJsonAsync(client, $"/api/category-groups/{id}");

        // Assert
        await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // Byte for byte, on BOTH legs. The 201 body comes from CategoryGroupDto.FromCategoryGroup over
        // the entity the handler just wrote and the read comes from the read service over a row it
        // fetched back — two different pieces of code, either of which could drop or re-encode the
        // member with the other still green.
        await Assert.That(created["description"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedDescription("Rent, food and the bus"));
        await Assert.That(fetched["description"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedDescription("Rent, food and the bus"));

        // The name travelled beside it and did not take the description's value, which is the mistake a
        // factory assigning one parameter twice would make. SealedNarrative's filler is
        // position-varying over the label, so a name and a description built from ONE label differ only
        // in length and two different labels share no byte at any offset — that is what makes this a
        // real check rather than two unequal strings.
        await Assert.That(fetched["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Essentials"));
    }

    /// <summary>
    /// <c>POST /api/category-groups</c> with NO description stores <see langword="null" />, and with
    /// <c>""</c> answers 400.
    /// </summary>
    /// <remarks>
    /// <b>One case, because the two halves are one rule and separating them lets half of it die
    /// unnoticed.</b> <c>PasskeyEncoding.TryDecode</c> — which <c>CiphertextEnvelopeText</c> delegates
    /// to — opens with <c>string.IsNullOrEmpty(value)</c> and so refuses <see langword="null" /> and
    /// <c>""</c> identically. The distinction therefore cannot live in the decoder and lives in exactly
    /// one place: the handlers' <c>command.Description is null</c>. Change that spelling to
    /// <c>IsNullOrEmpty</c> or <c>IsNullOrWhiteSpace</c> and the absent leg still passes while the empty
    /// leg becomes a 201 that silently stores NULL — a legal row, a success on the wire, and a note the
    /// person typed gone.
    /// </remarks>
    [Test]
    public async Task PostCategoryGroup_TellsAnAbsentDescriptionFromAnEmptyOne()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        var absentId = Guid.CreateVersion7();

        // Act — absent, then the same body with "" in place of the missing member.
        HttpResponseMessage absent = await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = absentId.ToString("D"),
            name = SealedNarrative.EncodedName("No note"),
            nameKey = SealedNarrative.EncodedIndex("No note"),
            description = (string?)null,
        });
        HttpResponseMessage empty = await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Empty note"),
            nameKey = SealedNarrative.EncodedIndex("Empty note"),
            description = string.Empty,
        });
        JsonNode problem = (await JsonNode.ParseAsync(await empty.Content.ReadAsStreamAsync()))!;

        // Assert — the absent leg is a row whose description is NULL, read off the COLUMN and not off
        // the response, because the 201 body is built by CategoryGroupDto.FromCategoryGroup over the
        // entity the handler just wrote: it is a picture of what was sent, not evidence of what landed.
        // A handler that decoded the member and then failed to assign it would answer the same body.
        //
        // NOT for the reason a reader will assume, and the wrong one was written here first: the DTO
        // does NOT render a stored zero-length value as null. Both shaping sites branch on `is null`
        // only, and PasskeyEncoding.Encode answers "" for an empty span, so a zero-length column would
        // come back as "" and be distinguishable from NULL on the wire. The column is read because the
        // response cannot testify about the column, not because the two answers collide.
        await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await Assert.That(await DescriptionLengthAsync(connection, absentId)).IsNull();

        // The empty leg is a 400 KEYED ON Description. The status alone is too weak: a 400 blamed on
        // the name would satisfy it while telling the caller to fix a member that is correct.
        await Assert.That(empty.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem["errors"]!["Description"] is not null).IsTrue();
    }

    /// <summary>
    /// <c>POST /api/category-groups</c> refuses every spelling of an identifier this API cannot
    /// reproduce, and names <c>Id</c> when it does.
    /// </summary>
    /// <remarks>
    /// The identifier is the associated data BOTH narrative members were sealed against, so a spelling
    /// this API folds is a name and a note nothing will ever open — a failure that surfaces in a browser
    /// months later and names no cause. The set is DERIVED from one identifier rather than typed out, so
    /// it cannot drift from what <see cref="Guid" /> can render.
    /// </remarks>
    [Test]
    public async Task PostCategoryGroup_WithANonCanonicalId_IsRejected()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        var canonical = Guid.CreateVersion7();
        string[] refused =
        [
            canonical.ToString("D").ToUpperInvariant(),
            canonical.ToString("B"),
            canonical.ToString("N"),
            $" {canonical.ToString("D")} ",

            // Canonically SPELLED and refused anyway, for its own reason: the all-zero uuid is what an
            // unset field sends and arrives indistinguishable from a value somebody chose. It is here
            // to keep a reader from concluding the route only judges spelling.
            Guid.Empty.ToString("D"),
        ];

        // Act
        List<string> accepted = [];
        List<string> misattributed = [];
        foreach (string spelling in refused)
        {
            HttpResponseMessage response = await client.PostAsJsonAsync("/api/category-groups", new
            {
                id = spelling,
                name = SealedNarrative.EncodedName("Essentials"),
                nameKey = SealedNarrative.EncodedIndex("Essentials"),
                description = (string?)null,
            });
            if (response.StatusCode is not HttpStatusCode.BadRequest)
            {
                accepted.Add($"'{spelling}' answered {(int)response.StatusCode}");
                continue;
            }

            JsonNode? problem = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (problem?["errors"]?["Id"] is null)
            {
                misattributed.Add($"'{spelling}' was refused without naming Id");
            }
        }

        // Non-vacuity: the canonical spelling of the same value, with the same name beside it, is
        // accepted — so the five refusals above are about the spelling and not about the request.
        HttpResponseMessage canonicalSpelling = await client.PostAsJsonAsync(
            "/api/category-groups",
            new
            {
                id = canonical.ToString("D"),
                name = SealedNarrative.EncodedName("Essentials"),
                nameKey = SealedNarrative.EncodedIndex("Essentials"),
                description = (string?)null,
            });

        // Assert — refused, AND keyed on the member a caller can correct.
        await Assert.That(accepted).IsEmpty();
        await Assert.That(misattributed).IsEmpty();
        await Assert.That(canonicalSpelling.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    /// <summary>
    /// A body wrong in the name AND wrong in the description is refused naming BOTH.
    /// </summary>
    /// <remarks>
    /// <b>The COUNT is what a fail-fast handler cannot pass.</b> Any single key-presence assertion is
    /// satisfied by whichever member the handler happened to stop on, so a <c>return</c> after the first
    /// failure would keep this case green if it only asked whether <c>Name</c> was named. The four
    /// members arrive together, from one piece of client code, and are opaque to this server in the
    /// same way — a caller that got two wrong should not learn about the second only after fixing the
    /// first and sending everything again. The description is the member most at risk of losing this
    /// property, because it alone sits behind a branch: judged inside an early return, or below the
    /// throw, it would never be reported alongside the others.
    /// </remarks>
    [Test]
    public async Task PostCategoryGroup_WithABadNameAndABadDescription_ReportsBoth()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;

        // Act — the id and the index are well-formed, so the count below is exactly two and a handler
        // that reported everything unconditionally could not pass by over-reporting either.
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = "not an envelope",
            nameKey = SealedNarrative.EncodedIndex("Essentials"),
            description = "not an envelope either",
        });
        JsonNode problem = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(problem["errors"]!.AsObject().Count).IsEqualTo(2);
        await Assert.That(problem["errors"]!["Name"] is not null).IsTrue();
        await Assert.That(problem["errors"]!["Description"] is not null).IsTrue();
    }

    /// <summary>
    /// A POST carrying a member this shape does not declare answers <b>400</b>, and writes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this closes is a typo that binds identically to an absent member.</b> Measured
    /// under <c>JsonSerializerDefaults.Web</c> with the options <c>Api/Program.cs</c> registers — whose
    /// <c>UnmappedMemberHandling</c> is the default <c>Skip</c> — a body sending <c>descriptionn</c> and
    /// a body sending no description at all both leave <c>Description</c> <see langword="null" />. On
    /// this route that is a 201 and a note that never arrives; <c>category_groups.description</c> is
    /// nullable, so the row is legal, the schema is satisfied and every later read agrees the group has
    /// no note. Nothing downstream can tell the defect from the operation, which is what makes
    /// <c>[JsonUnmappedMemberHandling(Disallow)]</c> on
    /// <see cref="Application.CategoryGroups.CreateCategoryGroup.CreateCategoryGroupCommand" /> a
    /// contract change rather than a lint.
    /// </para>
    /// <para>
    /// <b>The misspelling is of the member whose loss is invisible</b>, not of a required one. Misspell
    /// <c>name</c> and the shape's non-nullable parameter is missing, which System.Text.Json refuses on
    /// its own — that body is a 400 whether or not the attribute exists, and a case built on it would be
    /// green against a shape carrying nothing.
    /// </para>
    /// <para>
    /// <b>The empty list is the half a reader will drop.</b> A refusal that still wrote the row would be
    /// worse than the silent 201 it replaces: the caller is told the write failed while the note-less
    /// group is filed anyway.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PostCategoryGroup_WithAnUnknownMember_IsRefusedAndWritesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;

        // Act — every declared member is correct and well-formed; the ONLY fault is the extra one.
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Essentials"),
            nameKey = SealedNarrative.EncodedIndex("Essentials"),
            descriptionn = SealedNarrative.EncodedDescription("Rent, food and the bus"),
        });
        JsonNode listed = await GetJsonAsync(client, "/api/category-groups");

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(listed["items"]!.AsArray().Count).IsEqualTo(0);
    }

    /// <summary>
    /// A PUT carrying a member this shape does not declare answers <b>400</b>, and the note the group
    /// held is still there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This leg is owed separately and it is the destructive half.</b> The attribute is per-type, so
    /// <c>CreateCategoryGroupCommand</c> carrying it says nothing about
    /// <c>CategoryGroupEndpoints.UpdateCategoryGroupRequest</c>, which is a different declaration in a
    /// different project. Measured before this case existed: with the attribute on the POST shape alone
    /// and the PUT shape left bare, the whole suite stayed green at 1613/0 — nothing else in it sends an
    /// unknown member anywhere. This case is what the half-applied fix now reddens.
    /// </para>
    /// <para>
    /// <b>A PUT replaces, so an unmapped <c>description</c> is not a note that fails to arrive — it is a
    /// note that is DELETED.</b> The route reads an absent member as "this group has none", which is a
    /// real operation a caller performs, so the 204 it used to answer was indistinguishable from the one
    /// somebody asked for. The surviving description is therefore the assertion that matters; the status
    /// alone would be satisfied by a refusal that had already cleared the column.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PutCategoryGroup_WithAnUnknownMember_IsRefusedAndLeavesTheNoteStanding()
    {
        // Arrange — a group that HOLDS a note, or there is nothing for the refusal to fail to destroy.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        var id = Guid.CreateVersion7();
        (await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = id.ToString("D"),
            name = SealedNarrative.EncodedName("Essentials"),
            nameKey = SealedNarrative.EncodedIndex("Essentials"),
            description = SealedNarrative.EncodedDescription("Rent, food and the bus"),
        })).EnsureSuccessStatusCode();

        // Act — a rename that also means to change the note, with the note's member misspelled.
        HttpResponseMessage update = await client.PutAsJsonAsync($"/api/category-groups/{id}", new
        {
            name = SealedNarrative.EncodedName("Lifestyle"),
            nameKey = SealedNarrative.EncodedIndex("Lifestyle"),
            descriptionn = SealedNarrative.EncodedDescription("Coffee, books and the odd trip"),
        });
        JsonNode after = await GetJsonAsync(client, $"/api/category-groups/{id}");

        // Assert — refused, and BOTH columns are exactly as they were. Under Skip this answered 204
        // with the name rewritten and the note gone, and no status, constraint or later read could
        // separate that from a caller who asked for it.
        await Assert.That(update.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(after["description"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedDescription("Rent, food and the bus"));
        await Assert.That(after["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Essentials"));
    }

    /// <summary>
    /// The NEGATIVE CONTROL for the two cases above: <c>MoveCategoryGroupRequest</c> carries no
    /// <c>[JsonUnmappedMemberHandling(Disallow)]</c>, and an unknown member on its route is still
    /// ignored in silence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this case a GLOBAL setting passes both of its neighbours, and a global setting is what
    /// this repository forbids.</b> Measured: putting
    /// <c>options.SerializerOptions.UnmappedMemberHandling = Disallow</c> in <c>Api/Program.cs</c> — one
    /// line, silently changing the contract of every route in the product — left the whole suite green,
    /// because no case in it sent an unknown member anywhere. The two cases above cannot see the
    /// difference between a per-type decision and that; this one can, and it is the only reason they
    /// mean what they claim.
    /// </para>
    /// <para>
    /// <b>The sibling is chosen for being as close as a sibling gets.</b> It is declared in the same
    /// file, on the same route group, beside the shape under test — so the property asserted here is
    /// exactly per-TYPE, and not per-file, per-group or per-assembly. The production comment on
    /// <c>UpdateCategoryGroupRequest</c> names this record as the neighbour that deliberately does not
    /// get the attribute for company; this is where that sentence is checked.
    /// </para>
    /// <para>
    /// <b>The move is asserted as well as the status.</b> A 204 alone would be satisfied by a handler
    /// that refused nothing and did nothing, which is not the claim: the declared member has to bind
    /// while the undeclared one is dropped.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PatchCategoryGroupPosition_WithAnUnknownMember_StillIgnoresIt()
    {
        // Arrange — two groups, so a move to position 0 is a real reorder rather than a no-op.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid first = await CreateCategoryGroupAsync(client, "Essentials");
        Guid second = await CreateCategoryGroupAsync(client, "Lifestyle");

        // Act
        HttpResponseMessage move = await client.PatchAsJsonAsync(
            $"/api/category-groups/{second}/position",
            new { position = 0, positionn = 1 });
        JsonNode listed = await GetJsonAsync(client, "/api/category-groups");

        // Assert — accepted, and the member that DOES bind took effect.
        await Assert.That(move.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        List<Guid> ids =
            [.. listed["items"]!.AsArray().Select(item => item!["id"]!.GetValue<Guid>())];
        await Assert.That(ids).IsEquivalentTo(new[] { second, first }, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A POST landing on an identifier another group holds answers <b>409</b> with its own sentence,
    /// not the duplicate-name 400.
    /// </summary>
    /// <remarks>
    /// The id is client-minted, so a POST retried after a network timeout carries a byte-identical body
    /// and collides on <c>PK_category_groups</c> — measured on postgres:17.10 in the payees round, a row
    /// violating both the key and the name index is reported under the KEY, because PostgreSQL checks a
    /// relation's indexes in OID (creation) order and the key is created with the table. The two
    /// sentences are deliberately different: "re-read your list" is the duplicate-name remedy and is
    /// wrong here, because the row wearing this id may hold a different name or sit in a budget the
    /// caller cannot read.
    /// </remarks>
    [Test]
    public async Task PostCategoryGroup_WithAnIdAnotherGroupHolds_AnswersConflict()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        var id = Guid.CreateVersion7();
        HttpResponseMessage first = await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = id.ToString("D"),
            name = SealedNarrative.EncodedName("Essentials"),
            nameKey = SealedNarrative.EncodedIndex("Essentials"),
            description = (string?)null,
        });

        // Act — the SAME id under a DIFFERENT name, so the only index it can collide on is the key.
        // Sending the byte-identical body would collide on both and leave the case unable to say which
        // arm answered.
        HttpResponseMessage second = await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = id.ToString("D"),
            name = SealedNarrative.EncodedName("Lifestyle"),
            nameKey = SealedNarrative.EncodedIndex("Lifestyle"),
            description = (string?)null,
        });
        JsonNode problem = (await JsonNode.ParseAsync(await second.Content.ReadAsStreamAsync()))!;

        // Assert
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        // The sentence, not just the status: without the PrimaryKeyName arm this is a 500, and with the
        // arms in the wrong order it is the duplicate-name 400 whose remedy sends a client looking
        // through a list for a name that is not on it.
        await Assert.That(problem["detail"]!.GetValue<string>()).Contains("identifier");

        // The first group is untouched — a refused retry must not have rewritten the row it collided
        // with, which is what a handler catching the violation and then "helpfully" updating would do.
        JsonNode survivor = await GetJsonAsync(client, $"/api/category-groups/{id}");
        await Assert.That(survivor["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Essentials"));
    }

    /// <summary>
    /// <c>PUT /api/category-groups/{id}</c> changing the name AND a non-empty description answers 204,
    /// and both columns read back changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE ROUTE HALF OF THE GRANT PAIR, AND "204" IS NOT ENOUGH.</b> EF names only the columns its
    /// comparer says changed, so a rename that leaves the description alone emits <c>name</c> and
    /// <c>name_key</c> and — measured on postgres:17.10 under
    /// <c>GRANT UPDATE (name, name_key, position)</c> — SUCCEEDS under a grant missing
    /// <c>description</c>, where the same statement naming three columns answers <c>42501</c>. The
    /// existing projection case does exactly that: it PUTs a null description onto a group that had
    /// none, emits two columns, and would ship a broken grant green.
    /// </para>
    /// <para>
    /// Both members change and both are read back. Weaken this to asserting the status and a
    /// description silently dropped between the request and the row ships with nothing red: the PUT
    /// answers 204 whether or not the column moved.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PutCategoryGroup_WithANewNameAndANewDescription_ReturnsNoContentAndRewritesBoth()
    {
        // Arrange — created WITH a description, so the update changes it rather than setting it: a
        // group starting at NULL would leave "the column was written" and "the column was cleared"
        // indistinguishable on the way out.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        var id = Guid.CreateVersion7();
        HttpResponseMessage create = await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = id.ToString("D"),
            name = SealedNarrative.EncodedName("Essentials"),
            nameKey = SealedNarrative.EncodedIndex("Essentials"),
            description = SealedNarrative.EncodedDescription("Rent, food and the bus"),
        });

        // Act
        HttpResponseMessage update = await client.PutAsJsonAsync($"/api/category-groups/{id}", new
        {
            name = SealedNarrative.EncodedName("Discretionary"),
            nameKey = SealedNarrative.EncodedIndex("Discretionary"),
            description = SealedNarrative.EncodedDescription("Coffee, books and the odd trip"),
        });
        JsonNode fetched = await GetJsonAsync(client, $"/api/category-groups/{id}");

        // Assert
        await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(update.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // BOTH read back CHANGED. Either assertion alone leaves the other column free to be dropped.
        await Assert.That(fetched["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Discretionary"));
        await Assert.That(fetched["description"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedDescription("Coffee, books and the odd trip"));

        // And the index moved with the name. It is on no response by design — a client recomputes it
        // under a key only it holds — so this is read off the column, and it is what stops a rename
        // that rewrote the envelope while leaving the row findable under its old name.
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand indexCommand = new(
            "select name_key from category_groups where id = @id", connection);
        indexCommand.Parameters.AddWithValue("id", id);
        byte[] storedIndex = (byte[])(await indexCommand.ExecuteScalarAsync())!;
        await Assert.That(storedIndex)
            .IsEquivalentTo(
                SealedNarrative.BlindIndex("Discretionary").ToArray(),
                CollectionOrdering.Matching);
    }

    /// <summary>
    /// <c>PUT /api/category-groups/{id}</c> with NO description CLEARS the note the group held, and the
    /// column reads back NULL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>PUT's replacement semantics is written down in three places and was reachable by no route
    /// case.</b> <c>UpdateCategoryGroupRequest</c>'s remarks say a PUT with no description clears the
    /// note; <c>UpdateCategoryGroupHandler</c> says the same at its <c>is null</c> test; and
    /// <c>NarrativeField.SealedOrAbsent</c> is the member that carries it. What existed under all three
    /// was a unit case over an in-memory fake. Through EF, through the converter and through
    /// <c>GRANT UPDATE (…, description, …)</c>, nothing had ever cleared this column.
    /// </para>
    /// <para>
    /// <b>The starting row HOLDS a note, which is the whole of the case.</b> The existing projection
    /// case PUTs a null description onto a group that never had one — EF names only what its comparer
    /// says changed, so that statement emits <c>name, name_key</c> and cannot tell "cleared" from
    /// "already absent". Here the column moves, so the UPDATE names <c>description</c> and this is also
    /// the ROUTE-LEVEL half of the grant pair. Measured on postgres:17.10 by narrowing the live grant to
    /// <c>UPDATE (name, name_key, position)</c> and replaying this exact request: it answers <b>500</b>,
    /// the <c>42501</c> arriving as an unhandled <c>PostgresException</c> that nothing translates. So
    /// this case fails on the status line under a half grant, which is the whole reason it is worth the
    /// round trip — <c>AppRoleGrantMatrixTests</c> pins the column list and a pinned list is not a
    /// statement issued.
    /// </para>
    /// <para>
    /// <b>The column is read, not the response.</b> A 204 carries no body, and the follow-up GET is the
    /// read service — which renders NULL and a zero-length envelope differently, but is still one more
    /// piece of code between the assertion and the row. <c>octet_length</c> of NULL is NULL, which is
    /// what tells "the note is gone" from "the note was replaced by something short".
    /// </para>
    /// </remarks>
    [Test]
    public async Task PutCategoryGroup_WithNoDescription_ClearsTheNoteThroughTheRoute()
    {
        // Arrange — created WITH a note.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        var id = Guid.CreateVersion7();
        (await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = id.ToString("D"),
            name = SealedNarrative.EncodedName("Essentials"),
            nameKey = SealedNarrative.EncodedIndex("Essentials"),
            description = SealedNarrative.EncodedDescription("Rent, food and the bus"),
        })).EnsureSuccessStatusCode();

        // Act — the same name back, so the ONLY column that moves is the description and a green
        // assertion below cannot be a rename carrying it along. `null` rather than an absent member
        // because the two mean the same thing here and the explicit spelling is what a client sends.
        HttpResponseMessage update = await client.PutAsJsonAsync($"/api/category-groups/{id}", new
        {
            name = SealedNarrative.EncodedName("Essentials"),
            nameKey = SealedNarrative.EncodedIndex("Essentials"),
            description = (string?)null,
        });
        JsonNode after = await GetJsonAsync(client, $"/api/category-groups/{id}");

        // Assert
        await Assert.That(update.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // The COLUMN is NULL. Under a handler reading absence as "leave it alone" this is still the
        // twenty-nine-byte-plus envelope it was, and the 204 above says nothing either way.
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await Assert.That(await DescriptionLengthAsync(connection, id)).IsNull();

        // And the read service agrees, so a client is told the note is gone rather than shown "".
        await Assert.That(after["description"] is null || after["description"]!.GetValueKind()
            == System.Text.Json.JsonValueKind.Null).IsTrue();
    }

    /// <summary>
    /// A group holding a description survives being moved and then deleted through the routes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the change-tracking question the slice existed to answer, and nothing else asks
    /// it.</b> Both paths materialise every group in the budget as TRACKED entities and reindex through
    /// <c>SetPosition</c>, which makes them the first tracked loads of a converted NULLABLE narrative
    /// column outside <c>budgets</c>. The converter's comparer declares <c>Func&lt;T?, T?, bool&gt;</c>
    /// on its equality arm and non-null-tolerant hash and snapshot arms, and the claim that EF never
    /// calls the latter two with a null was inferred rather than run. If it is wrong, the symptom is a
    /// <c>NullReferenceException</c> inside materialisation.
    /// </para>
    /// <para>
    /// BOTH nullabilities travel together on purpose. A group with a description exercises the arms
    /// with a value; the one beside it holding NULL is what would trip a snapshot arm that cannot cope
    /// with absence, and it has to be in the SAME budget so that one tracked load pulls in both.
    /// </para>
    /// </remarks>
    [Test]
    public async Task MovingAndDeletingGroups_WorksWhetherOrNotTheyHoldADescription()
    {
        // Arrange — two groups in one budget, one annotated and one not.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        var annotatedId = Guid.CreateVersion7();
        var bareId = Guid.CreateVersion7();
        (await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = annotatedId.ToString("D"),
            name = SealedNarrative.EncodedName("Essentials"),
            nameKey = SealedNarrative.EncodedIndex("Essentials"),
            description = SealedNarrative.EncodedDescription("Rent, food and the bus"),
        })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = bareId.ToString("D"),
            name = SealedNarrative.EncodedName("Lifestyle"),
            nameKey = SealedNarrative.EncodedIndex("Lifestyle"),
            description = (string?)null,
        })).EnsureSuccessStatusCode();

        // Act — the move loads and rewrites every group in the budget, so one request touches both.
        HttpResponseMessage move = await client.PatchAsJsonAsync(
            $"/api/category-groups/{annotatedId}/position",
            new { position = 1 });
        JsonNode afterMove = await GetJsonAsync(client, "/api/category-groups");
        HttpResponseMessage delete = await client.DeleteAsync($"/api/category-groups/{bareId}");
        JsonNode afterDelete = await GetJsonAsync(client, "/api/category-groups");

        // Assert — the move went through and the annotated group kept its description across a tracked
        // load, a reindex and a save. A reindex that round-tripped the column through a broken snapshot
        // arm would either throw above or write something else here.
        await Assert.That(move.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        JsonNode moved = afterMove["items"]!.AsArray()
            .First(item => item!["id"]!.GetValue<Guid>() == annotatedId)!;
        await Assert.That(moved["position"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(moved["description"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedDescription("Rent, food and the bus"));

        // The bare group came back through the same load carrying an explicit null rather than an empty
        // string — the DTO keeps `string?` and adds no `?? string.Empty`, because "" is not a legal
        // envelope and a client cannot tell it from one.
        JsonNode bare = afterMove["items"]!.AsArray()
            .First(item => item!["id"]!.GetValue<Guid>() == bareId)!;
        await Assert.That(bare["description"] is null || bare["description"]!.GetValueKind()
            == System.Text.Json.JsonValueKind.Null).IsTrue();

        // And the delete reindexed what was left, again over a tracked load carrying both nullabilities.
        await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(afterDelete["items"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(afterDelete["items"]![0]!["position"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(afterDelete["items"]![0]!["description"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedDescription("Rent, food and the bus"));
    }

    /// <summary>
    /// <c>GET /api/category-groups</c> orders by position, carries the envelopes, and renders an absent
    /// description as <see langword="null" /> rather than as <c>""</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The ordering assertion is the one that stops being about anything the moment the column is
    /// sealed.</b> No read path in the product orders category groups by name — this one has always
    /// ordered by <c>Position</c> then <c>Id</c> — and this case exists so that nobody "restores" a name
    /// ordering that would now be an ordering by the nonce: every seal draws a fresh one, so sorting by
    /// <c>name</c> shuffles the list on each write and looks like a caching bug forever.
    /// </para>
    /// <para>
    /// The groups are created in an order that DISAGREES with the positions they end up holding, or the
    /// case would be satisfied by insertion order and would pass with the <c>order by</c> deleted
    /// outright.
    /// </para>
    /// </remarks>
    [Test]
    public async Task GetCategoryGroups_OrdersByPositionAndCarriesEnvelopes()
    {
        // Arrange — appended in creation order, then the last one is moved to the front, so position
        // order and creation order disagree.
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        Guid first = await CreateCategoryGroupAsync(client, "Essentials");
        Guid second = await CreateCategoryGroupAsync(client, "Lifestyle");
        var annotated = Guid.CreateVersion7();
        (await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = annotated.ToString("D"),
            name = SealedNarrative.EncodedName("Sinking Funds"),
            nameKey = SealedNarrative.EncodedIndex("Sinking Funds"),
            description = SealedNarrative.EncodedDescription("For the boiler"),
        })).EnsureSuccessStatusCode();
        (await client.PatchAsJsonAsync($"/api/category-groups/{annotated}/position", new { position = 0 }))
            .EnsureSuccessStatusCode();

        // Act
        JsonNode listed = await GetJsonAsync(client, "/api/category-groups");
        List<Guid> ids =
            [.. listed["items"]!.AsArray().Select(item => item!["id"]!.GetValue<Guid>())];

        // Assert — CollectionOrdering.Matching NAMED EXPLICITLY, because IsEquivalentTo defaults to
        // CollectionOrdering.Any and this assertion is entirely about the order. Under the default it
        // would pass with the read service ordering by anything at all, which is the failure this case
        // exists to catch.
        await Assert.That(ids)
            .IsEquivalentTo(new[] { annotated, first, second }, CollectionOrdering.Matching);

        // The envelopes travelled, and the moved group's description came with them.
        JsonNode moved = listed["items"]![0]!;
        await Assert.That(moved["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Sinking Funds"));
        await Assert.That(moved["description"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedDescription("For the boiler"));

        // And an absent description renders as null. `?? string.Empty` on the DTO reddens exactly here:
        // "" is not a legal envelope, so a client meeting one has no way to tell a note somebody
        // emptied from a member the server coerced.
        JsonNode bare = listed["items"]![1]!;
        await Assert.That(bare["description"] is null || bare["description"]!.GetValueKind()
            == System.Text.Json.JsonValueKind.Null).IsTrue();
    }

    /// <summary>
    /// The 201 body and the body <c>GET /api/category-groups/{id}</c> answers encode both narrative
    /// members identically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two are produced by DIFFERENT code — <c>CategoryGroupDto.FromCategoryGroup</c> over the
    /// entity the handler just wrote, and <c>CategoryGroupReadService</c> over a row it read back — so
    /// one could emit padded standard base64 and the other unpadded base64url with every other case in
    /// this file green, and the client's strict decoder would refuse whichever half it met second.
    /// <c>System.Text.Json</c>'s default handling of a <c>byte[]</c> is exactly that padded standard
    /// base64, which is why this is a live mistake rather than a hypothetical one.
    /// </para>
    /// <para>
    /// <b>THE LABEL IS CHOSEN AND NOT PICKED, AND THAT IS THE DIFFERENCE BETWEEN THIS CASE BITING AND
    /// NOT.</b> The two alphabets differ in three characters only — <c>+</c> and <c>/</c> against
    /// <c>-</c> and <c>_</c>, plus <c>=</c> padding — so an envelope whose bytes happen to encode
    /// without any of them renders IDENTICALLY under both, and every comparison here passes against
    /// the wrong encoder. See the note at the alphabet assertion for the measurement.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PostAndGetCategoryGroup_EncodeTheSameNameAndDescription()
    {
        // Arrange
        await using PostgresTestHost host = await StartApiHostAsync();
        HttpClient client = (await host.Factory.CreateSignedInClientAsync()).Client;
        var id = Guid.CreateVersion7();

        // THE PRECONDITION, AND IT IS WHAT MAKES EVERY COMPARISON BELOW MEAN ANYTHING. A label whose
        // envelope spells identically under padded standard base64 and unpadded base64url renders this
        // case decoration: all four equalities pass, the DoesNotContain loop passes, and the encoder the
        // production code reached for is unobservable. FOUR such labels are already in this suite -
        // "Essentials", "Discretionary", "Empty note" and "No note" - and the property is not readable
        // off the length: "Sinking Funds" and "Discretionary" are both 42 bytes and only one of them is
        // blind. So the case states the property for its OWN fixture rather than trusting the choice,
        // and an author who swaps in a prettier label gets a red bar here instead of a silent deletion.
        // Convert.ToBase64String is named because it IS the mutation - it is what System.Text.Json
        // reaches for on a byte[] by default, which is why the wrong encoder is a live mistake.
        byte[] nameBytes = SealedNarrative.Name("Sinking Funds").Envelope.ToArray();
        byte[] descriptionBytes =
            SealedNarrative.Description("Rent, food and the bus").Envelope.ToArray();
        await Assert.That(Convert.ToBase64String(nameBytes))
            .IsNotEqualTo(SealedNarrative.EncodedName("Sinking Funds"));
        await Assert.That(Convert.ToBase64String(descriptionBytes))
            .IsNotEqualTo(SealedNarrative.EncodedDescription("Rent, food and the bus"));
        HttpResponseMessage create = await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = id.ToString("D"),
            name = SealedNarrative.EncodedName("Sinking Funds"),
            nameKey = SealedNarrative.EncodedIndex("Sinking Funds"),
            description = SealedNarrative.EncodedDescription("Rent, food and the bus"),
        });
        JsonNode created = (await JsonNode.ParseAsync(await create.Content.ReadAsStreamAsync()))!;

        // Act — the header, not a path this test built. A Location assembled here would test the string
        // this file writes rather than the one the route answers with.
        HttpResponseMessage read = await client.GetAsync(create.Headers.Location!.ToString());
        JsonNode fetched = (await JsonNode.ParseAsync(await read.Content.ReadAsStreamAsync()))!;

        // Assert
        await Assert.That(create.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(fetched["id"]!.GetValue<Guid>()).IsEqualTo(id);

        // THE TWO ENCODERS AGREE, written as a comparison of the two responses and not of each against
        // the fixture: either half agreeing with the fixture is a weaker claim than the two agreeing
        // with each other, because the fixture is one more encoder and it is this file's.
        await Assert.That(fetched["name"]!.GetValue<string>())
            .IsEqualTo(created["name"]!.GetValue<string>());
        await Assert.That(fetched["description"]!.GetValue<string>())
            .IsEqualTo(created["description"]!.GetValue<string>());

        // Non-vacuity: both are the envelopes the client sent, so the two lines above are not two
        // routes agreeing on nothing.
        await Assert.That(fetched["name"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedName("Sinking Funds"));
        await Assert.That(fetched["description"]!.GetValue<string>())
            .IsEqualTo(SealedNarrative.EncodedDescription("Rent, food and the bus"));

        // The index is on NEITHER response, by design: a client recomputes it from the name it just
        // decrypted, under a key only it holds. Publishing it would put a deterministic, per-account
        // fingerprint of a name on the wire — the one property of the pair that survives having no key.
        await Assert.That(created["nameKey"]).IsNull();
        await Assert.That(fetched["nameKey"]).IsNull();

        // AND THE ALPHABET IS ASSERTED DIRECTLY, BECAUSE THE FOUR COMPARISONS ABOVE ARE FIXTURE LUCK.
        // MEASURED, and this case was written the wrong way first: with the label "Essentials" the name
        // envelope is 39 bytes — divisible by three, so nothing pads — and its bytes encode without a
        // single `+` or `/`, so standard base64 and base64url render it CHARACTER FOR CHARACTER THE
        // SAME. Swapping PasskeyEncoding.Encode for Convert.ToBase64String on the name reddened NOTHING
        // in this file, twice: once before this loop existed and once after, because a value clean in
        // both alphabets is clean under this loop too. The same swap on the description — 51 bytes,
        // containing a `+` — reddened two cases immediately.
        //
        // So BOTH guards are here and neither replaces the other. "Sinking Funds" is the label because
        // its 42-byte envelope carries a `+` AND a `/`; a prettier one is a silent removal of the test,
        // which is why the choice is written down rather than left to look arbitrary. And the loop below
        // is the durable half, a property of the OUTPUT rather than of the fixture: base64url uses `-`
        // and `_` and pads with nothing, so any `+`, `/` or `=` is standard base64 leaking through,
        // whatever label a later reader substitutes.
        foreach (string encoded in new[]
                 {
                     created["name"]!.GetValue<string>(),
                     created["description"]!.GetValue<string>(),
                     fetched["name"]!.GetValue<string>(),
                     fetched["description"]!.GetValue<string>(),
                 })
        {
            await Assert.That(encoded).DoesNotContain("+");
            await Assert.That(encoded).DoesNotContain("/");
            await Assert.That(encoded).DoesNotContain("=");
        }
    }

    /// <summary>
    /// Writes one category group as raw SQL, which is the only way to reach the column rules: the
    /// domain and the route refuse the same values client-side, so an EF insert never gets to
    /// PostgreSQL and would prove nothing about the constraint.
    /// </summary>
    /// <remarks>
    /// <paramref name="description" /> is <see langword="null" /> for "no description" and an empty
    /// array for the present-but-zero-length value that only a raw statement can produce.
    /// <c>DBNull.Value</c> rather than a null parameter, because Npgsql needs the distinction spelled.
    /// </remarks>
    private static async Task<Guid> InsertCategoryGroupAsync(
        NpgsqlConnection connection,
        Guid budgetId,
        byte[] name,
        byte[] nameKey,
        byte[]? description,
        int position)
    {
        var id = Guid.CreateVersion7();
        await using NpgsqlCommand command = new(
            """
            insert into category_groups
                (id, budget_id, name, name_key, description, position, created_at_utc)
            values (@id, @budget_id, @name, @name_key, @description, @position, @created_at_utc)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("budget_id", budgetId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("name_key", nameKey);
        command.Parameters.AddWithValue(
            "description", NpgsqlTypes.NpgsqlDbType.Bytea, (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("position", position);
        command.Parameters.AddWithValue("created_at_utc", UtcNow());
        await command.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>
    /// The stored byte length of one group's description, or <see langword="null" /> when the column
    /// holds NULL.
    /// </summary>
    /// <remarks>
    /// Read as a LENGTH rather than as bytes, because the three answers this distinguishes — absent,
    /// the twenty-nine-byte floor, and the cap — differ in exactly that. <c>octet_length</c> of NULL is
    /// NULL, which is what carries "absent" back rather than collapsing it into zero.
    /// </remarks>
    private static async Task<int?> DescriptionLengthAsync(NpgsqlConnection connection, Guid id)
    {
        await using NpgsqlCommand command = new(
            "select octet_length(description) from category_groups where id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        object? length = await command.ExecuteScalarAsync();
        return length is null or DBNull ? null : (int)length;
    }

    /// <summary>
    /// A <c>name</c> value the column must refuse, one shape per argument of
    /// <see cref="Database_RefusesANameTheEnvelopeRulesForbid" />.
    /// </summary>
    /// <remarks>
    /// Built from the constants that own the rules rather than from literals, so a floor, a cap or a
    /// version that moves moves these values with it instead of leaving a case measuring a number
    /// nobody uses any more. Bytes rather than base64url, unlike the payee twin's helper: nothing here
    /// crosses the wire, and a round trip through an encoder would put a second thing between the case
    /// and the constraint it is firing.
    /// </remarks>
    private static byte[] MalformedNameEnvelope(string shape)
    {
        switch (shape)
        {
            case "one byte under the framing floor":
                {
                    byte[] tooShort = new byte[CiphertextEnvelope.MinimumLength - 1];
                    tooShort[0] = CiphertextEnvelope.Version;
                    return tooShort;
                }

            case "one byte over the column's cap":
                {
                    byte[] tooLong = new byte[NarrativeFieldLimits.NameBytes + 1];
                    tooLong[0] = CiphertextEnvelope.Version;
                    return tooLong;
                }

            case "a version this deployment has never implemented":
                {
                    byte[] wrongVersion = new byte[CiphertextEnvelope.MinimumLength];
                    wrongVersion[0] = CiphertextEnvelope.Version + 1;
                    return wrongVersion;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown envelope shape.");
        }
    }

    private static async Task<PostgresException> ThrowsPostgresExceptionAsync(Func<Task> statement)
    {
        try
        {
            await statement();
        }
        catch (PostgresException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected PostgresException.");
    }

    /// <summary>A stored <c>category_groups</c> row, read without the API's ordering projection.</summary>
    private readonly record struct CategoryGroupRow(Guid Id, int Position);

    /// <summary>A stored <c>categories</c> row, read without the API's ordering projection.</summary>
    private readonly record struct CategoryRow(Guid Id, Guid CategoryGroupId, int Position);

    /// <summary>
    /// Asserts that the budget's groups are exactly <paramref name="expectedIds" /> and that their
    /// stored positions are exactly <c>0..m-1</c>. <c>IsEquivalentTo</c> defaults to
    /// <c>CollectionOrdering.Any</c>, which is what makes this a set comparison rather than a
    /// sequence one — the point is that no position is missing and none appears twice, not the order
    /// the rows came back in.
    /// </summary>
    private static async Task AssertGroupPositionsAreContiguousAsync(
        IReadOnlyList<CategoryGroupRow> groupRows,
        params Guid[] expectedIds)
    {
        await Assert.That(groupRows.Select(row => row.Id)).IsEquivalentTo(expectedIds);
        await Assert.That(groupRows.Select(row => row.Position))
            .IsEquivalentTo(Enumerable.Range(0, expectedIds.Length));
    }

    /// <summary>
    /// Asserts that <paramref name="categoryGroupId" /> holds exactly
    /// <paramref name="expectedMemberIds" /> and that their stored positions are exactly
    /// <c>0..n-1</c>. Category positions are group-scoped, so the invariant is per group and this
    /// runs once per surviving group.
    /// </summary>
    private static async Task AssertGroupContentsAreContiguousAsync(
        IReadOnlyList<CategoryRow> categoryRows,
        Guid categoryGroupId,
        params Guid[] expectedMemberIds)
    {
        CategoryRow[] members =
            [.. categoryRows.Where(row => row.CategoryGroupId == categoryGroupId)];
        await Assert.That(members.Select(row => row.Id)).IsEquivalentTo(expectedMemberIds);
        await Assert.That(members.Select(row => row.Position))
            .IsEquivalentTo(Enumerable.Range(0, expectedMemberIds.Length));
    }

    // No budget predicate on either read below: the API host holds a single signed-in account, so
    // the container holds exactly one budget and every row in these tables belongs to it. The `order by`
    // is there only so a failure dump reads well; the assertions are order-insensitive.
    private static async Task<IReadOnlyList<CategoryGroupRow>> ReadCategoryGroupRowsAsync(
        PostgresTestHost host)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select id, position from category_groups order by position, id",
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<CategoryGroupRow> rows = [];

        while (await reader.ReadAsync())
        {
            rows.Add(new CategoryGroupRow(reader.GetGuid(0), reader.GetInt32(1)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<CategoryRow>> ReadCategoryRowsAsync(PostgresTestHost host)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select id, category_group_id, position from categories order by category_group_id, position, id",
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<CategoryRow> rows = [];

        while (await reader.ReadAsync())
        {
            rows.Add(new CategoryRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2)));
        }

        return rows;
    }

    /// <summary>
    /// Creates one category group from <paramref name="label" /> and returns the identifier it minted.
    /// </summary>
    /// <remarks>
    /// <b>The parameter is a LABEL and not a name, and the identifier is MINTED HERE rather than read
    /// back off the response.</b> The client chooses the row id now, because that id is the associated
    /// data both narrative members are sealed against — a route that invented one would hand back a
    /// group whose name nobody can ever open, with every constraint satisfied. Reading the id off the
    /// 201 would still work today and would stop working silently the first time somebody made the
    /// server mint one, which is exactly the mistake the client-minted id exists to make impossible.
    /// </remarks>
    private static async Task<Guid> CreateCategoryGroupAsync(HttpClient client, string label)
    {
        Guid id = Guid.CreateVersion7();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/category-groups", new
        {
            id = id.ToString("D"),
            name = SealedNarrative.EncodedName(label),
            nameKey = SealedNarrative.EncodedIndex(label),
            description = (string?)null,
        });
        response.EnsureSuccessStatusCode();
        return id;
    }

    /// <summary>
    /// A category in <paramref name="categoryGroupId" />, created through the route.
    /// </summary>
    /// <param name="label">
    /// What distinguishes this category's name from the next one's. It is NOT the category's name and is
    /// never read back as one - the column holds an envelope this side has no key for.
    /// </param>
    /// <remarks>
    /// The id is minted HERE and sent, because the route requires one now: it is the associated data both
    /// narrative members were sealed against, so the server takes it and never invents it. It travels in
    /// the canonical spelling, which is what CanonicalIdentifier accepts. The response's own id is read
    /// back and returned rather than the minted one, so a server that ignored the member would still be
    /// caught by the cases that compare the two.
    /// </remarks>
    private static async Task<Guid> CreateCategoryAsync(
        HttpClient client,
        Guid categoryGroupId,
        string label)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/categories", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(label),
            nameKey = SealedNarrative.EncodedIndex(label),
            description = (string?)null,
            categoryGroupId,
        });
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
    }

    private static async Task<Guid> CreateAccountAsync(HttpClient client)
    {
        string label = $"Checking {Guid.CreateVersion7()}";
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts", new
        {
            // Sealed, indexed and identified through SealedNarrative rather than sent as a flat name:
            // accounts.name is an AEAD envelope and accounts.name_key a blind index, so plain text is a
            // 400 from CreateAccountHandler and this seeding would never reach the subject of the test.
            // The label stays unique per call for the reason it always was — IX_accounts_budget_id_name_key
            // refuses two accounts indexing alike in one budget, and the index is deterministic in the
            // label, so a fixed label would make the second call in a budget a 23505.
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName(label),
            nameKey = SealedNarrative.EncodedIndex(label),
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

    /// <summary>
    /// A host whose factory leaves the application's own authentication standing, because every
    /// request below authenticates from a session cookie rather than from a provider bearer.
    /// </summary>
    private static async Task<PostgresTestHost> StartApiHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
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
