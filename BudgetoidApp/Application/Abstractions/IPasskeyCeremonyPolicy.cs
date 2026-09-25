namespace Application.Abstractions;

/// <summary>
/// The relying party a WebAuthn ceremony runs as, and the origins it will accept a ceremony from.
/// </summary>
/// <remarks>
/// <para>
/// A port implemented in the presentation layer, mirroring <see cref="IBudgetContext"/> and
/// <see cref="IUserContext"/>, rather than an options package referenced from Application: these
/// values arrive from configuration, and configuration is a hosting concern. Application states what
/// it needs to judge a ceremony; the host decides where the values come from.
/// </para>
/// <para>
/// The session lifetime is deliberately absent. The relying party id and the origin allow-list have
/// to vary per environment — localhost is not the deployed site — while a session that lasts a
/// different length of time in one environment than another is a difference nobody meant, and one
/// that only shows up as an expiry somebody cannot reproduce.
/// </para>
/// </remarks>
public interface IPasskeyCeremonyPolicy
{
    /// <summary>
    /// The relying party id every ceremony is bound to — the registrable domain, never an origin.
    /// An authenticator hashes this into the authenticator data, so it is what a stored credential is
    /// scoped to for the rest of its life.
    /// </summary>
    string RelyingPartyId { get; }

    /// <summary>The name an authenticator shows the person while they confirm the ceremony.</summary>
    string RelyingPartyName { get; }

    /// <summary>
    /// The origins a ceremony may be run from, compared by equality and never by prefix.
    /// </summary>
    IReadOnlyList<string> AllowedOrigins { get; }

    /// <summary>
    /// How long the client is told it has to complete a ceremony. A hint to the client's own prompt,
    /// not an enforcement point — what the server enforces is the challenge's own lifetime.
    /// </summary>
    TimeSpan CeremonyTimeout { get; }
}
