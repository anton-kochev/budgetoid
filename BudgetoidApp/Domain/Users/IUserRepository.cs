namespace Domain.Users;

public interface IUserRepository
{
    Task<User?> FindByGoogleSubjectAsync(string googleSubject, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the user, returning <see langword="false"/> when the insert lost to an existing row
    /// on either unique rule — the <c>google_subject</c> or the email. Which of the two it was is
    /// not reported, because a losing insert can breach both at once; the caller decides by
    /// re-reading the subject, adopting the winning row when there is one.
    /// </summary>
    Task<bool> TryAddAsync(User user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a profile refresh, returning <see langword="false"/> when another user already holds
    /// the new email. On <see langword="false"/> the change is rolled back and
    /// <paramref name="user"/> is reset to its persisted state.
    /// </summary>
    Task<bool> UpdateProfileAsync(User user, CancellationToken cancellationToken = default);
}
