namespace Domain.Sessions;

// Locked is declared first so that default(SessionKind) — the value a struct default, a
// zero-initialised buffer, or a deserializer that saw no member produces — is the one that reaches
// nothing. The order is the fail-closed direction, not alphabetical or historical accident; the
// converter stores text, so reordering moves no data.
public enum SessionKind
{
    /// <summary>
    /// The session reads no budget content and offers a single action: requesting the account's
    /// erasure.
    /// </summary>
    Locked,

    /// <summary>
    /// The session may read and write the account's budget content.
    /// </summary>
    Full,
}
