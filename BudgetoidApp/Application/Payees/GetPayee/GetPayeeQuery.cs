namespace Application.Payees.GetPayee;

/// <summary>
/// Reads one payee of the ambient budget by its identifier.
/// </summary>
/// <param name="Id">The row to read.</param>
/// <remarks>
/// <b>This query exists because <c>POST /api/payees</c> answers 201 with a <c>Location</c>.</b> Without
/// the route the header would name an address that answers 404 — a header that lies, and the one part of
/// a 201 a caller is entitled to follow. It buys nothing else: one query, one handler, one read-service
/// method, no new grant, no new policy and no new column, over rows the payee list already returns.
/// </remarks>
public sealed record GetPayeeQuery(Guid Id);
