namespace Application.Payees;

public sealed record PayeeDto(Guid Id, string Name);

public sealed record PayeeListResponse(IReadOnlyList<PayeeDto> Items);
