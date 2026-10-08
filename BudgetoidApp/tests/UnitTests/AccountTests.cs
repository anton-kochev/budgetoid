using System.Globalization;
using Domain.Accounts;
using Domain.Common;
using Domain.Security;
using TestSupport;
using TUnit.Assertions.Enums;

namespace UnitTests;

public sealed class AccountTests
{
    /// <summary>
    /// Minor unit of a two-decimal currency such as USD. Named rather than inlined so a call site
    /// that does not care about precision does not read as if <c>2</c> were a magic rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    [Test]
    public async Task Create_UsesTheIdItWasGiven()
    {
        // Arrange — an id minted here and threaded in, so the assertion is not "an id came back" but
        // "this one did".
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();

        // Act
        Account account = Account.Create(
            id, budgetId, SealedNarrative.Indexed("Checking"), AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());

        // Assert — the identifier is the associated data the client sealed the name against, so a
        // factory that ignored this parameter and minted its own would satisfy every other case in this
        // file and produce a row whose name nobody can ever open: no constraint violated, nothing red,
        // and the symptom arriving months later as text that will not decrypt. Guid.CreateVersion7 has
        // left the production file for that reason, and this is the case that would notice it coming
        // back.
        await Assert.That(account.Id).IsEqualTo(id);
    }

    [Test]
    public async Task Create_WithValidInput_StoresBothHalvesOfTheNameTypeOpeningBalanceAndCreatedAtUtc()
    {
        // Arrange
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();
        IndexedName name = SealedNarrative.Indexed("Checking");
        DateTime createdAtUtc = new(2026, 6, 25, 13, 14, 15, DateTimeKind.Utc);

        // Act
        Account account = Account.Create(
            id, budgetId, name, AccountType.Checking, 100.25m, " usd ", UsdMinorUnit, createdAtUtc);

        // Assert — both halves are compared as the bytes they carry rather than by reference. Neither
        // NarrativeField nor ReadOnlyMemory<byte> gives content equality here for free, and a reference
        // comparison would pass for any instance the factory happened to hold on to.
        //
        // The name half used to be asserted as the string "Checking", trimmed from "  Checking  ". Both
        // the value and the trim are gone: the column holds an envelope over text this server has never
        // seen, so there is no string to compare and no whitespace to strip. What survives is that the
        // factory stored the value it was handed, unaltered, in both columns.
        //
        // CollectionOrdering.Matching IS PART OF THE ASSERTION, everywhere in this file. IsEqualTo over
        // two byte[] compares REFERENCES and fails even when the contents and the order agree, and
        // TUnit's failure message names IsEquivalentTo as the fix — whose default is
        // CollectionOrdering.Any, so the bare overload passes on every permutation of an envelope's or a
        // digest's bytes. Order is the whole of what a ciphertext is: a factory that permuted either
        // half would satisfy the bare overload and produce right-width, wrong-value bytes that no key
        // opens and no recomputation on this side can notice.
        await Assert.That(account.BudgetId).IsEqualTo(budgetId);
        await Assert.That(account.Name.Envelope.ToArray())
            .IsEquivalentTo(name.Name.Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(account.NameKey.ToArray())
            .IsEquivalentTo(name.BlindIndex.ToArray(), CollectionOrdering.Matching);
        await Assert.That(account.Type).IsEqualTo(AccountType.Checking);
        await Assert.That(account.OpeningBalance).IsEqualTo(100.25m);
        await Assert.That(account.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithEmptyId_ThrowsValidationExceptionForId()
    {
        // Act — the empty Guid became reachable the day the identifier stopped being minted here: it is
        // what a caller that threaded a default through hands over.
        ValidationException exception = ThrowsValidationException(() => Account.Create(
            Guid.Empty,
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Checking"),
            AccountType.Checking,
            0m,
            "USD",
            UsdMinorUnit,
            UtcNow()));

        // Assert — keyed on the id, not merely thrown. Left to the primary key instead, all-zero is a
        // legal uuid: the first such row stores and the second collides under a constraint name that
        // says nothing about the caller that never chose an id at all.
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
    }

    [Test]
    public async Task Create_WithEmptyBudgetId_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() => Account.Create(
            Guid.CreateVersion7(),
            Guid.Empty,
            SealedNarrative.Indexed("Checking"),
            AccountType.Checking,
            0m,
            "USD",
            UsdMinorUnit,
            UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("BudgetId")).IsTrue();
    }

    [Test]
    public async Task Create_WithABlankName_IsAccepted()
    {
        // Arrange — the shortest envelope the format can produce: a version, a nonce and a tag over an
        // empty plaintext. This is exactly what a client sealing a blank name, or a name of nothing but
        // spaces, sends.
        IndexedName blank = SealedNarrative.Indexed();

        // Act
        Account account = Account.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), blank, AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());

        // Assert — this factory used to refuse a blank name with a ValidationException keyed on Name,
        // and it no longer can. The capability moved to the client, which is the only side that holds a
        // key; it was not quietly dropped. THE SERVER CANNOT MEASURE CHARACTERS IN AN ENVELOPE AND NEVER
        // WILL — it holds ciphertext over text it has never seen, so "is this just whitespace?" is a
        // question about a plaintext that exists nowhere on this side. A future reader who reads the
        // absence as an oversight and restores the check has nothing to check it against; this case is
        // what stops that edit.
        await Assert.That(account.Name.Envelope.Length).IsEqualTo(CiphertextEnvelope.MinimumLength);
    }

    [Test]
    public async Task Create_WithANameFarPastTheOldCharacterLimit_IsAccepted()
    {
        // Arrange — an envelope well past the two hundred characters this factory used to refuse, and
        // well under NarrativeFieldLimits.NameBytes, which is the only ceiling left and is measured in
        // stored bytes by NarrativeField before the value ever reaches Account.
        IndexedName longName = SealedNarrative.Indexed(new string('x', 400));

        // Act
        Account account = Account.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), longName, AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());

        // Assert — the same relocation as the blank case, in the other direction. A length rule here
        // would be a rule about characters, and the server counts none: AES-GCM ciphertext is exactly as
        // long as its plaintext, but the framing and the encoding sit on top of it, so bytes stored and
        // characters typed are different questions and only the first is answerable here.
        await Assert.That(account.Name.Envelope.Length).IsGreaterThan(200);
    }

    [Test]
    public async Task Create_WithUndefinedAccountType_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() => Account.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Checking"),
            (AccountType)999,
            0m,
            "USD",
            UsdMinorUnit,
            UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("Type")).IsTrue();
    }

    [Test]
    public async Task Create_WithZeroOpeningBalance_ReturnsAccount()
    {
        // Arrange — a brand-new account starts empty, so zero is the common case and not an edge.

        // Act
        Account account = Account.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Checking"),
            AccountType.Checking,
            0m,
            "USD",
            UsdMinorUnit,
            UtcNow());

        // Assert
        await Assert.That(account.OpeningBalance).IsEqualTo(0m);
    }

    [Test]
    [Arguments(2, "1.234", "Opening balance must have no more than 2 decimal places.")]
    [Arguments(0, "10.5", "Opening balance must be a whole number.")]
    [Arguments(3, "10.0005", "Opening balance must have no more than 3 decimal places.")]
    public async Task Create_WithMoreDecimalPlacesThanTheMinorUnitAllows_ThrowsValidationExceptionStatingTheLimit(
        int minorUnit,
        string openingBalance,
        string expectedMessage)
    {
        // Arrange — precision follows the account's currency, so the same balance is legal in one
        // currency and not in another. Balances arrive as strings because decimal is not a legal
        // attribute argument type.
        //
        // This is the one message in the file asserted by value rather than by key, because it is
        // the one that is computed: it forks on the minor unit, and a fork that produced "no more
        // than 0 decimal places" for yen would be visible nonsense in a ledger that no key-only
        // assertion could see. Both sides of the fork are pinned, and the three-place row pins the
        // interpolated number rather than a coincidental 2. Update shares ValidateOrThrow with
        // Create, so pinning the sentence here covers both entry points.

        // Act
        ValidationException exception = ThrowsValidationException(() => Account.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Checking"),
            AccountType.Checking,
            Money(openingBalance),
            "USD",
            minorUnit,
            UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("OpeningBalance")).IsTrue();
        await Assert.That(exception.Errors["OpeningBalance"].Single()).IsEqualTo(expectedMessage);
    }

    [Test]
    [Arguments(2, "10.99")]
    [Arguments(0, "10")]
    [Arguments(3, "10.005")]
    [Arguments(4, "10.0005")]
    public async Task Create_WithDecimalPlacesTheMinorUnitAllows_ReturnsAccount(
        int minorUnit,
        string openingBalance)
    {
        // Arrange — the three-place case is the point of the change: BHD and KWD have a minor unit
        // of 3, so a hard-coded 2 cannot represent their smallest unit at all.
        decimal expected = Money(openingBalance);

        // Act
        Account account = Account.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Checking"),
            AccountType.Checking,
            expected,
            "USD",
            minorUnit,
            UtcNow());

        // Assert
        await Assert.That(account.OpeningBalance).IsEqualTo(expected);
    }

    [Test]
    [Arguments("1000000000.01")]
    [Arguments("-1000000000.01")]
    [Arguments("2000000000")]
    public async Task Create_WithOpeningBalanceBeyondTheMagnitudeLimit_ThrowsValidationException(
        string openingBalance)
    {
        // Arrange — the magnitude cap is unchanged, but it shares an else-if chain with the decimal
        // check that is being rewritten. The whole-number case passes the decimal check and so is
        // the one that proves the magnitude branch survived the rewrite.

        // Act
        ValidationException exception = ThrowsValidationException(() => Account.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Checking"),
            AccountType.Checking,
            Money(openingBalance),
            "USD",
            UsdMinorUnit,
            UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("OpeningBalance")).IsTrue();
    }

    [Test]
    [Arguments("1000000000")]
    [Arguments("-1000000000")]
    public async Task Create_WithOpeningBalanceExactlyAtTheMagnitudeLimit_ReturnsAccount(
        string openingBalance)
    {
        // Arrange — the rule refuses only above this value, so the limit itself is legitimate data.
        decimal expected = Money(openingBalance);

        // Act
        Account account = Account.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Checking"),
            AccountType.Checking,
            expected,
            "USD",
            UsdMinorUnit,
            UtcNow());

        // Assert
        await Assert.That(account.OpeningBalance).IsEqualTo(expected);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(5)]
    public async Task Create_WithMinorUnitOutsideTheSupportedRange_ThrowsArgumentOutOfRangeException(
        int minorUnit)
    {
        // Arrange — the minor unit is never user input: it comes from CurrencyDto.MinorUnit, which
        // the database bounds with CK_currencies_minor_unit. An out-of-range value is therefore a
        // programmer error, and a ValidationException here would leak it to the user as a form
        // error. A ValidationException escapes this helper uncaught, which is the failure we want.

        // Act
        ArgumentOutOfRangeException exception = ThrowsArgumentOutOfRangeException(() => Account.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Checking"),
            AccountType.Checking,
            0m,
            "USD",
            minorUnit,
            UtcNow()));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("minorUnit");
    }

    [Test]
    public async Task Create_WithoutAName_ThrowsArgumentNullException()
    {
        // Arrange — the signature says a name is present, so a null is a defect in this codebase rather
        // than a field a caller corrects by editing a request. It is refused ahead of ValidateOrThrow
        // and as an ArgumentNullException, not as the ValidationException that becomes a 400 about a
        // member the request may not even have.

        // Act
        ArgumentNullException? caught = null;
        try
        {
            Account.Create(
                Guid.CreateVersion7(), Guid.CreateVersion7(), null!, AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());
        }
        catch (ArgumentNullException exception)
        {
            caught = exception;
        }

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.ParamName).IsEqualTo("name");
    }

    [Test]
    public async Task Update_ReplacesBothHalvesOfTheNameTogether()
    {
        // Arrange — an account created under one label, updated to another. The two labels are what
        // make the halves distinguishable: SealedNarrative derives both from the same text, so
        // "Savings" produces an envelope and an index that neither matches "Checking".
        Account account = Account.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Checking"),
            AccountType.Checking,
            0m,
            "USD",
            UsdMinorUnit,
            UtcNow());

        // Act
        account.Update(SealedNarrative.Indexed("Savings"), AccountType.Savings, 50m, UsdMinorUnit);

        // Assert — THE INDEX IS ASSERTED TO BE THE NEW ONE AND EXPLICITLY NOT THE PREVIOUS ONE, which
        // is the whole of what the IndexedName parameter buys. A member that took a bare NarrativeField
        // would pass the envelope assertion below and fail only this one: the row would hold new
        // ciphertext under the old name's index, and every consequence of that is silent. The uniqueness
        // constraint would go on policing a name the row no longer holds, a search for "Savings" would
        // miss it, a search for "Checking" would return it, and a rename onto a name already taken would
        // be accepted. Nothing reads back wrong and no constraint is violated, because recomputing
        // either half needs the account's index key, which lives in a browser.
        //
        // CollectionOrdering.Matching on the two positive assertions, for the reason
        // Create_WithValidInput_StoresBothHalvesOfTheNameTypeOpeningBalanceAndCreatedAtUtc states. The
        // NEGATIVE one below
        // deliberately keeps the default: CollectionOrdering.Any there means "not even a permutation of
        // the old index", which is the stronger claim and the one worth making.
        await Assert.That(account.Name.Envelope.ToArray())
            .IsEquivalentTo(SealedNarrative.Name("Savings").Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(account.NameKey.ToArray())
            .IsEquivalentTo(SealedNarrative.BlindIndex("Savings").ToArray(), CollectionOrdering.Matching);
        await Assert.That(account.NameKey.ToArray())
            .IsNotEquivalentTo(SealedNarrative.BlindIndex("Checking").ToArray());
        await Assert.That(account.Type).IsEqualTo(AccountType.Savings);
        await Assert.That(account.OpeningBalance).IsEqualTo(50m);
    }

    [Test]
    public async Task Update_WithInvalidInput_ThrowsValidationExceptionAndLeavesAccountUnchanged()
    {
        // Arrange — this case used to be triggered by a blank name, which is no longer a rule this side
        // can state. The claim it was making — a refused update changes NOTHING, not even the members
        // that were acceptable — is untouched by that, so it is triggered by an undefined account type
        // instead: a rule ValidateOrThrow still owns, reached on the same path, after the name has
        // already been handed over.
        Account account = Account.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Checking"),
            AccountType.Checking,
            0m,
            "USD",
            UsdMinorUnit,
            UtcNow());

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            account.Update(SealedNarrative.Indexed("Savings"), (AccountType)999, 50m, UsdMinorUnit));

        // Assert — BOTH HALVES OF THE NAME ARE STILL THE OLD ONES. Update validates before it assigns,
        // and a version that assigned the name first would leave a row whose name says "Savings" and
        // whose type, balance and index say otherwise.
        //
        // CollectionOrdering.Matching for the reason stated on
        // Create_WithValidInput_StoresBothHalvesOfTheNameTypeOpeningBalanceAndCreatedAtUtc.
        await Assert.That(exception.Errors.ContainsKey("Type")).IsTrue();
        await Assert.That(account.Name.Envelope.ToArray())
            .IsEquivalentTo(SealedNarrative.Name("Checking").Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(account.NameKey.ToArray())
            .IsEquivalentTo(SealedNarrative.BlindIndex("Checking").ToArray(), CollectionOrdering.Matching);
        await Assert.That(account.Type).IsEqualTo(AccountType.Checking);
        await Assert.That(account.OpeningBalance).IsEqualTo(0m);
    }

    [Test]
    [Arguments(2, "1.234")]
    [Arguments(0, "10.5")]
    [Arguments(3, "10.0005")]
    public async Task Update_WithMoreDecimalPlacesThanTheMinorUnitAllows_ThrowsValidationException(
        int minorUnit,
        string openingBalance)
    {
        // Arrange — Update is the path that had no currency at all until now: it re-validated
        // against the account's stored CurrencyCode while rounding to a hard-coded 2.
        Account account = NewAccount("Cash");

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            account.Update(SealedNarrative.Indexed("Cash"), AccountType.Checking, Money(openingBalance), minorUnit));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("OpeningBalance")).IsTrue();
        await Assert.That(account.OpeningBalance).IsEqualTo(0m);
    }

    [Test]
    [Arguments(2, "10.99")]
    [Arguments(0, "10")]
    [Arguments(3, "10.005")]
    [Arguments(4, "10.0005")]
    public async Task Update_WithDecimalPlacesTheMinorUnitAllows_ReplacesOpeningBalance(
        int minorUnit,
        string openingBalance)
    {
        // Arrange
        Account account = NewAccount("Cash");
        decimal expected = Money(openingBalance);

        // Act
        account.Update(SealedNarrative.Indexed("Cash"), AccountType.Checking, expected, minorUnit);

        // Assert
        await Assert.That(account.OpeningBalance).IsEqualTo(expected);
    }

    [Test]
    [Arguments("1000000000.01")]
    [Arguments("-1000000000.01")]
    [Arguments("2000000000")]
    public async Task Update_WithOpeningBalanceBeyondTheMagnitudeLimit_ThrowsValidationException(
        string openingBalance)
    {
        // Arrange — same regression guard as on Create: the magnitude branch must survive the
        // rewrite of the decimal branch it shares an else-if chain with.
        Account account = NewAccount("Checking");

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            account.Update(SealedNarrative.Indexed("Checking"), AccountType.Checking, Money(openingBalance), UsdMinorUnit));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("OpeningBalance")).IsTrue();
        await Assert.That(account.OpeningBalance).IsEqualTo(0m);
    }

    [Test]
    [Arguments("1000000000")]
    [Arguments("-1000000000")]
    public async Task Update_WithOpeningBalanceExactlyAtTheMagnitudeLimit_ReplacesOpeningBalance(
        string openingBalance)
    {
        // Arrange
        Account account = NewAccount("Checking");
        decimal expected = Money(openingBalance);

        // Act
        account.Update(SealedNarrative.Indexed("Checking"), AccountType.Checking, expected, UsdMinorUnit);

        // Assert
        await Assert.That(account.OpeningBalance).IsEqualTo(expected);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(5)]
    public async Task Update_WithMinorUnitOutsideTheSupportedRange_ThrowsArgumentOutOfRangeException(
        int minorUnit)
    {
        // Arrange
        Account account = NewAccount("Checking");

        // Act — a programmer error, not user input, for the same reason as on Create.
        ArgumentOutOfRangeException exception = ThrowsArgumentOutOfRangeException(() =>
            account.Update(SealedNarrative.Indexed("Checking"), AccountType.Checking, 0m, minorUnit));

        // Assert
        await Assert.That(exception.ParamName).IsEqualTo("minorUnit");
    }

    [Test]
    public async Task Update_WithoutAName_ThrowsArgumentNullException()
    {
        // Arrange — Create's argument, restated on the other write path: half a name has no spelling
        // this signature accepts, and neither does no name at all.
        Account account = NewAccount("Checking");

        // Act
        ArgumentNullException? caught = null;
        try
        {
            account.Update(null!, AccountType.Savings, 50m, UsdMinorUnit);
        }
        catch (ArgumentNullException exception)
        {
            caught = exception;
        }

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.ParamName).IsEqualTo("name");
    }

    [Test]
    public async Task Create_StoresNormalizedCurrencyCode()
    {
        Account account = Account.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Checking"),
            AccountType.Checking,
            0m,
            " usd ",
            UsdMinorUnit,
            UtcNow());

        await Assert.That(account.CurrencyCode).IsEqualTo("USD");
    }

    [Arguments("")]
    [Arguments("US")]
    [Arguments("USDE")]
    [Arguments("1$2")]
    [Test]
    public async Task Create_WithInvalidCurrencyCode_ThrowsValidationException(string currencyCode)
    {
        ValidationException exception = ThrowsValidationException(() => Account.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SealedNarrative.Indexed("Checking"),
            AccountType.Checking,
            0m,
            currencyCode,
            UsdMinorUnit,
            UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("CurrencyCode")).IsTrue();
    }

    [Test]
    public async Task Update_DoesNotChangeCurrencyCode()
    {
        Account account = NewAccount("Checking");

        account.Update(SealedNarrative.Indexed("Savings"), AccountType.Savings, 50m, UsdMinorUnit);

        await Assert.That(account.CurrencyCode).IsEqualTo("USD");
    }

    /// <summary>
    /// A content-key rotation replaces both halves of the name with the pair sealed under the new keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two labels model one name under two keys, not two names.</b> A rotation re-encrypts the
    /// text the row already holds, so nothing about the account changes except the bytes — but
    /// <see cref="SealedNarrative" /> derives both halves from the label it is handed, so "the same text
    /// under a new key" has no other spelling here than a second label.
    /// </para>
    /// <para>
    /// <b>The index is asserted to have moved too, and that is the reason the parameter is an
    /// <see cref="IndexedName" />.</b> The index key rotates alongside the content key, so a reseal that
    /// replaced the envelope alone would leave the row's name sealed under the new content key and keyed
    /// under the old index one — the same half-written name
    /// <see cref="Update_ReplacesBothHalvesOfTheNameTogether" /> argues about, arriving by a path nobody
    /// is watching, on every row of the account at once.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Reseal_ReplacesBothHalvesOfTheName()
    {
        // Arrange
        Account account = NewAccount("Checking");

        // Act
        account.Reseal(SealedNarrative.Indexed("Checking resealed"), Guid.CreateVersion7());

        // Assert — CollectionOrdering.Matching on the positive assertions for the reason
        // Create_WithValidInput_StoresBothHalvesOfTheNameTypeOpeningBalanceAndCreatedAtUtc states; the
        // negative one keeps the default, which is the stronger "not even a permutation" claim.
        await Assert.That(account.Name.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Checking resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(account.NameKey.ToArray()).IsEquivalentTo(
            SealedNarrative.BlindIndex("Checking resealed").ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(account.NameKey.ToArray())
            .IsNotEquivalentTo(SealedNarrative.BlindIndex("Checking").ToArray());
    }

    /// <summary>
    /// A reseal stamps the row with the id of the rotation that rewrote it.
    /// </summary>
    /// <remarks>
    /// The stamp is the whole reason the column exists, argued at <c>Budget.RotationId</c>: a
    /// re-sealed envelope and an untouched one are byte-for-byte indistinguishable to a server holding
    /// no key, so the completion step — which destroys the only copies of the old keys — can only know
    /// the rewrite finished by being told, in the same transaction as the ciphertext. A reseal that
    /// replaced the envelopes and left this column null passes every other assertion in this file and
    /// makes the account un-completable: the run is refused for rows that were in fact rotated, and the
    /// person is stuck part-way with no way forward the product offers.
    /// </remarks>
    [Test]
    public async Task Reseal_StampsTheRotationItWasGiven()
    {
        // Arrange — the id minted here and threaded in, so the assertion is not "a stamp appeared" but
        // "this rotation's did". A member that minted its own would leave every row stamped with an id
        // no completion step is looking for.
        Account account = NewAccount("Checking");
        var rotationId = Guid.CreateVersion7();

        // Act
        account.Reseal(SealedNarrative.Indexed("Checking resealed"), rotationId);

        // Assert
        await Assert.That(account.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// A reseal moves the name and the stamp and touches nothing else.
    /// </summary>
    /// <remarks>
    /// <b>This is the case that catches a reseal built by reusing <see cref="Account.Update" />.</b>
    /// Update takes a type, a balance and a minor unit; a reseal that delegated to it would have to
    /// invent values for all three, and the obvious inventions are the entity's own current values —
    /// which looks correct and is, right up to the account whose type or balance a second request
    /// changed between the read and the reseal. A rotation is a re-encryption and nothing else: the
    /// arithmetic columns are values this server reads, and no rotation has any business writing them.
    /// The identity columns are stronger still — <see cref="Account.Id" /> is the associated data every
    /// envelope this row has ever held was sealed against, so a reseal that re-minted it would produce
    /// a name nobody can ever open.
    /// </remarks>
    [Test]
    public async Task Reseal_LeavesTypeOpeningBalanceCurrencyCodeAndIdentityUnchanged()
    {
        // Arrange — every non-narrative column set to something other than its default, so a reseal
        // that overwrote one with a default is visible rather than accidentally right.
        var id = Guid.CreateVersion7();
        var budgetId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();
        Account account = Account.Create(
            id,
            budgetId,
            SealedNarrative.Indexed("Checking"),
            AccountType.CreditCard,
            Money("123.45"),
            "USD",
            UsdMinorUnit,
            createdAtUtc);

        // Act
        account.Reseal(SealedNarrative.Indexed("Checking resealed"), Guid.CreateVersion7());

        // Assert
        await Assert.That(account.Id).IsEqualTo(id);
        await Assert.That(account.BudgetId).IsEqualTo(budgetId);
        await Assert.That(account.Type).IsEqualTo(AccountType.CreditCard);
        await Assert.That(account.OpeningBalance).IsEqualTo(Money("123.45"));
        await Assert.That(account.CurrencyCode).IsEqualTo("USD");
        await Assert.That(account.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    /// <summary>
    /// An ordinary rename clears the stamp a rotation left on the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the rule the whole stamp rests on, and it is the easiest one to leave out</b> —
    /// nothing about <see cref="Account.Update" /> reads as being part of a rotation, so a reader
    /// implementing the reseal members has no reason to open this one.
    /// </para>
    /// <para>
    /// <b>What goes wrong without it.</b> A second browser tab still holding the OLD content key can
    /// rename an account this rotation has already stamped. It writes old-key ciphertext, and — with
    /// this line missing — it does not touch the stamp, so the row ends up carrying old-key ciphertext
    /// under a current stamp. Completion then reads a full house, promotes the new keys and destroys the
    /// old ones, and that account's name is gone: no constraint violated, nothing red, and the symptom
    /// is a screen that will not decrypt. Clearing the stamp is what makes completion refuse instead,
    /// which is a run the person can retry.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Update_ClearsTheRotationStamp()
    {
        // Arrange — stamped through the real reseal path rather than reflected in, so the case
        // describes the sequence that actually happens: a chunk rewrites the row, then a stale tab
        // renames it.
        Account account = NewAccount("Checking");
        account.Reseal(SealedNarrative.Indexed("Checking resealed"), Guid.CreateVersion7());

        // Act
        account.Update(SealedNarrative.Indexed("Savings"), AccountType.Savings, 50m, UsdMinorUnit);

        // Assert
        await Assert.That(account.RotationId).IsNull();
    }

    /// <summary>
    /// A refused update leaves the stamp where it was.
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="Update_ClearsTheRotationStamp" />, and the reason the clearing cannot
    /// be a line at the top of <see cref="Account.Update" />: a refused update wrote no ciphertext, so
    /// there is nothing to disown. Cleared anyway, a rotation would be failed by edits that never
    /// landed — a completion step that refuses a run the person cannot see anything wrong with, and the
    /// only remedy on offer is to re-rotate the whole account.
    /// </remarks>
    [Test]
    public async Task Update_WithInvalidInput_LeavesTheRotationStampWhereItWas()
    {
        // Arrange — refused by an undefined account type, the rule Update still owns, for the reason
        // Update_WithInvalidInput_ThrowsValidationExceptionAndLeavesAccountUnchanged gives.
        Account account = NewAccount("Checking");
        var rotationId = Guid.CreateVersion7();
        account.Reseal(SealedNarrative.Indexed("Checking resealed"), rotationId);

        // Act
        ThrowsValidationException(() =>
            account.Update(SealedNarrative.Indexed("Savings"), (AccountType)999, 50m, UsdMinorUnit));

        // Assert
        await Assert.That(account.RotationId).IsEqualTo(rotationId);
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
    /// Whether that promotes the wrong generation or refuses forever depends on a lookup nothing in this
    /// entity can see, which is the reason the value is refused at the point it is written rather than
    /// argued about at the point it is read.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Reseal_WithTheEmptyRotationId_IsRefused()
    {
        // Arrange
        Account account = NewAccount("Checking");

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            account.Reseal(SealedNarrative.Indexed("Checking resealed"), Guid.Empty));

        // Assert — keyed on the member the stamp lands in, as KeyRotation.Begin keys its own. Both
        // halves of the name are read back too, so a member that assigned before it judged reddens here
        // rather than leaving a row rewritten under a rotation that does not exist.
        await Assert.That(exception.Errors.ContainsKey(nameof(Account.RotationId))).IsTrue();
        await Assert.That(account.Name.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Checking").Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(account.RotationId).IsNull();
    }

    /// <summary>
    /// A reseal handed no name at all is refused as an argument fault, not as a field error.
    /// </summary>
    /// <remarks>
    /// <b>The exception type comes from the sibling mutators, not from this file.</b>
    /// <see cref="Account.Create" /> and <see cref="Account.Update" /> both guard this parameter with
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
        Account account = NewAccount("Checking");

        // Act — the inline shape Create_WithoutAName_ThrowsArgumentNullException uses, restated rather
        // than lifted into a helper, so this case reads without a detour.
        ArgumentNullException? caught = null;
        try
        {
            account.Reseal(null!, Guid.CreateVersion7());
        }
        catch (ArgumentNullException exception)
        {
            caught = exception;
        }

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.ParamName).IsEqualTo("name");
    }

    /// <summary>
    /// A chunk re-sent under the rotation id it already carried is accepted, and leaves the row sealed
    /// and stamped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A re-sent chunk is the ordinary case and not an anomaly.</b> A rotation is cut into chunks
    /// precisely because an account can hold more rows than one request should carry, and every chunk of
    /// one run quotes the same rotation id — that is the whole of what the id is for. So a request that
    /// timed out on the wire, a retry, or a client that never saw the response sends the same rows under
    /// the same id again, and on this side the second arrival is indistinguishable from the first.
    /// </para>
    /// <para>
    /// <b>What this case is here to refuse.</b> A member that additionally rejected a rotation id equal
    /// to the stamp the row already carries reads as sensible idempotence protection and passes every
    /// other case in this file, because they all mint a fresh
    /// <see cref="Guid.CreateVersion7()" />. It would fail exactly the long runs that need chunking most
    /// — the retry, on the accounts with the most rows — and it would protect against nothing, because a
    /// reseal is a whole-value write: applying the same chunk twice lands the same bytes and the same
    /// stamp.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Reseal_RepeatedUnderTheSameRotationId_IsAccepted()
    {
        // Arrange — the chunk that landed, and the id its run is quoting throughout.
        Account account = NewAccount("Checking");
        var rotationId = Guid.CreateVersion7();
        account.Reseal(SealedNarrative.Indexed("Checking resealed"), rotationId);

        // Act — the same chunk again, under the same id, as a re-sent request carries it.
        account.Reseal(SealedNarrative.Indexed("Checking resealed"), rotationId);

        // Assert — both halves of the name still sealed under the new key, and the stamp still standing,
        // so a member that refused would redden on the call and one that cleared on a repeat would
        // redden here.
        await Assert.That(account.Name.Envelope.ToArray()).IsEquivalentTo(
            SealedNarrative.Name("Checking resealed").Envelope.ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(account.NameKey.ToArray()).IsEquivalentTo(
            SealedNarrative.BlindIndex("Checking resealed").ToArray(),
            CollectionOrdering.Matching);
        await Assert.That(account.RotationId).IsEqualTo(rotationId);
    }

    /// <summary>
    /// A USD account at zero, for the cases whose subject is the balance or the minor unit rather than
    /// the identifier or the name.
    /// </summary>
    private static Account NewAccount(string label) => Account.Create(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        SealedNarrative.Indexed(label),
        AccountType.Checking,
        0m,
        "USD",
        UsdMinorUnit,
        UtcNow());

    private static DateTime UtcNow() => new(2026, 6, 25, 13, 14, 15, DateTimeKind.Utc);

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
