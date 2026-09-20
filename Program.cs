using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SimRigMode.Services.Elevation;

namespace SimRigMode;

/// <summary>
/// Hand-written entry point, replacing the XAML-generated one (see DISABLE_XAML_GENERATED_MAIN
/// in SimRigMode.csproj).
///
/// The order here is load-bearing. The elevated device worker runs FIRST - before WinUI, before
/// the singleton mutex, before configuration, before any window exists - and always returns an
/// exit code. See ElevatedWorker for why each of those matters.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Returns null when these are not worker arguments; otherwise this process IS the
        // elevated worker and must terminate with its exit code. Returning a code rather than
        // calling Environment.Exit makes falling through into application startup impossible
        // by construction.
        if (ElevatedWorker.TryRun(args) is int exitCode) return exitCode;

        ElevationPolicy.Initialize(args);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            // Application.Start owns the instance; WinUI holds it via Application.Current.
            new App();
        });
        return 0;
    }
}
