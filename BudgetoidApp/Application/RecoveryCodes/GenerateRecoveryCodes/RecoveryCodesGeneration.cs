namespace Application.RecoveryCodes.GenerateRecoveryCodes;

/// <summary>
/// What a completed issue has to say for itself: the set exists, and replacing the previous one ended
/// this many sessions.
/// </summary>
/// <remarks>
/// <para>
/// A result type rather than a bare <see langword="int"/> so the number reaching the wire is named
/// where it is produced — the shape <c>PasskeyRevocation</c> already uses. The codes are not returned:
/// the server never held them, and the client already has the ones it derived its verifiers from.
/// </para>
/// <para>
/// <see cref="SessionsEnded"/> is the <b>only</b> observable evidence that replacing a set ended the
/// sessions it had opened. Deleting the set's <c>credentials</c> row takes those rows by
/// <c>ON DELETE CASCADE</c> either way, so the schema afterwards is byte-identical whether the
/// revocation ran or not — which is what makes this a response member rather than an internal return
/// value.
/// </para>
/// <para>
/// It names no credential and no account. An id in a response body is an id in a client log.
/// </para>
/// </remarks>
/// <param name="SessionsEnded">
/// How many live sessions the replaced set had opened and no longer holds. Zero when the account held
/// no previous set.
/// </param>
public sealed record RecoveryCodesGeneration(int SessionsEnded);
