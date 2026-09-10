using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoChopAudio.WinUI.ViewModels;

namespace PoChopAudio.WinUI.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    // The report is rebuilt on every visit rather than cached: a camera can be plugged in, the
    // model downloaded, or the scratch folder filled up while the app is running, and a stale
    // report is worse than none — it is the report someone pastes into a bug.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Host = App.MainWindow;
        ViewModel.RefreshCommand.Execute(null);
    }
}
