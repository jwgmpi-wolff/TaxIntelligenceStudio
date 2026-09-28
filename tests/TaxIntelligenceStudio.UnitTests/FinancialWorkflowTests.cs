using System.IO.Compression;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Financial;
using TaxIntelligenceStudio.Infrastructure.Reporting;

namespace TaxIntelligenceStudio.UnitTests;

public sealed class FinancialWorkflowTests
{
    [Fact]
    public async Task CsvImportAndAnalysis_Produces1040MappingsAndOpportunities()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                Date,Description,Amount
                01/15/2025,Employer Payroll,5000.00
                02/01/2025,Bank Interest,125.50
                03/10/2025,Charity Donation,-300.00
                04/20/2025,Business Software,-99.00
                """);

            var importer = new FinancialImportService();
            var importProgress = new CaptureProgress<FinancialImportProgress>();
            var importResult = await importer.ImportAsync([path], importProgress);
            var analysisProgress = new CaptureProgress<TaxAnalysisProgress>();
            var analysis = await new TaxAnalysisService(
                    TimeProvider.System,
                    new AlwaysVerifiedSourceVerifier())
                .AnalyzeAsync(importResult.Transactions, analysisProgress);

            Assert.Equal(4, importResult.Transactions.Count);
            Assert.True(Assert.Single(importResult.Files).Succeeded);
            Assert.Equal(1, importProgress.Items.Last().ProcessedFiles);
            Assert.Equal(4, importProgress.Items.Last().TransactionsFound);
            Assert.Contains(
                analysisProgress.Items,
                item => item.Stage.StartsWith("Verifying IRS source"));
            Assert.Equal(100, analysisProgress.Items.Last().Percentage);
            Assert.Contains(analysis.Mappings, mapping => mapping.Category == "Wages");
            Assert.Contains(analysis.Mappings, mapping => mapping.Form.Contains("Schedule B"));
            Assert.Contains(analysis.Opportunities, item => item.Title.Contains("itemized"));
            Assert.Contains(analysis.Opportunities, item => item.Title.Contains("business"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BrokerageCsv_WithDecoratedHeadersImportsRealRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                Report generated for account
                Run Date,Action,Symbol,Description,Type,Amount ($),Cash Balance ($)
                09/12/2025,DIVIDEND RECEIVED,FDRXX,Government Cash Reserves,Cash,12.34,100.00
                09/13/2025,DEBIT CARD PURCHASE,,Medical provider,Cash,-45.67,54.33
                """);

            var result = await new FinancialImportService().ImportAsync([path]);

            Assert.Equal(2, result.Transactions.Count);
            Assert.Contains(
                result.Transactions,
                transaction => transaction.Description.Contains("DIVIDEND RECEIVED"));
            Assert.Equal(12.34m, result.Transactions.First().Amount);
            Assert.True(Assert.Single(result.Files).Succeeded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Import_WithNoTransactionRowsReturnsExplicitFileDiagnostic()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(path, "This file contains no transaction rows.");

            var result = await new FinancialImportService().ImportAsync([path]);

            Assert.Empty(result.Transactions);
            var file = Assert.Single(result.Files);
            Assert.False(file.Succeeded);
            Assert.Contains("No supported tax fields or transaction rows matched", file.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PdfTaxSummary_ImportsDividendAndPropertyTaxFacts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");
        try
        {
            _ = new TaxReportService(new AlwaysVerifiedSourceVerifier());
            using (var document = new PdfDocument())
            {
                var page = document.AddPage();
                using var graphics = XGraphics.FromPdfPage(page);
                graphics.DrawString(
                    "Dividends $123.45",
                    new XFont("Arial", 12),
                    XBrushes.Black,
                    new XPoint(50, 50));
                graphics.DrawString(
                    "REAL ESTATE TAXES PAID $2,000.00",
                    new XFont("Arial", 12),
                    XBrushes.Black,
                    new XPoint(50, 80));
                document.Save(path);
            }

            var result = await new FinancialImportService().ImportAsync([path]);

            Assert.Contains(
                result.Transactions,
                transaction => transaction.Description.Contains("Dividends") &&
                               transaction.Amount == 123.45m);
            Assert.Contains(
                result.Transactions,
                transaction => transaction.Description.Contains("REAL ESTATE TAXES") &&
                               transaction.Amount == -2000m);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Form1098Pdf_ImportsMortgageInterestBox()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");
        try
        {
            _ = new TaxReportService(new AlwaysVerifiedSourceVerifier());
            using (var document = new PdfDocument())
            {
                var page = document.AddPage();
                using var graphics = XGraphics.FromPdfPage(page);
                graphics.DrawString(
                    "1 Mortgage interest received from payer(s)/borrower(s) 2 Outstanding mortgage principal",
                    new XFont("Arial", 10),
                    XBrushes.Black,
                    new XPoint(50, 50));
                graphics.DrawString(
                    "$1,234.56",
                    new XFont("Arial", 11),
                    XBrushes.Black,
                    new XPoint(100, 70));
                graphics.DrawString(
                    "$250,000.00",
                    new XFont("Arial", 11),
                    XBrushes.Black,
                    new XPoint(300, 70));
                document.Save(path);
            }

            var result = await new FinancialImportService().ImportAsync([path]);

            Assert.Contains(
                result.Transactions,
                transaction => transaction.Description == "1098 Box 1: Mortgage interest received" &&
                               transaction.Amount == -1234.56m);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ImageOnlyPdf_UsesLocalOcrAndExtractsPropertyTax()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = new PdfDocument())
            {
                document.AddPage();
                document.Save(path);
            }

            var importer = new FinancialImportService(new StubPdfOcrService(
            [
                new OcrPageText(1,
                [
                    "2025 PROPERTY TAX STATEMENT",
                    "TOTAL PROPERTY TAX",
                    "$4,321.09"
                ])
            ]));
            var result = await importer.ImportAsync([path]);

            var fact = Assert.Single(result.Transactions);
            Assert.Equal("Property tax statement", fact.TaxForm);
            Assert.Equal("Tax", fact.TaxBox);
            Assert.Equal(-4321.09m, fact.Amount);
            Assert.Equal(1, fact.SourceRow);
            Assert.True(Assert.Single(result.Files).Succeeded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CreditCardPdf_UsesExpenseSignsAndMarksPaymentsInformational()
    {
        var path = Path.Combine(Path.GetTempPath(), $"synthetic_VISA_{Guid.NewGuid():N}.pdf");
        try
        {
            _ = new TaxReportService(new AlwaysVerifiedSourceVerifier());
            using (var document = new PdfDocument())
            {
                var page = document.AddPage();
                using var graphics = XGraphics.FromPdfPage(page);
                var font = new XFont("Arial", 10);
                graphics.DrawString("Credit Card Statement", font, XBrushes.Black, new XPoint(50, 40));
                graphics.DrawString("Transactions", font, XBrushes.Black, new XPoint(50, 60));
                graphics.DrawString(
                    "08/05 OFFICE SUPPLY STORE $100.00",
                    font,
                    XBrushes.Black,
                    new XPoint(50, 90));
                graphics.DrawString(
                    "08/06 CREDIT CARD PAYMENT $50.00-",
                    font,
                    XBrushes.Black,
                    new XPoint(50, 110));
                document.Save(path);
            }

            var result = await new FinancialImportService().ImportAsync([path]);

            Assert.Contains(result.Transactions, item =>
                item.Description.Contains("OFFICE SUPPLY") && item.Amount == -100m);
            Assert.Contains(result.Transactions, item =>
                item.Description.Contains("PAYMENT") &&
                item.Amount == 50m &&
                item.IsInformational);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Scanned1099Misc_DoesNotAssignRentAmountToEmptyRoyaltyBox()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");
        try
        {
            _ = new TaxReportService(new AlwaysVerifiedSourceVerifier());
            using (var document = new PdfDocument())
            {
                document.AddPage();
                document.Save(path);
            }

            var importer = new FinancialImportService(new StubPdfOcrService(
            [
                new OcrPageText(1,
                [
                    "Form 1099-MISC",
                    "For calendar year 2025",
                    "Rents",
                    "1234.56",
                    "Royalties",
                    "Other income"
                ])
            ]));
            var result = await importer.ImportAsync([path]);

            var fact = Assert.Single(result.Transactions);
            Assert.Equal("1099-MISC", fact.TaxForm);
            Assert.Equal("1", fact.TaxBox);
            Assert.Equal(1234.56m, fact.Amount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FidelityYearEndReport_ExtractsInformationalIncomeAndRothContribution()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");
        try
        {
            _ = new TaxReportService(new AlwaysVerifiedSourceVerifier());
            using (var document = new PdfDocument())
            {
                var page = document.AddPage();
                using var graphics = XGraphics.FromPdfPage(page);
                var font = new XFont("Arial", 10);
                string[] lines =
                [
                    "2025 YEAR-END INVESTMENT REPORT",
                    "ROTH IRA",
                    "Income Summary",
                    "Tax-free $12.34",
                    "Contributions Made in 2025",
                    "2025 Contributions $2,000.00"
                ];
                for (var index = 0; index < lines.Length; index++)
                {
                    graphics.DrawString(
                        lines[index],
                        font,
                        XBrushes.Black,
                        new XPoint(50, 40 + index * 20));
                }

                document.Save(path);
            }

            var result = await new FinancialImportService().ImportAsync([path]);

            Assert.Contains(result.Transactions, item =>
                item.TaxForm == "Fidelity year-end summary" &&
                item.TaxBox == "Tax-free" &&
                item.IsInformational);
            Assert.Contains(result.Transactions, item =>
                item.TaxForm == "5498" &&
                item.TaxBox == "10" &&
                item.Amount == 2000m &&
                item.IsInformational);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WordReport_ContainsAnalysisAndVerifiedSources()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.docx");
        try
        {
            var analysis = await new TaxAnalysisService(TimeProvider.System).AnalyzeAsync(
            [
                new FinancialTransaction(
                    new DateOnly(2025, 1, 15),
                    "Bank Interest",
                    125.50m,
                    "bank.csv",
                    2)
            ]);
            var reportService = new TaxReportService(new AlwaysVerifiedSourceVerifier());

            await reportService.ExportAsync(analysis, path, ReportFormat.Word);

            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0);
            using var archive = ZipFile.OpenRead(path);
            Assert.Contains(archive.Entries, entry => entry.FullName == "word/document.xml");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PdfReport_ContainsAValidPdfHeader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");
        try
        {
            var analysis = await new TaxAnalysisService(TimeProvider.System).AnalyzeAsync(
            [
                new FinancialTransaction(
                    new DateOnly(2025, 1, 15),
                    "Bank Interest",
                    125.50m,
                    "bank.csv",
                    2)
            ]);
            var reportService = new TaxReportService(new AlwaysVerifiedSourceVerifier());

            await reportService.ExportAsync(analysis, path, ReportFormat.Pdf);

            var header = new byte[4];
            await using var stream = File.OpenRead(path);
            var read = await stream.ReadAsync(header);
            Assert.Equal(4, read);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(header));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Analysis_RefusesResultsWhenLiveIrsVerificationFails()
    {
        var service = new TaxAnalysisService(
            TimeProvider.System,
            new FailedSourceVerifier());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AnalyzeAsync(
            [
                new FinancialTransaction(
                    new DateOnly(2025, 1, 15),
                    "Bank Interest",
                    125.50m,
                    "bank.csv",
                    2)
            ]));

        Assert.Contains(
            GuidanceResponse.VerificationFailureMessage,
            exception.Message);
    }

    private sealed class AlwaysVerifiedSourceVerifier : IAuthoritativeSourceVerifier
    {
        public Task<VerifiedSource> VerifyAsync(
            AuthoritativeSource source,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerifiedSource(
                source,
                source.Publication,
                new string('A', 64),
                DateTimeOffset.UtcNow,
                1m,
                true,
                null));
    }

    private sealed class FailedSourceVerifier : IAuthoritativeSourceVerifier
    {
        public Task<VerifiedSource> VerifyAsync(
            AuthoritativeSource source,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerifiedSource(
                source,
                source.Publication,
                string.Empty,
                DateTimeOffset.UtcNow,
                0m,
                false,
                "Source unavailable."));
    }

    private sealed class CaptureProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];

        public void Report(T value) => Items.Add(value);
    }

    private sealed class StubPdfOcrService(IReadOnlyCollection<OcrPageText> pages)
        : IPdfOcrService
    {
        public Task<IReadOnlyCollection<OcrPageText>> RecognizeAsync(
            string filePath,
            IProgress<PdfOcrProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new PdfOcrProgress(pages.Count, pages.Count, "OCR complete"));
            return Task.FromResult(pages);
        }
    }
}
