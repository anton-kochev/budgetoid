namespace Application.KeyRotations.BeginKeyRotation;

/// <summary>
/// What an opened rotation hands the client to drive the rest of the run with: how much there is to
/// rewrite, and how much of it may travel in one request.
/// </summary>
/// <remarks>
/// <para>
/// <b>It echoes back neither the rotation identifier nor the factor.</b> The caller minted the first
/// and named the second, so repeating them adds nothing — and a response that restates what was sent
/// invites a client to read the echo as confirmation that the server agreed with it, which on this path
/// it would: the values are stored verbatim or the request was refused.
/// </para>
/// <para>
/// <b>Two members and no status.</b> There is no "rotation in progress" state to report, because a
/// begin that returned is a begin that staged — the row exists by the time the response is written, and
/// a member saying so would be a flag nothing could ever set to false.
/// </para>
/// </remarks>
/// <param name="Inventory">
/// The row count of each narrative-bearing table, which the client uses as its progress denominator.
/// </param>
/// <param name="MaxChunkBytes">
/// The byte budget one chunk's re-sealed rows may occupy — see
/// <c>BeginKeyRotationHandler.MaxChunkBytes</c>, which owns the number and the argument for it.
/// </param>
public sealed record KeyRotationBegun(RotationInventory Inventory, int MaxChunkBytes);
