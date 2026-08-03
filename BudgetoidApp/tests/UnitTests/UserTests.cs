using Domain.Common;
using Domain.Users;

namespace UnitTests;

public sealed class UserTests
{
    [Test]
    public async Task Create_WithValidInput_ReturnsInitializedUser()
    {
        DateTime createdAtUtc = UtcNow();

        User user = User.Create(" person@example.com ", " Person ", createdAtUtc);

        await Assert.That(user.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(user.Email.Value).IsEqualTo("person@example.com");
        await Assert.That(user.DisplayName).IsEqualTo("Person");
        await Assert.That(user.CreatedAtUtc).IsEqualTo(createdAtUtc);
    }

    [Test]
    public async Task Create_WithBlankEmail_ThrowsValidationException()
    {
        ValidationException exception = ThrowsValidationException(() => User.Create("   ", null, UtcNow()));

        await Assert.That(exception.Errors.ContainsKey("Email")).IsTrue();
    }

    [Test]
    public async Task Create_WithTheMaximumLengthDisplayName_IsAccepted()
    {
        // Arrange
        string displayName = new('d', User.MaxDisplayNameLength);

        // Act
        User user = User.Create("person@example.com", displayName, UtcNow());

        // Assert
        await Assert.That(user.DisplayName?.Length).IsEqualTo(User.MaxDisplayNameLength);
    }

    [Test]
    public async Task Create_WithAnOverLongDisplayName_ThrowsValidationException()
    {
        // Arrange
        string displayName = new('d', User.MaxDisplayNameLength + 1);

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            User.Create("person@example.com", displayName, UtcNow()));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("DisplayName")).IsTrue();
    }

    [Test]
    public async Task Create_MeasuresDisplayNameLengthAfterTrimming()
    {
        // Arrange
        string displayName = $"   {new string('d', User.MaxDisplayNameLength)}   ";

        // Act
        User user = User.Create("person@example.com", displayName, UtcNow());

        // Assert
        await Assert.That(user.DisplayName?.Length).IsEqualTo(User.MaxDisplayNameLength);
    }

    [Test]
    public async Task Create_WithAWhitespaceOnlyDisplayName_StoresNull()
    {
        // Arrange, Act — a missing display name is normal (Google need not supply one) and is not an
        // error; only an over-long one is.
        User user = User.Create("person@example.com", "   ", UtcNow());

        // Assert
        await Assert.That(user.DisplayName).IsNull();
    }

    [Test]
    public async Task Create_WithSeveralOverLongFields_ReportsThemAllAtOnce()
    {
        // Arrange — Create was restructured to share its profile validation with UpdateProfile; this
        // is the guard that the restructuring did not turn aggregation into fail-fast.
        string email = new string('a', Email.MaxLength + 1 - "@example.com".Length) + "@example.com";
        string displayName = new('d', User.MaxDisplayNameLength + 1);

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            User.Create(email, displayName, UtcNow()));

        // Assert
        await Assert.That(exception.Errors.Keys.ToArray())
            .IsEquivalentTo(new[] { "Email", "DisplayName" });
    }

    [Test]
    public async Task UpdateProfile_RefreshesEmailAndDisplayName()
    {
        User user = User.Create("old@example.com", "Old", UtcNow());

        user.UpdateProfile(" new@example.com ", " New ");

        await Assert.That(user.Email.Value).IsEqualTo("new@example.com");
        await Assert.That(user.DisplayName).IsEqualTo("New");
    }

    [Test]
    public async Task UpdateProfile_WithAWhitespaceOnlyDisplayName_ClearsIt()
    {
        // Arrange
        User user = User.Create("old@example.com", "Old", UtcNow());

        // Act
        user.UpdateProfile("new@example.com", "   ");

        // Assert
        await Assert.That(user.DisplayName).IsNull();
    }

    [Test]
    public async Task UpdateProfile_WithAnOverLongDisplayName_ThrowsAndLeavesTheEntityUntouched()
    {
        // Arrange — a returning user cannot bypass a bound a new user is held to, and the rejection
        // must not half-apply: the email here is valid and new, so an assign-then-validate order
        // would leave the entity holding it.
        User user = User.Create("old@example.com", "Old", UtcNow());
        string displayName = new('d', User.MaxDisplayNameLength + 1);

        // Act
        ValidationException exception = ThrowsValidationException(() =>
            user.UpdateProfile("new@example.com", displayName));

        // Assert — the handler's old-vs-new change detection reads these two properties, so a
        // half-updated entity would be persisted as a refresh that was never accepted.
        await Assert.That(exception.Errors.ContainsKey("DisplayName")).IsTrue();
        await Assert.That(user.Email.Value).IsEqualTo("old@example.com");
        await Assert.That(user.DisplayName).IsEqualTo("Old");
    }

    [Test]
    public async Task UpdateProfile_WithAnOverLongEmail_ThrowsAndLeavesTheEntityUntouched()
    {
        // Arrange
        User user = User.Create("old@example.com", "Old", UtcNow());
        string email = new string('a', Email.MaxLength + 1 - "@example.com".Length) + "@example.com";

        // Act
        ValidationException exception = ThrowsValidationException(() => user.UpdateProfile(email, "New"));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Email")).IsTrue();
        await Assert.That(user.Email.Value).IsEqualTo("old@example.com");
        await Assert.That(user.DisplayName).IsEqualTo("Old");
    }

    private static DateTime UtcNow() => new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

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
