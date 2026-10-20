using System.IO;
using System.Text;
using TaxIntelligenceStudio.Application;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TaxIntelligenceStudio.Desktop.Services;

public sealed class WindowsImageAttachmentAnalyzer(ILocalVisionService vision) : IImageAttachmentAnalyzer
{
    public async Task<string> AnalyzeAsync(
        string path, InferenceAttachmentOptions options, CancellationToken cancellationToken = default)
    {
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken);
        using var stream = await file.OpenReadAsync().AsTask(cancellationToken);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        if ((long)decoder.PixelWidth * decoder.PixelHeight > 40_000_000)
        {
            throw new InvalidDataException("Images are limited to 40 megapixels. Resize the image first.");
        }
        var scale = Math.Min(1, 2000d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1u, (uint)(decoder.PixelWidth * scale)),
            ScaledHeight = Math.Max(1u, (uint)(decoder.PixelHeight * scale))
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
        var text = new StringBuilder();
        if (decoder.FrameCount > 1)
        {
            text.AppendLine("Multi-frame image: only the first frame was analyzed.");
        }
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                "Windows OCR has no installed language. Install an OCR language pack in Windows Settings.");
        var ocr = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        text.AppendLine(string.IsNullOrWhiteSpace(ocr.Text)
            ? "OCR: no text detected."
            : $"OCR text (verify against original):\n{ocr.Text}");
        if (options.DetectFaces)
        {
            if (!FaceDetector.IsSupported || !FaceDetector.IsBitmapPixelFormatSupported(BitmapPixelFormat.Gray8))
            {
                throw new NotSupportedException("Windows face detection is unavailable on this device. Disable face detection and retry.");
            }
            using var gray = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Gray8);
            var detector = await FaceDetector.CreateAsync().AsTask(cancellationToken);
            var faces = await detector.DetectFacesAsync(gray).AsTask(cancellationToken);
            text.AppendLine($"Face detection: {faces.Count} possible face(s); no identity recognition.");
            foreach (var face in faces)
            {
                var box = face.FaceBox;
                text.AppendLine($"Face box on analyzed image ({bitmap.PixelWidth}x{bitmap.PixelHeight}): " +
                    $"x={box.X}, y={box.Y}, width={box.Width}, height={box.Height}");
            }
        }
        if (options.DescribeObjects)
        {
            using var encoded = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded).AsTask(cancellationToken);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync().AsTask(cancellationToken);
            if (encoded.Size > 4 * 1024 * 1024)
            {
                throw new InvalidDataException("The prepared image exceeds the 4 MB vision limit. Use a smaller image.");
            }
            encoded.Seek(0);
            using var reader = new DataReader(encoded.GetInputStreamAt(0));
            await reader.LoadAsync((uint)encoded.Size).AsTask(cancellationToken);
            var bytes = new byte[(int)encoded.Size];
            reader.ReadBytes(bytes);
            text.AppendLine(await vision.DescribeAsync(bytes, cancellationToken));
        }
        return text.ToString();
    }
}
