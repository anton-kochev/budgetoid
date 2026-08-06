using Application.Abstractions;

namespace Api.Infrastructure;

/// <summary>
/// Reads the relying party and the origin allow-list out of <c>Authentication:Passkey:*</c>.
/// </summary>
/// <remarks>
/// Registered as a singleton: nothing here is request state, and re-reading configuration once per
/// request would only add a way for two ceremonies in one sign-in to disagree about the relying party.
/// The values are read once in the constructor for the same reason.
/// </remarks>
public sealed class ConfiguredPasskeyCeremonyPolicy : IPasskeyCeremonyPolicy
{
    public const string RelyingPartyIdKey = "Authentication:Passkey:RelyingPartyId";
    public const string RelyingPartyNameKey = "Authentication:Passkey:RelyingPartyName";
    public const string AllowedOriginsKey = "Authentication:Passkey:AllowedOrigins";
    public const string CeremonyTimeoutSecondsKey = "Authentication:Passkey:CeremonyTimeoutSeconds";

    private const string DefaultRelyingPartyName = "Budgetoid";

    // Long enough to reach for a phone or a security key, and shorter than the challenge lifetime the
    // store enforces, so the prompt never outlives the nonce behind it.
    private static readonly TimeSpan DefaultCeremonyTimeout = TimeSpan.FromMinutes(2);

    private readonly string _relyingPartyId;
    private readonly string _relyingPartyName;
    private readonly string[] _allowedOrigins;
    private readonly TimeSpan _ceremonyTimeout;

    public ConfiguredPasskeyCeremonyPolicy(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // The two values that must never have a default. The relying party id is hashed into every
        // credential an authenticator stores, so guessing one here would register passkeys nobody can
        // ever use; and an empty origin allow-list refuses every ceremony while looking configured.
        // The API host refuses to boot without either (see Program.cs) — that is the enforcement, and
        // it is where the defect reaches the pipeline instead of a user. These stay as cheap argument
        // checks because this type is public and DI-constructible: any host composed without that boot
        // guard would otherwise silently get a policy nobody configured.
        _relyingPartyId = configuration[RelyingPartyIdKey] is { } relyingPartyId
            && !string.IsNullOrWhiteSpace(relyingPartyId)
            ? relyingPartyId
            : throw new InvalidOperationException(
                $"{RelyingPartyIdKey} is required: it is the domain every registered passkey is "
                + "permanently bound to.");
        _relyingPartyName = configuration[RelyingPartyNameKey] ?? DefaultRelyingPartyName;
        _allowedOrigins = configuration.GetSection(AllowedOriginsKey).Get<string[]>() is { Length: > 0 } origins
            ? origins
            : throw new InvalidOperationException(
                $"{AllowedOriginsKey} must list at least one origin: no ceremony can be accepted from "
                + "an empty allow-list.");
        _ceremonyTimeout = configuration.GetValue<int?>(CeremonyTimeoutSecondsKey) is { } seconds and > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultCeremonyTimeout;
    }

    public string RelyingPartyId => _relyingPartyId;

    public string RelyingPartyName => _relyingPartyName;

    public IReadOnlyList<string> AllowedOrigins => _allowedOrigins;

    public TimeSpan CeremonyTimeout => _ceremonyTimeout;
}
