namespace Application.Passkeys.BeginAssertion;

/// <summary>
/// Asks for the request options that open a sign-in ceremony.
/// </summary>
/// <remarks>
/// Carries nothing, and cannot be allowed to. A member naming an account — an address, a handle —
/// would turn the answer into a statement about whether that account exists, which is the
/// enumeration this whole leg is shaped to avoid.
/// </remarks>
public sealed record BeginAssertionCommand;
