using Domain.Users;

namespace Application.Users.ListCredentials;

/// <summary>
/// One way the account can be signed in to, in the shape the settings surface lists it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three members, and a fourth needs its own argument rather than a free ride on this one.</b> No
/// device name, no user-chosen label, no last-used instant, no AAGUID, no transports: two passkeys are
/// told apart by the day they were registered and by nothing better. Each omission is argued in the
/// decision-log entry of 2026-08-10 — a column on <c>credentials</c> breaks the pinned exemption column
/// set, whose doctrine is <em>move the column, never widen the pin</em>; the AAGUID arrives zeroed
/// because the ceremony asks for <c>attestation: "none"</c> precisely so registration collects no device
/// fingerprint; and a "last used" instant is a usage record standing next to the <c>last_login</c> that
/// <c>ProhibitedColumnVocabulary</c> refuses.
/// </para>
/// <para>
/// <see cref="Credential.Subject" /> is absent for a stronger reason than the rest. It is the provider
/// claim every authenticated request is resolved on, so handing it to a client hands over the name of
/// the principal rather than a fact about the account — the first half of a client-supplied identity
/// parameter.
/// </para>
/// </remarks>
public sealed record CredentialSummary(Guid Id, CredentialType Type, DateTime CreatedAtUtc);
