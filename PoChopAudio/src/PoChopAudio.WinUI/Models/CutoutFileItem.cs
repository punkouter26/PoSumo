using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PoChopAudio.WinUI.Models;

/// <summary>One photo taken on this page, and the cutout made from it.</summary>
public partial class CutoutFileItem : ObservableObject
{
    /// <summary>
    /// The view model this card belongs to, so the Save button inside its DataTemplate can reach
    /// the command with a compiled binding. A DataTemplate has its own namescope, so an
    /// ElementName binding out to the page would resolve to nothing at all.
    /// </summary>
    public ViewModels.CutoutViewModel? Owner { get; init; }

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private long _bytes;

    [ObservableProperty]
    private int _width;

    [ObservableProperty]
    private int _height;

    [ObservableProperty]
    private ItemProcessingStatus _status = ItemProcessingStatus.Queued;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private BitmapImage? _originalImage;

    [ObservableProperty]
    private BitmapImage? _cutoutImage;

    /// <summary>The finished PNG, held in memory until the user saves it or clears the list.</summary>
    public byte[]? CutoutPngBytes { get; set; }

    /// <summary>The frame as captured. Kept so re-tuning re-cuts from the source, not the cutout.</summary>
    public byte[]? OriginalPngBytes { get; set; }

    public bool IsReady => Status == ItemProcessingStatus.Ready;

    public string DetailsText => $"{Width} × {Height} px · {Bytes:N0} bytes";

    partial void OnWidthChanged(int value) => OnPropertyChanged(nameof(DetailsText));

    partial void OnHeightChanged(int value) => OnPropertyChanged(nameof(DetailsText));

    partial void OnBytesChanged(long value) => OnPropertyChanged(nameof(DetailsText));

    partial void OnStatusChanged(ItemProcessingStatus value) => OnPropertyChanged(nameof(IsReady));
}
