namespace Application.Users.ChangeEmail;

/// <summary>
/// What a completed email change has to say for itself: how many sessions went with the Google
/// credential it retired.
/// </summary>
/// <remarks>
/// It names no address and no subject. The caller sent both, and a value in a response body is a value
/// in a client log.
/// </remarks>
/// <param name="SessionsEnded">
/// How many sessions the retired federated credential had opened and no longer holds — zero when the
/// Google identity did not change, because then nothing was retired.
/// </param>
public sealed record EmailChange(int SessionsEnded);
