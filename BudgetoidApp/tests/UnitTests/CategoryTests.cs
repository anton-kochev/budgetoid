using Domain.Categories;
using Domain.Common;
using Domain.Security;
using TestSupport;
using TUnit.Assertions.Enums;

namespace UnitTests;

/// <summary>
/// The category entity once its name became an envelope with a blind index beside it, its description
/// became a sealed nullable free-text column, and its identifier stopped being minted here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four rules this file used to assert are gone, and every one of them moved rather than being
/// dropped.</b> A trimmed name, a name of at most two hundred characters, a description of at most five
/// hundred, and a whitespace-only description normalised to <see langword="null" /> were all questions
/// about characters. This server has none: it holds ciphertext over text it has never seen and no key to
/// open it with, so "how many characters is this?" and "is this only spaces?" are questions about a
/// plaintext that exists nowhere on this side. The client, which holds the key, is the only side that can
/// ask them. The cases below that accept a blank name, an over-long name and an over-long description are
/// what stop a future reader reading the absence as an oversight and restoring a check with nothing to
/// check against.
/// </para>
/// <para>
/// <b>The identifier is the caller's now, and that is the riskiest change in the slice.</b>
/// <c>Guid.CreateVersion7()</c> has left the entity and no minting overload replaces it, because the row
/// id is the associated data both narrative members were sealed against: a factory that ignored the
/// supplied id and minted its own writes a row whose name and description nobody can ever open, with
/// every constraint satisfied and nothing red. The symptom arrives months later as text that will not
/// decrypt. <see cref="Create_WithASuppliedId_StoresIt" /> is the case that notices a minting path coming
/// back through this signature, and <see cref="Create_WithTheEmptyGuid_IsRefusedNamingId" /> is the
/// refusal that only became reachable the day the caller started supplying the value.
/// </para>
/// <para>
/// <b>The description's disappearance is quieter than the name's and needs saying separately.</b> The
/// column is nullable, so a write path that decoded a description and then forgot to assign it writes
/// <see langword="null" /> — a legal row, byte-identical to one belonging to somebody who deliberately has
/// no description. On the <c>NOT NULL</c> name the same defect is a <c>23502</c> from the database. Here
/// nothing fires. <see cref="Create_WithANameAndANote_AssignsBothHalvesAndTheNote" />,
/// <see cref="Update_WithANewNameAndANewNote_ReplacesBothHalvesAndTheNote" /> and
/// <see cref="Place_LeavesTheNameAndTheNoteUnchanged" /> are the whole of the guard at this ring, which is
/// why none of them may be weakened to "the property is present".
/// </para>
/// <para>
/// <b><see cref="Category.Update" /> calls the validator and NO CASE IN THIS FILE HOLDS THAT.</b> Saying
/// which it does would take a category whose position, budget id, group id or identifier is already bad
/// at the moment <c>Update</c> runs, and there is no way to build one: <see cref="Category.Create" />
/// refuses each of them and <see cref="Category.Place" /> refuses a negative position and an empty group
/// id, so every instance reaching <c>Update</c> has already satisfied the rules <c>Update</c> would
/// re-check. This was measured on payees in the identical shape — whether <c>Rename</c> called the
/// validator was invisible to the suite in both directions — and the same is true here. What remains is
/// the null-name refusal, which is what <see cref="Update_WithANullName_LeavesAllThreeColumnsAsTheyWere" />
/// uses.
/// </para>
/// <para>
/// <b>The fixture labels are chosen, not decorative, and one property of them is stated as a
/// precondition rather than assumed.</b> <see cref="SealedNarrative.Name(string)" /> and
/// <see cref="SealedNarrative.Description(string)" /> run the SAME position-varying filler, so one label
/// produces the same bytes through both doors — which is exactly the situation in which a factory that
/// assigned one parameter to two fields would go unnoticed. Two DIFFERENT labels is what breaks the tie,
/// and a later author tidying the two labels into one would silently delete that guard. The cases that
/// depend on it therefore assert the fixtures differ, in the Arrange, before acting.
/// </para>
/// <para>
/// <b>What that precondition does NOT claim.</b> The shipped remark on the sibling file says two
/// different labels "share no byte at any offset"; measured, that is false — <c>Name("Groceries")</c> and
/// <c>Description("Weekly food shop")</c> agree at offset 35, and the sibling's own pair
/// (<c>"Essential Obligations"</c>, <c>"Required spending"</c>) agrees at offsets 39 and 44. Per-offset
/// disjointness is not a property of this filler and no case here relies on one. What the cases rely on
/// is that the two arrays are not EQUAL, which is a weaker claim, is true, and is the one the assertions
/// actually make.
/// </para>
/// </remarks>
public sealed class CategoryTests
{
    [Test]
    public async Task Create_WithASuppliedId_StoresIt()
    {
        // Arrange — an id minted here and threaded in, so the assertion is not "an id came back" but
        // "this one did".
        var id = Guid.CreateVersion7();

        // Act
        Category category = Category.Create(
            id,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Groceries"),
            null,
            0,
            UtcNow());

        // Assert — the identifier is the associated data the client sealed BOTH narrative members
        // against, so a factory that ignored this parameter and minted its own produces a row whose name
        // and description nobody can ever open: no constraint violated, nothing red, and the symptom
        // arriving months later as text that will not decrypt. Guid.CreateVersion7 leaves the production
        // file for that reason, and this is the case that would notice it coming back through this
        // signature.
        await Assert.That(category.Id).IsEqualTo(id);
    }

    [Test]
    public async Task Create_WithTheEmptyGuid_IsRefusedNamingId()
    {
        // Act — the empty Guid became reachable the day the identifier stopped being minted here: it is
        // what a caller that threaded a default through hands over.
        ValidationException exception = ThrowsValidationException(() => Category.Create(
            Guid.Empty,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Groceries"),
            null,
            0,
            UtcNow()));

        // Assert — keyed on the id, not merely thrown. Left to the primary key instead, all-zero is a
        // legal uuid: the first such row stores and the second collides under a constraint name that says
        // nothing about the caller that never chose an id at all.
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
    }

    [Test]
    public async Task Create_WithAnEmptyBudgetId_IsRefusedNamingBudgetId()
    {
        // Act — the tenancy rule, which survived the sealing unchanged.
        ValidationException exception = ThrowsValidationException(() => Category.Create(
            Guid.CreateVersion7(),
            Guid.Empty,
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Groceries"),
            null,
            0,
            UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("BudgetId")).IsTrue();
    }

    [Test]
    public async Task Create_WithAnEmptyCategoryGroupId_IsRefusedNamingCategoryGroupId()
    {
        // Act — a category with no group is not a shape any screen can render, and the composite foreign
        // key would refuse it anyway; this is the refusal that names the member a caller can correct.
        ValidationException exception = ThrowsValidationException(() => Category.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.Empty,
            SealedNarrative.Indexed("Groceries"),
            null,
            0,
            UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("CategoryGroupId")).IsTrue();
    }

    [Test]
    public async Task Create_WithANegativePosition_IsRefusedNamingPosition()
    {
        // Act — position is an int the server still reads, so sealing took no capability away from it. It
        // is the only rule left in this validator that is about a value rather than about an identifier.
        ValidationException exception = ThrowsValidationException(() => Category.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Groceries"),
            null,
            -1,
            UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Position")).IsTrue();
    }

    [Test]
    public async Task Create_WithEveryIdentifierEmptyAndANegativePosition_ReportsAllFourAtOnce()
    {
        // Arrange — the validator collects and does not fail fast, and this is the only case in the file
        // that can tell those apart: every other refusal above sends one bad argument, so a version that
        // threw on the first failure would satisfy all of them.

        // Act
        ValidationException exception = ThrowsValidationException(() => Category.Create(
            Guid.Empty,
            Guid.Empty,
            Guid.Empty,
            SealedNarrative.Indexed("Groceries"),
            null,
            -1,
            UtcNow()));

        // Assert — the COUNT is what a fail-fast validator cannot satisfy: it would carry exactly one key
        // and pass whichever ContainsKey happened to name it. Four keys, because a caller that threaded
        // defaults through got the three identifiers and the position wrong in one go.
        await Assert.That(exception.Errors.Count).IsEqualTo(4);
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("BudgetId")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("CategoryGroupId")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("Position")).IsTrue();
    }

    [Test]
    public async Task Create_WithANameAndANote_AssignsBothHalvesAndTheNote()
    {
        // Arrange — THE CASE THE FIXTURE'S POSITION-VARYING FILLER EXISTS FOR. Three narrative-shaped
        // values reach this factory — the name envelope, the blind index and the description — and a
        // factory that assigned one of them to two fields writes a row that violates nothing. Two
        // different labels are what make the three distinguishable, because Name and Description run the
        // same filler and one label produces identical bytes through both doors.
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();
        var categoryGroupId = Guid.CreateVersion7();
        IndexedName name = SealedNarrative.Indexed("Groceries");
        NarrativeField description = SealedNarrative.Description("Weekly food shop");
        DateTime createdAtUtc = new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);

        // Arrange, continued — THE PRECONDITION, stated rather than assumed. All three assertions below
        // are decoration the moment two of these values coincide, and the way they coincide is a later
        // author tidying "Groceries" and "Weekly food shop" into one label. Asserted here, in the open,
        // so that edit reddens on the line that caused it instead of quietly deleting the guard.
        await Assert.That(name.Name.Envelope.ToArray())
            .IsNotEquivalentTo(description.Envelope.ToArray());
        await Assert.That(name.Name.Envelope.ToArray())
            .IsNotEquivalentTo(name.BlindIndex.ToArray());

        // Act
        Category category = Category.Create(
            id, budgetId, categoryGroupId, name, description, 2, createdAtUtc);

        // Assert — both halves of the name and the note are compared as the bytes they carry rather than
        // by reference. Neither NarrativeField nor ReadOnlyMemory<byte> gives content equality here for
        // free, and a reference comparison would pass for any instance the factory happened to hold on to.
        //
        // The name half used to be asserted as the string "Groceries", trimmed from "  Groceries  ". Both
        // the value and the trim are gone: the column holds an envelope over text this server has never
        // seen, so there is no string to compare and no whitespace to strip. What survives is that the
        // factory stored the values it was handed, unaltered, in all three columns.
        //
        // CollectionOrdering.Matching IS PART OF THE ASSERTION, everywhere in this file. IsEqualTo over
        // two byte[] compares REFERENCES and fails even when the contents and the order agree, and TUnit's
        // failure message names IsEquivalentTo as the fix — whose default is CollectionOrdering.Any, so
        // the bare overload passes on every permutation of an envelope's or a digest's bytes. Order is the
        // whole of what a ciphertext is: a factory that permuted any of the three would satisfy the bare
        // overload and produce right-width, wrong-value bytes that no key opens and no recomputation on
        // this side can notice.
        await Assert.That(category.Id).IsEqualTo(id);
        await Assert.That(category.BudgetId).IsEqualTo(budgetId);
        await Assert.That(category.CategoryGroupId).IsEqualTo(categoryGroupId);
        await Assert.That(category.Name.Envelope.ToArray())
            .IsEquivalentTo(name.Name.Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(category.NameKey.ToArray())
            .IsEquivalentTo(name.BlindIndex.ToArray(), CollectionOrdering.Matching);
        await Assert.That(category.Description).IsNotNull();
        await Assert.That(category.Description!.Envelope.ToArray())
            .IsEquivalentTo(description.Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(category.Position).IsEqualTo(2);
        await Assert.That(category.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithNoDescription_LeavesItNull()
    {
        // Act — the nullable column's other legal state, and the one a category created through the route
        // with no description member lands in.
        Category category = Category.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Groceries"),
            null,
            0,
            UtcNow());

        // Assert
        await Assert.That(category.Description).IsNull();
    }

    [Test]
    public async Task Create_WithoutAName_ThrowsArgumentNullExceptionNamingName()
    {
        // Arrange — the signature says a name is present, so a null is a defect in this codebase rather
        // than a field a caller corrects by editing a request. It is refused ahead of ValidateOrThrow and
        // as an ArgumentNullException, not as the ValidationException that becomes a 400 about a member
        // the request may not even have. The description takes no such refusal: its parameter is declared
        // nullable, so absence there is an ordinary value.

        // Act
        ArgumentNullException exception = ThrowsArgumentNullException(() => Category.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            null!,
            null,
            0,
            UtcNow()));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("name");
    }

    [Test]
    public async Task Create_WithoutANameAndWithAnEmptyId_ThrowsArgumentNullExceptionRatherThanValidationException()
    {
        // Arrange — every argument is wrong at once, which is the only way to see WHICH refusal runs
        // first. The case above cannot: with good identifiers and a good position, ValidateOrThrow finds
        // nothing, so the null refusal wins whether it is written above the call or below it.

        // Act — a null name is a defect in this codebase and an empty id is a field a caller could
        // correct, so the order decides which the caller is told about. Below ValidateOrThrow, the
        // programmer error is reported as a 400 about the id and the real fault never surfaces.
        ArgumentNullException exception = ThrowsArgumentNullException(() => Category.Create(
            Guid.Empty, Guid.Empty, Guid.Empty, null!, null, -1, UtcNow()));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("name");
    }

    [Test]
    public async Task Create_WithABlankName_IsAccepted()
    {
        // Arrange — the shortest envelope the format can produce: a version, a nonce and a tag over an
        // empty plaintext. This is exactly what a client sealing a blank name, or a name of nothing but
        // spaces, sends.
        IndexedName blank = SealedNarrative.Indexed();

        // Act
        Category category = Category.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            blank,
            null,
            0,
            UtcNow());

        // Assert — this factory used to refuse a blank name with a ValidationException keyed on Name, and
        // it no longer can. The capability moved to the client, which is the only side that holds a key;
        // it was not quietly dropped. THE SERVER CANNOT MEASURE CHARACTERS IN AN ENVELOPE AND NEVER WILL —
        // it holds ciphertext over text it has never seen, so "is this just whitespace?" is a question
        // about a plaintext that exists nowhere on this side. A future reader who reads the absence as an
        // oversight and restores the check has nothing to check it against; this case is what stops that
        // edit.
        await Assert.That(category.Name.Envelope.Length).IsEqualTo(CiphertextEnvelope.MinimumLength);
    }

    [Test]
    public async Task Create_WithANameFarPastTheOldCharacterLimit_IsAccepted()
    {
        // Arrange — an envelope well past the two hundred characters this factory used to refuse, and well
        // under NarrativeFieldLimits.NameBytes, which is the only ceiling left and is measured in stored
        // bytes by NarrativeField before the value ever reaches Category.
        IndexedName longName = SealedNarrative.Indexed(new string('x', 400));

        // Act
        Category category = Category.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            longName,
            null,
            0,
            UtcNow());

        // Assert — the same relocation as the blank case, in the other direction. A length rule here would
        // be a rule about characters, and the server counts none: AES-GCM ciphertext is exactly as long as
        // its plaintext, but the framing and the encoding sit on top of it, so bytes stored and characters
        // typed are different questions and only the first is answerable here.
        await Assert.That(category.Name.Envelope.Length).IsGreaterThan(200);
    }

    [Test]
    public async Task Create_WithADescriptionFarPastTheOldCharacterLimit_IsAccepted()
    {
        // Arrange — an envelope past the five hundred characters this factory used to refuse. The
        // description's ceiling is NarrativeFieldLimits.DescriptionBytes and not NameBytes, and it is a
        // different number for a reason: the two are field CLASSES with different caps, so a helper that
        // sealed a description under the name's limit would refuse values this column accepts.
        NarrativeField longDescription = SealedNarrative.Description(new string('x', 600));

        // Act
        Category category = Category.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Groceries"),
            longDescription,
            0,
            UtcNow());

        // Assert
        await Assert.That(category.Description).IsNotNull();
        await Assert.That(category.Description!.Envelope.Length).IsGreaterThan(500);
    }

    [Test]
    public async Task Create_WithAnEmptiedDescription_StoresTheEnvelopeRatherThanNull()
    {
        // Arrange — the shortest envelope the format can produce, which is what a client sealing an empty
        // description sends. AES-GCM ciphertext is exactly the length of its plaintext, so an emptied note
        // seals to a version, a nonce and a tag and nothing else.
        NarrativeField blank = SealedNarrative.Description();

        // Act
        Category category = Category.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Groceries"),
            blank,
            0,
            UtcNow());

        // Assert — "CLEARED" AND "NEVER FILLED" ARE DIFFERENT ROWS AND THIS FACTORY MUST NOT FOLD THEM.
        // The deleted NormalizeDescription mapped a whitespace-only or empty description to null, which is
        // precisely that fold; it went with the string parameter and cannot come back in any form, because
        // there is no text left here to inspect. A twenty-nine-byte envelope is a description somebody
        // wrote and then emptied, and NULL is a description nobody ever wrote — the schema represents both,
        // and this case is where the entity is held to representing both too.
        await Assert.That(category.Description).IsNotNull();
        await Assert.That(category.Description!.Envelope.Length)
            .IsEqualTo(CiphertextEnvelope.MinimumLength);
    }

    [Test]
    public async Task Update_WithANewNameAndANewNote_ReplacesBothHalvesAndTheNote()
    {
        // Arrange — a category created under one name and note and corrected to another, which is the most
        // common use of this method. The labels are what make the halves distinguishable: SealedNarrative
        // derives an envelope and an index from the same text, so "Groceries" produces neither of the
        // values "Food Shopping" produces.
        Category category = NewCategory("Groceries", "Weekly food shop");

        // Arrange, continued — the same precondition Create's case states, for the same reason: the four
        // assertions below discriminate only while the two new labels differ from each other and from the
        // two old ones.
        await Assert.That(SealedNarrative.Name("Food Shopping").Envelope.ToArray())
            .IsNotEquivalentTo(SealedNarrative.Description("Household staples").Envelope.ToArray());

        // Act
        category.Update(
            SealedNarrative.Indexed("Food Shopping"), SealedNarrative.Description("Household staples"));

        // Assert — THE INDEX IS ASSERTED TO BE THE NEW ONE AND EXPLICITLY NOT THE PREVIOUS ONE, which is
        // the whole of what the IndexedName parameter buys. A member that took a bare NarrativeField would
        // pass the envelope assertion and fail only the two index ones: the row would hold new ciphertext
        // under the old name's index. That is bad and silent — recomputing a digest to check needs the
        // account's index key, which lives in a browser. What it costs is the uniqueness rule:
        // IX_categories_budget_id_name_key stops guarding the name the row now holds and starts guarding
        // one it does not.
        //
        // The description is asserted in the same breath and for the reason the class remarks give: it is
        // the parameter whose loss the schema cannot see. An Update that replaced the name and left the
        // description alone, or nulled it, satisfies every constraint on the table.
        //
        // CollectionOrdering.Matching on the positive assertions, for the reason
        // Create_WithANameAndANote_AssignsBothHalvesAndTheNote states. The NEGATIVE one below deliberately
        // keeps the default: CollectionOrdering.Any there means "not even a permutation of the old index",
        // which is the stronger claim and the one worth making.
        await Assert.That(category.Name.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Name("Food Shopping").Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(category.NameKey.ToArray())
            .IsEquivalentTo(
                SealedNarrative.BlindIndex("Food Shopping").ToArray(), CollectionOrdering.Matching);
        await Assert.That(category.NameKey.ToArray())
            .IsNotEquivalentTo(SealedNarrative.BlindIndex("Groceries").ToArray());
        await Assert.That(category.Description).IsNotNull();
        await Assert.That(category.Description!.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Description("Household staples").Envelope.ToArray(),
                CollectionOrdering.Matching);
    }

    [Test]
    public async Task Update_WithANewNameAndNoDescription_ClearsTheNote()
    {
        // Arrange — a category that HAS a description, so that "no description" is a change rather than a
        // restatement of what was already there. Started from null, this case passes for an Update that
        // never touches the field at all.
        Category category = NewCategory("Groceries", "Weekly food shop");

        // Act
        category.Update(SealedNarrative.Indexed("Food Shopping"), null);

        // Assert — THE ROUTE IS A PUT AND A PUT IS A FULL REPLACEMENT, so an absent description means this
        // category has no description, and Optional<T> has no business appearing anywhere on this path.
        // The transaction routes carry Optional<T> because they are PATCH and genuinely have a third
        // "leave it alone" state; inventing one here would be a state the route does not have and the
        // client has never sent.
        await Assert.That(category.Description).IsNull();

        // And the name really was replaced, so a no-op Update cannot pass this case on the null alone.
        await Assert.That(category.NameKey.ToArray())
            .IsEquivalentTo(
                SealedNarrative.BlindIndex("Food Shopping").ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Update_LeavesIdBudgetIdCategoryGroupIdPositionAndCreatedAtUtcUnchanged()
    {
        // Arrange — BudgetId is the tenancy rule: a category that changed budget would carry its
        // transactions into someone else's ledger. Id is load-bearing for a second reason — it is the
        // associated data every envelope this row has ever held was sealed against, so an update that also
        // re-minted it would leave a name and a description nobody can open. CategoryGroupId and Position
        // are the arrangement the person made by hand, and this method is not the one that moves a
        // category; Place is.
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();
        var categoryGroupId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();
        Category category = Category.Create(
            id,
            budgetId,
            categoryGroupId,
            SealedNarrative.Indexed("Groceries"),
            SealedNarrative.Description("Weekly food shop"),
            2,
            createdAtUtc);

        // Act
        category.Update(
            SealedNarrative.Indexed("Food Shopping"), SealedNarrative.Description("Household staples"));

        // Assert
        await Assert.That(category.Id).IsEqualTo(id);
        await Assert.That(category.BudgetId).IsEqualTo(budgetId);
        await Assert.That(category.CategoryGroupId).IsEqualTo(categoryGroupId);
        await Assert.That(category.Position).IsEqualTo(2);
        await Assert.That(category.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Update_WithANullName_ThrowsNamingTheParameter()
    {
        // Arrange — Create's argument, restated on the other write path: half a name has no spelling this
        // signature accepts, and neither does no name at all.
        Category category = NewCategory("Groceries", "Weekly food shop");

        // Act
        ArgumentNullException exception = ThrowsArgumentNullException(() =>
            category.Update(null!, SealedNarrative.Description("Household staples")));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("name");
    }

    [Test]
    public async Task Update_WithANullName_LeavesAllThreeColumnsAsTheyWere()
    {
        // Arrange — the claim is that a refused update changes NOTHING, on all three columns it can write.
        // There is no ValidationException path this file can reach to make the claim with: every rule
        // Update's validator still owns is about a value no reachable instance can be holding — see the
        // class remarks. The null refusal is what remains, and it is reached ahead of all three
        // assignments.
        Category category = NewCategory("Groceries", "Weekly food shop");

        // Act
        ThrowsArgumentNullException(() => category.Update(null!, null));

        // Assert — WHAT THIS CASE HOLDS IS NARROWER THAN IT LOOKS. An Update that dereferenced the
        // parameter into Name and only then hit the null cannot be written: `name.Name` on a null throws
        // NullReferenceException on the dereference, before any assignment, so the half-updated row is
        // unreachable in C#. What it does hold is that the description survived a refused call — an Update
        // that assigned the description FIRST and then dereferenced the name would leave a category with a
        // cleared note and the old name, which is a real ordering and the one this case sees. The three
        // assertions also read all three columns off a category this case never updated successfully, so
        // they stand behind Create's assignment as much as behind Update's refusal.
        //
        // CollectionOrdering.Matching for the reason stated on
        // Create_WithANameAndANote_AssignsBothHalvesAndTheNote.
        await Assert.That(category.Name.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Name("Groceries").Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(category.NameKey.ToArray())
            .IsEquivalentTo(
                SealedNarrative.BlindIndex("Groceries").ToArray(), CollectionOrdering.Matching);
        await Assert.That(category.Description).IsNotNull();
        await Assert.That(category.Description!.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Description("Weekly food shop").Envelope.ToArray(),
                CollectionOrdering.Matching);
    }

    [Test]
    public async Task Place_WithValidDestination_ChangesGroupAndPosition()
    {
        // Arrange — Place survived the sealing untouched: group id and position are values the server
        // still reads.
        Category category = NewCategory("Groceries", "Weekly food shop");
        var destinationGroupId = Guid.CreateVersion7();

        // Act
        category.Place(destinationGroupId, 4);

        // Assert
        await Assert.That(category.CategoryGroupId).IsEqualTo(destinationGroupId);
        await Assert.That(category.Position).IsEqualTo(4);
    }

    [Test]
    public async Task Place_WithInvalidDestination_ThrowsAndLeavesPlacementUnchanged()
    {
        // Arrange
        Category category = NewCategory("Groceries", "Weekly food shop");
        Guid originalGroupId = category.CategoryGroupId;
        int originalPosition = category.Position;

        // Act
        ValidationException exception =
            ThrowsValidationException(() => category.Place(Guid.Empty, -1));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("CategoryGroupId")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("Position")).IsTrue();
        await Assert.That(category.CategoryGroupId).IsEqualTo(originalGroupId);
        await Assert.That(category.Position).IsEqualTo(originalPosition);
    }

    [Test]
    public async Task Place_LeavesTheNameAndTheNoteUnchanged()
    {
        // Arrange — Place is the THIRD write path on this entity and the only one whose signature mentions
        // no narrative value at all, which is exactly why a Place that reset one would go unnoticed. On
        // the NOT NULL name that reset is a 23502 the database reports; on the nullable description it is
        // a legal row, and nothing anywhere else in the suite reads a note back across a move.
        Category category = NewCategory("Groceries", "Weekly food shop");

        // Act
        category.Place(Guid.CreateVersion7(), 4);

        // Assert
        await Assert.That(category.Name.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Name("Groceries").Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(category.NameKey.ToArray())
            .IsEquivalentTo(
                SealedNarrative.BlindIndex("Groceries").ToArray(), CollectionOrdering.Matching);
        await Assert.That(category.Description).IsNotNull();
        await Assert.That(category.Description!.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Description("Weekly food shop").Envelope.ToArray(),
                CollectionOrdering.Matching);
    }

    /// <summary>
    /// A category under <paramref name="label" /> and <paramref name="descriptionLabel" />, for the cases
    /// whose subject is the update or the move rather than the identifier, the position or the creation
    /// instant.
    /// </summary>
    /// <param name="label">
    /// What distinguishes this category's name from the next one's. It is NOT the category's name and is
    /// never read back as one — the column holds an envelope this side has no key for.
    /// </param>
    /// <param name="descriptionLabel">
    /// The same, for the description, or <see langword="null" /> for a category that has none. It must
    /// differ from <paramref name="label" />: the two fixtures run one filler, so a shared label makes a
    /// factory that assigned one parameter twice invisible.
    /// </param>
    /// <summary>
    /// A content-key rotation replaces both halves of the name and the note together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The second labels model the same text under two keys, not new text.</b> A rotation
    /// re-encrypts what the row already holds, so nothing about the category changes except the bytes —
    /// but <see cref="SealedNarrative" /> derives everything from the label it is handed, so "the same
    /// text under a new key" has no other spelling here than a second label.
    /// </para>
    /// <para>
    /// <b>The index is asserted to have moved too, and that is the reason the parameter is an
    /// <see cref="IndexedName" />.</b> The index key rotates alongside the content key, so a reseal that
    /// replaced the envelope alone would leave the row's name sealed under the new content key and keyed
    /// under the old index one — the half-written name
    /// <see cref="Update_WithANewNameAndANewNote_ReplacesBothHalvesAndTheNote" /> argues about, arriving
    /// on every category in the budget at once and by a path nobody is watching.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Reseal_ReplacesBothHalvesOfTheNameAndTheNote()
    {
        // Arrange
        Category category = NewCategory("Groceries", "Weekly food shop");

        // Act
        category.Reseal(
            SealedNarrative.Indexed("Groceries resealed"),
            SealedNarrative.Description("Weekly food shop resealed"),
            Guid.CreateVersion7());

        // Assert — CollectionOrdering.Matching on the positive assertions for the reason
        // Create_WithANameAndANote_AssignsBothHalvesAndTheNote states; the negative one keeps the
        // default, which is the stronger "not even a permutation" claim.
        await Assert.That(category.Name.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Groceries resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(category.NameKey.ToArray()).IsEquivalentTo(
            SealedNarrative.BlindIndex("Groceries resealed").ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(category.NameKey.ToArray())
            .IsNotEquivalentTo(SealedNarrative.BlindIndex("Groceries").ToArray());
        await Assert.That(category.Description!.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Description("Weekly food shop resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
    }

    /// <summary>
    /// A reseal stamps the row with the id of the rotation that rewrote it.
    /// </summary>
    /// <remarks>
    /// The stamp is the whole reason the column exists, argued at <c>Budget.RotationId</c>: a re-sealed
    /// envelope and an untouched one are byte-for-byte indistinguishable to a server holding no key, so
    /// the completion step — which destroys the only copies of the old keys — can only know the rewrite
    /// finished by being told, in the same transaction as the ciphertext. A reseal that replaced the
    /// envelopes and left this column null passes every other accepting case in this file and makes the
    /// account un-completable.
    /// </remarks>
    [Test]
    public async Task Reseal_StampsTheRotationItWasGiven()
    {
        // Arrange — the id minted here and threaded in, so the assertion is not "a stamp appeared" but
        // "this rotation's did". A member that minted its own would leave every row stamped with an id
        // no completion step is looking for.
        Category category = NewCategory("Groceries", "Weekly food shop");
        var rotationId = Guid.CreateVersion7();

        // Act
        category.Reseal(
            SealedNarrative.Indexed("Groceries resealed"),
            SealedNarrative.Description("Weekly food shop resealed"),
            rotationId);

        // Assert
        await Assert.That(category.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// A reseal moves the two narrative columns and the stamp and touches nothing else.
    /// </summary>
    /// <remarks>
    /// <b>This is the case that catches a reseal built by reusing <see cref="Category.Update" /> or
    /// <see cref="Category.Place" />.</b> A category's placement is two columns, not one: the group it
    /// sits in and its position inside that group, and both are relative to the whole tree — which is
    /// why the category list is one of the reads delivered whole. A rotation that wrote either would
    /// rearrange somebody's budget as a side effect of re-encrypting it, in bulk and with nothing to
    /// show for it on screen but a tree that moved. The identity columns are stronger still —
    /// <see cref="Category.Id" /> is the associated data every envelope this row has ever held was
    /// sealed against, so a reseal that re-minted it would produce values nobody can ever open.
    /// </remarks>
    [Test]
    public async Task Reseal_LeavesPositionCategoryGroupIdAndIdentityUnchanged()
    {
        // Arrange — a non-zero position, so a reseal that reset it to the default is visible rather than
        // accidentally right.
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();
        var categoryGroupId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();
        Category category = Category.Create(
            id,
            budgetId,
            categoryGroupId,
            SealedNarrative.Indexed("Groceries"),
            SealedNarrative.Description("Weekly food shop"),
            2,
            createdAtUtc);

        // Act
        category.Reseal(
            SealedNarrative.Indexed("Groceries resealed"),
            SealedNarrative.Description("Weekly food shop resealed"),
            Guid.CreateVersion7());

        // Assert
        await Assert.That(category.Id).IsEqualTo(id);
        await Assert.That(category.BudgetId).IsEqualTo(budgetId);
        await Assert.That(category.CategoryGroupId).IsEqualTo(categoryGroupId);
        await Assert.That(category.Position).IsEqualTo(2);
        await Assert.That(category.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    /// <summary>
    /// A rotation that supplies no note for a category that has one is refused.
    /// </summary>
    /// <remarks>
    /// The clearing arm of the presence rule <c>NarrativeReseal.Resealed</c> owns, reached through this
    /// entity so that the category's note is actually routed through it. The column is nullable, so
    /// writing the absence through produces a legal row that violates no constraint and is
    /// byte-identical to one belonging to somebody who deliberately filed no note — the failure
    /// <see cref="Category.Description" />'s own remarks describe as invisible where the same omission
    /// on a <c>NOT NULL</c> name is <c>23502</c>.
    /// </remarks>
    [Test]
    public async Task Reseal_WithNoNoteOverACategoryThatHasOne_IsRefused()
    {
        // Arrange
        Category category = NewCategory("Groceries", "Weekly food shop");

        // Act
        ValidationException exception = ThrowsValidationException(() => category.Reseal(
            SealedNarrative.Indexed("Groceries resealed"),
            null,
            Guid.CreateVersion7()));

        // Assert — keyed on the member the request carries. A refusal filed under a word invented by the
        // entity reaches the client verbatim as a 400 naming a member no request has.
        await Assert.That(exception.Errors.ContainsKey(nameof(Category.Description))).IsTrue();
    }

    /// <summary>
    /// A rotation that supplies a note for a category that has none is refused.
    /// </summary>
    /// <remarks>
    /// The quieter arm, and the one a reviewer will propose relaxing: filling in an empty note harms no
    /// data. It is refused because presence is the only property this side can check at all, so an arm
    /// that admits a change of presence gives up the whole of what the rule is made of — and what lands
    /// in that column is text the server cannot read, attributed to a person who never wrote it, in a
    /// run they authorised as "re-encrypt what I have".
    /// </remarks>
    [Test]
    public async Task Reseal_WithANoteOverACategoryThatHasNone_IsRefused()
    {
        // Arrange
        Category category = NewCategory("Groceries", null);

        // Act
        ValidationException exception = ThrowsValidationException(() => category.Reseal(
            SealedNarrative.Indexed("Groceries resealed"),
            SealedNarrative.Description("Weekly food shop"),
            Guid.CreateVersion7()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(Category.Description))).IsTrue();
    }

    /// <summary>
    /// A category that never had a note is rotated, stamped, and left without one.
    /// </summary>
    /// <remarks>
    /// <b>The control for both refusals, and not a filler case.</b> Without it, a reseal that threw
    /// whenever either side of the note was null would pass both cases above and would make every
    /// account holding one note-less category un-rotatable — a refusal the person cannot act on, because
    /// the field they are being refused for is one they never filled in. The stamp is asserted here for
    /// the same reason: completion needs a full house, so the rows with less to re-encrypt still have to
    /// be accounted for, and a reseal that returned early on a null note would leave exactly those rows
    /// unstamped and the run permanently one short.
    /// </remarks>
    [Test]
    public async Task Reseal_WithNoNoteOverACategoryThatHasNone_IsAcceptedAndStillStamps()
    {
        // Arrange
        Category category = NewCategory("Groceries", null);
        var rotationId = Guid.CreateVersion7();

        // Act
        category.Reseal(SealedNarrative.Indexed("Groceries resealed"), null, rotationId);

        // Assert
        await Assert.That(category.Description).IsNull();
        await Assert.That(category.Name.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Groceries resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(category.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// A refused reseal writes nothing at all — not the name, not the note, and above all not the stamp.
    /// </summary>
    /// <remarks>
    /// <b>The stamp is the assertion that matters here.</b> A reseal that stamped before it judged the
    /// note, or that assigned the name first and refused afterwards, leaves a row marked as rotated that
    /// was not — and the stamp is the one signal completion trusts, so the destructive step would
    /// promote the new keys over a row still sealed under the old one. That is the exact loss the column
    /// was added to prevent, produced by the member that writes it. The previous rotation's id is the
    /// fixture rather than <see langword="null" /> so that a member which cleared the stamp on refusal
    /// also reddens.
    /// </remarks>
    [Test]
    public async Task Reseal_WithARefusedNote_LeavesEveryColumnAndTheStampAsTheyWere()
    {
        // Arrange — a category already carried through one rotation, now handed a chunk that drops its
        // note.
        Category category = NewCategory("Groceries", "Weekly food shop");
        var firstRotationId = Guid.CreateVersion7();
        category.Reseal(
            SealedNarrative.Indexed("Groceries resealed"),
            SealedNarrative.Description("Weekly food shop resealed"),
            firstRotationId);

        // Act
        ThrowsValidationException(() => category.Reseal(
            SealedNarrative.Indexed("Groceries rotated twice"),
            null,
            Guid.CreateVersion7()));

        // Assert — CollectionOrdering.Matching for the reason
        // Create_WithANameAndANote_AssignsBothHalvesAndTheNote states.
        await Assert.That(category.Name.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Groceries resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(category.NameKey.ToArray()).IsEquivalentTo(
            SealedNarrative.BlindIndex("Groceries resealed").ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(category.Description!.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Description("Weekly food shop resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(category.RotationId).IsEqualTo(firstRotationId);
    }

    /// <summary>
    /// An ordinary update clears the stamp a rotation left on the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the rule the whole stamp rests on, and it is the easiest one to leave out</b> —
    /// nothing about <see cref="Category.Update" /> reads as being part of a rotation, so a reader
    /// implementing the reseal member has no reason to open this one.
    /// </para>
    /// <para>
    /// <b>What goes wrong without it.</b> A second browser tab still holding the OLD content key can
    /// rename a category this rotation has already stamped. It writes old-key ciphertext, and — with
    /// this line missing — it does not touch the stamp, so the row ends up carrying old-key ciphertext
    /// under a current stamp. Completion then reads a full house, promotes the new keys and destroys the
    /// old ones, and that category's name and note are gone: no constraint violated, nothing red, and
    /// the symptom is a screen that will not decrypt. Clearing the stamp is what makes completion refuse
    /// instead, which is a run the person can retry.
    /// </para>
    /// <para>
    /// <b><see cref="Category.Place" /> is deliberately not given the same case.</b> It writes no
    /// narrative column, so a stale tab moving a category between groups invalidates no ciphertext and
    /// has nothing to disown. Clearing there would fail rotations for edits that cost them nothing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Update_ClearsTheRotationStamp()
    {
        // Arrange — stamped through the real reseal path rather than reflected in, so the case describes
        // the sequence that actually happens: a chunk rewrites the row, then a stale tab edits it.
        Category category = NewCategory("Groceries", "Weekly food shop");
        category.Reseal(
            SealedNarrative.Indexed("Groceries resealed"),
            SealedNarrative.Description("Weekly food shop resealed"),
            Guid.CreateVersion7());

        // Act
        category.Update(
            SealedNarrative.Indexed("Household"),
            SealedNarrative.Description("Everything else"));

        // Assert
        await Assert.That(category.RotationId).IsNull();
    }

    /// <summary>
    /// Moving a category between groups, or within one, leaves the stamp standing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The inverse of <see cref="Update_ClearsTheRotationStamp" />, and the mutation it catches is
    /// clearing the stamp HERE.</b> <see cref="Category.Place" /> writes a group id and an int, both
    /// values the server reads; it touches no envelope, so after it runs the row's ciphertext is still
    /// whatever the rotation sealed and the stamp is still honest. A reader who takes "an edit clears
    /// the stamp" as the rule rather than "a NARRATIVE write clears the stamp" will put the line in both
    /// members, and every case in this file except this one stays green.
    /// </para>
    /// <para>
    /// <b>Why that is worse than it looks, and why it is not a data-loss bug.</b> Nothing is lost — the
    /// row is fine. What breaks is convergence. Completion refuses a rotation that genuinely finished,
    /// the client re-seals the un-stamped rows, and on an account where somebody is rearranging the
    /// category tree while the run proceeds, each pass re-stamps rows the next drag un-stamps. The
    /// rotation may never finish, on exactly the accounts large enough to need several chunks, and there
    /// is nothing on screen to explain why.
    /// </para>
    /// <para>
    /// <b>This is a pin, not a red bar.</b> It goes green the moment the member exists, because
    /// <see cref="Category.Place" /> writes no stamp today. Its value is entirely in the mutation named
    /// above.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Place_LeavesTheRotationStampStanding()
    {
        // Arrange — stamped through the real reseal path, then moved.
        Category category = NewCategory("Groceries", "Weekly food shop");
        var rotationId = Guid.CreateVersion7();
        category.Reseal(
            SealedNarrative.Indexed("Groceries resealed"),
            SealedNarrative.Description("Weekly food shop resealed"),
            rotationId);
        var destinationGroupId = Guid.CreateVersion7();

        // Act
        category.Place(destinationGroupId, 5);

        // Assert
        await Assert.That(category.CategoryGroupId).IsEqualTo(destinationGroupId);
        await Assert.That(category.Position).IsEqualTo(5);
        await Assert.That(category.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// A reseal quoting the empty rotation id is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule is <see cref="Domain.Users.KeyRotation.Begin" />'s, restated where the stamp is
    /// written rather than invented here.</b> That factory already refuses <see cref="Guid.Empty" /> for
    /// this identifier, keyed on the same member name, and says why: all-zeros is what a client that has
    /// not begun a run sends, and it is the one value two accounts reach independently.
    /// </para>
    /// <para>
    /// <b>What an accepting version produces.</b> A storable uuid in every row's stamp, matching no
    /// <c>key_rotations</c> row — so completion reads a house that is full of a rotation nobody started.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Reseal_WithTheEmptyRotationId_IsRefused()
    {
        // Arrange
        Category category = NewCategory("Groceries", "Weekly food shop");

        // Act
        ValidationException exception = ThrowsValidationException(() => category.Reseal(
            SealedNarrative.Indexed("Groceries resealed"),
            SealedNarrative.Description("Weekly food shop resealed"),
            Guid.Empty));

        // Assert — keyed on the member the stamp lands in, as KeyRotation.Begin keys its own. The name
        // is read back too, so a member that assigned before it judged reddens here rather than leaving
        // a row rewritten under a rotation that does not exist.
        await Assert.That(exception.Errors.ContainsKey(nameof(Category.RotationId))).IsTrue();
        await Assert.That(category.Name.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Groceries").Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(category.RotationId).IsNull();
    }

    /// <summary>
    /// A reseal handed no name at all is refused as an argument fault, not as a field error.
    /// </summary>
    /// <remarks>
    /// <b>The exception type comes from the sibling mutators, not from this file.</b>
    /// <see cref="Category.Create" /> and <see cref="Category.Update" /> both guard this parameter with
    /// <c>ArgumentNullException.ThrowIfNull</c> and both argue why in the entity: the signature says a
    /// name is present, so a null is a defect in this codebase rather than a field a caller corrects by
    /// editing a request, and a <see cref="ValidationException" /> would report it as a 400 about a
    /// member the request may not even have. A reseal that let the null reach <c>name.Name</c> instead
    /// answers with a <see cref="NullReferenceException" /> — a 500, with no parameter named — which
    /// would make it the only write path on this entity that behaves that way.
    /// </remarks>
    [Test]
    public async Task Reseal_WithoutAName_ThrowsArgumentNullException()
    {
        // Arrange
        Category category = NewCategory("Groceries", "Weekly food shop");

        // Act
        ArgumentNullException exception = ThrowsArgumentNullException(() => category.Reseal(
            null!,
            SealedNarrative.Description("Weekly food shop resealed"),
            Guid.CreateVersion7()));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("name");
    }

    /// <summary>
    /// A chunk re-sent under the rotation id it already carried is accepted, and leaves every sealed
    /// column and the stamp where the first arrival put them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A re-sent chunk is the ordinary case and not an anomaly.</b> A rotation is cut into chunks
    /// because an account can hold more rows than one request should carry, and every chunk of one run
    /// quotes the same rotation id — that is what the id is for. A request that timed out on the wire, a
    /// retry, or a client that never saw the response sends the same rows under the same id again.
    /// </para>
    /// <para>
    /// <b>What this case is here to refuse.</b> A member that additionally rejected a rotation id equal
    /// to the stamp the row already carries reads as sensible idempotence protection and passes every
    /// other case in this file, because they all mint a fresh id. It would fail exactly the runs long
    /// enough to need chunking, and it protects against nothing: a reseal is a whole-value write, so the
    /// same chunk applied twice lands the same bytes and the same stamp.
    /// </para>
    /// <para>
    /// <b>The note is carried through the repeat deliberately.</b> A second pass over a row whose
    /// description is present is where a presence rule reading the wrong side would show up — one
    /// comparing the incoming value against a stale copy rather than against the column would refuse the
    /// repeat as an attempt to change a field, and a category with a note is the fixture that catches it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Reseal_RepeatedUnderTheSameRotationId_IsAccepted()
    {
        // Arrange — the chunk that landed, and the id its run is quoting throughout.
        Category category = NewCategory("Groceries", "Weekly food shop");
        var rotationId = Guid.CreateVersion7();
        category.Reseal(
            SealedNarrative.Indexed("Groceries resealed"),
            SealedNarrative.Description("Weekly food shop resealed"),
            rotationId);

        // Act — the same chunk again, under the same id, as a re-sent request carries it.
        category.Reseal(
            SealedNarrative.Indexed("Groceries resealed"),
            SealedNarrative.Description("Weekly food shop resealed"),
            rotationId);

        // Assert — both halves of the name, the note, and the stamp, all as the first arrival left them.
        await Assert.That(category.Name.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Groceries resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(category.NameKey.ToArray()).IsEquivalentTo(
            SealedNarrative.BlindIndex("Groceries resealed").ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(category.Description!.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Description("Weekly food shop resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(category.RotationId).IsEqualTo(rotationId);
    }

    private static Category NewCategory(string label, string? descriptionLabel) =>
        Category.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed(label),
            descriptionLabel is null ? null : SealedNarrative.Description(descriptionLabel),
            0,
            UtcNow());

    private static DateTime UtcNow() => new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);

    private static ValidationException ThrowsValidationException(Action action)
    {
        try
        {
            action();
        }
        catch (ValidationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }

    private static ArgumentNullException ThrowsArgumentNullException(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentNullException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ArgumentNullException.");
    }
}
