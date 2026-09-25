using Application.CategoryGroups.UpdateCategoryGroup;
using Domain.CategoryGroups;
using Domain.Common;
using Domain.Security;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using TUnit.Assertions.Enums;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// The update leg of <c>/api/category-groups/{id}</c>, once its body carried three members this server
/// cannot read.
/// </summary>
/// <remarks>
/// <para>
/// <b>The route is a PUT, so there is no third state and <c>Optional&lt;T&gt;</c> must not appear.</b> A
/// PUT is a full replacement: an absent <c>description</c> member and an explicit <see langword="null" />
/// both mean "this group has no description", and that is correct for this verb.
/// <see cref="HandleAsync_WithNoDescription_ClearsTheOneTheRowHeld" /> is where that is written as an
/// assertion. The transaction routes carry <c>Optional&lt;T&gt;</c> because they are PATCH and genuinely
/// have a "leave it alone" state; giving this one the same shape would invent a state the route does not
/// have and no client has ever sent.
/// </para>
/// <para>
/// <b>No id parse.</b> The route parameter stays <c>{id:guid}</c> and needs no canonical check, because a
/// client re-sealing an update binds the row's EXISTING id — read back from this API in the one form
/// <see cref="Guid" /> renders — and never the text it happened to put in the URL. That is why this
/// file's every-member case counts three keys where the create leg's counts four.
/// </para>
/// <para>
/// <b>A refused member must reach the caller before <see cref="CategoryGroup.Update" /> runs, not merely
/// instead of the save.</b> The entity handed back by the repository is the tracked instance in
/// production, so a handler that mutated it and then threw would leave a dirty entity for the next
/// <c>SaveChanges</c> on that context to commit — a rename and a cleared description nobody asked for,
/// arriving with some later request. Every refusal case below reads all three columns back for that
/// reason.
/// </para>
/// <para>
/// <b>Two caps, and this leg owes its own three cases because a constraint is a property of a code
/// path.</b> <c>CreateCategoryGroupHandlerTests</c> holds the identical trio and says nothing at all
/// about the lines in <c>UpdateCategoryGroupHandler</c> — measured: swapping either of this handler's
/// two <see cref="NarrativeFieldLimits.DescriptionBytes" /> references for
/// <see cref="NarrativeFieldLimits.NameBytes" />, or handing <c>int.MaxValue</c> to its name decode,
/// killed no test in the suite while every create-leg case stayed green.
/// <see cref="HandleAsync_WithADescriptionPastTheNameCap_IsAccepted" /> says the description's ceiling
/// is not the name's, <see cref="HandleAsync_WithADescriptionOverItsOwnCap_RefusesAndLeavesTheRowUnchanged" />
/// says there is one at all, and
/// <see cref="HandleAsync_WithANameOverItsOwnCap_ThrowsValidationExceptionRatherThanArgumentException" />
/// says the name's refusal is the caller's 400 and not the domain's 500.
/// </para>
/// <para>
/// <b>The first of those three is the one whose SIZE is the case</b>, and it is what the file was
/// missing rather than a fourth restatement. A description over BOTH caps is refused whichever number
/// is in force, so the over-cap case beside it cannot tell them apart; only a value <em>between</em>
/// <see cref="NarrativeFieldLimits.NameBytes" /> and <see cref="NarrativeFieldLimits.DescriptionBytes" />
/// can. The two cases that build envelopes by hand do so because <c>SealedNarrative</c> caps by
/// construction and therefore cannot produce the values that catch a widened ceiling.
/// </para>
/// </remarks>
public sealed class UpdateCategoryGroupHandlerTests
{
    [Test]
    public async Task HandleAsync_ReplacesBothHalvesOfTheNameAndTheDescriptionAndSaves()
    {
        // Arrange — a group created under one name and note and corrected to another, which is the most
        // common use of this route. The labels are what make the halves distinguishable: SealedNarrative
        // derives an envelope and an index from the same text, so "Essentials" produces neither of the
        // values "Essential Obligations" produces.
        Fixture fixture = Fixture.Create();
        CategoryGroup group = await fixture.SeedAsync("Essential Obligations", "Required spending");

        // Act
        await fixture.Handler.HandleAsync(new UpdateCategoryGroupCommand(
            group.Id,
            SealedNarrative.EncodedName("Essentials"),
            SealedNarrative.EncodedIndex("Essentials"),
            SealedNarrative.EncodedDescription("Must pay")));

        // Assert — THE INDEX IS ASSERTED TO BE THE NEW ONE AND EXPLICITLY NOT THE PREVIOUS ONE. A handler
        // that decoded only the envelope and reused the row's existing index would pass the first
        // assertion and leave a row holding new ciphertext under the old name's index — nothing on this
        // side notices, because no read path anywhere orders or looks a category group up by name, and
        // recomputing the digest to check needs the account's index key, which lives in a browser. What
        // it costs is the uniqueness rule: IX_category_groups_budget_id_name_key goes on guarding a name
        // the row no longer holds.
        //
        // CollectionOrdering.Matching IS PART OF THE ASSERTION. IsEqualTo over two byte[] compares
        // REFERENCES and fails even when the contents and the order agree, and TUnit's failure message
        // names IsEquivalentTo as the fix — which DEFAULTS TO CollectionOrdering.Any and would then pass
        // on any permutation of an envelope's bytes. The negative below takes no ordering: it says the
        // index is not the old VALUE, and any permutation of the old value is also not it.
        await Assert.That(group.Name.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Name("Essentials").Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(group.NameKey.ToArray())
            .IsEquivalentTo(
                SealedNarrative.BlindIndex("Essentials").ToArray(), CollectionOrdering.Matching);
        await Assert.That(group.NameKey.ToArray())
            .IsNotEquivalentTo(SealedNarrative.BlindIndex("Essential Obligations").ToArray());
        await Assert.That(group.Description).IsNotNull();
        await Assert.That(group.Description!.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Description("Must pay").Envelope.ToArray(),
                CollectionOrdering.Matching);
        await Assert.That(fixture.Groups.UpdateCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task HandleAsync_WithNoDescription_ClearsTheOneTheRowHeld()
    {
        // Arrange — a row that HAS a description, so that "no description" is a change rather than a
        // restatement of what was already there. Seeded from null, this case passes for a handler that
        // never touches the field at all.
        Fixture fixture = Fixture.Create();
        CategoryGroup group = await fixture.SeedAsync("Essential Obligations", "Required spending");

        // Act
        await fixture.Handler.HandleAsync(new UpdateCategoryGroupCommand(
            group.Id,
            SealedNarrative.EncodedName("Essentials"),
            SealedNarrative.EncodedIndex("Essentials"),
            null));

        // Assert — the PUT rule, as an assertion. This is also the case that would redden if somebody
        // introduced an Optional<string?> here and defaulted it to "unset".
        await Assert.That(group.Description).IsNull();
        await Assert.That(fixture.Groups.UpdateCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task HandleAsync_ReplacesADescriptionOnARowThatHadNone()
    {
        // Arrange — the other direction across the nullable column, which no other case covers: a group
        // filed with no note gains one.
        Fixture fixture = Fixture.Create();
        CategoryGroup group = await fixture.SeedAsync("Essential Obligations", null);

        // Act
        await fixture.Handler.HandleAsync(new UpdateCategoryGroupCommand(
            group.Id,
            SealedNarrative.EncodedName("Essential Obligations"),
            SealedNarrative.EncodedIndex("Essential Obligations"),
            SealedNarrative.EncodedDescription("Required spending")));

        // Assert — a handler that decoded this member and then forgot to pass it on leaves NULL, which is
        // exactly what the row already held: no constraint fires, the response is a 204, and the note the
        // person typed is gone. Nothing but an assertion that reads the value back can see it.
        await Assert.That(group.Description).IsNotNull();
        await Assert.That(group.Description!.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Description("Required spending").Envelope.ToArray(),
                CollectionOrdering.Matching);
    }

    [Test]
    public async Task HandleAsync_WhenNoSuchCategoryGroup_ThrowsNotFoundAndSavesNothing()
    {
        // Arrange — the lookup runs through the budget query filter in production, so another budget's
        // group is indistinguishable from one that never existed. Both end here.
        Fixture fixture = Fixture.Create();

        // Act
        NotFoundException exception = await ThrowsAsync<NotFoundException>(() =>
            fixture.Handler.HandleAsync(new UpdateCategoryGroupCommand(
                Guid.CreateVersion7(),
                SealedNarrative.EncodedName("Essentials"),
                SealedNarrative.EncodedIndex("Essentials"),
                null)));

        // Assert
        await Assert.That(exception.Message).IsEqualTo("Category group was not found.");
        await Assert.That(fixture.Groups.UpdateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithAMalformedNameAndAnUnknownId_RefusesTheMemberRatherThanReportingNotFound()
    {
        // Arrange — the ORDER of the two refusals, which nothing else in this file can see: every other
        // case sends either a bad body against a real row or a good body against a missing one, so a
        // handler with the block in either position satisfies all of them.
        //
        // The three-member decode sits ABOVE the lookup. It is a shape check on the request, so it needs
        // nothing from the database; run below, the handler has already pulled a tracked entity out of
        // the context before deciding the body was unusable. Neither order leaks existence — a caller
        // with a malformed body learns nothing about the row either way — so what decides it is that the
        // cheap, self-contained judgement belongs first.
        Fixture fixture = Fixture.Create();

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new UpdateCategoryGroupCommand(
                Guid.CreateVersion7(),
                "not an envelope",
                SealedNarrative.EncodedIndex("Essentials"),
                null)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(fixture.Groups.UpdateCallCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("not base64url at all")]
    [Arguments("")]
    public async Task HandleAsync_WithAMalformedName_RefusesAndLeavesEveryColumnUnchanged(string name)
    {
        // Arrange — the envelope member is opaque to this server, so the only thing it can refuse is the
        // shape: base64url text decoding to a well-framed envelope under the column's cap.
        Fixture fixture = Fixture.Create();
        CategoryGroup group = await fixture.SeedAsync("Essential Obligations", "Required spending");

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new UpdateCategoryGroupCommand(
                group.Id,
                name,
                SealedNarrative.EncodedIndex("Essentials"),
                SealedNarrative.EncodedDescription("Must pay"))));

        // Assert — all three columns, for the reason the class remarks give: the entity is tracked in
        // production, so a partial write survives the throw and commits with some later request.
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(group.Name.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Name("Essential Obligations").Envelope.ToArray(),
                CollectionOrdering.Matching);
        await Assert.That(group.NameKey.ToArray())
            .IsEquivalentTo(
                SealedNarrative.BlindIndex("Essential Obligations").ToArray(),
                CollectionOrdering.Matching);
        await Assert.That(group.Description).IsNotNull();
        await Assert.That(group.Description!.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Description("Required spending").Envelope.ToArray(),
                CollectionOrdering.Matching);
        await Assert.That(fixture.Groups.UpdateCallCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("not base64url at all")]
    [Arguments("aGVsbG8")]
    public async Task HandleAsync_WithAMalformedDescription_RefusesAndLeavesEveryColumnUnchanged(
        string description)
    {
        // Arrange — two ways a present description can be wrong that have nothing to do with absence: an
        // alphabet the decoder refuses, and perfectly good base64url that decodes to five bytes, which
        // is under the framing's floor. The second is what says the refusal is the ENVELOPE's rules and
        // not merely a decode.
        Fixture fixture = Fixture.Create();
        CategoryGroup group = await fixture.SeedAsync("Essential Obligations", "Required spending");

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new UpdateCategoryGroupCommand(
                group.Id,
                SealedNarrative.EncodedName("Essentials"),
                SealedNarrative.EncodedIndex("Essentials"),
                description)));

        // Assert — the name is read back too, because this is the member most likely to be judged last
        // and a handler that renamed the row before deciding the description was malformed leaves a
        // tracked entity holding half the caller's request.
        await Assert.That(exception.Errors.ContainsKey("Description")).IsTrue();
        await Assert.That(group.Name.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Name("Essential Obligations").Envelope.ToArray(),
                CollectionOrdering.Matching);
        await Assert.That(group.Description).IsNotNull();
        await Assert.That(group.Description!.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Description("Required spending").Envelope.ToArray(),
                CollectionOrdering.Matching);
        await Assert.That(fixture.Groups.UpdateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithAnEmptyStringDescription_RefusesAndLeavesTheRowUnchanged()
    {
        // Arrange — THE CASE THAT TELLS `is null` FROM `IsNullOrEmpty`, on this leg. The decoder
        // underneath refuses null and "" identically, so the distinction cannot live down there. Under
        // IsNullOrEmpty this body answers 204 and CLEARS a description the caller never asked to remove;
        // under `is null` it is a malformed member and a 400 the caller can act on. The two readings
        // differ by an entire column of somebody's data, and only this case separates them.
        Fixture fixture = Fixture.Create();
        CategoryGroup group = await fixture.SeedAsync("Essential Obligations", "Required spending");

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new UpdateCategoryGroupCommand(
                group.Id,
                SealedNarrative.EncodedName("Essentials"),
                SealedNarrative.EncodedIndex("Essentials"),
                string.Empty)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Description")).IsTrue();
        await Assert.That(group.Description).IsNotNull();
        await Assert.That(fixture.Groups.UpdateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithADescriptionOverItsOwnCap_RefusesAndLeavesTheRowUnchanged()
    {
        // Arrange — THE CAP HAS ONLY ONE OWNER ON THIS PATH. The name's ceiling is stated twice — once by
        // this handler and again inside IndexedName.Of — so a number mistyped on that line is refused by
        // the domain as a defect in this codebase. The description's is stated once, by this handler, and
        // widening it or handing int.MaxValue to the decode leaves every other case in this file green:
        // the over-cap value travels the whole ring and lands on CK_category_groups_description_length,
        // reaching the caller as a 23514 nothing translates rather than the 400 it should have been.
        //
        // The envelope is built here and not by SealedNarrative, because that fixture caps at
        // DescriptionBytes by construction and cannot produce the one value that catches this.
        Fixture fixture = Fixture.Create();
        CategoryGroup group = await fixture.SeedAsync("Essential Obligations", "Required spending");
        byte[] overCap = new byte[NarrativeFieldLimits.DescriptionBytes + 1];
        overCap[0] = CiphertextEnvelope.Version;

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new UpdateCategoryGroupCommand(
                group.Id,
                SealedNarrative.EncodedName("Essentials"),
                SealedNarrative.EncodedIndex("Essentials"),
                Base64UrlText.Encode(overCap))));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Description")).IsTrue();
        await Assert.That(group.Description).IsNotNull();
        await Assert.That(fixture.Groups.UpdateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithADescriptionPastTheNameCap_IsAccepted()
    {
        // Arrange — THE VALUE THAT SEPARATES THE TWO CAPS, AND ITS SIZE IS THE WHOLE OF THE CASE. An
        // envelope longer than NarrativeFieldLimits.NameBytes and well under DescriptionBytes: a value
        // OVER BOTH caps is refused whichever number is in force and therefore cannot tell them apart,
        // which is exactly why the neighbour below —
        // HandleAsync_WithADescriptionOverItsOwnCap_RefusesAndLeavesTheRowUnchanged, whose value is
        // DescriptionBytes + 1 — leaves this claim uncovered. It has to sit BETWEEN them.
        //
        // Two lines of this handler read a cap for the description and both are mutable to the name's
        // with nothing going red: the decode, which would answer a 400 on a note the column accepts,
        // and NarrativeField.SealedOrAbsent one line lower, which would throw ArgumentException and
        // reach the caller as a 500. This case is the only one on this leg that sees either.
        // CreateCategoryGroupHandlerTests carries the twin for the create leg; a constraint is a
        // property of a code path, and that one says nothing about this one.
        Fixture fixture = Fixture.Create();
        CategoryGroup group = await fixture.SeedAsync("Essential Obligations", null);
        string longLabel = new('x', NarrativeFieldLimits.NameBytes);

        // Act
        await fixture.Handler.HandleAsync(new UpdateCategoryGroupCommand(
            group.Id,
            SealedNarrative.EncodedName("Essentials"),
            SealedNarrative.EncodedIndex("Essentials"),
            SealedNarrative.EncodedDescription(longLabel)));

        // Assert — the note landed AND it is past the name's ceiling. Without the second assertion a
        // fixture whose label stopped producing an over-cap envelope would leave this case green while
        // measuring nothing.
        await Assert.That(group.Description).IsNotNull();
        await Assert.That(group.Description!.Envelope.Length)
            .IsGreaterThan(NarrativeFieldLimits.NameBytes);
        await Assert.That(fixture.Groups.UpdateCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task HandleAsync_WithANameOverItsOwnCap_ThrowsValidationExceptionRatherThanArgumentException()
    {
        // Arrange — the same value one column over, and the assertion is about WHICH exception. The
        // domain refuses an over-cap name too, but as an ArgumentException that becomes a 500:
        // IndexedName.Of is written to catch a ceiling mistyped in this codebase, not a body a caller
        // can correct. So the claim is not "an over-long name is refused" — it would be, either way —
        // but that the refusal reaches the caller as the 400 naming the member they sent.
        //
        // Hand int.MaxValue to this handler's name decode and every other case in this file stays
        // green: the over-cap envelope sails through the wire edge, reaches IndexedName.Of, and the
        // caller gets a 500 on a request that named its own fault.
        //
        // The row is seeded rather than left missing, so the value gets past the lookup and reaches the
        // domain factory at all. Against an unknown id the handler answers NotFoundException under the
        // mutation and this case would redden for the wrong reason.
        //
        // The envelope is built here and not by SealedNarrative, because that fixture caps at
        // NameBytes by construction and cannot produce the one value that catches this.
        Fixture fixture = Fixture.Create();
        CategoryGroup group = await fixture.SeedAsync("Essential Obligations", "Required spending");
        byte[] overCap = new byte[NarrativeFieldLimits.NameBytes + 1];
        overCap[0] = CiphertextEnvelope.Version;

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new UpdateCategoryGroupCommand(
                group.Id,
                Base64UrlText.Encode(overCap),
                SealedNarrative.EncodedIndex("Essentials"),
                null)));

        // Assert — all three columns, for the reason the class remarks give: the entity is tracked in
        // production, so a mutation that survived the throw commits with some later request.
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(group.Name.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Name("Essential Obligations").Envelope.ToArray(),
                CollectionOrdering.Matching);
        await Assert.That(group.NameKey.ToArray())
            .IsEquivalentTo(
                SealedNarrative.BlindIndex("Essential Obligations").ToArray(),
                CollectionOrdering.Matching);
        await Assert.That(group.Description).IsNotNull();
        await Assert.That(fixture.Groups.UpdateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithAnIndexOfTheWrongWidth_RefusesKeyedOnTheIndex()
    {
        // Arrange — a blind index is a keyed digest with no framing, so the width is the only shape check
        // this side can make. Thirty-one bytes of perfectly good base64url is the value that proves the
        // check is a width and not merely a decode: nothing here can recompute an index, so a wrong 32
        // bytes keys perfectly, never collides, and stands for a name the row does not hold.
        Fixture fixture = Fixture.Create();
        CategoryGroup group = await fixture.SeedAsync("Essential Obligations", "Required spending");
        string tooShort = Base64UrlText.Encode(new byte[IndexedName.BlindIndexLength - 1]);

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new UpdateCategoryGroupCommand(
                group.Id, SealedNarrative.EncodedName("Essentials"), tooShort, null)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("NameKey")).IsTrue();
        await Assert.That(fixture.Groups.UpdateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithEveryOpaqueMemberMalformed_ReportsAllThreeAtOnce()
    {
        // Arrange — THE HANDLER CLAIMS EVERY MEMBER IS ATTEMPTED AND EVERY FAILURE REPORTED, and this is
        // the only case that can tell that apart from fail-fast. Every other case in this file sends one
        // bad member, so a handler that threw the moment the envelope decode failed would satisfy all of
        // them. The description is the member most at risk, because it was added last and sits behind an
        // `is not null` branch: a handler that judged it inside an early return, or after the throw,
        // reports the two older members and nothing else.
        Fixture fixture = Fixture.Create();
        CategoryGroup group = await fixture.SeedAsync("Essential Obligations", "Required spending");

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(new UpdateCategoryGroupCommand(
                group.Id, "not an envelope", "not an index", "not an envelope either")));

        // Assert — three and not four: this leg parses no identifier. The COUNT is what makes this case
        // impossible for a fail-fast handler to pass — it would carry exactly one key and satisfy
        // whichever ContainsKey happened to name it.
        await Assert.That(exception.Errors.Count).IsEqualTo(3);
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("NameKey")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("Description")).IsTrue();
        await Assert.That(fixture.Groups.UpdateCallCount).IsEqualTo(0);
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class Fixture
    {
        private static readonly DateTimeOffset CreatedAtUtc =
            new(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);

        private Fixture()
        {
        }

        public required Guid BudgetId { get; init; }
        public required InMemoryCategoryGroupRepository Groups { get; init; }
        public required UpdateCategoryGroupHandler Handler { get; init; }

        public static Fixture Create()
        {
            var budgetId = Guid.CreateVersion7();
            var groups = new InMemoryCategoryGroupRepository(
                budgetId, new FakeTimeProvider(CreatedAtUtc));

            return new Fixture
            {
                BudgetId = budgetId,
                Groups = groups,
                Handler = new UpdateCategoryGroupHandler(groups),
            };
        }

        /// <summary>
        /// Seeds a group whose name is sealed and indexed from <paramref name="label" /> and whose
        /// description is sealed from <paramref name="descriptionLabel" />, or absent when that is
        /// <see langword="null" />.
        /// </summary>
        /// <remarks>
        /// Built through <see cref="CategoryGroup.Create" /> and the port's own <c>AddAsync</c> rather
        /// than through a convenience member on the fake, so this file depends only on the two contracts
        /// the slice pins and not on a seeding helper's parameter list.
        /// </remarks>
        public async Task<CategoryGroup> SeedAsync(string label, string? descriptionLabel)
        {
            CategoryGroup group = CategoryGroup.Create(
                Guid.CreateVersion7(),
                BudgetId,
                SealedNarrative.Indexed(label),
                descriptionLabel is null ? null : SealedNarrative.Description(descriptionLabel),
                await Groups.GetNextPositionAsync(),
                CreatedAtUtc.UtcDateTime);

            await Groups.AddAsync(group);
            return group;
        }
    }
}
