using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoChopAudio.WinUI.Common;
using PoChopAudio.WinUI.Services;
using PoChopAudio.WinUI.Views;

namespace PoChopAudio.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly AppSettingsService _settings;

    public MainWindow()
    {
        _settings = App.GetService<AppSettingsService>();

        InitializeComponent();
        WindowHelper.TrySetMicaBackdrop(this);
        WindowHelper.SetWindowSizeToWorkAreaFraction(this, 0.82, 0.88);
        WindowHelper.SetMinimumSize(this, 480, 560);

        // Drawing into the title bar is what lets the Mica backdrop run to the top of the window
        // instead of stopping under a grey system caption.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyTheme(_settings.Current.Theme);
        _settings.Changed += OnSettingsChanged;
        Closed += OnClosed;

        SelectNavItem("Chop");
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _settings.Changed -= OnSettingsChanged;
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        ApplyTheme(settings.Theme);
    }

    /// <summary>
    /// Themes the whole app by setting the root element's requested theme. Every page lives inside
    /// this frame, so one assignment re-themes the lot; <c>Application.RequestedTheme</c> is
    /// deliberately not used because it can only be set before the first window exists.
    /// </summary>
    private void ApplyTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            NavigateTo("Settings");
            return;
        }

        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void SelectNavItem(string tag)
    {
        var item = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (i.Tag as string) == tag);

        if (item is not null)
        {
            NavView.SelectedItem = item;
        }

        NavigateTo(tag);
    }

    private void NavigateTo(string tag)
    {
        Type pageType = tag switch
        {
            "Cutout" => typeof(CutoutPage),
            "Settings" => typeof(SettingsPage),
            _ => typeof(ChopPage),
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }
}
