namespace Domain.Sessions;

/// <summary>
/// How far into the account a <see cref="SessionKind" /> reaches — FR-109, stated once, on the kind
/// itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>On the kind rather than on <see cref="Session" />, because the two callers do not both have a
/// session.</b> The domain reads it off a loaded entity; the API reads it off a claim published by the
/// authentication that produced the request, and rehydrating a <see cref="Session" /> there to ask the
/// question would be the API consulting persistence to decide something the cookie already carried.
/// While each spelled <c>Kind == SessionKind.Full</c> for itself, a member added to the enum would be
/// admitted by one of them and refused by the other, and the disagreement would show up as a
/// signed-in person served content the domain says their credential cannot open.
/// </para>
/// <para>
/// Its own type rather than a member on the enum's declaration file, so the enum stays what it is: a
/// closed vocabulary the schema also restates. See <see cref="Session.Establish" /> for which
/// credential opens which kind, and why that derivation runs by enumeration.
/// </para>
/// </remarks>
public static class SessionKindReach
{
    /// <summary>
    /// Whether a session of this kind may reach the account's budget content.
    /// </summary>
    /// <remarks>
    /// Written as an equality against the one kind that may, not as "anything that is not
    /// <see cref="SessionKind.Locked" />": a kind added later reaches nothing until somebody edits this
    /// line, which is the fail-closed direction and the reason <see cref="SessionKind.Locked" /> is
    /// declared first.
    /// </remarks>
    public static bool ReadsBudgetContent(this SessionKind kind) => kind == SessionKind.Full;
}
