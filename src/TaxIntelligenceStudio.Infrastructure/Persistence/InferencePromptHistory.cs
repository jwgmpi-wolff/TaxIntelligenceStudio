using Microsoft.EntityFrameworkCore;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Persistence;

public sealed class InferencePromptHistory(TaxIntelligenceDbContext dbContext, TimeProvider timeProvider)
    : IInferencePromptHistory
{
    public async Task<IReadOnlyList<SavedInferencePrompt>> GetAsync(CancellationToken cancellationToken = default)
    {
        var prompts = await dbContext.InferencePrompts.AsNoTracking().ToListAsync(cancellationToken);
        return prompts.OrderByDescending(item => item.SavedAt).ThenBy(item => item.Id).ToArray();
    }

    public async Task SaveAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var saved = new SavedInferencePrompt
        {
            Id = Guid.NewGuid(),
            SavedAt = timeProvider.GetUtcNow(),
            Prompt = prompt
        };
        dbContext.InferencePrompts.Add(saved);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            dbContext.Entry(saved).State = EntityState.Detached;
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.InferencePrompts.Where(item => item.Id == id).ExecuteDeleteAsync(cancellationToken);

    public async Task ClearAsync(CancellationToken cancellationToken = default) =>
        await dbContext.InferencePrompts.ExecuteDeleteAsync(cancellationToken);
}
