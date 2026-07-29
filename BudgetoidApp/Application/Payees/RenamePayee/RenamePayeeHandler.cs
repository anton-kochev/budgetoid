using Application.Abstractions;
using Domain.Common;
using Domain.Payees;

namespace Application.Payees.RenamePayee;

public sealed class RenamePayeeHandler(IPayeeRepository repository)
    : ICommandHandler<RenamePayeeCommand>
{
    public async Task HandleAsync(
        RenamePayeeCommand command,
        CancellationToken cancellationToken = default)
    {
        // The lookup runs through the budget query filter, so a payee belonging to another budget is
        // indistinguishable from one that never existed — both end here as a 404.
        Payee? payee = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (payee is null)
        {
            throw new NotFoundException("Payee was not found.");
        }

        // The name rules are the domain's; Rename throws a ValidationException that surfaces as a 400.
        payee.Rename(command.Name);
        await repository.UpdateAsync(payee, cancellationToken);
    }
}
