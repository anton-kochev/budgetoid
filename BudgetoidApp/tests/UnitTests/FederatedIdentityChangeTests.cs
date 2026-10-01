using Domain.Common;
using Domain.Users;

namespace UnitTests;

public sealed class FederatedIdentityChangeTests
{
    private const string Subject = "google-subject";
    private const string Address = "person@example.com";

    [Test]
    public async Task Decide_SameSubjectSameAddress_ChangesNothing()
    {
        // Arrange
        User user = User.CreateWithId(Guid.CreateVersion7(), Address, UtcNow());
        Credential current = Credential.CreateFederated(user.Id, Credential.GoogleProvider, Subject, UtcNow());

        // Act
        FederatedIdentityChange change = FederatedIdentityChange.Decide(user, current, Subject, Address, Later());

        // Assert
        await Assert.That(change.IsNoChange).IsTrue();
        await Assert.That(change.Retired).IsNull();
        await Assert.That(change.Filed).IsNull();
        await Assert.That(change.Email.Value).IsEqualTo(Address);
    }

    [Test]
    public async Task Decide_SameSubjectNewAddress_ChangesTheAddressAndRetiresNothing()
    {
        // Arrange
        User user = User.CreateWithId(Guid.CreateVersion7(), Address, UtcNow());
        Credential current = Credential.CreateFederated(user.Id, Credential.GoogleProvider, Subject, UtcNow());

        // Act
        FederatedIdentityChange change = FederatedIdentityChange.Decide(
            user, current, Subject, " moved@example.com ", Later());

        // Assert
        await Assert.That(change.IsNoChange).IsFalse();
        await Assert.That(change.Retired).IsNull();
        await Assert.That(change.Filed).IsNull();
        await Assert.That(change.Email.Value).IsEqualTo("moved@example.com");

        // A decision, not an act: the account keeps its address until the caller applies it.
        await Assert.That(user.Email.Value).IsEqualTo(Address);
    }

    [Test]
    public async Task Decide_AddressDifferingOnlyInCase_IsAnAddressChange()
    {
        // Arrange — the comparison is ordinal. The unique index's case-insensitive collation is a
        // wider rule about who else may hold the address, not about whether this one changed.
        User user = User.CreateWithId(Guid.CreateVersion7(), Address, UtcNow());
        Credential current = Credential.CreateFederated(user.Id, Credential.GoogleProvider, Subject, UtcNow());

        // Act
        FederatedIdentityChange change = FederatedIdentityChange.Decide(
            user, current, Subject, "Person@Example.com", Later());

        // Assert
        await Assert.That(change.IsNoChange).IsFalse();
        await Assert.That(change.Retired).IsNull();
        await Assert.That(change.Filed).IsNull();
        await Assert.That(change.Email.Value).IsEqualTo("Person@Example.com");
    }

    [Test]
    public async Task Decide_NewSubject_RetiresTheCurrentAndFilesTheReplacementUnderTheSameUser()
    {
        // Arrange — credentials carry no UPDATE grant, so a new subject is one row retired and one
        // row filed, never a subject rewritten in place.
        User user = User.CreateWithId(Guid.CreateVersion7(), Address, UtcNow());
        Credential current = Credential.CreateFederated(user.Id, Credential.GoogleProvider, Subject, UtcNow());
        DateTime nowUtc = Later();

        // Act
        FederatedIdentityChange change = FederatedIdentityChange.Decide(
            user, current, " another-google-subject ", "moved@example.com", nowUtc);

        // Assert
        await Assert.That(change.IsNoChange).IsFalse();
        await Assert.That(ReferenceEquals(change.Retired, current)).IsTrue();

        Credential? filed = change.Filed;
        await Assert.That(filed).IsNotNull();
        await Assert.That(filed!.Type).IsEqualTo(CredentialType.Federated);
        await Assert.That(filed.Provider).IsEqualTo(Credential.GoogleProvider);
        await Assert.That(filed.Subject).IsEqualTo("another-google-subject");
        await Assert.That(filed.Subject).IsNotEqualTo(user.Id.ToString());
        await Assert.That(filed.UserId).IsEqualTo(user.Id);
        await Assert.That(filed.Id).IsNotEqualTo(current.Id);
        await Assert.That(filed.CreatedAtUtc).IsEqualTo(nowUtc);
        await Assert.That(change.Email.Value).IsEqualTo("moved@example.com");

        // A decision, not an act: the account keeps its address until the caller applies it.
        await Assert.That(user.Email.Value).IsEqualTo(Address);
    }

    [Test]
    public async Task Decide_NewSubjectSameAddress_RetiresAndFilesWithoutChangingTheAddress()
    {
        // Arrange
        User user = User.CreateWithId(Guid.CreateVersion7(), Address, UtcNow());
        Credential current = Credential.CreateFederated(user.Id, Credential.GoogleProvider, Subject, UtcNow());

        // Act
        FederatedIdentityChange change = FederatedIdentityChange.Decide(
            user, current, "another-google-subject", Address, Later());

        // Assert
        await Assert.That(change.IsNoChange).IsFalse();
        await Assert.That(ReferenceEquals(change.Retired, current)).IsTrue();
        await Assert.That(change.Filed).IsNotNull();
        await Assert.That(change.Filed!.Subject).IsEqualTo("another-google-subject");
        await Assert.That(change.Email.Value).IsEqualTo(Address);
    }

    [Test]
    public async Task Decide_SubjectDifferingOnlyInCase_IsANewSubject()
    {
        // Arrange — the sub claim is case-sensitive, so a case-only difference is another Google
        // identity, and the discovery lookup matches the column ordinally too.
        User user = User.CreateWithId(Guid.CreateVersion7(), Address, UtcNow());
        Credential current = Credential.CreateFederated(user.Id, Credential.GoogleProvider, Subject, UtcNow());

        // Act
        FederatedIdentityChange change = FederatedIdentityChange.Decide(
            user, current, "Google-Subject", Address, Later());

        // Assert
        await Assert.That(change.IsNoChange).IsFalse();
        await Assert.That(ReferenceEquals(change.Retired, current)).IsTrue();
        await Assert.That(change.Filed).IsNotNull();
        await Assert.That(change.Filed!.Subject).IsEqualTo("Google-Subject");
        await Assert.That(change.Filed.UserId).IsEqualTo(user.Id);
    }

    [Test]
    public async Task Decide_SubjectWithSurroundingWhitespace_IsComparedAfterTrim()
    {
        // Arrange — CreateFederated stored the trimmed subject, so the untrimmed assertion of the
        // same subject is the same identity.
        User user = User.CreateWithId(Guid.CreateVersion7(), Address, UtcNow());
        Credential current = Credential.CreateFederated(user.Id, Credential.GoogleProvider, Subject, UtcNow());

        // Act
        FederatedIdentityChange change = FederatedIdentityChange.Decide(
            user, current, $"  {Subject}\t", Address, Later());

        // Assert
        await Assert.That(change.IsNoChange).IsTrue();
        await Assert.That(change.Retired).IsNull();
        await Assert.That(change.Filed).IsNull();
    }

    [Test]
    public async Task Decide_SameSubjectWithTheStoredAddressInSurroundingWhitespace_ChangesNothing()
    {
        // Arrange — the address is compared once normalised, as it would be stored, never raw.
        User user = User.CreateWithId(Guid.CreateVersion7(), Address, UtcNow());
        Credential current = Credential.CreateFederated(user.Id, Credential.GoogleProvider, Subject, UtcNow());

        // Act
        FederatedIdentityChange change = FederatedIdentityChange.Decide(
            user, current, Subject, " " + Address + " ", Later());

        // Assert
        await Assert.That(change.IsNoChange).IsTrue();
        await Assert.That(change.Email.Value).IsEqualTo(Address);
    }

    [Test]
    public async Task Decide_WithABlankAddress_Throws()
    {
        // Arrange
        User user = User.CreateWithId(Guid.CreateVersion7(), Address, UtcNow());
        Credential current = Credential.CreateFederated(user.Id, Credential.GoogleProvider, Subject, UtcNow());

        // Act — refused, never answered with the stored address in its place.
        Exception refusal = Refusal(
            () => FederatedIdentityChange.Decide(user, current, Subject, "   ", Later()));

        // Assert
        await Assert.That(refusal).IsTypeOf<ValidationException>();
        await Assert.That(((ValidationException)refusal).Errors.ContainsKey("Email")).IsTrue();
    }

    [Test]
    public async Task Decide_WithAnAddressLongerThanTheLimit_Throws()
    {
        // Arrange — Email.MaxLength is 254; this is one past it.
        User user = User.CreateWithId(Guid.CreateVersion7(), Address, UtcNow());
        Credential current = Credential.CreateFederated(user.Id, Credential.GoogleProvider, Subject, UtcNow());
        string overLong = new string('a', Email.MaxLength + 1 - "@example.com".Length) + "@example.com";

        // Act
        Exception refusal = Refusal(
            () => FederatedIdentityChange.Decide(user, current, Subject, overLong, Later()));

        // Assert
        await Assert.That(overLong.Length).IsEqualTo(255);
        await Assert.That(refusal).IsTypeOf<ValidationException>();
        await Assert.That(((ValidationException)refusal).Errors.ContainsKey("Email")).IsTrue();
    }

    [Test]
    public async Task Decide_WhenCurrentIsNotFederated_Throws()
    {
        // Arrange
        User user = User.CreateWithId(Guid.CreateVersion7(), Address, UtcNow());
        Credential passkey = Credential.CreatePasskey(user.Id, UtcNow());

        // Act
        Exception refusal = Refusal(
            () => FederatedIdentityChange.Decide(user, passkey, Subject, Address, Later()));

        // Assert
        await Assert.That(refusal).IsTypeOf<ArgumentException>();
        await Assert.That(((ArgumentException)refusal).ParamName).IsEqualTo("current");
    }

    [Test]
    public async Task Decide_WhenCurrentBelongsToAnotherUser_Throws()
    {
        // Arrange
        User user = User.CreateWithId(Guid.CreateVersion7(), Address, UtcNow());
        Credential someoneElses = Credential.CreateFederated(
            Guid.CreateVersion7(), Credential.GoogleProvider, Subject, UtcNow());

        // Act
        Exception refusal = Refusal(
            () => FederatedIdentityChange.Decide(user, someoneElses, Subject, Address, Later()));

        // Assert
        await Assert.That(refusal).IsTypeOf<ArgumentException>();
        await Assert.That(((ArgumentException)refusal).ParamName).IsEqualTo("current");
    }

    private static DateTime UtcNow() => new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private static DateTime Later() => new(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);

    private static Exception Refusal(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected a refusal.");
    }
}
