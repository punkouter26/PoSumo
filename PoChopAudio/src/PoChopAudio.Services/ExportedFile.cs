namespace PoChopAudio.Services;

/// <summary>
/// Bytes plus the name and type they should be saved under. The API turns this into a file
/// response; a desktop host writes it straight to disk.
/// </summary>
public sealed record ExportedFile(byte[] Content, string FileName, string ContentType)
{
    public const string Wav = "audio/wav";
    public const string Png = "image/png";
    public const string Zip = "application/zip";
}
