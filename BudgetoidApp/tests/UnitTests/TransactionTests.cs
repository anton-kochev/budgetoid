using System.Globalization;
using System.Reflection;
using Domain.Common;
using Domain.Security;
using Domain.Transactions;
using TestSupport;
using TUnit.Assertions.Enums;

namespace UnitTests;

/// <summary>
/// The transaction entity once its description became a sealed nullable column and its identifier
/// stopped being minted here.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the first sealed table in the product with no name column, and the absence changes what
/// the cases can be.</b> There is no <c>IndexedName</c>, no blind index, no <c>name_key</c> and no unique
/// index, so every name-shaped case the sibling entities carry has no counterpart here — the pair type
/// has nothing to be half of. What is left is one narrative column, <c>description</c>, and it is
/// nullable, which is the quietest shape a sealed column comes in.
/// </para>
/// <para>
/// <b>The identifier is the caller's now, and that is the riskiest change in the slice.</b>
/// <c>Guid.CreateVersion7()</c> has left the entity and no minting overload replaces it, because the row
/// id is the associated data the description was sealed against: a factory that ignored the supplied id
/// and minted its own writes a row whose note nobody can ever open, with every constraint satisfied and
/// nothing red. <see cref="Create_WithASuppliedId_StoresIt" /> is the case that notices a minting path
/// coming back, and <see cref="Create_WithTheEmptyGuid_IsRefusedNamingId" /> is the refusal that only
/// became reachable the day the caller started supplying the value.
/// </para>
/// <para>
/// <b>Three rules this file used to assert are gone, and every one of them moved rather than being
/// dropped.</b> A trimmed description, a whitespace-only description folded to <see langword="null" />,
/// and a description of at most five hundred characters were all questions about characters. This server
/// has none: it holds ciphertext over text it has never seen and no key to open it with. The client,
/// which holds the key, is the only side that can ask them.
/// <see cref="Create_WithAnEmptiedDescription_StoresTheEnvelopeRatherThanNull" /> and
/// <see cref="Create_WithADescriptionFarPastTheOldCharacterLimit_IsAccepted" /> are what stop a future
/// reader reading the absence as an oversight and restoring a check with nothing to check against.
/// <c>ValidateOrThrow</c> no longer RETURNS anything either — it used to hand back the normalised
/// description, and a return value that exists is a return value somebody will find a use for.
/// </para>
/// <para>
/// <b>A lost description is invisible to the schema, and the guard at this ring is three cases reading a
/// NON-NULL note back.</b> The column is nullable, so a write path that decoded a description and then
/// forgot to assign it writes <see langword="null" /> — a legal row, byte-identical to one belonging to
/// somebody who deliberately filed no note. Nothing fires.
/// <see cref="Create_WithADescription_StoresTheEnvelopeItWasGiven" />,
/// <see cref="Update_WithANewDescription_ReplacesIt" /> and
/// <see cref="Update_WithARefusedAmount_LeavesTheDescriptionUnchanged" /> are that guard, which is why
/// none of them may be weakened to "the property is present" or to "no exception was thrown".
/// </para>
/// <para>
/// <b>This entity has something the category and the category group do not: a REACHABLE validation
/// refusal on the edit path.</b> Amount, account id and minor unit are values the server still reads, so
/// unlike the sibling entities — where every rule <c>Update</c> re-checks is one no reachable instance
/// can be violating — a refused <c>Update</c> here can be built from a legal transaction.
/// <see cref="Update_WithARefusedAmount_LeavesTheDescriptionUnchanged" /> uses that, and it is a stronger
/// statement of "a refused update changes nothing" than the null-argument version the siblings are
/// limited to: the refusal happens inside the validator, with the assignments below it, rather than at a
/// dereference the compiler forces to come first.
/// </para>
/// </remarks>
public sealed class TransactionTests
{
    /// <summary>
    /// Minor unit of a two-decimal currency such as USD. Named rather than inlined so a call site
    /// that does not care about precision does not read as if <c>2</c> were a magic rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    [Test]
    public async Task BudgetId_IsImmutableAfterCreation()
    {
        // Write-side half of the data-isolation invariant: the read-side query filter cannot
        // stop SaveChanges from moving a row between budgets, so BudgetId must never be reassignable.
        PropertyInfo budgetId = typeof(Transaction).GetProperty(nameof(Transaction.BudgetId))!;

        bool hasPublicSetter = budgetId.SetMethod is { IsPublic: true };

        await Assert.That(hasPublicSetter).IsFalse();
    }

    [Test]
    public async Task Create_WithASuppliedId_StoresIt()
    {
        // Arrange — an id minted here and threaded in, so the assertion is not "an id came back" but
        // "this one did".
        var id = Guid.CreateVersion7();

        // Act
        Transaction transaction = Transaction.Create(
            id,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            -42.50m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            SealedNarrative.Description("Corner shop"),
            UtcNow());

        // Assert — the identifier is the associated data the client sealed the description against, so a
        // factory that ignored this parameter and minted its own produces a row whose note nobody can ever
        // open: no constraint violated, nothing red, and the symptom arriving months later as text that
        // will not decrypt. Guid.CreateVersion7 leaves the production file for that reason, and this is
        // the case that would notice it coming back through this signature.
        await Assert.That(transaction.Id).IsEqualTo(id);
    }

    [Test]
    public async Task Create_WithValidInput_StoresEveryColumnItWasGiven()
    {
        // Arrange
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var date = new DateOnly(2026, 6, 12);
        DateTime createdAtUtc = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

        // Act
        Transaction transaction = Transaction.Create(
            id,
            budgetId,
            accountId,
            -42.50m,
            UsdMinorUnit,
            date,
            SealedNarrative.Description("Corner shop"),
            createdAtUtc);

        // Assert — the description used to be asserted as the string "Groceries", trimmed from
        // " Groceries ". Both the value and the trim are gone: the column holds an envelope over text this
        // server has never seen. Its byte-level assertion lives in
        // Create_WithADescription_StoresTheEnvelopeItWasGiven, which is the case that can discriminate;
        // here the point is that the other six columns landed where they were sent.
        await Assert.That(transaction.Id).IsEqualTo(id);
        await Assert.That(transaction.BudgetId).IsEqualTo(budgetId);
        await Assert.That(transaction.AccountId).IsEqualTo(accountId);
        await Assert.That(transaction.Amount).IsEqualTo(-42.50m);
        await Assert.That(transaction.Date).IsEqualTo(date);
        await Assert.That(transaction.CreatedAtUtc).IsEqualTo(createdAtUtc);
        await Assert.That(transaction.PayeeId).IsNull();
        await Assert.That(transaction.CategoryId).IsNull();
    }

    [Test]
    public async Task Create_WithTheEmptyGuid_IsRefusedNamingId()
    {
        // Act — the empty Guid became reachable the day the identifier stopped being minted here: it is
        // what a caller that threaded a default through hands over.
        ValidationException exception = ThrowsValidationException(() => Transaction.Create(
            Guid.Empty,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            null,
            UtcNow()));

        // Assert — keyed on the id, not merely thrown. Left to the primary key instead, all-zero is a
        // legal uuid: the first such row stores and the second collides under a constraint name that says
        // nothing about the caller that never chose an id at all.
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
    }

    [Test]
    public async Task Create_WithEmptyBudgetId_ThrowsValidationException()
    {
        // Act
        ValidationException exception = ThrowsValidationException(() => Transaction.Create(
            Guid.CreateVersion7(),
            Guid.Empty,
            Guid.CreateVersion7(),
            1m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            null,
            UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("BudgetId")).IsTrue();
    }

    [Test]
    public async Task Create_WithEveryIdentifierEmpty_ReportsIdBudgetIdAndAccountIdAtOnce()
    {
        // Arrange — the validator collects and does not fail fast, and this is the only case in the file
        // that can tell those apart: every other identifier refusal sends one bad argument, so a version
        // that threw on the first failure would satisfy all of them. It matters more here than on the
        // sibling entities, because this validator ALREADY fails fast on one argument — the minor unit,
        // deliberately, as an ArgumentOutOfRangeException — so "this one collects" is a claim about a
        // method that visibly does both.

        // Act
        ValidationException exception = ThrowsValidationException(() => Transaction.Create(
            Guid.Empty,
            Guid.Empty,
            Guid.Empty,
            1m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            null,
            UtcNow()));

        // Assert — the COUNT is what a fail-fast validator cannot satisfy: it would carry exactly one key
        // and pass whichever ContainsKey happened to name it.
        await Assert.That(exception.Errors.Count).IsEqualTo(3);
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("BudgetId")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("AccountId")).IsTrue();
    }

    [Test]
    public async Task Create_WithADescription_StoresTheEnvelopeItWasGiven()
    {
        // Arrange — ONE OF THE THREE CASES THE NULLABLE SEALED COLUMN RESTS ON, and the only one on the
        // create path. The fixture's filler is position-varying so that a factory which stored a buffer of
        // its own, or the wrong parameter, produces bytes this assertion can see; a fixed buffer of one
        // repeated byte would match almost anything of the right width.
        NarrativeField description = SealedNarrative.Description("Corner shop");

        // Act
        Transaction transaction = Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            -42.50m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            description,
            UtcNow());

        // Assert — THE ONE GUARD AT THIS RING ON A DEFECT THE SCHEMA CANNOT SEE. The description column is
        // nullable, so a factory that took this parameter and never assigned it writes NULL: a legal row,
        // violating nothing, and indistinguishable from a transaction whose owner never filed a note.
        // There is no NOT NULL column on this entity to catch the same omission, because this is the only
        // narrative column it has.
        //
        // CollectionOrdering.Matching IS PART OF THE ASSERTION, here and everywhere in this file that
        // compares bytes. IsEqualTo over two byte[] compares REFERENCES and fails even when the contents
        // and the order agree, and TUnit's failure message names IsEquivalentTo as the fix — whose default
        // is CollectionOrdering.Any, so the bare overload passes on every permutation of an envelope's
        // bytes. Order is the whole of what a ciphertext is: a factory that permuted the buffer would
        // satisfy the bare overload and produce a right-width, wrong-value note that no key opens and no
        // recomputation on this side can notice.
        await Assert.That(transaction.Description).IsNotNull();
        await Assert.That(transaction.Description!.Envelope.ToArray())
            .IsEquivalentTo(description.Envelope.ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Create_WithNoDescription_LeavesItNull()
    {
        // Act — the nullable column's other legal state, and the one an imported or quickly typed
        // transaction lands in.
        Transaction transaction = Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            null,
            UtcNow());

        // Assert
        await Assert.That(transaction.Description).IsNull();
    }

    [Test]
    public async Task Create_WithAnEmptiedDescription_StoresTheEnvelopeRatherThanNull()
    {
        // Arrange — the shortest envelope the format can produce: a version, a nonce and a tag over an
        // empty plaintext. AES-GCM ciphertext is exactly the length of its plaintext, so this is what a
        // client sealing an empty note — or a note of nothing but spaces — sends.
        NarrativeField emptied = SealedNarrative.Description();

        // Act
        Transaction transaction = Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            emptied,
            UtcNow());

        // Assert — "CLEARED" AND "NEVER FILLED" ARE DIFFERENT ROWS AND THIS FACTORY MUST NOT FOLD THEM.
        // This file used to carry two cases asserting the opposite —
        // Create_WithBlankDescription_SetsDescriptionToNull and its Update twin — because the entity held
        // text and folded whitespace onto null. That normalisation went with the string parameter and
        // CANNOT COME BACK IN ANY FORM: there is no text left here to inspect, so an entity that folded
        // would have to fold on the envelope's LENGTH, turning every emptied note in the product into a
        // note nobody ever wrote. A twenty-nine-byte envelope is a note somebody emptied; NULL is a note
        // nobody ever filed. The schema represents both and so must this.
        await Assert.That(transaction.Description).IsNotNull();
        await Assert.That(transaction.Description!.Envelope.Length)
            .IsEqualTo(CiphertextEnvelope.MinimumLength);
    }

    [Test]
    public async Task Create_WithADescriptionFarPastTheOldCharacterLimit_IsAccepted()
    {
        // Arrange — an envelope past the five hundred characters this factory used to refuse, and well
        // under NarrativeFieldLimits.DescriptionBytes, which is the only ceiling left and is measured in
        // stored bytes by NarrativeField before the value ever reaches Transaction. The description's cap
        // is DescriptionBytes and not NameBytes: the two are field CLASSES with different numbers.
        NarrativeField longDescription = SealedNarrative.Description(new string('x', 600));

        // Act
        Transaction transaction = Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            longDescription,
            UtcNow());

        // Assert — the capability moved to the client, which is the only side that holds a key; it was not
        // quietly dropped. THE SERVER CANNOT MEASURE CHARACTERS IN AN ENVELOPE AND NEVER WILL. A future
        // reader who reads the absence as an oversight and restores the 500-character check has nothing to
        // check it against; this case is what stops that edit.
        await Assert.That(transaction.Description).IsNotNull();
        await Assert.That(transaction.Description!.Envelope.Length).IsGreaterThan(500);
    }

    [Test]
    public async Task Create_WithZeroAmount_ReturnsTransactionCarryingZero()
    {
        // Arrange — a zero-net event (a fully discounted purchase, a refund that cancels out, a
        // zero-value invoice) is a real ledger entry whose value is the record, not the number.
        var budgetId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();

        // Act
        Transaction transaction = Transaction.Create(
            Guid.CreateVersion7(),
            budgetId,
            accountId,
            0m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            SealedNarrative.Description("Corner shop"),
            UtcNow());

        // Assert
        await Assert.That(transaction.Amount).IsEqualTo(0m);
        await Assert.That(transaction.BudgetId).IsEqualTo(budgetId);
        await Assert.That(transaction.AccountId).IsEqualTo(accountId);
    }

    [Test]
    public async Task AssignPayee_SetsPayeeId()
    {
        // Arrange
        Transaction transaction = NewTransaction();
        var payeeId = Guid.CreateVersion7();

        // Act
        transaction.AssignPayee(payeeId);

        // Assert
        await Assert.That(transaction.PayeeId).IsEqualTo(payeeId);
    }

    [Test]
    public async Task AssignPayee_WithEmptyPayeeId_ThrowsArgumentException()
    {
        // Arrange
        Transaction transaction = NewTransaction();

        // Act
        ArgumentException? caught = null;
        try
        {
            transaction.AssignPayee(Guid.Empty);
        }
        catch (ArgumentException exception)
        {
            caught = exception;
        }

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.ParamName).IsEqualTo("payeeId");
    }

    [Test]
    public async Task AssignCategory_SetsCategoryId()
    {
        // Arrange
        Transaction transaction = NewTransaction();
        var categoryId = Guid.CreateVersion7();

        // Act
        transaction.AssignCategory(categoryId);

        // Assert
        await Assert.That(transaction.CategoryId).IsEqualTo(categoryId);
    }

    [Test]
    public async Task AssignCategory_WithEmptyCategoryId_ThrowsArgumentException()
    {
        // Arrange
        Transaction transaction = NewTransaction();

        // Act
        ArgumentException? caught = null;
        try
        {
            transaction.AssignCategory(Guid.Empty);
        }
        catch (ArgumentException exception)
        {
            caught = exception;
        }

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.ParamName).IsEqualTo("categoryId");
    }

    [Test]
    [Arguments(2, "10.005", "Amount must have no more than 2 decimal places.")]
    [Arguments(0, "10.5", "Amount must be a whole number.")]
    [Arguments(3, "10.0005", "Amount must have no more than 3 decimal places.")]
    public async Task Create_WithMoreDecimalPlacesThanTheMinorUnitAllows_ThrowsValidationExceptionStatingTheLimit(
        int minorUnit,
        string amount,
        string expectedMessage)
    {
        // Arrange — the minor unit comes from the account's currency, so the same amount is legal
        // in one currency and not in another. Amounts arrive as strings because decimal is not a
        // legal attribute argument type.
        //
        // This is the one message in the file asserted by value rather than by key, because it is
        // the one that is computed: it forks on the minor unit, and a fork that produced "no more
        // than 0 decimal places" for yen would be visible nonsense in a ledger that no key-only
        // assertion could see. Both sides of the fork are pinned, and the three-place row pins the
        // interpolated number rather than a coincidental 2.

        // Act
        ValidationException exception = ThrowsValidationException(() => Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Money(amount),
            minorUnit,
            new DateOnly(2026, 6, 12),
            null,
            UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Amount")).IsTrue();
        await Assert.That(exception.Errors["Amount"].Single()).IsEqualTo(expectedMessage);
    }

    [Test]
    [Arguments(2, "10.99")]
    [Arguments(0, "10")]
    [Arguments(3, "10.005")]
    [Arguments(4, "10.0005")]
    public async Task Create_WithDecimalPlacesTheMinorUnitAllows_ReturnsTransaction(
        int minorUnit,
        string amount)
    {
        // Arrange — the three- and four-place cases are the point of the change: BHD and KWD have a
        // minor unit of 3, so a hard-coded 2 cannot represent their smallest unit at all.
        decimal expected = Money(amount);

        // Act
        Transaction transaction = Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            expected,
            minorUnit,
            new DateOnly(2026, 6, 12),
            null,
            UtcNow());

        // Assert
        await Assert.That(transaction.Amount).IsEqualTo(expected);
    }

    [Test]
    [Arguments("1000000000.01")]
    [Arguments("-1000000000.01")]
    [Arguments("2000000000")]
    public async Task Create_WithAmountBeyondTheMagnitudeLimit_ThrowsValidationException(string amount)
    {
        // Arrange — the magnitude cap is unchanged, so this looks like a test of nothing new. It is
        // the regression guard for the validation chain: the zero-amount rule used to be the first
        // branch of an else-if chain whose later branches are the decimal-places and magnitude
        // checks. Deleting the zero branch without care takes the magnitude check with it, and only
        // a whole-number over-limit amount (which passes the decimal check) proves it survived.

        // Act
        ValidationException exception = ThrowsValidationException(() => Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Money(amount),
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            null,
            UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Amount")).IsTrue();
    }

    [Test]
    [Arguments("1000000000")]
    [Arguments("-1000000000")]
    public async Task Create_WithAmountExactlyAtTheMagnitudeLimit_ReturnsTransaction(string amount)
    {
        // Arrange — the rule refuses only above this value, so the limit itself is legitimate data.
        decimal expected = Money(amount);

        // Act
        Transaction transaction = Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            expected,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            null,
            UtcNow());

        // Assert
        await Assert.That(transaction.Amount).IsEqualTo(expected);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(5)]
    public async Task Create_WithMinorUnitOutsideTheSupportedRange_ThrowsArgumentOutOfRangeException(int minorUnit)
    {
        // Arrange — the minor unit is never user input: it comes from CurrencyDto.MinorUnit, which
        // the database bounds with CK_currencies_minor_unit. An out-of-range value is therefore a
        // programmer error, and a ValidationException here would leak it to the user as a form
        // error. A ValidationException escapes this helper uncaught, which is the failure we want.

        // Act
        ArgumentOutOfRangeException exception = ThrowsArgumentOutOfRangeException(() => Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1m,
            minorUnit,
            new DateOnly(2026, 6, 12),
            null,
            UtcNow()));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("minorUnit");
    }

    [Test]
    public async Task Create_WithAnOutOfRangeMinorUnitAndAnEmptyId_RaisesTheProgrammerErrorFirst()
    {
        // Arrange — THIS ENTITY REFUSES ARGUMENTS IN TWO DIFFERENT CLASSES AND THE CASE EXISTS BECAUSE
        // THIS SLICE MADE THEM SIMULTANEOUSLY REACHABLE, NOT BECAUSE THE ORDERING IS NEW. The minor unit
        // has always failed fast; what it had nothing to collide with until now is a user-facing refusal
        // on an identifier the caller supplies. Before the id was threaded in, the only way to be wrong
        // about both at once was an empty budget or account id, which arrives from the session and the
        // request path rather than from a body — so nobody wrote the case and either order passed.
        //
        // The two classes and why they are ordered this way:
        //
        //   minorUnit — NOT user input. It comes from the account's currency, which the database bounds
        //   to 0..4 with CK_currencies_minor_unit, so an out-of-range value is a broken caller. It throws
        //   ArgumentOutOfRangeException BEFORE the errors dictionary exists, and the entity already
        //   argues why: a bad minor unit makes the precision check below it meaningless, so collecting it
        //   would report a nonsense Amount message beside it — "Amount must have no more than -1 decimal
        //   places" is not a sentence anybody can act on.
        //
        //   Id — genuinely user-facing. A caller minted it and a caller can correct it, so it collects
        //   with BudgetId and AccountId and becomes a 400 naming the member.
        //
        // Reversed, a broken caller is told it typed a bad identifier, and the real fault — a currency
        // row or a lookup this request never should have got past — is never reported at all.

        // Act
        ArgumentOutOfRangeException exception = ThrowsArgumentOutOfRangeException(() => Transaction.Create(
            Guid.Empty,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1m,
            -1,
            new DateOnly(2026, 6, 12),
            null,
            UtcNow()));

        // Assert — a ValidationException escapes this helper uncaught, which is the failure we want: it
        // is exactly what an implementation that collected the id first would produce.
        await Assert.That(exception.ParamName).IsEqualTo("minorUnit");
    }

    [Test]
    public async Task Update_WithValidInput_ReplacesAccountAmountDateAndDescription()
    {
        // Arrange
        Transaction transaction = NewTransaction();
        var newAccountId = Guid.CreateVersion7();
        var newDate = new DateOnly(2026, 7, 1);

        // Act
        transaction.Update(
            newAccountId, -19.99m, UsdMinorUnit, newDate, SealedNarrative.Description("Coffee run"));

        // Assert
        await Assert.That(transaction.AccountId).IsEqualTo(newAccountId);
        await Assert.That(transaction.Amount).IsEqualTo(-19.99m);
        await Assert.That(transaction.Date).IsEqualTo(newDate);
    }

    [Test]
    public async Task Update_WithANewDescription_ReplacesIt()
    {
        // Arrange — a transaction created under one note and corrected to another. The two labels differ
        // because SealedNarrative runs one position-varying filler over whichever label it is handed: a
        // shared label would make an Update that never touched the field pass this case.
        Transaction transaction = NewTransaction();

        // Arrange, continued — THE PRECONDITION, stated rather than assumed. The assertion below
        // discriminates only while the two envelopes differ, and the way they stop differing is a later
        // author tidying "Corner shop" and "Coffee run" into one label.
        await Assert.That(SealedNarrative.Description("Coffee run").Envelope.ToArray())
            .IsNotEquivalentTo(SealedNarrative.Description("Corner shop").Envelope.ToArray());

        // Act
        transaction.Update(
            Guid.CreateVersion7(),
            -19.99m,
            UsdMinorUnit,
            new DateOnly(2026, 7, 1),
            SealedNarrative.Description("Coffee run"));

        // Assert — the second of the three non-null read-backs the class remarks name. An Update that
        // replaced the amount and the date and left the note alone satisfies every constraint on the
        // table, and the 204 the route answers is a picture of the request rather than of the column.
        await Assert.That(transaction.Description).IsNotNull();
        await Assert.That(transaction.Description!.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Description("Coffee run").Envelope.ToArray(),
                CollectionOrdering.Matching);
    }

    [Test]
    public async Task Update_WithNoDescription_ClearsTheNote()
    {
        // Arrange — a transaction that HAS a note, so that "no note" is a change rather than a restatement
        // of what was already there. Started from null, this case passes for an Update that never touches
        // the field at all.
        Transaction transaction = NewTransaction();

        // Act
        transaction.Update(Guid.CreateVersion7(), -19.99m, UsdMinorUnit, new DateOnly(2026, 7, 1), null);

        // Assert — clearing a note is a thing a person does, and at THIS ring null means exactly that. The
        // third "leave it alone" state belongs to the PATCH route's Optional<string?> and is decided
        // before the entity is reached; an entity that invented one here would make the route's clear leg
        // unwritable.
        await Assert.That(transaction.Description).IsNull();
    }

    [Test]
    public async Task Update_WithAnEmptiedDescription_StoresTheEnvelopeRatherThanNull()
    {
        // Arrange — the edit-path twin of the create case, and the one that replaces the deleted
        // Update_WithBlankDescription_SetsDescriptionToNull. A note somebody selected and deleted seals to
        // a version, a nonce and a tag; a note somebody removed arrives as null. Those are two rows.
        Transaction transaction = NewTransaction();

        // Act
        transaction.Update(
            Guid.CreateVersion7(),
            -19.99m,
            UsdMinorUnit,
            new DateOnly(2026, 7, 1),
            SealedNarrative.Description());

        // Assert
        await Assert.That(transaction.Description).IsNotNull();
        await Assert.That(transaction.Description!.Envelope.Length)
            .IsEqualTo(CiphertextEnvelope.MinimumLength);
    }

    [Test]
    public async Task Update_WithARefusedAmount_LeavesTheDescriptionUnchanged()
    {
        // Arrange — THE CASE THE SIBLING ENTITIES CANNOT WRITE. On Category and CategoryGroup every rule
        // Update re-checks is one no reachable instance can be violating, so "a refused update changes
        // nothing" can only be made against a null argument — and a null argument is refused at a
        // dereference the compiler forces to happen before any assignment, which makes the claim nearly
        // vacuous. Here the refusal is a real ValidationException raised INSIDE the validator, with all
        // four assignments below it, so an Update that assigned the description first and validated
        // afterwards is a shape that exists and this case is what sees it.
        Transaction transaction = NewTransaction();

        // Act — an amount with more decimal places than USD has, which the validator refuses.
        ValidationException exception = ThrowsValidationException(() => transaction.Update(
            Guid.CreateVersion7(),
            Money("10.005"),
            UsdMinorUnit,
            new DateOnly(2026, 7, 1),
            SealedNarrative.Description("Coffee run")));

        // Assert — the third non-null read-back, and the only one whose subject is a write that did NOT
        // happen. The note is still the one the transaction was created with, byte for byte.
        await Assert.That(exception.Errors.ContainsKey("Amount")).IsTrue();
        await Assert.That(transaction.Description).IsNotNull();
        await Assert.That(transaction.Description!.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Description("Corner shop").Envelope.ToArray(),
                CollectionOrdering.Matching);
        await Assert.That(transaction.Amount).IsEqualTo(-42.50m);
    }

    [Test]
    public async Task Update_DoesNotChangeBudgetIdOrIdentity()
    {
        // Arrange — a transaction that changed budget would carry its history into someone else's
        // ledger, so the edit path must leave BudgetId alone; Id and CreatedAtUtc are identity and
        // are equally not the caller's to rewrite. Id carries a second job now: it is the associated data
        // the description was sealed against, so an Update that re-minted it would leave a note nobody can
        // open.
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();
        Transaction transaction = Transaction.Create(
            id,
            budgetId,
            Guid.CreateVersion7(),
            -42.50m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            SealedNarrative.Description("Corner shop"),
            createdAtUtc);

        // Act
        transaction.Update(
            Guid.CreateVersion7(),
            -19.99m,
            UsdMinorUnit,
            new DateOnly(2026, 7, 1),
            SealedNarrative.Description("Coffee run"));

        // Assert
        await Assert.That(transaction.BudgetId).IsEqualTo(budgetId);
        await Assert.That(transaction.Id).IsEqualTo(id);
        await Assert.That(transaction.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Update_DoesNotChangeAssignedPayeeAndCategory()
    {
        // Arrange — payee and category have their own four methods (AssignPayee, ClearPayee,
        // AssignCategory, ClearCategory); Update is not one of them.
        Transaction transaction = NewTransaction();
        var payeeId = Guid.CreateVersion7();
        var categoryId = Guid.CreateVersion7();
        transaction.AssignPayee(payeeId);
        transaction.AssignCategory(categoryId);

        // Act
        transaction.Update(
            Guid.CreateVersion7(),
            -19.99m,
            UsdMinorUnit,
            new DateOnly(2026, 7, 1),
            SealedNarrative.Description("Coffee run"));

        // Assert
        await Assert.That(transaction.PayeeId).IsEqualTo(payeeId);
        await Assert.That(transaction.CategoryId).IsEqualTo(categoryId);
    }

    [Test]
    [Arguments(2, "10.005", "Amount must have no more than 2 decimal places.")]
    [Arguments(0, "10.5", "Amount must be a whole number.")]
    public async Task Update_WithMoreDecimalPlacesThanTheMinorUnitAllows_ThrowsValidationExceptionStatingTheLimit(
        int minorUnit,
        string amount,
        string expectedMessage)
    {
        // Arrange — both sides of the message fork are pinned, because a zero-minor-unit currency
        // such as JPY takes the other branch and "no more than 0 decimal places" would be visible
        // nonsense in a ledger. Amounts arrive as strings because decimal is not a legal attribute
        // argument type.
        Transaction transaction = NewTransaction();

        // Act
        ValidationException exception = ThrowsValidationException(() => transaction.Update(
            Guid.CreateVersion7(),
            Money(amount),
            minorUnit,
            new DateOnly(2026, 7, 1),
            SealedNarrative.Description("Coffee run")));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Amount")).IsTrue();
        await Assert.That(exception.Errors["Amount"].Single()).IsEqualTo(expectedMessage);
    }

    [Test]
    [Arguments("1000000000.01")]
    [Arguments("-1000000000.01")]
    [Arguments("2000000000")]
    public async Task Update_WithAmountBeyondTheMagnitudeLimit_ThrowsValidationException(string amount)
    {
        // Arrange — the whole-number row passes the decimal-places check, so it is the one that
        // proves the magnitude branch is reachable at all on the edit path.
        Transaction transaction = NewTransaction();

        // Act
        ValidationException exception = ThrowsValidationException(() => transaction.Update(
            Guid.CreateVersion7(),
            Money(amount),
            UsdMinorUnit,
            new DateOnly(2026, 7, 1),
            SealedNarrative.Description("Coffee run")));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Amount")).IsTrue();
    }

    [Test]
    public async Task Update_WithEmptyAccountId_ThrowsValidationException()
    {
        // Arrange
        Transaction transaction = NewTransaction();

        // Act
        ValidationException exception = ThrowsValidationException(() => transaction.Update(
            Guid.Empty,
            -19.99m,
            UsdMinorUnit,
            new DateOnly(2026, 7, 1),
            SealedNarrative.Description("Coffee run")));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("AccountId")).IsTrue();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(5)]
    public async Task Update_WithMinorUnitOutsideTheSupportedRange_ThrowsArgumentOutOfRangeException(int minorUnit)
    {
        // Arrange — same reasoning as Create: the minor unit comes from the account's currency,
        // which the database bounds, so an out-of-range value is a programmer error and a
        // ValidationException here would leak it to the user as a form error. A ValidationException
        // escapes this helper uncaught, which is the failure we want.
        Transaction transaction = NewTransaction();

        // Act
        ArgumentOutOfRangeException exception = ThrowsArgumentOutOfRangeException(() => transaction.Update(
            Guid.CreateVersion7(),
            1m,
            minorUnit,
            new DateOnly(2026, 7, 1),
            SealedNarrative.Description("Coffee run")));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("minorUnit");
    }

    [Test]
    public async Task ClearPayee_RemovesAssignedPayee()
    {
        // Arrange — clearing is a legitimate user action and gets its own method, because
        // AssignPayee treats an empty id as a programmer error rather than as "no payee".
        Transaction transaction = NewTransaction();
        transaction.AssignPayee(Guid.CreateVersion7());

        // Act
        transaction.ClearPayee();

        // Assert
        await Assert.That(transaction.PayeeId).IsNull();
    }

    [Test]
    public async Task ClearCategory_RemovesAssignedCategory()
    {
        // Arrange
        Transaction transaction = NewTransaction();
        transaction.AssignCategory(Guid.CreateVersion7());

        // Act
        transaction.ClearCategory();

        // Assert
        await Assert.That(transaction.CategoryId).IsNull();
    }

    [Test]
    public async Task ClearPayee_WithNoPayeeAssigned_LeavesPayeeNull()
    {
        // Arrange — an uncategorised, unpayeed transaction is the normal state of a freshly
        // imported row, so clearing what is already clear must be a no-op rather than a throw.
        Transaction transaction = NewTransaction();

        // Act
        transaction.ClearPayee();

        // Assert
        await Assert.That(transaction.PayeeId).IsNull();
    }

    [Test]
    public async Task ClearCategory_WithNoCategoryAssigned_LeavesCategoryNull()
    {
        // Arrange
        Transaction transaction = NewTransaction();

        // Act
        transaction.ClearCategory();

        // Assert
        await Assert.That(transaction.CategoryId).IsNull();
    }

    /// <summary>
    /// A content-key rotation replaces the note with the envelope sealed under the new key.
    /// </summary>
    /// <remarks>
    /// <b>The second label models the same text under two keys, not new text.</b> A rotation
    /// re-encrypts what the row already holds, so nothing about the transaction changes except the bytes
    /// — but <see cref="SealedNarrative" /> derives the envelope from the label it is handed, so "the
    /// same text under a new key" has no other spelling here than a second label. A reseal that decided
    /// the presence question correctly and then left the column's existing envelope in place passes
    /// every assertion that only checks for a non-null note: same width, same version byte, same
    /// <c>CHECK</c> constraint underneath. What it produces is a row that survived a rotation without
    /// being rotated, and the promotion step at the end of the run destroys the only key that opens it.
    /// </remarks>
    [Test]
    public async Task ResealDescription_ReplacesTheNote()
    {
        // Arrange
        Transaction transaction = NewTransaction();

        // Act
        transaction.ResealDescription(
            SealedNarrative.Description("Corner shop resealed"), Guid.CreateVersion7());

        // Assert — CollectionOrdering.Matching on the positive assertion for the reason
        // Create_WithValidInput_StoresEveryColumnItWasGiven states; the negative one keeps the default,
        // which is the stronger "not even a permutation" claim.
        await Assert.That(transaction.Description).IsNotNull();
        await Assert.That(transaction.Description!.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Description("Corner shop resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(transaction.Description.Envelope.ToArray())
            .IsNotEquivalentTo(SealedNarrative.Description("Corner shop").Envelope.ToArray());
    }

    /// <summary>
    /// A reseal stamps the row with the id of the rotation that rewrote it.
    /// </summary>
    /// <remarks>
    /// The stamp is the whole reason the column exists, argued at <c>Budget.RotationId</c>: a re-sealed
    /// envelope and an untouched one are byte-for-byte indistinguishable to a server holding no key, so
    /// the completion step — which destroys the only copies of the old keys — can only know the rewrite
    /// finished by being told, in the same transaction as the ciphertext. A reseal that replaced the
    /// envelope and left this column null passes every other accepting case in this file and makes the
    /// account un-completable — and transactions are the rows there are most of, so this is the entity
    /// where "the run never finishes" will be noticed first.
    /// </remarks>
    [Test]
    public async Task ResealDescription_StampsTheRotationItWasGiven()
    {
        // Arrange — the id minted here and threaded in, so the assertion is not "a stamp appeared" but
        // "this rotation's did". A member that minted its own would leave every row stamped with an id
        // no completion step is looking for.
        Transaction transaction = NewTransaction();
        var rotationId = Guid.CreateVersion7();

        // Act
        transaction.ResealDescription(
            SealedNarrative.Description("Corner shop resealed"), rotationId);

        // Assert
        await Assert.That(transaction.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// A reseal moves the note and the stamp and touches nothing else.
    /// </summary>
    /// <remarks>
    /// <b>This is the case that catches a reseal built by reusing <see cref="Transaction.Update" />.</b>
    /// Update takes an account, an amount, a minor unit and a date; a reseal that delegated to it would
    /// have to invent values for all four, and the obvious inventions are the row's own current values —
    /// which looks correct until a second request changes one of them between the read and the reseal.
    /// A transaction is the one narrative-bearing row whose other columns are money, so this is where a
    /// rotation that wrote more than ciphertext stops being a display bug: the amounts are what every
    /// balance in the product is summed from, and nothing about a re-encryption gives it standing to
    /// touch them. The assignments are asserted too, because they are nullable and a reseal that reset
    /// them would silently uncategorise a ledger.
    /// </remarks>
    [Test]
    public async Task ResealDescription_LeavesAmountDateAccountPayeeCategoryAndIdentityUnchanged()
    {
        // Arrange — every non-narrative column set to something other than its default, including both
        // assignments, so a reseal that overwrote one with a default is visible rather than accidentally
        // right.
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var payeeId = Guid.CreateVersion7();
        var categoryId = Guid.CreateVersion7();
        DateOnly date = new(2026, 6, 12);
        DateTime createdAtUtc = UtcNow();
        Transaction transaction = Transaction.Create(
            id,
            budgetId,
            accountId,
            Money("-42.50"),
            UsdMinorUnit,
            date,
            SealedNarrative.Description("Corner shop"),
            createdAtUtc);
        transaction.AssignPayee(payeeId);
        transaction.AssignCategory(categoryId);

        // Act
        transaction.ResealDescription(
            SealedNarrative.Description("Corner shop resealed"), Guid.CreateVersion7());

        // Assert
        await Assert.That(transaction.Id).IsEqualTo(id);
        await Assert.That(transaction.BudgetId).IsEqualTo(budgetId);
        await Assert.That(transaction.AccountId).IsEqualTo(accountId);
        await Assert.That(transaction.Amount).IsEqualTo(Money("-42.50"));
        await Assert.That(transaction.Date).IsEqualTo(date);
        await Assert.That(transaction.PayeeId).IsEqualTo(payeeId);
        await Assert.That(transaction.CategoryId).IsEqualTo(categoryId);
        await Assert.That(transaction.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    /// <summary>
    /// A rotation that supplies no note for a transaction that has one is refused.
    /// </summary>
    /// <remarks>
    /// The clearing arm of the presence rule <c>NarrativeReseal.Resealed</c> owns, reached through this
    /// entity so that the transaction's note is actually routed through it. The column is nullable, so
    /// writing the absence through produces a legal row that violates no constraint and is
    /// byte-identical to one belonging to somebody who deliberately filed no note. Nothing in the schema
    /// can tell that bug from an operation, which is why the refusal has to be in the domain.
    /// </remarks>
    [Test]
    public async Task ResealDescription_WithNoNoteOverATransactionThatHasOne_IsRefused()
    {
        // Arrange
        Transaction transaction = NewTransaction();

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            transaction.ResealDescription(null, Guid.CreateVersion7()));

        // Assert — keyed on the member the request carries. A refusal filed under a word invented by the
        // entity reaches the client verbatim as a 400 naming a member no request has.
        await Assert.That(exception.Errors.ContainsKey(nameof(Transaction.Description))).IsTrue();
    }

    /// <summary>
    /// A rotation that supplies a note for a transaction that has none is refused.
    /// </summary>
    /// <remarks>
    /// The quieter arm, and the one a reviewer will propose relaxing: filling in an empty note harms no
    /// data. It is refused because presence is the only property this side can check at all, so an arm
    /// that admits a change of presence gives up the whole of what the rule is made of — and what lands
    /// in that column is text the server cannot read, attributed to a person who never wrote it, in a
    /// run they authorised as "re-encrypt what I have".
    /// </remarks>
    [Test]
    public async Task ResealDescription_WithANoteOverATransactionThatHasNone_IsRefused()
    {
        // Arrange — the note-less transaction is built here rather than taken from NewTransaction, which
        // always carries one for the reason its own remarks give.
        Transaction transaction = Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Money("-42.50"),
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            null,
            UtcNow());

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            transaction.ResealDescription(
                SealedNarrative.Description("Corner shop"), Guid.CreateVersion7()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(Transaction.Description))).IsTrue();
    }

    /// <summary>
    /// A transaction that never had a note is stamped anyway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The control for both refusals, and not a filler case.</b> Without it, a reseal that threw
    /// whenever either side of the note was null would pass both cases above and would make every
    /// account un-rotatable the moment it held one note-less transaction — which is most accounts, on
    /// the entity there are most rows of. A refusal the person cannot act on, because the field they are
    /// being refused for is one they never filled in.
    /// </para>
    /// <para>
    /// <b>The stamp is the half that matters, and this is the only row in the product where a reseal
    /// writes the stamp and nothing else.</b> The sibling entities all have a non-nullable name to
    /// re-encrypt, so their note-less cases still produce a visible write; here there is nothing to
    /// re-encrypt at all, and the row must still be accounted for or completion is permanently one
    /// short. A member that returned early on a null note would look correct on every other case in this
    /// file and would leave the run stuck on exactly the rows that had the least to hide.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealDescription_WithNoNoteOverATransactionThatHasNone_IsAcceptedAndStillStamps()
    {
        // Arrange
        Transaction transaction = Transaction.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Money("-42.50"),
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            null,
            UtcNow());
        var rotationId = Guid.CreateVersion7();

        // Act
        transaction.ResealDescription(null, rotationId);

        // Assert
        await Assert.That(transaction.Description).IsNull();
        await Assert.That(transaction.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// A refused reseal writes nothing at all — not the note, and above all not the stamp.
    /// </summary>
    /// <remarks>
    /// <b>The stamp is the assertion that matters here.</b> A reseal that stamped before it judged the
    /// note leaves a row marked as rotated that was not — and the stamp is the one signal completion
    /// trusts, so the destructive step would promote the new keys over a row still sealed under the old
    /// one. That is the exact loss the column was added to prevent, produced by the member that writes
    /// it. The previous rotation's id is the fixture rather than <see langword="null" /> so that a
    /// member which cleared the stamp on refusal also reddens.
    /// </remarks>
    [Test]
    public async Task ResealDescription_WithARefusedNote_LeavesTheNoteAndTheStampAsTheyWere()
    {
        // Arrange — a transaction already carried through one rotation, now handed a chunk that drops
        // its note.
        Transaction transaction = NewTransaction();
        var firstRotationId = Guid.CreateVersion7();
        transaction.ResealDescription(
            SealedNarrative.Description("Corner shop resealed"), firstRotationId);

        // Act
        ThrowsValidationException(() =>
            transaction.ResealDescription(null, Guid.CreateVersion7()));

        // Assert — CollectionOrdering.Matching for the reason
        // Create_WithValidInput_StoresEveryColumnItWasGiven states.
        await Assert.That(transaction.Description!.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Description("Corner shop resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(transaction.RotationId).IsEqualTo(firstRotationId);
    }

    /// <summary>
    /// An ordinary update clears the stamp a rotation left on the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the rule the whole stamp rests on, and it is the easiest one to leave out</b> —
    /// nothing about <see cref="Transaction.Update" /> reads as being part of a rotation, so a reader
    /// implementing the reseal member has no reason to open this one.
    /// </para>
    /// <para>
    /// <b>What goes wrong without it.</b> A second browser tab still holding the OLD content key can
    /// edit a transaction this rotation has already stamped. It writes old-key ciphertext, and — with
    /// this line missing — it does not touch the stamp, so the row ends up carrying old-key ciphertext
    /// under a current stamp. Completion then reads a full house, promotes the new keys and destroys the
    /// old ones, and that transaction's note is gone: no constraint violated, nothing red, and the
    /// symptom is a ledger line that will not decrypt. Clearing the stamp is what makes completion
    /// refuse instead, which is a run the person can retry.
    /// </para>
    /// <para>
    /// <b><see cref="Transaction.AssignPayee" />, <see cref="Transaction.ClearPayee" />,
    /// <see cref="Transaction.AssignCategory" /> and <see cref="Transaction.ClearCategory" /> are
    /// deliberately not given the same case.</b> None of them writes a narrative column, so a stale tab
    /// recategorising a transaction invalidates no ciphertext and has nothing to disown. Clearing there
    /// would fail rotations for edits that cost them nothing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Update_ClearsTheRotationStamp()
    {
        // Arrange — stamped through the real reseal path rather than reflected in, so the case describes
        // the sequence that actually happens: a chunk rewrites the row, then a stale tab edits it.
        Transaction transaction = NewTransaction();
        transaction.ResealDescription(
            SealedNarrative.Description("Corner shop resealed"), Guid.CreateVersion7());

        // Act
        transaction.Update(
            Guid.CreateVersion7(),
            Money("-12.00"),
            UsdMinorUnit,
            new DateOnly(2026, 6, 13),
            SealedNarrative.Description("Chemist"));

        // Assert
        await Assert.That(transaction.RotationId).IsNull();
    }

    /// <summary>
    /// A refused update leaves the stamp where it was.
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="Update_ClearsTheRotationStamp" />, and the reason the clearing cannot
    /// be a line at the top of <see cref="Transaction.Update" />: a refused update wrote no ciphertext,
    /// so there is nothing to disown. Cleared anyway, a rotation would be failed by edits that never
    /// landed — a completion step that refuses a run the person can see nothing wrong with, whose only
    /// remedy is to re-rotate the whole account. This is the one entity where the claim is testable
    /// against a ValidationException rather than a null dereference, for the reason
    /// <see cref="Update_WithARefusedAmount_LeavesTheDescriptionUnchanged" /> gives.
    /// </remarks>
    [Test]
    public async Task Update_WithARefusedAmount_LeavesTheRotationStampWhereItWas()
    {
        // Arrange
        Transaction transaction = NewTransaction();
        var rotationId = Guid.CreateVersion7();
        transaction.ResealDescription(
            SealedNarrative.Description("Corner shop resealed"), rotationId);

        // Act
        ThrowsValidationException(() => transaction.Update(
            Guid.CreateVersion7(),
            Money("10.005"),
            UsdMinorUnit,
            new DateOnly(2026, 6, 13),
            SealedNarrative.Description("Chemist")));

        // Assert
        await Assert.That(transaction.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// Assigning a payee leaves the stamp standing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The inverse of <see cref="Update_ClearsTheRotationStamp" />, and the mutation it catches is
    /// clearing the stamp HERE.</b> This is the first of four cases making the same claim about the four
    /// assignment members; the argument is written out once, on this one, and the other three point at
    /// it. All four write a nullable <see cref="Guid" /> the server reads and touch no envelope, so after
    /// any of them runs the row's ciphertext is still whatever the rotation sealed and the stamp is
    /// still honest. A reader who takes "an edit clears the stamp" as the rule rather than "a NARRATIVE
    /// write clears the stamp" will put the line in all five members of this entity, and every case in
    /// this file except these four stays green.
    /// </para>
    /// <para>
    /// <b>Why that is worse than it looks, and why it is not a data-loss bug.</b> Nothing is lost — the
    /// row is fine. What breaks is convergence. Completion refuses a rotation that genuinely finished,
    /// the client re-seals the un-stamped rows, and on an account where somebody is categorising a
    /// backlog of transactions while the run proceeds, each pass re-stamps rows the next assignment
    /// un-stamps. The rotation may never finish, and transactions are the rows there are most of, so
    /// this is the entity where a non-converging run will actually happen.
    /// </para>
    /// <para>
    /// <b>These four are pins, not red bars.</b> They go green the moment the member exists, because no
    /// assignment member writes a stamp today. Their value is entirely in the mutation named above.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AssignPayee_LeavesTheRotationStampStanding()
    {
        // Arrange — stamped through the real reseal path, then categorised.
        Transaction transaction = NewTransaction();
        var rotationId = Guid.CreateVersion7();
        transaction.ResealDescription(
            SealedNarrative.Description("Corner shop resealed"), rotationId);
        var payeeId = Guid.CreateVersion7();

        // Act
        transaction.AssignPayee(payeeId);

        // Assert
        await Assert.That(transaction.PayeeId).IsEqualTo(payeeId);
        await Assert.That(transaction.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// Clearing a payee leaves the stamp standing.
    /// </summary>
    /// <remarks>
    /// The same claim <see cref="AssignPayee_LeavesTheRotationStampStanding" /> argues, on the member
    /// that removes an assignment rather than adds one. It is its own case because clearing reads like a
    /// deletion, which is the shape a reader is most likely to reach for the stamp over.
    /// </remarks>
    [Test]
    public async Task ClearPayee_LeavesTheRotationStampStanding()
    {
        // Arrange
        Transaction transaction = NewTransaction();
        transaction.AssignPayee(Guid.CreateVersion7());
        var rotationId = Guid.CreateVersion7();
        transaction.ResealDescription(
            SealedNarrative.Description("Corner shop resealed"), rotationId);

        // Act
        transaction.ClearPayee();

        // Assert
        await Assert.That(transaction.PayeeId).IsNull();
        await Assert.That(transaction.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// Assigning a category leaves the stamp standing.
    /// </summary>
    /// <remarks>
    /// The same claim <see cref="AssignPayee_LeavesTheRotationStampStanding" /> argues, on the
    /// assignment a person makes most often — categorising a backlog is the bulk edit most likely to be
    /// running at the same time as a rotation.
    /// </remarks>
    [Test]
    public async Task AssignCategory_LeavesTheRotationStampStanding()
    {
        // Arrange
        Transaction transaction = NewTransaction();
        var rotationId = Guid.CreateVersion7();
        transaction.ResealDescription(
            SealedNarrative.Description("Corner shop resealed"), rotationId);
        var categoryId = Guid.CreateVersion7();

        // Act
        transaction.AssignCategory(categoryId);

        // Assert
        await Assert.That(transaction.CategoryId).IsEqualTo(categoryId);
        await Assert.That(transaction.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// Clearing a category leaves the stamp standing.
    /// </summary>
    /// <remarks>
    /// The same claim <see cref="AssignPayee_LeavesTheRotationStampStanding" /> argues, on the fourth
    /// and last of the assignment members. Listed rather than folded into its sibling so that the four
    /// members and the four cases are the same count, which is what makes a fifth assignment member
    /// arrive as a row somebody has to write.
    /// </remarks>
    [Test]
    public async Task ClearCategory_LeavesTheRotationStampStanding()
    {
        // Arrange
        Transaction transaction = NewTransaction();
        transaction.AssignCategory(Guid.CreateVersion7());
        var rotationId = Guid.CreateVersion7();
        transaction.ResealDescription(
            SealedNarrative.Description("Corner shop resealed"), rotationId);

        // Act
        transaction.ClearCategory();

        // Assert
        await Assert.That(transaction.CategoryId).IsNull();
        await Assert.That(transaction.RotationId).IsEqualTo(rotationId);
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
    public async Task ResealDescription_WithTheEmptyRotationId_IsRefused()
    {
        // Arrange
        Transaction transaction = NewTransaction();

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            transaction.ResealDescription(
                SealedNarrative.Description("Corner shop resealed"), Guid.Empty));

        // Assert — keyed on the member the stamp lands in, as KeyRotation.Begin keys its own. The note
        // is read back too, so a member that assigned before it judged reddens here rather than leaving
        // a row rewritten under a rotation that does not exist.
        await Assert.That(exception.Errors.ContainsKey(nameof(Transaction.RotationId))).IsTrue();
        await Assert.That(transaction.Description!.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Description("Corner shop").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(transaction.RotationId).IsNull();
    }

    /// <summary>
    /// A chunk re-sent under the rotation id it already carried is accepted, and leaves the note and the
    /// stamp where the first arrival put them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A re-sent chunk is the ordinary case and not an anomaly.</b> A rotation is cut into chunks
    /// because an account can hold more rows than one request should carry, and every chunk of one run
    /// quotes the same rotation id — that is what the id is for. This is the entity that makes chunking
    /// necessary in the first place: transactions outnumber every other narrative-bearing row in the
    /// product by orders of magnitude, so a rotation's longest leg is here and so is its likeliest
    /// timeout.
    /// </para>
    /// <para>
    /// <b>What this case is here to refuse.</b> A member that additionally rejected a rotation id equal
    /// to the stamp the row already carries reads as sensible idempotence protection and passes every
    /// other case in this file, because they all mint a fresh id. It would fail exactly the runs long
    /// enough to need chunking — the retry, on the accounts with the most rows — and it protects against
    /// nothing: a reseal is a whole-value write, so the same chunk applied twice lands the same bytes and
    /// the same stamp.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealDescription_RepeatedUnderTheSameRotationId_IsAccepted()
    {
        // Arrange — the chunk that landed, and the id its run is quoting throughout.
        Transaction transaction = NewTransaction();
        var rotationId = Guid.CreateVersion7();
        transaction.ResealDescription(SealedNarrative.Description("Corner shop resealed"), rotationId);

        // Act — the same chunk again, under the same id, as a re-sent request carries it.
        transaction.ResealDescription(SealedNarrative.Description("Corner shop resealed"), rotationId);

        // Assert — the note still sealed under the new key, and the stamp still standing, so a member
        // that refused would redden on the call and one that cleared on a repeat would redden here.
        await Assert.That(transaction.Description!.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Description("Corner shop resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(transaction.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// A legal transaction carrying the note labelled <c>"Corner shop"</c>, for the cases whose subject is
    /// something other than the arguments <see cref="Transaction.Create" /> was handed.
    /// </summary>
    /// <remarks>
    /// It always carries a description, and deliberately: the cases about a refused or a partial
    /// <c>Update</c> need a note that was already there to be able to say it survived. A helper returning
    /// a note-less transaction would make every one of them pass against an entity that dropped the
    /// column.
    /// </remarks>
    private static Transaction NewTransaction() => Transaction.Create(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        -42.50m,
        UsdMinorUnit,
        new DateOnly(2026, 6, 12),
        SealedNarrative.Description("Corner shop"),
        UtcNow());

    private static DateTime UtcNow() => new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Parses a money literal the culture-invariant way. The values arrive as strings because
    /// <c>decimal</c> is not a legal attribute argument type.
    /// </summary>
    private static decimal Money(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

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

    private static ArgumentOutOfRangeException ThrowsArgumentOutOfRangeException(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ArgumentOutOfRangeException.");
    }
}
