using Application.Abstractions;

namespace UnitTests.Fakes;

/// <summary>
/// The relying party and origin a unit test's ceremony runs as, standing in for the values the host
/// reads from configuration.
/// </summary>
public sealed class StubPasskeyCeremonyPolicy(string relyingPartyId, params string[] allowedOrigins)
    : IPasskeyCeremonyPolicy
{
    public string RelyingPartyId { get; } = relyingPartyId;

    public string RelyingPartyName => "Budgetoid";

    public IReadOnlyList<string> AllowedOrigins { get; } = allowedOrigins;

    public TimeSpan CeremonyTimeout => TimeSpan.FromMinutes(5);
}
