using Domain.Common;
using Domain.Payees;
using Domain.Security;
using TestSupport;
using TUnit.Assertions.Enums;

namespace UnitTests;

public sealed class PayeeTests
{
    [Test]
    public async Task Create_UsesTheIdItWasGiven()
    {
        // Arrange — an id minted here and threaded in, so the assertion is not "an id came back" but
        // "this one did".
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();

        // Act
        Payee payee = Payee.Create(id, budgetId, SealedNarrative.Indexed("Starbucks"), UtcNow());

        // Assert — the identifier is the associated data the client sealed the name against, so a
        // factory that ignored this parameter and minted its own would satisfy every other case in this
        // file and produce a row whose name nobody can ever open: no constraint violated, nothing red,
        // and the symptom arriving months later as text that will not decrypt. Guid.CreateVersion7 has
        // left the production file for that reason, and this is the case that would notice it coming
        // back through this signature.
        await Assert.That(payee.Id).IsEqualTo(id);
    }

    [Test]
    public async Task Create_WithValidInput_StoresBothHalvesOfTheNameBudgetIdAndCreatedAtUtc()
    {
        // Arrange
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();
        IndexedName name = SealedNarrative.Indexed("Starbucks");
        DateTime createdAtUtc = new(2026, 6, 24, 13, 14, 15, DateTimeKind.Utc);

        // Act
        Payee payee = Payee.Create(id, budgetId, name, createdAtUtc);

        // Assert — both halves are compared as the bytes they carry rather than by reference. Neither
        // NarrativeField nor ReadOnlyMemory<byte> gives content equality here for free, and a reference
        // comparison would pass for any instance the factory happened to hold on to.
        //
        // The name half used to be asserted as the string "Starbucks", trimmed from "  Starbucks  ".
        // Both the value and the trim are gone: the column holds an envelope over text this server has
        // never seen, so there is no string to compare and no whitespace to strip. What survives is that
        // the factory stored the value it was handed, unaltered, in both columns.
        //
        // CollectionOrdering.Matching IS PART OF THE ASSERTION, everywhere in this file. IsEqualTo over
        // two byte[] compares REFERENCES and fails even when the contents and the order agree, and
        // TUnit's failure message names IsEquivalentTo as the fix — whose default is
        // CollectionOrdering.Any, so the bare overload passes on every permutation of an envelope's or a
        // digest's bytes. Order is the whole of what a ciphertext is: a factory that permuted either
        // half would satisfy the bare overload and produce right-width, wrong-value bytes that no key
        // opens and no recomputation on this side can notice.
        await Assert.That(payee.BudgetId).IsEqualTo(budgetId);
        await Assert.That(payee.Name.Envelope.ToArray())
            .IsEquivalentTo(name.Name.Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(payee.NameKey.ToArray())
            .IsEquivalentTo(name.BlindIndex.ToArray(), CollectionOrdering.Matching);
        await Assert.That(payee.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithEmptyId_ThrowsValidationException()
    {
        // Act — the empty Guid became reachable the day the identifier stopped being minted here: it is
        // what a caller that threaded a default through hands over.
        ValidationException exception = ThrowsValidationException(() =>
            Payee.Create(Guid.Empty, Guid.CreateVersion7(), SealedNarrative.Indexed("Starbucks"), UtcNow()));

        // Assert — keyed on the id, not merely thrown. Left to the primary key instead, all-zero is a
        // legal uuid: the first such row stores and the second collides under a constraint name that
        // says nothing about the caller that never chose an id at all.
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
    }

    [Test]
    public async Task Create_WithEmptyBudgetId_ThrowsValidationException()
    {
        // Act — the tenancy rule, which survived the sealing unchanged.
        ValidationException exception = ThrowsValidationException(() =>
            Payee.Create(Guid.CreateVersion7(), Guid.Empty, SealedNarrative.Indexed("Starbucks"), UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("BudgetId")).IsTrue();
    }

    [Test]
    public async Task Create_WithoutAName_ThrowsArgumentNullException()
    {
        // Arrange — the signature says a name is present, so a null is a defect in this codebase rather
        // than a field a caller corrects by editing a request. It is refused ahead of ValidateOrThrow
        // and as an ArgumentNullException, not as the ValidationException that becomes a 400 about a
        // member the request may not even have.

        // Act
        ArgumentNullException exception = ThrowsArgumentNullException(() =>
            Payee.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), null!, UtcNow()));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("name");
    }

    [Test]
    public async Task Create_WithoutANameAndWithAnEmptyId_ThrowsArgumentNullExceptionRatherThanValidationException()
    {
        // Arrange — every argument is wrong at once, which is the only way to see WHICH refusal runs
        // first. The case above cannot: with a good id and a good budget id, ValidateOrThrow finds
        // nothing, so the null refusal wins whether it is written above the call or below it.

        // Act — a null name is a defect in this codebase and an empty id is a field a caller could
        // correct, so the order decides which the caller is told about. Below ValidateOrThrow, the
        // programmer error is reported as a 400 about the id and the real fault never surfaces.
        ArgumentNullException exception = ThrowsArgumentNullException(() =>
            Payee.Create(Guid.Empty, Guid.Empty, null!, UtcNow()));

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
        Payee payee = Payee.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), blank, UtcNow());

        // Assert — this factory used to refuse a blank name with a ValidationException keyed on Name,
        // and it no longer can. The capability moved to the client, which is the only side that holds a
        // key; it was not quietly dropped. THE SERVER CANNOT MEASURE CHARACTERS IN AN ENVELOPE AND NEVER
        // WILL — it holds ciphertext over text it has never seen, so "is this just whitespace?" is a
        // question about a plaintext that exists nowhere on this side. A future reader who reads the
        // absence as an oversight and restores the check has nothing to check it against; this case is
        // what stops that edit.
        await Assert.That(payee.Name.Envelope.Length).IsEqualTo(CiphertextEnvelope.MinimumLength);
    }

    [Test]
    public async Task Create_WithANameFarPastTheOldCharacterLimit_IsAccepted()
    {
        // Arrange — an envelope well past the two hundred characters this factory used to refuse, and
        // well under NarrativeFieldLimits.NameBytes, which is the only ceiling left and is measured in
        // stored bytes by NarrativeField before the value ever reaches Payee.
        IndexedName longName = SealedNarrative.Indexed(new string('x', 400));

        // Act
        Payee payee = Payee.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), longName, UtcNow());

        // Assert — the same relocation as the blank case, in the other direction. A length rule here
        // would be a rule about characters, and the server counts none: AES-GCM ciphertext is exactly as
        // long as its plaintext, but the framing and the encoding sit on top of it, so bytes stored and
        // characters typed are different questions and only the first is answerable here.
        await Assert.That(payee.Name.Envelope.Length).IsGreaterThan(200);
    }

    [Test]
    public async Task Rename_ReplacesBothHalvesOfTheName()
    {
        // Arrange — a payee created under a misspelling and renamed to the correction, which is the
        // most common use of this method. The two labels are what make the halves distinguishable:
        // SealedNarrative derives both from the same text, so "Starbucks" produces an envelope and an
        // index that neither matches "Starbuks".
        Payee payee = NewPayee("Starbuks");

        // Act
        payee.Rename(SealedNarrative.Indexed("Starbucks"));

        // Assert — THE INDEX IS ASSERTED TO BE THE NEW ONE AND EXPLICITLY NOT THE PREVIOUS ONE, which is
        // the whole of what the IndexedName parameter buys. A member that took a bare NarrativeField
        // would pass the envelope assertion below and fail only the two index ones: the row would hold
        // new ciphertext under the old name's index.
        //
        // On accounts that is bad and silent and that is all, because nothing looks an account up by
        // name. Here it is worse, because the payee list IS the deduplication mechanism of the domain.
        // Creating the new name afterwards is accepted — its index is free — so the budget gains a
        // second payee for one counterparty, which is precisely the duplication find-or-create existed
        // to prevent, arriving by the path meant to fix a typo. Creating the old name is refused with a
        // 409 pointing at a payee that no longer has it: the client re-reads, decrypts every name, finds
        // no match, and has nowhere to go. Nothing on this side can notice either — recomputing a digest
        // needs the budget's index key, which lives in a browser.
        //
        // CollectionOrdering.Matching on the two positive assertions, for the reason
        // Create_WithValidInput_StoresBothHalvesOfTheNameBudgetIdAndCreatedAtUtc states. The NEGATIVE
        // one below deliberately keeps the default: CollectionOrdering.Any there means "not even a
        // permutation of the old index", which is the stronger claim and the one worth making.
        await Assert.That(payee.Name.Envelope.ToArray())
            .IsEquivalentTo(SealedNarrative.Name("Starbucks").Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(payee.NameKey.ToArray())
            .IsEquivalentTo(SealedNarrative.BlindIndex("Starbucks").ToArray(), CollectionOrdering.Matching);
        await Assert.That(payee.NameKey.ToArray())
            .IsNotEquivalentTo(SealedNarrative.BlindIndex("Starbuks").ToArray());
    }

    [Test]
    public async Task Rename_LeavesIdBudgetIdAndCreatedAtUtcUnchanged()
    {
        // Arrange — BudgetId is the tenancy rule: a payee that changed budget would carry its
        // transaction history into someone else's ledger. Id is now load-bearing for a second reason —
        // it is the associated data every envelope this row has ever held was sealed against, so a
        // rename that also re-minted it would leave a name nobody can open.
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();
        Payee payee = Payee.Create(id, budgetId, SealedNarrative.Indexed("Starbuks"), createdAtUtc);

        // Act
        payee.Rename(SealedNarrative.Indexed("Starbucks"));

        // Assert
        await Assert.That(payee.Id).IsEqualTo(id);
        await Assert.That(payee.BudgetId).IsEqualTo(budgetId);
        await Assert.That(payee.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Rename_WithoutAName_ThrowsArgumentNullException()
    {
        // Arrange — Create's argument, restated on the other write path: half a name has no spelling
        // this signature accepts, and neither does no name at all.
        Payee payee = NewPayee("Starbucks");

        // Act
        ArgumentNullException exception = ThrowsArgumentNullException(() => payee.Rename(null!));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("name");
    }

    [Test]
    public async Task Rename_WithoutAName_LeavesBothHalvesOfTheNameUnchanged()
    {
        // Arrange — the claim is that a refused rename changes NOTHING. There is no ValidationException
        // path left on this member to make it with: every rule Rename used to own was a name rule, and
        // every name rule has moved to the client. The null refusal is what remains, and it is reached
        // ahead of both assignments.
        Payee payee = NewPayee("Starbucks");

        // Act
        ThrowsArgumentNullException(() => payee.Rename(null!));

        // Assert — WHAT THIS CASE HOLDS IS NARROWER THAN IT LOOKS, AND THE PREVIOUS SENTENCE HERE WAS
        // WRONG. It used to say its subject was a version that dereferenced the parameter into Name and
        // only then hit the null on NameKey. That version cannot be written: `name.Name` on a null
        // throws NullReferenceException on the dereference, before any assignment, so the half-updated
        // row it described is unreachable in C#. Measured, by deleting Rename's ThrowIfNull in a copy of
        // the entity and replaying this case — it fails on NullReferenceException, never on either
        // assertion below.
        //
        // What it does hold: the two assertions read both halves off a payee this case never renamed
        // successfully, so they stand behind Create's pairing as much as behind Rename's refusal — a
        // factory storing the envelope in both columns reddens here, measured, alongside
        // Create_WithValidInput_StoresBothHalvesOfTheNameBudgetIdAndCreatedAtUtc. NO MUTATION KILLS
        // THIS CASE ALONE, and it is kept for the moment one will: it is the only place where "a
        // refused rename changes nothing" is written as an assertion rather than as a paragraph, so it
        // is the body that has to move the day somebody proposes a two-parameter
        // Rename(NarrativeField, ReadOnlyMemory<byte>) — which is where the half-written row Payee.Rename
        // argues about finally becomes reachable.
        //
        // CollectionOrdering.Matching for the reason stated on
        // Create_WithValidInput_StoresBothHalvesOfTheNameBudgetIdAndCreatedAtUtc.
        await Assert.That(payee.Name.Envelope.ToArray())
            .IsEquivalentTo(SealedNarrative.Name("Starbucks").Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(payee.NameKey.ToArray())
            .IsEquivalentTo(SealedNarrative.BlindIndex("Starbucks").ToArray(), CollectionOrdering.Matching);
    }

    /// <summary>
    /// A payee under <paramref name="label" />, for the cases whose subject is the rename rather than
    /// the identifier or the creation instant.
    /// </summary>
    /// <summary>
    /// A content-key rotation replaces both halves of the name with the pair sealed under the new keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two labels model one name under two keys, not two names.</b> A rotation re-encrypts the
    /// text the row already holds, so nothing about the payee changes except the bytes — but
    /// <see cref="SealedNarrative" /> derives both halves from the label it is handed, so "the same text
    /// under a new key" has no other spelling here than a second label.
    /// </para>
    /// <para>
    /// <b>The index is asserted to have moved too, and on a payee that is the half that bites.</b> The
    /// index key rotates alongside the content key, so a reseal that replaced the envelope alone would
    /// leave every payee in the budget sealed under the new content key and keyed under the old index
    /// one — which is <see cref="Rename_ReplacesBothHalvesOfTheName" />'s failure applied to the whole
    /// list at once, on the mechanism the domain deduplicates counterparties with. Find-or-create stops
    /// finding anything, so the next transaction against every existing payee mints a duplicate.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Reseal_ReplacesBothHalvesOfTheName()
    {
        // Arrange
        Payee payee = NewPayee("Starbucks");

        // Act
        payee.Reseal(SealedNarrative.Indexed("Starbucks resealed"), Guid.CreateVersion7());

        // Assert — CollectionOrdering.Matching on the positive assertions for the reason
        // Create_WithValidInput_StoresBothHalvesOfTheNameBudgetIdAndCreatedAtUtc states; the negative
        // one keeps the default, which is the stronger "not even a permutation" claim.
        await Assert.That(payee.Name.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Starbucks resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(payee.NameKey.ToArray()).IsEquivalentTo(
            SealedNarrative.BlindIndex("Starbucks resealed").ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(payee.NameKey.ToArray())
            .IsNotEquivalentTo(SealedNarrative.BlindIndex("Starbucks").ToArray());
    }

    /// <summary>
    /// A reseal stamps the row with the id of the rotation that rewrote it.
    /// </summary>
    /// <remarks>
    /// The stamp is the whole reason the column exists, argued at <c>Budget.RotationId</c>: a re-sealed
    /// envelope and an untouched one are byte-for-byte indistinguishable to a server holding no key, so
    /// the completion step — which destroys the only copies of the old keys — can only know the rewrite
    /// finished by being told, in the same transaction as the ciphertext. A reseal that replaced the
    /// envelopes and left this column null passes every other assertion in this file and makes the
    /// account un-completable.
    /// </remarks>
    [Test]
    public async Task Reseal_StampsTheRotationItWasGiven()
    {
        // Arrange — the id minted here and threaded in, so the assertion is not "a stamp appeared" but
        // "this rotation's did". A member that minted its own would leave every row stamped with an id
        // no completion step is looking for.
        Payee payee = NewPayee("Starbucks");
        var rotationId = Guid.CreateVersion7();

        // Act
        payee.Reseal(SealedNarrative.Indexed("Starbucks resealed"), rotationId);

        // Assert
        await Assert.That(payee.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// A reseal moves the name and the stamp and touches nothing else.
    /// </summary>
    /// <remarks>
    /// <b>This is the case that catches a reseal built by reusing <see cref="Payee.Rename" />.</b> A
    /// payee carries no arithmetic at all, so what is left to protect is the identity: BudgetId is the
    /// tenancy rule, and <see cref="Payee.Id" /> is the associated data every envelope this row has ever
    /// held was sealed against — a reseal that re-minted it would store a name nobody can open, in the
    /// one operation whose entire purpose is that the names keep opening.
    /// </remarks>
    [Test]
    public async Task Reseal_LeavesIdBudgetIdAndCreatedAtUtcUnchanged()
    {
        // Arrange
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();
        Payee payee = Payee.Create(id, budgetId, SealedNarrative.Indexed("Starbucks"), createdAtUtc);

        // Act
        payee.Reseal(SealedNarrative.Indexed("Starbucks resealed"), Guid.CreateVersion7());

        // Assert
        await Assert.That(payee.Id).IsEqualTo(id);
        await Assert.That(payee.BudgetId).IsEqualTo(budgetId);
        await Assert.That(payee.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    /// <summary>
    /// An ordinary rename clears the stamp a rotation left on the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the rule the whole stamp rests on, and it is the easiest one to leave out</b> —
    /// nothing about <see cref="Payee.Rename" /> reads as being part of a rotation, so a reader
    /// implementing the reseal member has no reason to open this one.
    /// </para>
    /// <para>
    /// <b>What goes wrong without it.</b> A second browser tab still holding the OLD content key can
    /// rename a payee this rotation has already stamped. It writes old-key ciphertext, and — with this
    /// line missing — it does not touch the stamp, so the row ends up carrying old-key ciphertext under
    /// a current stamp. Completion then reads a full house, promotes the new keys and destroys the old
    /// ones, and that payee's name is gone: no constraint violated, nothing red, and the symptom is a
    /// list entry that will not decrypt. Clearing the stamp is what makes completion refuse instead,
    /// which is a run the person can retry.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Rename_ClearsTheRotationStamp()
    {
        // Arrange — stamped through the real reseal path rather than reflected in, so the case describes
        // the sequence that actually happens: a chunk rewrites the row, then a stale tab renames it.
        Payee payee = NewPayee("Starbuks");
        payee.Reseal(SealedNarrative.Indexed("Starbuks resealed"), Guid.CreateVersion7());

        // Act
        payee.Rename(SealedNarrative.Indexed("Starbucks"));

        // Assert
        await Assert.That(payee.RotationId).IsNull();
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
        Payee payee = NewPayee("Starbucks");

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            payee.Reseal(SealedNarrative.Indexed("Starbucks resealed"), Guid.Empty));

        // Assert — keyed on the member the stamp lands in, as KeyRotation.Begin keys its own. The name
        // is read back too, so a member that assigned before it judged reddens here rather than leaving
        // a row rewritten under a rotation that does not exist.
        await Assert.That(exception.Errors.ContainsKey(nameof(Payee.RotationId))).IsTrue();
        await Assert.That(payee.Name.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Starbucks").Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(payee.RotationId).IsNull();
    }

    /// <summary>
    /// A reseal handed no name at all is refused as an argument fault, not as a field error.
    /// </summary>
    /// <remarks>
    /// <b>The exception type comes from the sibling mutators, not from this file.</b>
    /// <see cref="Payee.Create" /> and <see cref="Payee.Rename" /> both guard this parameter with
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
        Payee payee = NewPayee("Starbucks");

        // Act
        ArgumentNullException exception =
            ThrowsArgumentNullException(() => payee.Reseal(null!, Guid.CreateVersion7()));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("name");
    }

    /// <summary>
    /// A chunk re-sent under the rotation id it already carried is accepted, and leaves the row sealed
    /// and stamped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A re-sent chunk is the ordinary case and not an anomaly.</b> A rotation is cut into chunks
    /// because an account can hold more rows than one request should carry, and every chunk of one run
    /// quotes the same rotation id — that is what the id is for. Payees are the list most likely to make
    /// that happen: an account accumulates one row per place it has ever paid, so this is the entity
    /// whose rotation runs longest and whose requests are likeliest to be re-sent.
    /// </para>
    /// <para>
    /// <b>What this case is here to refuse.</b> A member that additionally rejected a rotation id equal
    /// to the stamp the row already carries reads as sensible idempotence protection and passes every
    /// other case in this file, because they all mint a fresh id. It would fail exactly the runs long
    /// enough to need chunking, and it would protect against nothing: a reseal is a whole-value write,
    /// so the same chunk applied twice lands the same bytes and the same stamp.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Reseal_RepeatedUnderTheSameRotationId_IsAccepted()
    {
        // Arrange — the chunk that landed, and the id its run is quoting throughout.
        Payee payee = NewPayee("Starbucks");
        var rotationId = Guid.CreateVersion7();
        payee.Reseal(SealedNarrative.Indexed("Starbucks resealed"), rotationId);

        // Act — the same chunk again, under the same id, as a re-sent request carries it.
        payee.Reseal(SealedNarrative.Indexed("Starbucks resealed"), rotationId);

        // Assert — both halves of the name still sealed under the new key, and the stamp still standing.
        await Assert.That(payee.Name.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Starbucks resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(payee.NameKey.ToArray()).IsEquivalentTo(
            SealedNarrative.BlindIndex("Starbucks resealed").ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(payee.RotationId).IsEqualTo(rotationId);
    }

    private static Payee NewPayee(string label) =>
        Payee.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), SealedNarrative.Indexed(label), UtcNow());

    private static DateTime UtcNow() => new(2026, 6, 24, 13, 14, 15, DateTimeKind.Utc);

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
