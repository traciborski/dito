using FormsApplication = System.Windows.Forms.Application;

namespace Dito.Tests;

internal static class WinFormsTest
{
    public static Task RunAsync(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunMessageLoop(test, completion)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            yield return control;
            foreach (var descendant in Descendants(control))
            {
                yield return descendant;
            }
        }
    }

    private static void RunMessageLoop(Func<Task> test, TaskCompletionSource completion)
    {
        try
        {
            FormsApplication.SetUnhandledExceptionMode(
                UnhandledExceptionMode.ThrowException, threadScope: true);
            using var context = new ApplicationContext();
            using var dispatcher = new Control();
            _ = dispatcher.Handle;
            Exception? failure = null;

            async void OnIdle(object? sender, EventArgs e)
            {
                FormsApplication.Idle -= OnIdle;
                try
                {
                    await test();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    context.ExitThread();
                }
            }

            FormsApplication.Idle += OnIdle;
            try
            {
                FormsApplication.Run(context);
            }
            finally
            {
                FormsApplication.Idle -= OnIdle;
            }

            if (failure is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(failure);
            }
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }
}
