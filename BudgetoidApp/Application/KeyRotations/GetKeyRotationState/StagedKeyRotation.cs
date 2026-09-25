using Application.KeyRotations.BeginKeyRotation;

namespace Application.KeyRotations.GetKeyRotationState;

/// <summary>
/// The run an account has in flight, as a client resuming it needs to be handed it back: which run,
/// what it staged, when it began, and how much of the account is left to drive it against.
/// </summary>
/// <remarks>
/// <para>
/// <b>WITHOUT THIS ANSWER AN INTERRUPTED ROTATION IS PERMANENT DATA LOSS RATHER THAN A RECOVERABLE
/// STATE.</b> The client that began the run drew a content key and an index key, encapsulated the pair
/// to every factor, and holds both only in the tab that drew them —
/// <c>AccountKeyCustodyService</c> persists nothing across reloads, by decision. Meanwhile
/// <c>wrapped_account_keys</c> still holds the <em>superseded</em> generation until a completion
/// promotes, so <see cref="Seals" /> are the only copies of the new one anywhere, and every row a
/// chunk already rewrote is sealed under it.
/// </para>
/// <para>
/// <b>It is the staging row's own account of itself and never the live factor set's.</b>
/// <see cref="Seals" /> comes from <c>key_rotation_seals</c> rather than from the account's factors
/// joined to them, so a factor enrolled after the begin is simply absent: inventing an entry for it
/// out of that factor's live <c>encapsulated_account_keys</c> would be a well-formed 158-byte value of
/// the right version carrying the generation the run is <em>replacing</em>, and a resuming client that
/// adopted it would believe the factor already holds the new keys. Such a run cannot be completed —
/// <c>IKeyRotationRepository.PromoteAsync</c>'s caller refuses it as <c>ConflictKind.FactorSetMoved</c>
/// and the remedy is a fresh begin — and what this answer owes is an honest account of what was
/// staged, not a guess at what a client would like to be true.
/// </para>
/// <para>
/// <b><see cref="Inventory" /> is recomputed rather than remembered, and that is the honest
/// denominator.</b> Nothing stores the counts a begin published — <c>key_rotations</c> has no such
/// columns — and storing them would be worse than not: a row created after the begin is sealed under
/// the generation the run is replacing, so a chunk has to visit it and the completeness gate counts
/// it. A denominator frozen at the begin is one a resuming client can pass without finishing.
/// </para>
/// <para>
/// <b><see cref="StagedManifest" /> is bytes, as every binary value is in this ring.</b> The base64url
/// spelling is the API's business, the mirror of the decode <c>KeyRotationEndpoints</c> runs on the
/// way in. Nothing here reads into the bytes: a manifest is sealed under the account's content key,
/// which this server has never held.
/// </para>
/// </remarks>
/// <param name="RotationId">
/// The client-minted identifier of the run in flight — what a resumed chunk and the completion quote.
/// </param>
/// <param name="StagedManifest">
/// The next generation's manifest of factor public keys, as it was staged and as a promotion will file
/// it.
/// </param>
/// <param name="StagedRotationEpoch">The generation that manifest will be filed at.</param>
/// <param name="StartedAtUtc">When the run was begun.</param>
/// <param name="Inventory">
/// How many rows carrying a narrative value each of the six narrative-bearing tables holds <b>now</b>,
/// which is the denominator the rest of the run is driven against.
/// </param>
/// <param name="MaxChunkBytes">
/// The byte budget one chunk's re-sealed rows may occupy — <c>BeginKeyRotationHandler.MaxChunkBytes</c>
/// owns the number and the argument for it, and a resuming client needs it exactly as the client that
/// began the run did.
/// </param>
/// <param name="Seals">
/// One staged copy of the next generation's account keys per factor the run sealed for, ordered by
/// factor. <b>The factors the run staged</b>, which is not always the factors the account holds.
/// </param>
public sealed record StagedKeyRotation(
    Guid RotationId,
    ReadOnlyMemory<byte> StagedManifest,
    int StagedRotationEpoch,
    DateTime StartedAtUtc,
    RotationInventory Inventory,
    int MaxChunkBytes,
    IReadOnlyList<RotationSeal> Seals);
