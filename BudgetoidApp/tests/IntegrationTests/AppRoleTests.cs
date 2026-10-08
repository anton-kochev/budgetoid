using Infrastructure.Persistence.Provisioning;

namespace IntegrationTests;

/// <summary>
/// Pins the one way a role other than <c>budgetoid_app</c> reaches the provisioning code: a name that
/// cannot carry SQL, rendered into the grants script by whole word, with production's rendering
/// byte-identical to the script as written.
/// </summary>
public sealed class AppRoleTests
{
    private const string Script =
        """
        IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'budgetoid_app') THEN
            CREATE ROLE budgetoid_app WITH LOGIN;
        GRANT SELECT ON currencies TO budgetoid_app;
        -- budgetoid_app_renamed_away and xbudgetoid_app are other names, not this role.
        """;

    [Test]
    public async Task Production_IsTheRoleTheApplicationConnectsAs() =>
        await Assert.That(AppRole.Production.Name).IsEqualTo(DatabaseProvisioning.AppRoleName);

    [Test]
    public async Task Render_ForProduction_IsTheEmbeddedScriptByteForByte()
    {
        // Arrange
        string script = await DatabaseProvisioning.ReadGrantsScriptAsync(CancellationToken.None);

        // Act
        string rendered = AppRole.Production.Render(script);

        // Assert
        await Assert.That(rendered).IsEqualTo(script);
    }

    [Test]
    public async Task Render_ReplacesTheRoleOnlyWhereItIsAWholeWord()
    {
        // Act
        string rendered = AppRole.For("bt_0a1b2c3d_app_7").Render(Script);

        // Assert
        await Assert.That(rendered).IsEqualTo(
            """
            IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'bt_0a1b2c3d_app_7') THEN
                CREATE ROLE bt_0a1b2c3d_app_7 WITH LOGIN;
            GRANT SELECT ON currencies TO bt_0a1b2c3d_app_7;
            -- budgetoid_app_renamed_away and xbudgetoid_app are other names, not this role.
            """);
    }

    [Test]
    public async Task Render_LeavesNoTraceOfTheProductionRole()
    {
        // Arrange
        string script = await DatabaseProvisioning.ReadGrantsScriptAsync(CancellationToken.None);

        // Act
        string rendered = AppRole.For("bt_0a1b2c3d_app_7").Render(script);

        // Assert — whole words only; the regex is the same rule Render applies, so a miss here is a
        // spelling of the role Render cannot see, such as one inside a longer identifier.
        await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(rendered, @"\bbudgetoid_app\b"))
            .IsFalse();
    }

    [Test]
    [Arguments("")]
    [Arguments("Budgetoid_app")]
    [Arguments("1app")]
    [Arguments("app; drop role postgres")]
    [Arguments("app'")]
    [Arguments("app\"")]
    [Arguments("app-role")]
    [Arguments("app\n")]
    [Arguments("a234567890123456789012345678901234567890123456789012345678901234")]
    public async Task For_RefusesANameThatIsNotAPlainLowercaseIdentifier(string name) =>
        await Assert.That(() => AppRole.For(name)).Throws<ArgumentException>();

    [Test]
    [Arguments("budgetoid_app")]
    [Arguments("_x")]
    [Arguments("a23456789012345678901234567890123456789012345678901234567890123")]
    public async Task For_AcceptsAPlainLowercaseIdentifier(string name) =>
        await Assert.That(AppRole.For(name).Name).IsEqualTo(name);
}
