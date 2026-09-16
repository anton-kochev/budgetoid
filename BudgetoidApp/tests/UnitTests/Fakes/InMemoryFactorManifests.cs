using Domain.Users;

namespace UnitTests.Fakes;

/// <summary>
/// The one <c>factor_manifests</c> row an account holds, in memory, with the half of the real stack a
/// retried unit of work turns on: the stored generation and the generation a tracked instance has been
/// promoted to are two different values, and a discard is what puts the second back to the first.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is one type used by two fakes rather than the same model written out twice, and that is a
/// different call from the one production makes.</b> <c>PasskeyRepository.FindFactorManifestAsync</c>
/// and <c>RecoveryCodeRepository.FindFactorManifestAsync</c> are deliberately verbatim copies of each
/// other, because each repository owns the statements its own catches read. What is duplicated there is
/// four lines of query. What would be duplicated here is an <em>identity map with a rollback</em> — the
/// one piece of behaviour in either fake that a test can be wrong about without noticing — so two
/// copies of it is two chances for a fake to model a replay the database does not perform.
/// </para>
/// <para>
/// <b><see cref="Find" /> hands back the same instance every time, which is what makes a promotion
/// visible to the save that follows it.</b> EF resolves a second read of a row it is already tracking
/// to the instance it holds, so a handler that reads the manifest, promotes it and then saves is saving
/// the instance it promoted. A table materialising a fresh entity per call would hand the save a row
/// still at the old generation and every test of the promotion would pass over a handler that promoted
/// nothing.
/// </para>
/// <para>
/// <b><see cref="Discard" /> is the rollback, and it is why the row value is kept beside the tracked
/// instance.</b> An abandoned attempt's UPDATE went back with its transaction, so the row still holds
/// <c>N</c> and the surviving attempt promotes it to <c>N + 1</c> exactly once. Modelled by forgetting
/// the tracked instance: the next <see cref="Find" /> materialises a new one at the stored generation,
/// which is what a real re-read does after <c>ChangeTracker.Clear()</c>. A table that kept the promoted
/// instance would have the replay meet <c>Promote</c>'s own refusal and report a handler that converges
/// as one that cannot.
/// </para>
/// <para>
/// <b>Nothing here commits.</b> There is no save to model: a test reads <see cref="Current" /> for what
/// the database would hold if this unit of work committed now, exactly as
/// <see cref="InMemoryRecoveryCodeRepository.Credentials" /> answers for its own rows.
/// </para>
/// <para>
/// The <see cref="User" /> a row is materialised through is fabricated from the owner id.
/// <see cref="FactorManifest.For" /> reads <c>user.Id</c> and nothing else off it, so nothing about the
/// account this stands for is claimed by the fabrication — and the alternative, a table holding the
/// caller's own <see cref="User" />, would make seeding a manifest need an account object no test in
/// either handler's file has.
/// </para>
/// </remarks>
public sealed class InMemoryFactorManifests
{
    /// <summary>
    /// The instant the fabricated <see cref="User" /> carries. It reaches no assertion — nothing on
    /// <c>factor_manifests</c> is a timestamp — and is fixed so nothing here depends on the wall clock.
    /// </summary>
    private static readonly DateTime FabricatedUserInstant = new(2026, 1, 5, 9, 30, 0, DateTimeKind.Utc);

    private readonly Dictionary<Guid, Row> _rows = [];
    private readonly Dictionary<Guid, FactorManifest> _tracked = [];

    /// <summary>Files the one manifest row an account holds, the way registration left it.</summary>
    /// <exception cref="InvalidOperationException">
    /// The account already holds one. <c>user_id</c> is the primary key, so a second row is a state the
    /// database refuses and a fake holding one would answer questions production cannot.
    /// </exception>
    public void Seed(Guid userId, byte[] manifest, int rotationEpoch)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!_rows.TryAdd(userId, new Row([.. manifest], rotationEpoch)))
        {
            throw new InvalidOperationException("That account already holds a factor manifest.");
        }
    }

    /// <summary>
    /// The manifest the account would hold if this unit of work committed now — the tracked instance's
    /// value when one has been materialised, and the stored row's otherwise.
    /// </summary>
    public (byte[] Manifest, int RotationEpoch)? Current(Guid userId) =>
        _tracked.TryGetValue(userId, out FactorManifest? tracked)
            ? (tracked.Manifest.ToArray(), tracked.RotationEpoch)
            : _rows.TryGetValue(userId, out Row? row) ? (row.Manifest, row.RotationEpoch) : null;

    /// <summary>
    /// The account's row as the tracker holds it, or <see langword="null" /> when it holds none.
    /// </summary>
    public FactorManifest? Find(Guid userId)
    {
        if (_tracked.TryGetValue(userId, out FactorManifest? tracked))
        {
            return tracked;
        }

        if (!_rows.TryGetValue(userId, out Row? row))
        {
            return null;
        }

        FactorManifest materialised = FactorManifest.For(
            User.CreateWithId(userId, $"{userId:D}@example.invalid", FabricatedUserInstant),
            row.Manifest,
            row.RotationEpoch);

        _tracked[userId] = materialised;

        return materialised;
    }

    /// <summary>
    /// Forgets every materialised instance, which is what clearing the change tracker does to them —
    /// see this type's remarks for why that, and not a stored value being rewound, is the rollback.
    /// </summary>
    public void Discard() => _tracked.Clear();

    /// <summary>One stored row: the bytes and the generation they belong to.</summary>
    private sealed record Row(byte[] Manifest, int RotationEpoch);
}
