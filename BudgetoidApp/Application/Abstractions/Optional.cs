namespace Application.Abstractions;

/// <summary>
/// One field of a partial update, carrying the three states a JSON property can arrive in: absent,
/// present and null, or present with a value.
/// </summary>
/// <remarks>
/// <c>default(Optional&lt;T&gt;)</c> is the absent state, and that is not an arbitrary choice: a
/// deserializer cannot signal "this property was missing", it simply leaves the constructor
/// parameter at its default. Absence is therefore the one state that has to fall out of the default
/// rather than be constructed.
/// </remarks>
/// <typeparam name="T">
/// The field's type. Use a nullable <typeparamref name="T"/> for a field the caller is allowed to
/// clear; a non-nullable one makes an explicit null a deserialization failure instead.
/// </typeparam>
public readonly struct Optional<T>
{
    /// <summary>Creates a field that was supplied, possibly with a null value.</summary>
    public Optional(T? value)
    {
        Value = value;
        IsSet = true;
    }

    /// <summary>
    /// Whether the caller mentioned the field at all. False means leave the current value alone;
    /// true with a null <see cref="Value"/> means the caller asked for it to be cleared.
    /// </summary>
    public bool IsSet { get; }

    /// <summary>The supplied value. Only meaningful when <see cref="IsSet"/> is true.</summary>
    public T? Value { get; }

    /// <summary>
    /// Merges this field over the value it is updating: the supplied value when the field was
    /// mentioned, <paramref name="current"/> when it was not.
    /// </summary>
    public T? OrElse(T? current) => IsSet ? Value : current;
}
