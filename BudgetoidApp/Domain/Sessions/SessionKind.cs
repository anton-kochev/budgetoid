namespace Domain.Sessions;

// Locked is declared first so that default(SessionKind) — the value a struct default, a
// zero-initialised buffer, or a deserializer that saw no member produces — is the one that reaches
// nothing. The order is the fail-closed direction, not alphabetical or historical accident; the
// converter stores text, so reordering moves no data.
public enum SessionKind
{
    /// <summary>
    /// The session reads no budget content. It may end itself, and it may schedule the account's
    /// erasure — the release valve for somebody holding nothing but a provider sign-in, and a route
    /// only this kind may reach.
    /// </summary>
    Locked,

    /// <summary>
    /// The session may read and write the account's budget content.
    /// </summary>
    Full,
}
