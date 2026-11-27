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
    private static int Main(string[] args)
    {
        if (args.Length != 0)
        {
            return RunImportCommand(args);
        }

        RunDesktop();
        return 0;
    }

    private static int RunImportCommand(string[] args)
    {
        DittoImport.AttachToConsole();
        if (!DittoImport.TryParse(args, out var sourcePath, out var dryRun))
        {
            Console.Error.WriteLine("Usage: Dito.exe --import-from-ditto <backup.db> [--dry-run]");
            return 2;
        }

        try
        {
            using var instance = AcquireInstance(out var firstInstance);
            if (!firstInstance)
            {
                Console.Error.WriteLine("Exit Dito from its tray menu before importing.");
                return 1;
            }

            try
            {
                var result = DittoImport.RunAsync(sourcePath, GetHistoryPath(), dryRun).GetAwaiter().GetResult();
                var action = dryRun ? "Would import" : "Imported";
                Console.WriteLine($"{action}: {result.Added}; duplicates: {result.Duplicates}; skipped clips: {result.Skipped}.");
                return 0;
            }
            finally
            {
                instance.ReleaseMutex();
            }
        }
        catch (Exception)
        {
            Console.Error.WriteLine(
                "Import failed. Check that the source is a readable Ditto database backup, " +
                "it differs from Dito's history database, and storage is accessible.");
            return 1;
        }
    }

    private static void RunDesktop()
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
