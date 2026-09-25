using System.Diagnostics.CodeAnalysis;

namespace Application.Passkeys.Verification;

/// <summary>
/// Either a verified value or the reason the ceremony was refused.
/// </summary>
/// <remarks>
/// A refusal here is an expected answer, not a fault, so it is a return value: it keeps the choice of
/// HTTP shape with the caller and it keeps every branch of the verifier reachable from a unit test
/// without an exception filter.
/// </remarks>
/// <typeparam name="T">The value produced when the ceremony is accepted.</typeparam>
public readonly struct PasskeyVerificationResult<T>
    where T : notnull
{
    private readonly T? _value;
    private readonly PasskeyVerificationFailure? _failure;

    private PasskeyVerificationResult(T? value, PasskeyVerificationFailure? failure)
    {
        _value = value;
        _failure = failure;
    }

    /// <summary>Whether the ceremony was accepted.</summary>
    public bool IsVerified => _failure is null && _value is not null;

    /// <summary>The refusal reason, or null when the ceremony was accepted.</summary>
    public PasskeyVerificationFailure? Failure => _failure;

    public static PasskeyVerificationResult<T> Verified(T value) => new(value, null);

    public static PasskeyVerificationResult<T> Refused(PasskeyVerificationFailure failure) =>
        new(default, failure);

    /// <summary>
    /// Unpacks the result. <paramref name="failure"/> is only meaningful when this returns false.
    /// </summary>
    public bool TryGetValue([NotNullWhen(true)] out T? value, out PasskeyVerificationFailure failure)
    {
        value = _value;
        failure = _failure.GetValueOrDefault();

        return IsVerified;
    }
}
