namespace Application.Users.ExportData;

/// <summary>
/// The server cannot assemble a complete export, and refuses rather than handing back the part it
/// can read.
/// </summary>
/// <remarks>
/// <para>
/// <b>It deliberately has no <c>IExceptionHandler</c> of its own.</b> It falls to the catch-all
/// <c>GlobalExceptionHandler</c> and surfaces as a 500 <c>application/problem+json</c>, like any
/// other unclaimed exception. Each named alternative says something false: 404 claims the export
/// does not exist when it does and it is the server that cannot assemble it; 400 blames a
/// well-formed request that carries no field to correct; 409 implies a resolution the client can
/// perform, and the client can neither create, delete nor select a budget because no endpoint does.
/// A <em>named</em> 5xx mapping is worse than all three — it is the seam a later reader softens into
/// "return the ambient budget and a warning", which is the truncation this refusal exists to
/// prevent. Left on the catch-all, the only way to change the answer is to change the throw.
/// </para>
/// <para>
/// <b>The message names counts and never ids.</b> The Development branch of
/// <c>GlobalExceptionHandler</c> echoes <see cref="Exception.Message" /> into the response body
/// beside the exception type and the stack trace, so an identifier spelled into it is an identifier
/// handed to the caller of a request that was refused precisely so that nothing would be.
/// </para>
/// </remarks>
public sealed class ExportCompletenessException(string message) : InvalidOperationException(message);
