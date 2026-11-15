using System.Threading.Tasks;
using System.Windows;

namespace MyOverlayPOC;

/// <summary>
/// Interaction logic for App.xaml.
/// License check runs before MainWindow is shown.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _ = RunLicenseFlowAsync();
    }

    private async Task RunLicenseFlowAsync()
    {
        string? savedToken = LicenseManager.LoadSavedToken();

        if (savedToken != null)
        {
            // ── Returning user: silent check ──────────────────────────
            var (ok, message, error) = await LicenseManager.ValidateAsync(savedToken);

            if (ok)
            {
                LaunchMain();
                return;
            }

            if (error == LicenseError.HwidMismatch)
            {
                // Show blocked screen with Telegram button — no token input
                var blocked = new LicenseWindow(blockedMode: true);
                blocked.ShowDialog();
                Shutdown();
                return;
            }

            if (error == LicenseError.NetworkError)
            {
                // Let them in offline if token file exists — avoids locking out
                // users with no internet. Remove this block if you want strict online-only.
                LaunchMain();
                return;
            }

            // Token revoked or any other hard failure — force re-activation
            var licWin = new LicenseWindow();
            licWin.ShowDialog();
            if (licWin.Activated) LaunchMain(); else Shutdown();
            return;
        }

        // ── First launch: show token input ────────────────────────────
        var firstRun = new LicenseWindow();
        firstRun.ShowDialog();
        if (firstRun.Activated) LaunchMain(); else Shutdown();
    }

    private void LaunchMain()
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }
}
