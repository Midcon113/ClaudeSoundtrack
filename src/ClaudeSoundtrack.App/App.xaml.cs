using System.Windows;
using System.Windows.Threading;

namespace ClaudeSoundtrack.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DarkTitleBar.ApplyToAllWindows();

        // A rip runs for the better part of an hour with a spinning disc and open
        // file handles. An unhandled exception that silently killed the process
        // would leave a half-written album with no explanation, so surface it and
        // let the user decide whether to carry on.
        DispatcherUnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var result = MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\n" +
            "Continue running? Any album already written to disk is safe.",
            "ClaudeSoundtrack",
            MessageBoxButton.YesNo,
            MessageBoxImage.Error);

        e.Handled = result == MessageBoxResult.Yes;
    }
}
