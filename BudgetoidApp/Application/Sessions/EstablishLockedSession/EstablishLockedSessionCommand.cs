namespace Application.Sessions.EstablishLockedSession;

/// <summary>Opens a locked session for the account a provider identity names, if one does.</summary>
/// <param name="Subject">The provider's stable identifier for the caller, read off the principal the
/// provider scheme authenticated and never off a body.</param>
/// <remarks>
/// <para>
/// <b>The subject and nothing else.</b> No address: the account is found by the identity the provider
/// vouched for, and an address is a value two accounts' histories can share — an email change frees one
/// for somebody else to register under — so a lookup keyed on it would open a session on an account the
/// token never named. The claim gate on the route has already refused an unverified address; that
/// judgement is about whether the provider's answer is usable, not about which account it names.
/// </para>
/// <para>
/// <b>No provider either.</b> The only provider this product files a federated credential under is
/// Google's, so the handler names it, exactly as registration does when it writes the row this reads.
/// </para>
/// </remarks>
public sealed record EstablishLockedSessionCommand(string Subject);
