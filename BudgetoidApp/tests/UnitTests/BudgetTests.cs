using Domain.Budgets;
using Domain.Common;
using Domain.Security;
using TestSupport;

namespace UnitTests;

public sealed class BudgetTests
{
    [Test]
    public async Task Create_UsesTheIdItWasGiven()
    {
        // Arrange — an id minted here and threaded in, so the assertion is not "an id came back" but
        // "this one did".
        var id = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        NarrativeField name = SealedNarrative.Name("Household");

        // Act
        Budget budget = Budget.Create(id, userId, name, UtcNow());

        // Assert — the identifier is the associated data the client sealed the name against, so a
        // factory that ignored this parameter and minted its own would satisfy every other case here
        // and produce a row whose name nobody can ever open: no constraint violated, nothing red, and
        // the symptom arriving months later as text that will not decrypt. Guid.CreateVersion7 has
        // left the production file for that reason, and this is the case that would notice it coming
        // back.
        await Assert.That(budget.Id).IsEqualTo(id);
    }

    [Test]
    public async Task Create_WithValidInput_ReturnsBudgetWithTheSealedNameAndNoBaseCurrency()
    {
        // Arrange
        var id = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        NarrativeField name = SealedNarrative.Name("Household");
        DateTime createdAtUtc = new(2026, 7, 25, 9, 30, 0, DateTimeKind.Utc);

        // Act
        Budget budget = Budget.Create(id, userId, name, createdAtUtc);

        // Assert — the name is compared as the bytes it carries rather than by reference, so a factory
        // that stored a different envelope of the same shape is caught. NarrativeField declares no
        // Equals of its own, and a reference comparison here would pass for any instance the factory
        // happened to hold on to.
        await Assert.That(budget.UserId).IsEqualTo(userId);
        await Assert.That(budget.Name).IsNotNull();
        await Assert.That(budget.Name!.Envelope.ToArray()).IsEquivalentTo(name.Envelope.ToArray());
        await Assert.That(budget.BaseCurrencyCode).IsNull();
        await Assert.That(budget.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithEmptyId_ThrowsValidationExceptionForId()
    {
        // Act — the empty Guid became reachable the day the identifier stopped being minted here: it
        // is what a caller that threaded a default through hands over.
        ValidationException exception = ThrowsValidationException(() =>
            Budget.Create(Guid.Empty, Guid.CreateVersion7(), SealedNarrative.Name("Household"), UtcNow()));

        // Assert — keyed on the id, not merely thrown. Left to the primary key instead, all-zero is a
        // legal uuid: the first such row stores and the second collides under a constraint name that
        // says nothing about the caller that never chose an id at all.
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
    }

    [Test]
    public async Task Create_WithEmptyUserId_ThrowsValidationExceptionForUserId()
    {
        // Act
        ValidationException exception = ThrowsValidationException(() =>
            Budget.Create(Guid.CreateVersion7(), Guid.Empty, SealedNarrative.Name("Household"), UtcNow()));

        // Assert — the one rule that survived the column becoming ciphertext, asserted so that the
        // rules which did not cannot take it with them.
        await Assert.That(exception.Errors.ContainsKey("UserId")).IsTrue();
    }

    [Test]
    public async Task Create_WithABlankName_IsAccepted()
    {
        // Arrange — the shortest envelope the format can produce: a version, a nonce and a tag over an
        // empty plaintext. This is exactly what a client sealing a blank name, or a name of nothing but
        // spaces, sends.
        var id = Guid.CreateVersion7();
        NarrativeField blank = SealedNarrative.Name();

        // Act
        Budget budget = Budget.Create(id, Guid.CreateVersion7(), blank, UtcNow());

        // Assert — this server used to refuse a blank name with a ValidationException keyed on Name,
        // and it no longer can. The capability moved to the client, which is the only side that holds a
        // key; it was not quietly dropped. THE SERVER CANNOT MEASURE CHARACTERS IN AN ENVELOPE AND
        // NEVER WILL — it holds ciphertext over text it has never seen, so "is this just whitespace?"
        // is a question about a plaintext that exists nowhere on this side. A future reader who reads
        // the absence as an oversight and restores the check has nothing to check it against; this case
        // is what stops that edit.
        await Assert.That(budget.Name).IsNotNull();
        await Assert.That(budget.Name!.Envelope.Length).IsEqualTo(CiphertextEnvelope.MinimumLength);
    }

    [Test]
    public async Task Create_WithANameFarPastTheOldCharacterLimit_IsAccepted()
    {
        // Arrange — an envelope well past the two hundred characters this factory used to refuse, and
        // well under NarrativeFieldLimits.NameBytes, which is the only ceiling left and is measured in
        // stored bytes by NarrativeField before the value ever reaches Budget.
        var id = Guid.CreateVersion7();
        NarrativeField longName = SealedNarrative.Name(new string('x', 400));

        // Act
        Budget budget = Budget.Create(id, Guid.CreateVersion7(), longName, UtcNow());

        // Assert — the same relocation as the blank case, in the other direction. A length rule here
        // would be a rule about characters, and the server counts none: AES-GCM ciphertext is exactly
        // as long as its plaintext, but the framing and the encoding sit on top of it, so bytes stored
        // and characters typed are different questions and only the first is answerable here.
        await Assert.That(budget.Name).IsNotNull();
        await Assert.That(budget.Name!.Envelope.Length).IsGreaterThan(200);
    }

    [Test]
    public async Task CreateDefault_LeavesTheBudgetWithoutAName()
    {
        // Arrange
        var id = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        DateTime createdAtUtc = UtcNow();

        // Act
        Budget budget = Budget.CreateDefault(id, userId, createdAtUtc);

        // Assert — the budget a user never asked for carries no name at all. The string a client
        // shows for it is presentation, so it lives in the client; storing a literal here would put
        // a display decision in the database and give provisioning a constant that can drift — and it
        // is now unwritable besides, since nothing on this side can seal one.
        await Assert.That(budget.Name).IsNull();
        await Assert.That(budget.Id).IsEqualTo(id);
        await Assert.That(budget.UserId).IsEqualTo(userId);
        await Assert.That(budget.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task CreateDefault_WithEmptyUserId_ThrowsValidationExceptionForUserId()
    {
        // Act — pins the check that has to survive CreateDefault no longer delegating to Create.
        // An ownerless budget is still nonsense, and dropping this guard would push a null user_id
        // down to a foreign key violation.
        ValidationException exception = ThrowsValidationException(() =>
            Budget.CreateDefault(Guid.CreateVersion7(), Guid.Empty, UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("UserId")).IsTrue();
    }

    private static DateTime UtcNow() =>
        new(2026, 7, 25, 9, 30, 0, DateTimeKind.Utc);

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
}
