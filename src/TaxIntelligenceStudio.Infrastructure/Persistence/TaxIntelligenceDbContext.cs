using Microsoft.EntityFrameworkCore;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Persistence;

public sealed class TaxIntelligenceDbContext(DbContextOptions<TaxIntelligenceDbContext> options)
    : DbContext(options)
{
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();
    public DbSet<SavedInferencePrompt> InferencePrompts => Set<SavedInferencePrompt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var prompt = modelBuilder.Entity<SavedInferencePrompt>();
        prompt.ToTable("InferencePrompts");
        prompt.HasKey(item => item.Id);
        prompt.Property(item => item.Prompt).IsRequired();
        prompt.Ignore(item => item.DisplayText);

        var audit = modelBuilder.Entity<AuditEntry>();
        audit.ToTable("AuditEntries");
        audit.HasKey(entry => entry.Id);
        audit.Property(entry => entry.EventType).HasMaxLength(128).IsRequired();
        audit.Property(entry => entry.InputJson).IsRequired();
        audit.Property(entry => entry.OutputJson).IsRequired();
        audit.Property(entry => entry.ModelVersion).HasMaxLength(256).IsRequired();
        audit.Property(entry => entry.IntegrityHash).HasMaxLength(64).IsRequired();
        audit.HasIndex(entry => entry.CreatedAt);

        var document = modelBuilder.Entity<KnowledgeDocument>();
        document.ToTable("KnowledgeDocuments");
        document.HasKey(item => item.Id);
        document.Property(item => item.SourceUrl).HasMaxLength(2048).IsRequired();
        document.Property(item => item.Title).HasMaxLength(1024).IsRequired();
        document.Property(item => item.Content).IsRequired();
        document.Property(item => item.ContentHash).HasMaxLength(64).IsRequired();
        document.Property(item => item.ContentType).HasMaxLength(256);
        document.HasIndex(item => new { item.SourceUrl, item.Version }).IsUnique();
        document.HasIndex(item => new { item.SourceUrl, item.IsCurrent });
    }
}
