using Application.Groups;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadServices;

public sealed class GroupReadService(BudgetoidDbContext dbContext) : IGroupReadService
{
    public async Task<IReadOnlyList<GroupDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.Groups
            .AsNoTracking()
            .OrderBy(group => group.Name)
            .Select(group => new GroupDto(group.Id, group.Name, group.Description))
            .ToListAsync(cancellationToken);
    }
}
