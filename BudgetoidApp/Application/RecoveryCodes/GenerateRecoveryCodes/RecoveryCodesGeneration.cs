namespace Application.RecoveryCodes.GenerateRecoveryCodes;

/// <summary>
/// What a completed issue has to say for itself: the set exists, replacing the previous one ended this
/// many sessions, and — when it ended any — the session opened over the new set in their place.
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
/// <b>It is also the condition the re-established session is written on</b>, which is what raises the
/// cost of ever changing what the number counts — see the parameter's own remarks.
/// </para>
/// <para>
/// <b><see cref="Session"/> is one nullable member carrying both facts</b> rather than a kind and an
/// expiry side by side. Two nullable members admit "kind present, expiry absent", which is a state no
/// handler means and every client has to branch on; nested, the question a client asks is the one it
/// has — was I signed back in, and until when.
/// </para>
/// <para>
/// It names no credential and no account. An id in a response body is an id in a client log.
/// </para>
/// </remarks>
/// <param name="SessionsEnded">
/// How many of the replaced set's sessions <b>this request revoked</b>. Zero when the account held no
/// previous set, and zero when the set it held had opened no session or had none left unrevoked.
/// <para>
/// <b>Unrevoked, not live.</b> The sweep narrows on <c>revoked_at_utc is null</c> and says nothing
/// about expiry, so a session that expired without anybody revoking it is counted here — the reading
/// <c>ISessionRepository.RevokeForCredentialAsync</c> documents, and the one
/// <c>RevokePasskeyHandler</c> reports through the same call. That was a prose inaccuracy while the
/// number was only reported; it is load-bearing now that <c>GenerateRecoveryCodesHandler</c> writes a
/// <see cref="Session"/> row exactly when this is greater than zero. The handler argues why the
/// generosity is deliberate and safe in that direction, and why narrowing it to "live at the handler's
/// instant" is not a tightening anybody may make here alone.
/// </para>
/// </param>
/// <param name="Session">
/// The session opened over the new set when <paramref name="SessionsEnded"/> is greater than zero, and
/// <see langword="null"/> when it is zero.
/// <para>
/// Null rather than absent on the wire: a member that appears only sometimes makes "the server did not
/// tell me" and "the server told me no" the same observation for a client.
/// </para>
/// </param>
public sealed record RecoveryCodesGeneration(int SessionsEnded, ReestablishedSession? Session);
