using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Persistence;

internal sealed class AuditRepository(TaxIntelligenceDbContext dbContext) : IAuditRepository
{
    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        dbContext.AuditEntries.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
