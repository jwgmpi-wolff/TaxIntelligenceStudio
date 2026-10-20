using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Reporting;
using UglyToad.PdfPig;

namespace TaxIntelligenceStudio.UnitTests;

public sealed class InferenceReportTests
{
    private static ModelInferenceTurn Turn(string prompt = "Rental question", string content =
        "# Records\nKeep **receipts**.\n- Track expenses\n1. Check depreciation\n\n```text\nAmount: 125.00\n```") =>
        new(prompt, new ModelInferenceResponse(content, "Trained", "Qwen test adapter", "stop", null,
            TimeSpan.FromSeconds(3), ["https://www.irs.gov/publications/p527"], true,
            "cached", ["Cached evidence may be stale."]));

    [Theory]
    [InlineData(ReportFormat.Pdf)]
    [InlineData(ReportFormat.Word)]
    public async Task Export_PreservesConversationAndMetadataInReadableDocuments(ReportFormat format)
    {
        var path = Path.Combine(Path.GetTempPath(), $"inference-{Guid.NewGuid():N}");
        try
        {
            await new InferenceReportService(TimeProvider.System).ExportAsync(
                [Turn(), Turn("Follow-up question", "Second response")], path, format);
            string text;
            if (format == ReportFormat.Pdf)
            {
                using var pdf = PdfDocument.Open(path);
                text = string.Join("\n", pdf.GetPages().Select(page => page.Text));
            }
            else
            {
                using var docx = WordprocessingDocument.Open(path, false);
                Assert.Empty(new OpenXmlValidator().Validate(docx));
                var document = Assert.IsType<Document>(docx.MainDocumentPart?.Document);
                text = document.InnerText;
                Assert.Contains(document.Descendants<OutlineLevel>(), level => level.Val?.Value == 1);
                Assert.NotEmpty(document.Descendants<Bold>());
            }
            foreach (var expected in new[]
                     {
                         "Tax Intelligence Studio", "Rental question", "Keep receipts.", "Track expenses",
                         "Check depreciation", "Amount: 125.00", "Follow-up question", "Second response",
                         "Trained", "Qwen test adapter", "https://www.irs.gov/publications/p527",
                         "Cached evidence may be stale.", "not a verified tax opinion",
                         "private taxpayer information", "Used cached sources: Yes"
                     })
            {
                Assert.Contains(expected, text);
            }
            Assert.DoesNotContain("```", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Pdf_PaginatesAndWrapsLongUnbrokenTextWithoutClipping()
    {
        var path = Path.Combine(Path.GetTempPath(), $"inference-{Guid.NewGuid():N}.pdf");
        var longToken = new string('W', 600);
        var content = string.Join("\n", Enumerable.Repeat("Paragraph marker " + longToken, 65)) + "\nFinal marker";
        try
        {
            await new InferenceReportService(TimeProvider.System).ExportAsync([Turn(content: content)], path, ReportFormat.Pdf);
            using var pdf = PdfDocument.Open(path);
            Assert.True(pdf.NumberOfPages > 2);
            var text = string.Join("\n", pdf.GetPages().Select(page => page.Text));
            Assert.Contains("Final marker", text);
            Assert.Equal(600 * 65 + 1, text.Count(character => character == 'W')); // Includes "Warnings".
            foreach (var page in pdf.GetPages())
            {
                Assert.All(page.Letters, letter =>
                {
                    Assert.True(letter.BoundingBox.Left >= 43);
                    Assert.True(letter.BoundingBox.Right <= 568);
                    Assert.True(letter.BoundingBox.Bottom >= 30);
                    Assert.True(letter.BoundingBox.Top <= 750);
                });
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(ReportFormat.Pdf)]
    [InlineData(ReportFormat.Word)]
    public async Task Cancellation_LeavesExistingFileUnchangedAndRemovesTemporaryFile(ReportFormat format)
    {
        var path = Path.Combine(Path.GetTempPath(), $"inference-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(path, "Existing export");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new InferenceReportService(TimeProvider.System).ExportAsync([Turn()], path, format, cancelled.Token));
            Assert.Equal("Existing export", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task InvalidInput_DoesNotCreateOutput()
    {
        var path = Path.Combine(Path.GetTempPath(), $"inference-{Guid.NewGuid():N}");
        var service = new InferenceReportService(TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync([], path, ReportFormat.Pdf));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync([Turn(content: " ")], path, ReportFormat.Word));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ExportAsync([Turn()], path, (ReportFormat)99));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task FailedReplacement_ReportsErrorAndRemovesTemporaryFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"inference-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                new InferenceReportService(TimeProvider.System).ExportAsync([Turn()], path, ReportFormat.Word));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
        }
        finally
        {
            Directory.Delete(path);
        }
    }
}
