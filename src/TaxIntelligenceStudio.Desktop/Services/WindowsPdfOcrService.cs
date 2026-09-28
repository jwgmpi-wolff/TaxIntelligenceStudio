using System.IO;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TaxIntelligenceStudio.Desktop.Services;

public sealed class WindowsPdfOcrService : IPdfOcrService
{
    public async Task<IReadOnlyCollection<OcrPageText>> RecognizeAsync(
        string filePath,
        IProgress<PdfOcrProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("The PDF selected for OCR was not found.", filePath);
        }

        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                "Windows OCR has no installed recognition language. Install an English OCR language pack in Windows Settings.");
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(filePath));
        var document = await PdfDocument.LoadFromFileAsync(file);
        var pages = new List<OcrPageText>((int)document.PageCount);
        for (var index = 0u; index < document.PageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new PdfOcrProgress(
                (int)index,
                (int)document.PageCount,
                $"Rendering page {index + 1:N0} for local OCR"));
            using var page = document.GetPage(index);
            using var stream = new InMemoryRandomAccessStream();
            var renderOptions = new PdfPageRenderOptions
            {
                DestinationWidth = 2400
            };
            await page.RenderToStreamAsync(stream, renderOptions);
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);
            var positionedLines = result.Lines.Select(line =>
            {
                var words = line.Words.ToList();
                var left = words.Min(word => word.BoundingRect.X);
                var top = words.Min(word => word.BoundingRect.Y);
                var right = words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
                var bottom = words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);
                return new OcrTextLine(line.Text, left, top, right - left, bottom - top);
            }).ToList();
            pages.Add(new OcrPageText(
                (int)index + 1,
                positionedLines.Select(line => line.Text).ToList(),
                positionedLines));
            progress?.Report(new PdfOcrProgress(
                (int)index + 1,
                (int)document.PageCount,
                $"Recognized page {index + 1:N0} of {document.PageCount:N0}"));
        }

        return pages;
    }
}
