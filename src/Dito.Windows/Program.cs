using System.Security.Principal;
using Dito.Application;
using Dito.Storage;
using Dito.UI;
using Dito.Windows;
using FormsApplication = System.Windows.Forms.Application;

namespace Dito;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        try
        {
            using var instance = AcquireInstance(out var firstInstance);
            if (!firstInstance)
            {
                MessageBox.Show("Dito is already running in this session. Use Ctrl+` or its tray icon.",
                    "Dito", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                RunApplication();
            }
            finally
            {
                instance.ReleaseMutex();
            }
        }
        catch (Exception)
        {
            MessageBox.Show(
                "Dito could not start or shut down cleanly. Check database access and disk space. " +
                "Existing history has not been removed.",
                "Dito", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Mutex AcquireInstance(out bool firstInstance)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new Mutex(true, $@"Local\Dito.{identity.User!.Value}", out firstInstance);
    }

    private static void RunApplication()
    {
        var history = new HistoryStore(GetHistoryPath());
        CaptureController? capture = null;
        try
        {
            using var messages = new MessageWindow();
            var clipboard = new ClipboardTransfer(new WindowsClipboard(messages.Handle));
            var paste = new PasteService(new WindowsDesktop());
            capture = new CaptureController(history, clipboard);
            using var search = new HistorySearch(history);
            var recall = new RecallController(history, clipboard, paste);
            using var context = new TrayApplicationContext(
                history, messages, capture, search, recall, paste);
            FormsApplication.Run(context);
        }
        finally
        {
            capture?.StopAsync().GetAwaiter().GetResult();
            history.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static string GetHistoryPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Dito", "history.db");
    }
}
