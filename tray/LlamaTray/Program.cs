using System.Windows.Forms;

namespace LlamaTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Global\LlamaTray.SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("llama.cpp tray app is already running.", "LlamaTray",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        // Autostart replacement for the old "llama-server" scheduled task: bring the
        // server up on launch if it isn't already running. Fire-and-forget so the tray
        // icon appears immediately instead of blocking on the ~20s startup/health-check.
        var bootstrapController = new ServerController();
        _ = bootstrapController.StartAsync();

        Application.Run(new TrayAppContext());
    }
}
