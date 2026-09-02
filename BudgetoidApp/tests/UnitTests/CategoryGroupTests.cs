using Domain.CategoryGroups;
using Domain.Common;
using Domain.Security;
using TestSupport;
using TUnit.Assertions.Enums;

namespace UnitTests;

/// <summary>
/// The category-group entity once its name became an envelope with a blind index beside it and its
/// description became the first sealed free-text column in the product.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four rules this file used to assert are gone, and every one of them moved rather than being
/// dropped.</b> A trimmed name, a name of at most two hundred characters, a description of at most five
/// hundred, and a whitespace-only description normalised to <see langword="null" /> were all questions
/// about characters. This server has none: it holds ciphertext over text it has never seen and no key to
/// open it with, so "how many characters is this?" and "is this only spaces?" are questions about a
/// plaintext that exists nowhere on this side. The client, which holds the key, is the only side that
/// can ask them. The cases below that accept a blank name, an over-long name and an over-long
/// description are what stop a future reader reading the absence as an oversight and restoring a check
/// with nothing to check against.
/// </para>
/// <para>
/// <b>The description's disappearance is quieter than the name's and needs saying separately.</b> The
/// column is nullable, so a write path that decoded a description and then forgot to assign it writes
/// <see langword="null" /> — a legal row, byte-identical to one belonging to somebody who deliberately
/// has no description. On the <c>NOT NULL</c> name the same defect is a <c>23502</c> from the database.
/// Here nothing fires. <see cref="Create_WithADescription_StoresIt" /> and
/// <see cref="Update_ReplacesBothHalvesOfTheNameAndTheDescription" /> are the whole of the guard at this
/// ring, which is why neither may be weakened to "the property is present".
/// </para>
/// <para>
/// <b><see cref="CategoryGroup.Update" /> calls the validator and <c>Payee.Rename</c> does not, and NO
/// CASE IN THIS FILE HOLDS THAT.</b> Saying which it does would take a group whose position, budget id
/// or identifier is already bad at the moment <c>Update</c> runs, and there is no way to build one:
/// <see cref="CategoryGroup.Create" /> refuses each of them and <see cref="CategoryGroup.SetPosition" />
/// refuses a negative position, so every instance reaching <c>Update</c> has already satisfied the rules
/// <c>Update</c> would re-check. This was measured on payees in the identical shape — whether
/// <c>Rename</c> called the validator was invisible to the suite in both directions — and the same is
/// true here. The asymmetry is held by the paragraph on the entity and by review, and this note exists
/// so nobody adds a comment claiming otherwise.
/// </para>
/// </remarks>
public sealed class CategoryGroupTests
{
    [Test]
    public async Task Create_UsesTheIdItWasGiven()
    {
        // Arrange — an id minted here and threaded in, so the assertion is not "an id came back" but
        // "this one did".
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();

        // Act
        CategoryGroup group = CategoryGroup.Create(
            id, budgetId, SealedNarrative.Indexed("Essentials"), null, 0, UtcNow());

        // Assert — the identifier is the associated data the client sealed BOTH narrative members
        // against, so a factory that ignored this parameter and minted its own produces a row whose name
        // and description nobody can ever open: no constraint violated, nothing red, and the symptom
        // arriving months later as text that will not decrypt. Guid.CreateVersion7 has left the
        // production file for that reason, and this is the case that would notice it coming back through
        // this signature.
        await Assert.That(group.Id).IsEqualTo(id);
    }

    [Test]
    public async Task Create_WithValidInput_StoresBothHalvesOfTheNameBudgetIdPositionAndCreatedAtUtc()
    {
        // Arrange
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();
        IndexedName name = SealedNarrative.Indexed("Essential Obligations");
        DateTime createdAtUtc = new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);

        // Act
        CategoryGroup group = CategoryGroup.Create(id, budgetId, name, null, 2, createdAtUtc);

        // Assert — both halves of the name are compared as the bytes they carry rather than by
        // reference. Neither NarrativeField nor ReadOnlyMemory<byte> gives content equality here for
        // free, and a reference comparison would pass for any instance the factory happened to hold on
        // to.
        //
        // The name half used to be asserted as the string "Essential Obligations", trimmed from
        // "  Essential Obligations  ". Both the value and the trim are gone: the column holds an
        // envelope over text this server has never seen, so there is no string to compare and no
        // whitespace to strip. What survives is that the factory stored the value it was handed,
        // unaltered, in both columns.
        //
        // CollectionOrdering.Matching IS PART OF THE ASSERTION, everywhere in this file. IsEqualTo over
        // two byte[] compares REFERENCES and fails even when the contents and the order agree, and
        // TUnit's failure message names IsEquivalentTo as the fix — whose default is
        // CollectionOrdering.Any, so the bare overload passes on every permutation of an envelope's or a
        // digest's bytes. Order is the whole of what a ciphertext is: a factory that permuted either
        // half would satisfy the bare overload and produce right-width, wrong-value bytes that no key
        // opens and no recomputation on this side can notice.
        await Assert.That(group.BudgetId).IsEqualTo(budgetId);
        await Assert.That(group.Name.Envelope.ToArray())
            .IsEquivalentTo(name.Name.Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(group.NameKey.ToArray())
            .IsEquivalentTo(name.BlindIndex.ToArray(), CollectionOrdering.Matching);
        await Assert.That(group.Position).IsEqualTo(2);
        await Assert.That(group.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithADescription_StoresIt()
    {
        // Arrange — a description distinct from the name, because the two are the columns most likely to
        // be crossed by a factory assigning one parameter to both fields. SealedNarrative's filler is
        // position-varying for exactly that reason, so "Required spending" and "Essential Obligations"
        // produce envelopes that share no byte at any offset.
        NarrativeField description = SealedNarrative.Description("Required spending");

        // Act
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Essential Obligations"),
            description,
            0,
            UtcNow());

        // Assert — THE ONE GUARD AT THIS RING ON A DEFECT THE SCHEMA CANNOT SEE. The description column
        // is nullable, so a factory that took this parameter and never assigned it writes NULL: a legal
        // row, violating nothing, and indistinguishable from a group whose owner never filed a
        // description. On the NOT NULL name the same omission is a 23502 the database reports. Here the
        // only thing that reddens is an assertion that reads the value back.
        await Assert.That(group.Description).IsNotNull();
        await Assert.That(group.Description!.Envelope.ToArray())
            .IsEquivalentTo(description.Envelope.ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Create_WithNoDescription_StoresNull()
    {
        // Act — the nullable column's other legal state, and the one a group created through the route
        // with no description member lands in.
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Essential Obligations"),
            null,
            0,
            UtcNow());

        // Assert
        await Assert.That(group.Description).IsNull();
    }

    [Test]
    public async Task Create_WithEmptyId_ThrowsValidationException()
    {
        // Act — the empty Guid became reachable the day the identifier stopped being minted here: it is
        // what a caller that threaded a default through hands over.
        ValidationException exception = ThrowsValidationException(() => CategoryGroup.Create(
            Guid.Empty,
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Essential Obligations"),
            null,
            0,
            UtcNow()));

        // Assert — keyed on the id, not merely thrown. Left to the primary key instead, all-zero is a
        // legal uuid: the first such row stores and the second collides under a constraint name that
        // says nothing about the caller that never chose an id at all.
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
    }

    [Test]
    public async Task Create_WithEmptyBudgetId_ThrowsValidationException()
    {
        // Act — the tenancy rule, which survived the sealing unchanged.
        ValidationException exception = ThrowsValidationException(() => CategoryGroup.Create(
            Guid.CreateVersion7(),
            Guid.Empty,
            SealedNarrative.Indexed("Essential Obligations"),
            null,
            0,
            UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("BudgetId")).IsTrue();
    }

    [Test]
    public async Task Create_WithANegativePosition_ThrowsValidationException()
    {
        // Act — the ordering rule, which also survived unchanged. Position is an int the server can
        // still read, so sealing took no capability away from it; it is the only rule left in this
        // validator that is about a value rather than about an identifier.
        ValidationException exception = ThrowsValidationException(() => CategoryGroup.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Essential Obligations"),
            null,
            -1,
            UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Position")).IsTrue();
    }

    [Test]
    public async Task Create_WithAnEmptyIdAndANegativePosition_ReportsBothAtOnce()
    {
        // Arrange — the validator collects and does not fail fast, and this is the only case in the file
        // that can tell those apart: every other refusal above sends one bad argument, so a version that
        // threw on the first failure would satisfy all of them.

        // Act
        ValidationException exception = ThrowsValidationException(() => CategoryGroup.Create(
            Guid.Empty,
            Guid.Empty,
            SealedNarrative.Indexed("Essential Obligations"),
            null,
            -1,
            UtcNow()));

        // Assert — the COUNT is what a fail-fast validator cannot satisfy: it would carry exactly one
        // key and pass whichever ContainsKey happened to name it. Three keys, because a caller that
        // threaded defaults through got the two identifiers and the position wrong in one go.
        await Assert.That(exception.Errors.Count).IsEqualTo(3);
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("BudgetId")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("Position")).IsTrue();
    }

    [Test]
    public async Task Create_WithoutAName_ThrowsArgumentNullException()
    {
        // Arrange — the signature says a name is present, so a null is a defect in this codebase rather
        // than a field a caller corrects by editing a request. It is refused ahead of ValidateOrThrow
        // and as an ArgumentNullException, not as the ValidationException that becomes a 400 about a
        // member the request may not even have. The description takes no such refusal: its parameter is
        // declared nullable, so absence there is an ordinary value.

        // Act
        ArgumentNullException exception = ThrowsArgumentNullException(() => CategoryGroup.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), null!, null, 0, UtcNow()));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("name");
    }

    [Test]
    public async Task Create_WithoutANameAndWithAnEmptyId_ThrowsArgumentNullExceptionRatherThanValidationException()
    {
        // Arrange — every argument is wrong at once, which is the only way to see WHICH refusal runs
        // first. The case above cannot: with a good id, a good budget id and a good position,
        // ValidateOrThrow finds nothing, so the null refusal wins whether it is written above the call
        // or below it.

        // Act — a null name is a defect in this codebase and an empty id is a field a caller could
        // correct, so the order decides which the caller is told about. Below ValidateOrThrow, the
        // programmer error is reported as a 400 about the id and the real fault never surfaces.
        ArgumentNullException exception = ThrowsArgumentNullException(() =>
            CategoryGroup.Create(Guid.Empty, Guid.Empty, null!, null, -1, UtcNow()));

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
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), blank, null, 0, UtcNow());

        // Assert — this factory used to refuse a blank name with a ValidationException keyed on Name,
        // and it no longer can. The capability moved to the client, which is the only side that holds a
        // key; it was not quietly dropped. THE SERVER CANNOT MEASURE CHARACTERS IN AN ENVELOPE AND NEVER
        // WILL — it holds ciphertext over text it has never seen, so "is this just whitespace?" is a
        // question about a plaintext that exists nowhere on this side. A future reader who reads the
        // absence as an oversight and restores the check has nothing to check it against; this case is
        // what stops that edit.
        await Assert.That(group.Name.Envelope.Length).IsEqualTo(CiphertextEnvelope.MinimumLength);
    }

    [Test]
    public async Task Create_WithANameFarPastTheOldCharacterLimit_IsAccepted()
    {
        // Arrange — an envelope well past the two hundred characters this factory used to refuse, and
        // well under NarrativeFieldLimits.NameBytes, which is the only ceiling left and is measured in
        // stored bytes by NarrativeField before the value ever reaches CategoryGroup.
        IndexedName longName = SealedNarrative.Indexed(new string('x', 400));

        // Act
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), longName, null, 0, UtcNow());

        // Assert — the same relocation as the blank case, in the other direction. A length rule here
        // would be a rule about characters, and the server counts none: AES-GCM ciphertext is exactly as
        // long as its plaintext, but the framing and the encoding sit on top of it, so bytes stored and
        // characters typed are different questions and only the first is answerable here.
        await Assert.That(group.Name.Envelope.Length).IsGreaterThan(200);
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
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Essential Obligations"),
            longDescription,
            0,
            UtcNow());

        // Assert
        await Assert.That(group.Description).IsNotNull();
        await Assert.That(group.Description!.Envelope.Length).IsGreaterThan(500);
    }

    [Test]
    public async Task Create_WithABlankDescription_StoresItRatherThanNull()
    {
        // Arrange — the shortest envelope the format can produce, which is what a client sealing an
        // empty description sends. AES-GCM ciphertext is exactly the length of its plaintext, so an
        // empty note seals to a version, a nonce and a tag and nothing else.
        NarrativeField blank = SealedNarrative.Description();

        // Act
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Essential Obligations"),
            blank,
            0,
            UtcNow());

        // Assert — "CLEARED" AND "NEVER FILLED" ARE DIFFERENT ROWS AND THIS FACTORY MUST NOT FOLD THEM.
        // The deleted NormalizeDescription mapped a whitespace-only description to null, which is
        // precisely that fold; it went with the string parameter and cannot come back in any form,
        // because there is no text left here to inspect. A twenty-nine-byte envelope is a description
        // somebody wrote and then emptied, and NULL is a description nobody ever wrote — the schema
        // represents both, and this case is where the entity is held to representing both too.
        await Assert.That(group.Description).IsNotNull();
        await Assert.That(group.Description!.Envelope.Length)
            .IsEqualTo(CiphertextEnvelope.MinimumLength);
    }

    [Test]
    public async Task Update_ReplacesBothHalvesOfTheNameAndTheDescription()
    {
        // Arrange — a group created under one name and description and corrected to another, which is
        // the most common use of this method. The labels are what make the halves distinguishable:
        // SealedNarrative derives an envelope and an index from the same text, so "Essentials" produces
        // neither of the values "Essential Obligations" produces.
        CategoryGroup group = NewGroup("Essential Obligations", "Required spending");

        // Act
        group.Update(SealedNarrative.Indexed("Essentials"), SealedNarrative.Description("Must pay"));

        // Assert — THE INDEX IS ASSERTED TO BE THE NEW ONE AND EXPLICITLY NOT THE PREVIOUS ONE, which is
        // the whole of what the IndexedName parameter buys. A member that took a bare NarrativeField
        // would pass the envelope assertion and fail only the two index ones: the row would hold new
        // ciphertext under the old name's index. That is bad and silent — no read path anywhere orders
        // or looks a category group up by name, so nothing on this side notices, and recomputing a digest
        // to check needs the account's index key, which lives in a browser. What it costs is the
        // uniqueness rule: IX_category_groups_budget_id_name_key stops guarding the name the row now
        // holds and starts guarding one it does not.
        //
        // The description is asserted in the same breath and for the reason the class remarks give: it is
        // the parameter whose loss the schema cannot see. An Update that replaced the name and left the
        // description alone, or nulled it, satisfies every constraint on the table.
        //
        // CollectionOrdering.Matching on the positive assertions, for the reason
        // Create_WithValidInput_StoresBothHalvesOfTheNameBudgetIdPositionAndCreatedAtUtc states. The
        // NEGATIVE one below deliberately keeps the default: CollectionOrdering.Any there means "not even
        // a permutation of the old index", which is the stronger claim and the one worth making.
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
    }

    [Test]
    public async Task Update_WithNoDescription_ClearsTheOneTheGroupHeld()
    {
        // Arrange — a group that has a description, so that "no description" is a change rather than a
        // restatement of what was already there. Started from null, this case passes for an Update that
        // never touches the field at all.
        CategoryGroup group = NewGroup("Essential Obligations", "Required spending");

        // Act
        group.Update(SealedNarrative.Indexed("Essentials"), null);

        // Assert — THE ROUTE IS A PUT AND A PUT IS A FULL REPLACEMENT, so an absent description means
        // this group has no description, and Optional<T> has no business appearing anywhere on this path.
        // The transaction routes carry Optional<T> because they are PATCH and genuinely have a third
        // "leave it alone" state; inventing one here would be a state the route does not have and the
        // client has never sent.
        await Assert.That(group.Description).IsNull();
    }

    [Test]
    public async Task Update_LeavesIdBudgetIdPositionAndCreatedAtUtcUnchanged()
    {
        // Arrange — BudgetId is the tenancy rule: a group that changed budget would carry its categories
        // into someone else's ledger. Id is load-bearing for a second reason — it is the associated data
        // every envelope this row has ever held was sealed against, so an update that also re-minted it
        // would leave a name and a description nobody can open. Position is the ordering the person
        // arranged by hand, and this method is not the one that moves a group.
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();
        CategoryGroup group = CategoryGroup.Create(
            id,
            budgetId,
            SealedNarrative.Indexed("Essential Obligations"),
            SealedNarrative.Description("Required spending"),
            2,
            createdAtUtc);

        // Act
        group.Update(SealedNarrative.Indexed("Essentials"), SealedNarrative.Description("Must pay"));

        // Assert
        await Assert.That(group.Id).IsEqualTo(id);
        await Assert.That(group.BudgetId).IsEqualTo(budgetId);
        await Assert.That(group.Position).IsEqualTo(2);
        await Assert.That(group.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Update_WithoutAName_ThrowsArgumentNullException()
    {
        // Arrange — Create's argument, restated on the other write path: half a name has no spelling
        // this signature accepts, and neither does no name at all.
        CategoryGroup group = NewGroup("Essential Obligations", "Required spending");

        // Act
        ArgumentNullException exception =
            ThrowsArgumentNullException(() => group.Update(null!, SealedNarrative.Description("Must pay")));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("name");
    }

    [Test]
    public async Task Update_WithoutAName_LeavesTheNameAndTheDescriptionUnchanged()
    {
        // Arrange — the claim is that a refused update changes NOTHING, on all three columns it can
        // write. There is no ValidationException path this file can reach to make the claim with: every
        // rule Update's validator still owns is about a value no reachable instance can be holding — see
        // the class remarks. The null refusal is what remains, and it is reached ahead of all three
        // assignments.
        CategoryGroup group = NewGroup("Essential Obligations", "Required spending");

        // Act
        ThrowsArgumentNullException(() => group.Update(null!, null));

        // Assert — WHAT THIS CASE HOLDS IS NARROWER THAN IT LOOKS. An Update that dereferenced the
        // parameter into Name and only then hit the null cannot be written: `name.Name` on a null throws
        // NullReferenceException on the dereference, before any assignment, so the half-updated row is
        // unreachable in C#. What it does hold is that the description survived a refused call — an
        // Update that assigned the description FIRST and then dereferenced the name would leave a group
        // with a cleared note and the old name, which is a real ordering and the one this case sees. The
        // three assertions also read all three columns off a group this case never updated successfully,
        // so they stand behind Create's assignment as much as behind Update's refusal.
        //
        // CollectionOrdering.Matching for the reason stated on
        // Create_WithValidInput_StoresBothHalvesOfTheNameBudgetIdPositionAndCreatedAtUtc.
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
    }

    [Test]
    public async Task SetPosition_WithValidPosition_ChangesPosition()
    {
        // Arrange — position survived the sealing untouched, and this pair of cases is what says so.
        CategoryGroup group = NewGroup("Essential Obligations", null);

        // Act
        group.SetPosition(3);

        // Assert
        await Assert.That(group.Position).IsEqualTo(3);
    }

    [Test]
    public async Task SetPosition_WithNegativePosition_ThrowsAndLeavesPositionUnchanged()
    {
        // Arrange
        CategoryGroup group = CategoryGroup.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Essential Obligations"),
            null,
            1,
            UtcNow());

        // Act
        ValidationException exception = ThrowsValidationException(() => group.SetPosition(-1));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Position")).IsTrue();
        await Assert.That(group.Position).IsEqualTo(1);
    }

    /// <summary>
    /// A group under <paramref name="label" /> and <paramref name="descriptionLabel" />, for the cases
    /// whose subject is the update rather than the identifier, the position or the creation instant.
    /// </summary>
    /// <param name="label">
    /// What distinguishes this group's name from the next one's. It is NOT the group's name and is never
    /// read back as one — the column holds an envelope this side has no key for.
    /// </param>
    /// <param name="descriptionLabel">
    /// The same, for the description, or <see langword="null" /> for a group that has none.
    /// </param>
    private static CategoryGroup NewGroup(string label, string? descriptionLabel) =>
        CategoryGroup.Create(
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
