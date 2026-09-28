namespace TaxIntelligenceStudio.Domain;

public sealed record FinancialTransaction(
    DateOnly? Date,
    string Description,
    decimal Amount,
    string SourceFile,
    int SourceRow,
    string? TaxForm = null,
    string? TaxBox = null,
    bool IsInformational = false,
    int? TaxYear = null,
    decimal? RecognitionConfidence = null);

public sealed record FinancialFileImport(
    string FileName,
    string FileType,
    int TransactionCount,
    bool Succeeded,
    string Message);

public sealed record FinancialImportResult(
    IReadOnlyCollection<FinancialTransaction> Transactions,
    IReadOnlyCollection<FinancialFileImport> Files);

public sealed record FinancialImportProgress(
    int ProcessedFiles,
    int TotalFiles,
    string CurrentFile,
    int TransactionsFound,
    string Stage);

public sealed record TaxAnalysisProgress(
    double Percentage,
    string Stage);

public sealed record OcrPageText(
    int PageNumber,
    IReadOnlyCollection<string> Lines,
    IReadOnlyCollection<OcrTextLine>? PositionedLines = null);

public sealed record OcrTextLine(
    string Text,
    double X,
    double Y,
    double Width,
    double Height);

public sealed record PdfOcrProgress(
    int ProcessedPages,
    int TotalPages,
    string Stage);

public sealed record TaxMapping(
    string Category,
    decimal Amount,
    string Form,
    string ReportingDestination,
    string Rationale,
    Uri SourceUrl,
    decimal Confidence);

public sealed record TaxOpportunity(
    string Title,
    string Explanation,
    decimal PotentialAmount,
    Uri SourceUrl,
    decimal Confidence);

public sealed record TaxAnalysisResult(
    IReadOnlyCollection<FinancialTransaction> Transactions,
    IReadOnlyCollection<TaxMapping> Mappings,
    IReadOnlyCollection<TaxOpportunity> Opportunities,
    decimal TotalIncome,
    decimal TotalExpenses,
    DateTimeOffset AnalyzedAt);

public enum ReportFormat
{
    Pdf,
    Word
}
