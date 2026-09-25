namespace Application.Passkeys;

/// <summary>
/// Works out the <c>timeout</c> a ceremony's options tell the client.
/// </summary>
public static class PasskeyCeremonyTimeout
{
    /// <summary>
    /// The shorter of the configured prompt timeout and the life the issued challenge actually has,
    /// in milliseconds.
    /// </summary>
    /// <remarks>
    /// Two numbers describe this window — the client's prompt and the nonce behind it — and only the
    /// shorter one is honest. Telling a client it has longer than the challenge does produces a
    /// ceremony a person completes and the server then refuses, with nothing in the response able to
    /// say why. The configured value can therefore shorten the prompt and never extend the window.
    /// </remarks>
    public static long Milliseconds(
        TimeSpan ceremonyTimeout,
        DateTime challengeExpiresAtUtc,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        TimeSpan remaining = challengeExpiresAtUtc - timeProvider.GetUtcNow().UtcDateTime;
        TimeSpan timeout = remaining < ceremonyTimeout ? remaining : ceremonyTimeout;

        return (long)Math.Max(timeout.TotalMilliseconds, 0);
    }
}
