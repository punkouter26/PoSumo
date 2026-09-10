using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using PoChopAudio.Services.Chop;
using PoChopAudio.Services.Cutout;
using PoChopAudio.Services.Cutout.Engines;
using PoChopAudio.WinUI.Services;
using PoChopAudio.WinUI.ViewModels;

namespace PoChopAudio.WinUI;

public partial class App : Application
{
    private static IServiceProvider? _serviceProvider;
    private static MainWindow? _mainWindow;

    public static MainWindow MainWindow => _mainWindow ?? throw new InvalidOperationException("Main window not initialized.");

    public static T GetService<T>() where T : class
    {
        return _serviceProvider?.GetRequiredService<T>()
            ?? throw new InvalidOperationException($"Service {typeof(T).Name} is not registered.");
    }

    public App()
    {
        InitializeComponent();
        ConfigureServices();

        // There is no console and the log was the only window into this app - except that nothing
        // wrote to it when the process died. A stowed WinRT failure (0xC000027B) took the whole
        // app down leaving an empty log and a WER bucket, which is the worst of both worlds.
        UnhandledException += (_, e) =>
        {
            LogFatal("XAML", e.Exception);
            e.Handled = false;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogFatal("AppDomain", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogFatal("Task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Writes a crash straight to the log file. Deliberately not routed through ILogger: by the
    /// time these fire the provider may already be gone, and a failed log line during a crash
    /// would hide the crash it was trying to record.
    /// </summary>
    private static void LogFatal(string source, Exception? exception)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PoChopAudio", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "pochopaudio.log"),
                $"{DateTime.Now:HH:mm:ss.fff} FATAL [{source}] {exception}{Environment.NewLine}");
        }
        catch
        {
            // Nothing useful is left to do if even this fails.
        }
    }

    private void ConfigureServices()
    {
        var services = new ServiceCollection();

        // Without a provider every log call was discarded, which left the image pipeline with no
        // way to explain itself when a result looked wrong. The provider is registered as well as
        // added, because the Settings page shows where it writes and offers to open the folder.
        var logProvider = new FileLoggerProvider();
        services.AddSingleton(logProvider);
        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(logProvider));

        // The only state that outlives a run. Registered first so anything below could read it.
        services.AddSingleton<AppSettingsService>();

        // The shared engine room, registered exactly as the API registers it. Running the same
        // ChopService and CutoutService in-process is what makes this app work with no server.
        services.AddSingleton<ChopJobStore>();
        services.AddSingleton<ChopService>();

        services.AddSingleton(new CutoutModelOptions(
            Path.Combine(AppContext.BaseDirectory, "Content", "Models", "u2netp.onnx")));
        services.AddSingleton<IBackgroundRemover, OnnxU2NetRemover>();
        services.AddSingleton<EnginePicker>();

        // Face detection is a Windows API, so Services declares the interface and the app supplies
        // the implementation. It degrades on its own if the OS component is missing.
        services.AddSingleton<IFaceLocator, WindowsFaceLocator>();
        services.AddSingleton<CutoutService>();

        // Hardware / Media Services
        services.AddSingleton<AudioRecorderService>();
        services.AddSingleton<AudioPlayerService>();

        // Its own output device, separate from clip audition: a count-in tick must never tear down
        // the take someone is in the middle of listening to.
        services.AddSingleton<AudioCueService>();
        services.AddSingleton<CameraService>();
        services.AddSingleton<ExportService>();

        // ViewModels
        services.AddTransient<RecordingViewModel>();
        services.AddTransient<ChopViewModel>();
        services.AddTransient<CutoutViewModel>();
        services.AddTransient<SettingsViewModel>();

        _serviceProvider = services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }
}

