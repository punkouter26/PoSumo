using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PoChopAudio.Services.Cutout;

/// <summary>
/// Decodes an uploaded image into raw RGBA bytes, EXIF-auto-rotated, with the Pixel Motion Photo
/// MP4 trailer stripped. The output is always 8-bit RGBA, no padding, and never wider than
/// <see cref="CutoutLimits.MaxDimension"/> on its longest edge.
/// </summary>
public static class ImageDecoder
{
    private static readonly string[] AdvertisedExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>
    /// <c>.bmp</c> decodes but is not advertised: nothing produces one worth cutting out, and it
    /// only ever reaches here when a user types the name. This is the single list every caller
    /// reads — the picker, the diagnostics page and <see cref="IsSupportedExtension"/> alike, so
    /// the app cannot tell the user it reads a format the picker will not let them choose.
    /// </summary>
    private static readonly string[] AcceptedExtensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];

    /// <summary>File extensions the UI should offer in the file picker.</summary>
    public static IReadOnlyList<string> SupportedExtensions => AdvertisedExtensions;

    /// <summary>True if this build can read the extension at all, advertised or not.</summary>
    public static bool IsSupportedExtension(string extension) =>
        AcceptedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads, EXIF-rotates, and resizes-guards an image. Returns (rgba, width, height).</summary>
    public static DecodedImage Decode(Stream input, string fileName)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(fileName);

        // Read once into memory so we can rewind after the MP trailer sniff.
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return DecodeBytes(ms.ToArray(), fileName);
    }

    internal static DecodedImage DecodeBytes(byte[] bytes, string fileName)
    {
        var isMotionPhoto = DetectMotionPhoto(bytes);
        var imageBytes = isMotionPhoto ? StripMotionPhoto(bytes) : bytes;

        using var image = Image.Load<Rgba32>(imageBytes);
        image.Mutate(c => c.AutoOrient());

        var width = image.Width;
        var height = image.Height;

        if (width > CutoutLimits.MaxDimension || height > CutoutLimits.MaxDimension)
        {
            throw new InvalidDataException(
                $"Image is {width}x{height}; the longest edge must be at most {CutoutLimits.MaxDimension} pixels.");
        }

        // Copy into a contiguous buffer so the remover can address it without going through the ImageSharp pixel accessor.
        var rgba = new byte[CutoutLimits.AlphaChannels * width * height];
        image.CopyPixelDataTo(rgba);

        return new DecodedImage(rgba, width, height, image.Metadata.DecodedImageFormat?.DefaultMimeType ?? "image/png", isMotionPhoto);
    }

    /// <summary>True if the JPEG has a "ftypmp42" MP4 trailer. Pixel Motion Photo only.</summary>
    public static bool DetectMotionPhoto(byte[] bytes)
    {
        // Lightweight sniff: find the JPEGs EOI marker (FF D9) and look for an MP4 ftyp box right after it.
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return false;
        }

        var eoi = FindMarkerEnd(bytes, from: 0);
        if (eoi < 0)
        {
            return false;
        }

        var trailer = eoi + 2;
        if (trailer + 8 > bytes.Length)
        {
            return false;
        }

        // Read the box type only. We don't enforce that the declared size fits — truncated trailers
        // are common in the wild and we only care that an MP4 box begins here.
        var tag = System.Text.Encoding.ASCII.GetString(bytes, trailer + 4, 4);
        return tag is "ftyp";
    }

    private static int FindMarkerEnd(byte[] bytes, int from)
    {
        // Skip any segments before EOI (FF D9).
        var i = from;
        while (i < bytes.Length - 1)
        {
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xD9)
            {
                return i;
            }

            i++;
        }

        return -1;
    }

    private static byte[] StripMotionPhoto(byte[] bytes)
    {
        var eoi = FindMarkerEnd(bytes, 0);
        if (eoi < 0)
        {
            return bytes;
        }

        return bytes.AsSpan(0, eoi + 2).ToArray();
    }

    /// <summary>Re-encodes RGBA bytes as a transparent PNG.</summary>
    public static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);

        using var image = Image.LoadPixelData<Rgba32>(rgba, width, height);

        using var output = new MemoryStream();
        var encoder = new PngEncoder { CompressionLevel = PngCompressionLevel.Level6 };
        image.Save(output, encoder);
        return output.ToArray();
    }
}

/// <summary>Result of decoding one image.</summary>
public sealed record DecodedImage(
    byte[] Rgba,
    int Width,
    int Height,
    string ContentType,
    bool WasMotionPhoto);
