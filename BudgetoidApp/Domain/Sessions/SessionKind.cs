namespace Domain.Sessions;

// Locked is declared first so that default(SessionKind) — the value a struct default, a
// zero-initialised buffer, or a deserializer that saw no member produces — is the one that reaches
// nothing. The order is the fail-closed direction, not alphabetical or historical accident; the
// converter stores text, so reordering moves no data.
public enum SessionKind
{
    /// <summary>
    /// The session reads no budget content. Today it may end itself and nothing else: the sign-out is
    /// the only route that opts out of the gate. Requesting the account's erasure is the one action it
    /// is meant to gain — the release valve for somebody holding nothing but a provider sign-in — and
    /// that is later work, not what ships.
    /// </summary>
    Locked,

    /// <summary>
    /// The session may read and write the account's budget content.
    /// </summary>
    Full,
}
