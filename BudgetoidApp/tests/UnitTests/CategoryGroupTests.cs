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
        // position-varying for exactly that reason: two DIFFERENT labels produce envelopes that are not
        // EQUAL, which is what the assertion below compares and what a doubled assignment fails.
        //
        // THIS PARAGRAPH USED TO CLAIM THE TWO "SHARE NO BYTE AT ANY OFFSET", AND THAT IS FALSE.
        // Measured, ignoring the version byte at offset 0, Name("Essential Obligations") and
        // Description("Required spending") — this case's own pair — agree at offsets 39 and 44. The
        // filler is (labelByte + position * 31) mod 256, so two labels collide wherever their cycling
        // bytes happen to agree, which is common rather than rare. Nothing here relies on per-offset
        // disjointness and nothing should: the guard is whole-array inequality, and a reader who builds
        // on the stronger reading is building on a property this fixture does not have.
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
    /// A content-key rotation replaces both halves of the name and the description together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The second labels model the same text under two keys, not new text.</b> A rotation
    /// re-encrypts what the row already holds, so nothing about the group changes except the bytes — but
    /// <see cref="SealedNarrative" /> derives everything from the label it is handed, so "the same text
    /// under a new key" has no other spelling here than a second label.
    /// </para>
    /// <para>
    /// <b>The index is asserted to have moved too, and that is the reason the parameter is an
    /// <see cref="IndexedName" />.</b> The index key rotates alongside the content key, so a reseal that
    /// replaced the envelope alone would leave the row's name sealed under the new content key and keyed
    /// under the old index one — the half-written name
    /// <see cref="Update_ReplacesBothHalvesOfTheNameAndTheDescription" /> argues about, arriving on
    /// every group in the budget at once and by a path nobody is watching.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Reseal_ReplacesBothHalvesOfTheNameAndTheDescription()
    {
        // Arrange
        CategoryGroup group = NewGroup("Essential Obligations", "Required spending");

        // Act
        group.Reseal(
            SealedNarrative.Indexed("Essential Obligations resealed"),
            SealedNarrative.Description("Required spending resealed"),
            Guid.CreateVersion7());

        // Assert — CollectionOrdering.Matching on the positive assertions for the reason
        // Create_WithValidInput_StoresBothHalvesOfTheNameBudgetIdPositionAndCreatedAtUtc states; the
        // negative one keeps the default, which is the stronger "not even a permutation" claim.
        await Assert.That(group.Name.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Essential Obligations resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(group.NameKey.ToArray()).IsEquivalentTo(
            SealedNarrative.BlindIndex("Essential Obligations resealed").ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(group.NameKey.ToArray())
            .IsNotEquivalentTo(SealedNarrative.BlindIndex("Essential Obligations").ToArray());
        await Assert.That(group.Description!.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Description("Required spending resealed").Envelope.ToArray(),
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
        CategoryGroup group = NewGroup("Essential Obligations", "Required spending");
        var rotationId = Guid.CreateVersion7();

        // Act
        group.Reseal(
            SealedNarrative.Indexed("Essential Obligations resealed"),
            SealedNarrative.Description("Required spending resealed"),
            rotationId);

        // Assert
        await Assert.That(group.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// A reseal moves the two narrative columns and the stamp and touches nothing else.
    /// </summary>
    /// <remarks>
    /// <b>This is the case that catches a reseal built by reusing <see cref="CategoryGroup.Update" />.</b>
    /// Update reaches ValidateOrThrow with the group's current position and assigns nothing else, so the
    /// reuse looks harmless — until a reseal reads a position from its own request rather than off the
    /// row. Position is relative to the whole set, which is why the group list is one of the seven reads
    /// delivered whole; a rotation that renumbered it would reorder somebody's budget as a side effect
    /// of re-encrypting it, silently and in bulk. The identity columns are stronger still —
    /// <see cref="CategoryGroup.Id" /> is the associated data every envelope this row has ever held was
    /// sealed against, so a reseal that re-minted it would produce values nobody can ever open.
    /// </remarks>
    [Test]
    public async Task Reseal_LeavesPositionAndIdentityUnchanged()
    {
        // Arrange — a non-zero position, so a reseal that reset it to the default is visible rather than
        // accidentally right.
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();
        CategoryGroup group = CategoryGroup.Create(
            id,
            budgetId,
            SealedNarrative.Indexed("Essential Obligations"),
            SealedNarrative.Description("Required spending"),
            3,
            createdAtUtc);

        // Act
        group.Reseal(
            SealedNarrative.Indexed("Essential Obligations resealed"),
            SealedNarrative.Description("Required spending resealed"),
            Guid.CreateVersion7());

        // Assert
        await Assert.That(group.Id).IsEqualTo(id);
        await Assert.That(group.BudgetId).IsEqualTo(budgetId);
        await Assert.That(group.Position).IsEqualTo(3);
        await Assert.That(group.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    /// <summary>
    /// A rotation that supplies no description for a group that has one is refused.
    /// </summary>
    /// <remarks>
    /// The clearing arm of the presence rule <c>NarrativeReseal.Resealed</c> owns, reached through this
    /// entity so that the group's description is actually routed through it. The column is nullable, so
    /// writing the absence through produces a legal row that violates no constraint and is
    /// byte-identical to one belonging to somebody who deliberately filed no note. Nothing in the schema
    /// can tell that bug from an operation, which is why the refusal has to be in the domain.
    /// </remarks>
    [Test]
    public async Task Reseal_WithNoDescriptionOverAGroupThatHasOne_IsRefused()
    {
        // Arrange
        CategoryGroup group = NewGroup("Essential Obligations", "Required spending");

        // Act
        ValidationException exception = ThrowsValidationException(() => group.Reseal(
            SealedNarrative.Indexed("Essential Obligations resealed"),
            null,
            Guid.CreateVersion7()));

        // Assert — keyed on the member the request carries. A refusal filed under a word invented by the
        // entity reaches the client verbatim as a 400 naming a member no request has.
        await Assert.That(exception.Errors.ContainsKey(nameof(CategoryGroup.Description))).IsTrue();
    }

    /// <summary>
    /// A rotation that supplies a description for a group that has none is refused.
    /// </summary>
    /// <remarks>
    /// The quieter arm, and the one a reviewer will propose relaxing: filling in an empty note harms no
    /// data. It is refused because presence is the only property this side can check at all, so an arm
    /// that admits a change of presence gives up the whole of what the rule is made of — and what lands
    /// in that column is text the server cannot read, attributed to a person who never wrote it, in a
    /// run they authorised as "re-encrypt what I have".
    /// </remarks>
    [Test]
    public async Task Reseal_WithADescriptionOverAGroupThatHasNone_IsRefused()
    {
        // Arrange
        CategoryGroup group = NewGroup("Essential Obligations", null);

        // Act
        ValidationException exception = ThrowsValidationException(() => group.Reseal(
            SealedNarrative.Indexed("Essential Obligations resealed"),
            SealedNarrative.Description("Required spending"),
            Guid.CreateVersion7()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(CategoryGroup.Description))).IsTrue();
    }

    /// <summary>
    /// A group that never had a description is rotated, stamped, and left without one.
    /// </summary>
    /// <remarks>
    /// <b>The control for both refusals, and not a filler case.</b> Without it, a reseal that threw
    /// whenever either side of the description was null would pass both cases above and would make every
    /// account holding one note-less group un-rotatable — a refusal the person cannot act on, because
    /// the field they are being refused for is one they never filled in. The stamp is asserted here for
    /// the same reason: completion needs a full house, so the rows with nothing to re-encrypt still have
    /// to be accounted for, and a reseal that returned early on a null description would leave exactly
    /// those rows unstamped and the run permanently one short.
    /// </remarks>
    [Test]
    public async Task Reseal_WithNoDescriptionOverAGroupThatHasNone_IsAcceptedAndStillStamps()
    {
        // Arrange
        CategoryGroup group = NewGroup("Essential Obligations", null);
        var rotationId = Guid.CreateVersion7();

        // Act
        group.Reseal(
            SealedNarrative.Indexed("Essential Obligations resealed"), null, rotationId);

        // Assert
        await Assert.That(group.Description).IsNull();
        await Assert.That(group.Name.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Essential Obligations resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(group.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// A refused reseal writes nothing at all — not the name, not the note, and above all not the stamp.
    /// </summary>
    /// <remarks>
    /// <b>The stamp is the assertion that matters here.</b> A reseal that stamped before it judged the
    /// description, or that assigned the name first and refused afterwards, leaves a row marked as
    /// rotated that was not — and the stamp is the one signal completion trusts, so the destructive step
    /// would promote the new keys over a row still sealed under the old one. That is the exact loss the
    /// column was added to prevent, produced by the member that writes it. The previous rotation's id is
    /// the fixture rather than <see langword="null" /> so that a member which cleared the stamp on
    /// refusal also reddens.
    /// </remarks>
    [Test]
    public async Task Reseal_WithARefusedDescription_LeavesEveryColumnAndTheStampAsTheyWere()
    {
        // Arrange — a group already carried through one rotation, now handed a chunk that drops its
        // note.
        CategoryGroup group = NewGroup("Essential Obligations", "Required spending");
        var firstRotationId = Guid.CreateVersion7();
        group.Reseal(
            SealedNarrative.Indexed("Essential Obligations resealed"),
            SealedNarrative.Description("Required spending resealed"),
            firstRotationId);

        // Act
        ThrowsValidationException(() => group.Reseal(
            SealedNarrative.Indexed("Essential Obligations rotated twice"),
            null,
            Guid.CreateVersion7()));

        // Assert — CollectionOrdering.Matching for the reason
        // Create_WithValidInput_StoresBothHalvesOfTheNameBudgetIdPositionAndCreatedAtUtc states.
        await Assert.That(group.Name.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Essential Obligations resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(group.NameKey.ToArray()).IsEquivalentTo(
            SealedNarrative.BlindIndex("Essential Obligations resealed").ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(group.Description!.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Description("Required spending resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(group.RotationId).IsEqualTo(firstRotationId);
    }

    /// <summary>
    /// An ordinary update clears the stamp a rotation left on the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the rule the whole stamp rests on, and it is the easiest one to leave out</b> —
    /// nothing about <see cref="CategoryGroup.Update" /> reads as being part of a rotation, so a reader
    /// implementing the reseal member has no reason to open this one.
    /// </para>
    /// <para>
    /// <b>What goes wrong without it.</b> A second browser tab still holding the OLD content key can
    /// rename a group this rotation has already stamped. It writes old-key ciphertext, and — with this
    /// line missing — it does not touch the stamp, so the row ends up carrying old-key ciphertext under
    /// a current stamp. Completion then reads a full house, promotes the new keys and destroys the old
    /// ones, and that group's name and note are gone: no constraint violated, nothing red, and the
    /// symptom is a screen that will not decrypt. Clearing the stamp is what makes completion refuse
    /// instead, which is a run the person can retry.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Update_ClearsTheRotationStamp()
    {
        // Arrange — stamped through the real reseal path rather than reflected in, so the case describes
        // the sequence that actually happens: a chunk rewrites the row, then a stale tab edits it.
        CategoryGroup group = NewGroup("Essential Obligations", "Required spending");
        group.Reseal(
            SealedNarrative.Indexed("Essential Obligations resealed"),
            SealedNarrative.Description("Required spending resealed"),
            Guid.CreateVersion7());

        // Act
        group.Update(
            SealedNarrative.Indexed("Everyday Costs"),
            SealedNarrative.Description("Day to day"));

        // Assert
        await Assert.That(group.RotationId).IsNull();
    }

    /// <summary>
    /// Reordering a group leaves the stamp standing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The inverse of <see cref="Update_ClearsTheRotationStamp" />, and the mutation it catches is
    /// clearing the stamp HERE.</b> <see cref="CategoryGroup.SetPosition" /> writes an int the server
    /// reads; it touches no envelope, so after it runs the row's ciphertext is still whatever the
    /// rotation sealed and the stamp is still honest. A reader who takes "an edit clears the stamp" as
    /// the rule rather than "a NARRATIVE write clears the stamp" will put the line in both members, and
    /// every case in this file except this one stays green.
    /// </para>
    /// <para>
    /// <b>Why that is worse than it looks, and why it is not a data-loss bug.</b> Nothing is lost — the
    /// row is fine. What breaks is convergence. Completion refuses a rotation that genuinely finished,
    /// the client re-seals the un-stamped rows, and on an account where somebody is dragging groups
    /// around while the run proceeds, each pass re-stamps rows the next drag un-stamps. The rotation may
    /// never finish, on exactly the accounts large enough to need several chunks, and there is nothing
    /// on screen to explain why.
    /// </para>
    /// <para>
    /// <b>This is a pin, not a red bar.</b> It goes green the moment the member exists, because
    /// <see cref="CategoryGroup.SetPosition" /> writes no stamp today. Its value is entirely in the
    /// mutation named above.
    /// </para>
    /// </remarks>
    [Test]
    public async Task SetPosition_LeavesTheRotationStampStanding()
    {
        // Arrange — stamped through the real reseal path, then reordered.
        CategoryGroup group = NewGroup("Essential Obligations", "Required spending");
        var rotationId = Guid.CreateVersion7();
        group.Reseal(
            SealedNarrative.Indexed("Essential Obligations resealed"),
            SealedNarrative.Description("Required spending resealed"),
            rotationId);

        // Act
        group.SetPosition(4);

        // Assert
        await Assert.That(group.Position).IsEqualTo(4);
        await Assert.That(group.RotationId).IsEqualTo(rotationId);
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
        CategoryGroup group = NewGroup("Essential Obligations", "Required spending");

        // Act
        ValidationException exception = ThrowsValidationException(() => group.Reseal(
            SealedNarrative.Indexed("Essential Obligations resealed"),
            SealedNarrative.Description("Required spending resealed"),
            Guid.Empty));

        // Assert — keyed on the member the stamp lands in, as KeyRotation.Begin keys its own. The name
        // is read back too, so a member that assigned before it judged reddens here rather than leaving
        // a row rewritten under a rotation that does not exist.
        await Assert.That(exception.Errors.ContainsKey(nameof(CategoryGroup.RotationId))).IsTrue();
        await Assert.That(group.Name.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Essential Obligations").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(group.RotationId).IsNull();
    }

    /// <summary>
    /// A reseal handed no name at all is refused as an argument fault, not as a field error.
    /// </summary>
    /// <remarks>
    /// <b>The exception type comes from the sibling mutators, not from this file.</b>
    /// <see cref="CategoryGroup.Create" /> and <see cref="CategoryGroup.Update" /> both guard this
    /// parameter with <c>ArgumentNullException.ThrowIfNull</c> and both argue why in the entity: the
    /// signature says a name is present, so a null is a defect in this codebase rather than a field a
    /// caller corrects by editing a request, and a <see cref="ValidationException" /> would report it as
    /// a 400 about a member the request may not even have. A reseal that let the null reach
    /// <c>name.Name</c> instead answers with a <see cref="NullReferenceException" /> — a 500, with no
    /// parameter named — which would make it the only write path on this entity that behaves that way.
    /// </remarks>
    [Test]
    public async Task Reseal_WithoutAName_ThrowsArgumentNullException()
    {
        // Arrange
        CategoryGroup group = NewGroup("Essential Obligations", "Required spending");

        // Act
        ArgumentNullException exception = ThrowsArgumentNullException(() => group.Reseal(
            null!,
            SealedNarrative.Description("Required spending resealed"),
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
    /// <b>The description is carried through the repeat deliberately</b>, for the reason the sibling case
    /// on <c>Category</c> gives: a second pass over a row whose description is present is where a
    /// presence rule reading a stale copy rather than the column would refuse the repeat.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Reseal_RepeatedUnderTheSameRotationId_IsAccepted()
    {
        // Arrange — the chunk that landed, and the id its run is quoting throughout.
        CategoryGroup group = NewGroup("Essential Obligations", "Required spending");
        var rotationId = Guid.CreateVersion7();
        group.Reseal(
            SealedNarrative.Indexed("Essential Obligations resealed"),
            SealedNarrative.Description("Required spending resealed"),
            rotationId);

        // Act — the same chunk again, under the same id, as a re-sent request carries it.
        group.Reseal(
            SealedNarrative.Indexed("Essential Obligations resealed"),
            SealedNarrative.Description("Required spending resealed"),
            rotationId);

        // Assert — both halves of the name, the description, and the stamp, all as the first arrival
        // left them.
        await Assert.That(group.Name.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Essential Obligations resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(group.NameKey.ToArray()).IsEquivalentTo(
            SealedNarrative.BlindIndex("Essential Obligations resealed").ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(group.Description!.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Description("Required spending resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(group.RotationId).IsEqualTo(rotationId);
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
