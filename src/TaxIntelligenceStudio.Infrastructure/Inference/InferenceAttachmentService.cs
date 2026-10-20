using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using TaxIntelligenceStudio.Application;
using UglyToad.PdfPig;

namespace TaxIntelligenceStudio.Infrastructure.Inference;

public sealed class InferenceAttachmentService(
    IImageAttachmentAnalyzer images,
    IPdfOcrService pdfOcr) : IInferenceAttachmentService
{
    public const long MaximumFileBytes = 20 * 1024 * 1024;
    public const int MaximumTextCharacters = 20_000;
    public const int MaximumPdfPages = 30;

    public async Task<InferenceAttachment> ExtractAsync(
        string path,
        InferenceAttachmentOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The selected attachment was not found.", path);
        }
        if (file.Length == 0 || file.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("Attachments must be non-empty and no larger than 20 MB.");
        }
        progress?.Report($"Extracting attachment locally: {file.Name}");
        var extension = file.Extension.ToLowerInvariant();
        string text;
        switch (extension)
        {
            case ".png": case ".jpg": case ".jpeg": case ".bmp": case ".tif": case ".tiff":
                text = await images.AnalyzeAsync(file.FullName, options, cancellationToken);
                break;
            case ".pdf":
                text = await ExtractPdfAsync(file.FullName, progress, cancellationToken);
                break;
            case ".txt": case ".csv":
                using (var reader = new StreamReader(file.FullName))
                {
                    var buffer = new char[MaximumTextCharacters + 1];
                    var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
                    text = new string(buffer, 0, count);
                }
                break;
            case ".docx":
            case ".xlsx":
                text = await Task.Run(() => ExtractOffice(file.FullName, extension, cancellationToken), cancellationToken);
                break;
            default:
                throw new NotSupportedException($"Attachment type '{extension}' is not supported.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException($"No text or requested visual findings were extracted from '{file.Name}'.");
        }
        if (text.Length > MaximumTextCharacters)
        {
            throw new InvalidDataException(
                $"'{file.Name}' exceeds the {MaximumTextCharacters:N0}-character extraction limit. Use a smaller document or excerpt.");
        }
        return new InferenceAttachment(file.Name, text,
            $"{file.Name} - {text.Length:N0} characters extracted locally");
    }

    private async Task<string> ExtractPdfAsync(
        string path, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var pages = await Task.Run(() =>
        {
            using var document = PdfDocument.Open(path);
            if (document.NumberOfPages > MaximumPdfPages)
            {
                throw new InvalidDataException($"PDF attachments are limited to {MaximumPdfPages} pages.");
            }
            var result = new List<string>();
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(page.Text);
                if (result.Sum(text => text.Length) > MaximumTextCharacters)
                {
                    throw new InvalidDataException("PDF text exceeds the attachment extraction limit. Use fewer pages.");
                }
            }
            return result;
        }, cancellationToken);
        if (pages.Any(string.IsNullOrWhiteSpace))
        {
            progress?.Report("Scanned PDF pages found. Running local Windows OCR...");
            var recognized = await pdfOcr.RecognizeAsync(path, cancellationToken: cancellationToken);
            foreach (var page in recognized)
            {
                if (page.PageNumber > 0 && page.PageNumber <= pages.Count &&
                    string.IsNullOrWhiteSpace(pages[page.PageNumber - 1]))
                {
                    pages[page.PageNumber - 1] = string.Join("\n", page.Lines);
                }
            }
        }
        if (pages.All(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("No readable PDF text was found, including after local OCR.");
        }
        for (var index = 0; index < pages.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(pages[index]))
            {
                pages[index] = "[No text recognized on this page. Review the original.]";
            }
        }
        return string.Join("\n\n", pages.Select((text, index) => $"Page {index + 1}:\n{text}"));
    }

    private static string ExtractOffice(string path, string extension, CancellationToken cancellationToken)
    {
        using (var zip = ZipFile.OpenRead(path))
        {
            if (zip.Entries.Count > 10_000 || zip.Entries.Sum(entry => entry.Length) > 100 * 1024 * 1024)
            {
                throw new InvalidDataException("The expanded Office document exceeds the attachment safety limit.");
            }
        }
        var text = new StringBuilder();
        void Append(string value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            text.AppendLine(value);
            if (text.Length > MaximumTextCharacters)
            {
                throw new InvalidDataException("Office document text exceeds the extraction limit. Use a smaller excerpt.");
            }
        }
        if (extension == ".docx")
        {
            using var document = WordprocessingDocument.Open(path, false);
            var body = document.MainDocumentPart?.Document?.Body
                ?? throw new InvalidDataException("The Word document has no readable body.");
            foreach (var paragraph in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
            {
                Append(paragraph.InnerText);
            }
        }
        else
        {
            using var workbook = new XLWorkbook(path);
            foreach (var sheet in workbook.Worksheets)
            {
                Append($"Worksheet: {sheet.Name}");
                foreach (var row in sheet.RowsUsed())
                {
                    Append(string.Join("\t", row.CellsUsed().Select(cell =>
                        $"{cell.Address}: {cell.GetFormattedString()}")));
                }
            }
        }
        return text.ToString();
    }
}
