using Domain.Common;

namespace Domain.Users;

public sealed class User
{
    private User()
    {
    }

    public Guid Id { get; private set; }
    public Email Email { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }

    public static User Create(string email, DateTime createdAtUtc)
    {
        Email emailValue = Email.Create(email);

        return new User
        {
            Id = Guid.CreateVersion7(),
            Email = emailValue,
            CreatedAtUtc = createdAtUtc
        };
    }

    /// <summary>
    /// Creates the account under an identifier the caller already holds, for the one path whose two
    /// requests have to reach the same value without carrying it between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The only legitimate source of <paramref name="id"/> is
    /// <c>Application.Registration.RegistrationAccountId.For</c></b>, called after the challenge store
    /// has confirmed it issued and spent exactly those bytes for the account-registration pool. That
    /// identifier is the WebAuthn user handle the authenticator was given when the ceremony opened, and
    /// an account row written under any other value answers no assertion that device will ever produce —
    /// silently, permanently, with no error naming the cause. Any other source is a caller choosing which
    /// account identifiers exist.
    /// </para>
    /// <para>
    /// <b>A second factory rather than an optional parameter on <see cref="Create"/>.</b> Widening that
    /// one would put a caller-chosen id on the path provisioning uses, where nothing distinguishes it
    /// from the minted one: <c>OwnershipKeyImmutabilityTests</c> asks whether the key is written once,
    /// not where the value came from, so the widening would redden nothing. Two factories make "which
    /// path may name an account" a fact about the call site.
    /// </para>
    /// </remarks>
    /// <param name="id">The derived account identifier. Never <see cref="Guid.Empty"/>.</param>
    /// <param name="email">The address the provider asserted for this account.</param>
    /// <param name="createdAtUtc">The instant the whole account comes into existence at.</param>
    /// <exception cref="ValidationException">
    /// The identifier is empty, or the address is not one this product accepts.
    /// </exception>
    public static User CreateWithId(Guid id, string email, DateTime createdAtUtc)
    {
        // Refused rather than replaced with a fresh uuid: all-zeros is what an unset field sends, and a
        // repair here would create the account under an identifier the authenticator was never handed —
        // the one failure this whole derivation exists to make unreachable. Judged before the address so
        // that a caller passing nothing at all is told about the identifier rather than about the email.
        if (id == Guid.Empty)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(Id)] = ["Account id is required."],
            });
        }

        Email emailValue = Email.Create(email);

        return new User
        {
            Id = id,
            Email = emailValue,
            CreatedAtUtc = createdAtUtc,
        };
    }
}
