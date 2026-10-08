using Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// Pins the boot-time refusal of an unconfigured passkey ceremony.
/// </summary>
/// <remarks>
/// Every other host in this suite is handed both values, so without these two tests the guard in
/// <c>Program.cs</c> and the argument checks in <see cref="ConfiguredPasskeyCeremonyPolicy" /> can be
/// deleted with the whole suite green. What the guard buys is where the defect surfaces: a deployment
/// missing either value comes up healthy and fails only when somebody attempts a ceremony — and an
/// empty origin allow-list fails with the one deliberately uninformative 401, so it reads as a working
/// deployment that nobody can sign in to. Boot is the last moment the pipeline is still watching.
/// </remarks>
public sealed class PasskeyConfigurationTests
{
    [Test]
    public async Task AHostWithABlankRelyingPartyId_RefusesToStart()
    {
        // Arrange
        Dictionary<string, string?> blankRelyingPartyId = new()
        {
            [ConfiguredPasskeyCeremonyPolicy.RelyingPartyIdKey] = string.Empty,
        };

        // Act
        Exception? failure = await CaptureStartupFailureAsync(blankRelyingPartyId);

        // Assert — on the sentence naming the key, not merely on something having thrown. A host that
        // fell over for an unrelated reason would satisfy a bare "it threw" and say nothing about this
        // guard.
        await Assert.That(failure).IsNotNull();
        await Assert.That(NamesTheKey(failure, ConfiguredPasskeyCeremonyPolicy.RelyingPartyIdKey)).IsTrue();
    }

    [Test]
    public async Task AHostWithAnEmptyAllowedOriginsList_RefusesToStart()
    {
        // Arrange — the key present and holding no entries, which is the shape a deployment that
        // declared the setting and listed nothing under it produces.
        Dictionary<string, string?> emptyAllowedOrigins = new()
        {
            [ConfiguredPasskeyCeremonyPolicy.AllowedOriginsKey] = null,
        };

        // Act
        Exception? failure = await CaptureStartupFailureAsync(emptyAllowedOrigins);

        // Assert
        await Assert.That(failure).IsNotNull();
        await Assert.That(NamesTheKey(failure, ConfiguredPasskeyCeremonyPolicy.AllowedOriginsKey)).IsTrue();
    }

    [Test]
    public async Task AHostWithAnOriginThatIsNotAnAbsoluteUri_RefusesToStart()
    {
        // Arrange — a bare host name, the shape somebody types when they think of an origin as a
        // domain rather than as a URL.
        const string origin = "budgetoid.app";

        // Act
        Exception? failure = await CaptureStartupFailureAsync(CeremonyConfiguration("budgetoid.app", origin));

        // Assert
        await Assert.That(failure).IsNotNull();
        await Assert.That(NamesTheKey(failure, ConfiguredPasskeyCeremonyPolicy.AllowedOriginsKey)).IsTrue();
        await Assert.That(NamesTheOffendingValue(failure, origin)).IsTrue();
    }

    [Test]
    public async Task AHostWithATrailingSlashOnAnOrigin_RefusesToStart()
    {
        // Arrange — what a person gets by copying a site address out of a browser, and the form a real
        // deploy is most likely to arrive with. It parses, so only comparing the serialization catches it.
        const string origin = "https://budgetoid.app/";

        // Act
        Exception? failure = await CaptureStartupFailureAsync(CeremonyConfiguration("budgetoid.app", origin));

        // Assert
        await Assert.That(failure).IsNotNull();
        await Assert.That(NamesTheKey(failure, ConfiguredPasskeyCeremonyPolicy.AllowedOriginsKey)).IsTrue();
        await Assert.That(NamesTheOffendingValue(failure, origin)).IsTrue();
    }

    [Test]
    public async Task AHostWithAPathOnAnOrigin_RefusesToStart()
    {
        // Arrange — an address of the sign-in page rather than of the site it lives on. Client data
        // never carries a path, so this matches nothing that will ever arrive.
        const string origin = "https://budgetoid.app/sign-in?from=email#top";

        // Act
        Exception? failure = await CaptureStartupFailureAsync(CeremonyConfiguration("budgetoid.app", origin));

        // Assert
        await Assert.That(failure).IsNotNull();
        await Assert.That(NamesTheKey(failure, ConfiguredPasskeyCeremonyPolicy.AllowedOriginsKey)).IsTrue();
        await Assert.That(NamesTheOffendingValue(failure, origin)).IsTrue();
    }

    [Test]
    public async Task AHostWithPlaintextHttpOnANonLocalhostOrigin_RefusesToStart()
    {
        // Arrange — well formed and in agreement with the relying party id, and still unusable: a
        // browser runs a ceremony only from a potentially trustworthy origin.
        const string origin = "http://budgetoid.app";

        // Act
        Exception? failure = await CaptureStartupFailureAsync(CeremonyConfiguration("budgetoid.app", origin));

        // Assert
        await Assert.That(failure).IsNotNull();
        await Assert.That(NamesTheKey(failure, ConfiguredPasskeyCeremonyPolicy.AllowedOriginsKey)).IsTrue();
        await Assert.That(NamesTheOffendingValue(failure, origin)).IsTrue();
    }

    [Test]
    public async Task AHostWhoseOriginIsNotUnderTheRelyingPartyId_RefusesToStart()
    {
        // Arrange — both values are individually well formed and describe different sites, which is the
        // drift two independent deploy parameters produce. The browser refuses this before a request is
        // ever made, so nothing about it reaches the logs of a running host.
        const string origin = "https://budgetoid.example.com";

        // Act
        Exception? failure = await CaptureStartupFailureAsync(CeremonyConfiguration("budgetoid.app", origin));

        // Assert
        await Assert.That(failure).IsNotNull();
        await Assert.That(NamesTheKey(failure, ConfiguredPasskeyCeremonyPolicy.AllowedOriginsKey)).IsTrue();
        await Assert.That(NamesTheOffendingValue(failure, origin)).IsTrue();
        await Assert.That(NamesTheOffendingValue(failure, "budgetoid.app")).IsTrue();
    }

    [Test]
    public async Task AHostWithARelyingPartyIdThatIsNotAHostName_RefusesToStart()
    {
        // Arrange — the relying party id pasted as a URL. Checked on its own so the refusal names the
        // one wrong value rather than reporting every origin as mismatched.
        const string relyingPartyId = "https://budgetoid.app";

        // Act
        Exception? failure = await CaptureStartupFailureAsync(
            CeremonyConfiguration(relyingPartyId, "https://budgetoid.app"));

        // Assert — and on the refusal saying nothing about the allow-list. Without that second half the
        // test passes on the agreement check downstream, which rejects every origin against a relying
        // party id shaped like a URL and so reports the one wrong value as a list of wrong ones.
        await Assert.That(failure).IsNotNull();
        await Assert.That(NamesTheKey(failure, ConfiguredPasskeyCeremonyPolicy.RelyingPartyIdKey)).IsTrue();
        await Assert.That(NamesTheOffendingValue(failure, relyingPartyId)).IsTrue();
        await Assert.That(NamesTheKey(failure, ConfiguredPasskeyCeremonyPolicy.AllowedOriginsKey)).IsFalse();
    }

    [Test]
    public async Task AHostWithASubdomainOrigin_StartsNormally()
    {
        // Arrange — the deployment shape where the site is served from a subdomain of the relying party
        // it registers passkeys for, which a browser accepts.

        // Act
        Exception? failure = await CaptureStartupFailureAsync(
            CeremonyConfiguration("budgetoid.app", "https://app.budgetoid.app"));

        // Assert
        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task AHostWithThePlaintextLocalhostDevelopmentOrigin_StartsNormally()
    {
        // Arrange — the development configuration. WebAuthn permits plaintext http for localhost alone,
        // so a guard that refused this would break local sign-in rather than protect a deployment.

        // Act
        Exception? failure = await CaptureStartupFailureAsync(
            CeremonyConfiguration("localhost", "http://localhost:4200"));

        // Assert
        await Assert.That(failure).IsNull();
    }

    // Production, so the Development startup block never runs and no database is touched: the refusal
    // under test happens before any of that, and a host that needed a container would make this test
    // about the container.
    private const string UnusedConnectionString =
        "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres";

    /// <summary>
    /// The pair of settings the boot guard reads, as a single override dictionary.
    /// </summary>
    /// <remarks>
    /// The origin is written under an element key rather than under the section key, because that is the
    /// only shape that replaces the factory's default allow-list with the one entry under test — see the
    /// note on the default in <see cref="ApiFactory" />.
    /// </remarks>
    private static Dictionary<string, string?> CeremonyConfiguration(string relyingPartyId, string allowedOrigin) =>
        new()
        {
            [ConfiguredPasskeyCeremonyPolicy.RelyingPartyIdKey] = relyingPartyId,
            [$"{ConfiguredPasskeyCeremonyPolicy.AllowedOriginsKey}:0"] = allowedOrigin,
        };

    /// <summary>
    /// Builds a host with the supplied overrides and returns whatever starting it threw, or
    /// <see langword="null" /> when it came up.
    /// </summary>
    private static async Task<Exception?> CaptureStartupFailureAsync(IReadOnlyDictionary<string, string?> settings)
    {
        await using ApiFactory factory = new(
            UnusedConnectionString,
            environment: "Production",
            settings: settings);

        try
        {
            // Resolving anything is what forces the host to be built, and the guard runs as the
            // application is composed rather than on a request.
            _ = factory.Services.GetRequiredService<IServiceProvider>();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// Whether the failure, or anything it wraps, is an <see cref="InvalidOperationException" /> whose
    /// message names <paramref name="key" />.
    /// </summary>
    /// <remarks>
    /// The chain is walked because the host builder is free to wrap what a startup guard threw, and a
    /// test that depended on the outermost type would break on a framework upgrade while the guard it
    /// exists for still worked. A host that came up at all is <see langword="null" /> here and answers
    /// <see langword="false" />, which keeps the call sites free of a null-forgiving operator that
    /// would be asserting the same thing twice.
    /// </remarks>
    private static bool NamesTheKey(Exception? failure, string key)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException && current.Message.Contains(key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the failure, or anything it wraps, is an <see cref="InvalidOperationException" /> quoting
    /// <paramref name="value" />.
    /// </summary>
    /// <remarks>
    /// A form or agreement refusal is only useful if the operator can see which of several configured
    /// values was rejected, so the value itself is asserted on alongside the key. Reuses the same walk as
    /// <see cref="NamesTheKey" /> for the same reason: the outermost exception type belongs to the host
    /// builder, not to the guard.
    /// </remarks>
    private static bool NamesTheOffendingValue(Exception? failure, string value) => NamesTheKey(failure, value);
}
