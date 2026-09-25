namespace Domain.Common;

/// <summary>
/// A request that conflicts with the state of the resource, carrying both what a person is told and what
/// a client branches on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The kind is required, and that is the whole enforcement.</b> There is no default and no overload
/// taking a message alone: a conflict raised without a kind does not compile, so the next one somebody
/// adds cannot quietly inherit a generic word. That property is worth more than the convenience it
/// costs, because the failure it prevents is invisible — a 409 wearing the wrong kind is a well-formed
/// response that sends a client down the wrong remedy, and nothing on this side would ever see it.
/// </para>
/// <para>
/// <b>The message and the kind are two answers to two readers and neither replaces the other.</b> Every
/// 409 in this product answers under one fixed title, so the message is still the whole of what a person
/// is shown; what the kind adds is the only part of that a machine can read. See
/// <see cref="ConflictKind"/> for why sites sharing a remedy share a member.
/// </para>
/// </remarks>
public sealed class ConflictException : Exception
{
    /// <summary>
    /// Raises a conflict saying <paramref name="message"/> to a person and <paramref name="kind"/> to a
    /// client.
    /// </summary>
    /// <param name="message">
    /// What the caller does next, said whole: it is rendered as the problem document's <c>detail</c>
    /// beside a title fixed for every conflict in the product, so it has to stand on its own.
    /// </param>
    /// <param name="kind">Which remedy this is, from the closed vocabulary.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is not a declared <see cref="ConflictKind"/> member — including
    /// <c>default</c>, which no member takes. Resolved here rather than where the response is written so
    /// that the stack names the code that raised the conflict; the cost is that a programming error at a
    /// throw site answers 500 instead of 409, which is the honest status for it.
    /// </exception>
    public ConflictException(string message, ConflictKind kind)
        : base(message)
    {
        Spelling = ConflictKindSpelling.Of(kind);
        Kind = kind;
    }

    /// <summary>Which remedy this conflict asks of the caller.</summary>
    public ConflictKind Kind { get; }

    /// <summary>
    /// The token <see cref="Kind"/> is written down as, resolved at construction so an undeclared member
    /// cannot reach the response writer.
    /// </summary>
    public string Spelling { get; }
}
