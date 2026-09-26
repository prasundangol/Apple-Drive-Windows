using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace AppleDrive.App;

/// <summary>
/// Entry point. Apple Drive runs as a single instance: two instances would compete for the
/// phone's single stream, and the second one's startup recovery could remove the temporary files
/// of a transfer the first one is still running. A second launch hands its activation to the
/// running instance, which brings its window forward, and then exits.
/// </summary>
/// <remarks>The standard Windows App SDK pattern; needs <c>DISABLE_XAML_GENERATED_MAIN</c>.</remarks>
public static partial class Program
{
    private const string InstanceKey = "AppleDrive.Main";

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectToRunningInstance())
        {
            return 0;
        }

        Microsoft.UI.Xaml.Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    private static bool RedirectToRunningInstance()
    {
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (instance.IsCurrent)
        {
            instance.Activated += (_, _) => (Microsoft.UI.Xaml.Application.Current as App)?.OnRedirectedActivation();
            return false;
        }

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();

        // Redirecting is asynchronous; wait without blocking COM calls on this STA thread.
        var done = CreateEvent(IntPtr.Zero, bManualReset: true, bInitialState: false, lpName: null);
        _ = Task.Run(() =>
        {
            instance.RedirectActivationToAsync(activation).AsTask().Wait();
            SetEvent(done);
        });
        _ = CoWaitForMultipleObjects(0, uint.MaxValue, 1, [done], out _);
        CloseHandle(done);

        // This process was just launched by the user, so it may hand the foreground over.
        try
        {
            var running = Process.GetProcessById((int)instance.ProcessId);
            SetForegroundWindow(running.MainWindowHandle);
        }
        catch (ArgumentException)
        {
            // The other instance exited meanwhile.
        }

        return true;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateEvent(IntPtr lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, [MarshalAs(UnmanagedType.Bool)] bool bInitialState, string? lpName);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(IntPtr hEvent);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("ole32.dll")]
    private static partial uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, uint nHandles, IntPtr[] pHandles, out uint dwIndex);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);
}
