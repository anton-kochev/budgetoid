using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// Pins that a host this suite builds watches no settings file. <see cref="ApiFactory" />'s
/// <c>CreateHost</c> remarks carry the argument: each watched file costs the process one FSEvents
/// stream it never gets back, a process gets 512, and the 513th boot hangs the whole run.
/// </summary>
/// <remarks>
/// A test rather than trust, because the failure it guards shows up nowhere near its cause: removing
/// the setting keeps every test green until the suite builds its 513th host, and then a test that has
/// nothing to do with configuration times out after five minutes.
/// </remarks>
public sealed class ApiFactoryConfigurationReloadTests
{
    [Test]
    public async Task CreateHost_FileConfigurationSources_AreNotWatched()
    {
        // Arrange
        await using PostgresTestHost host = new();
        await host.StartAsync();

        // Act
        IConfigurationRoot configuration =
            (IConfigurationRoot)host.Factory.Services.GetRequiredService<IConfiguration>();
        FileConfigurationSource[] fileSources =
        [
            .. configuration.Providers
                .OfType<FileConfigurationProvider>()
                .Select(provider => provider.Source),
        ];

        // Assert — non-vacuity first: appsettings.json is always among them, so an empty set means
        // the check stopped looking rather than that nothing is watched.
        await Assert.That(fileSources.Select(source => source.Path)).Contains("appsettings.json");
        string[] watched = [.. fileSources.Where(source => source.ReloadOnChange).Select(source => source.Path ?? "?")];
        await Assert.That(watched).IsEmpty();
    }
}
