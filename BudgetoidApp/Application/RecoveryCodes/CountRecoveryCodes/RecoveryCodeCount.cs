namespace Application.RecoveryCodes.CountRecoveryCodes;

/// <summary>
/// How many recovery codes the account has left, and nothing else about the set.
/// </summary>
/// <remarks>
/// <para>
/// A result type rather than a bare <see langword="int"/> so the number reaching the wire is named
/// where it is produced — the shape <c>SignedInUser</c> and <c>RecoveryCodesGeneration</c> already use.
/// </para>
/// <para>
/// <b>One member, and a second needs its own argument rather than a free ride on this one.</b> No
/// credential id — an id in a response body is an id in a client log, and the set's id is what a
/// revocation route would address it by. No issued instant, no total, and above all no hash: a stored
/// hash is the value a redemption is matched against, so publishing one would turn the read every
/// settings screen makes into the whole secret. A screen wanting to say "3 of 10" is asking for the
/// total, and that is a widening to argue for, not to assume.
/// </para>
/// <para>
/// Zero is a legitimate value, not an absence. An account that has never been issued a set holds no
/// rows and is described by this record with <see cref="Remaining"/> at zero; the alternative — a 404 —
/// would make a client branch on a distinction whose two arms render the same control.
/// </para>
/// </remarks>
/// <param name="Remaining">How many unredeemed codes the account still holds.</param>
public sealed record RecoveryCodeCount(int Remaining);
