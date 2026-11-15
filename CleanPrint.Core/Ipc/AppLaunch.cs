using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace CleanPrint.Core.Ipc;

/// <summary>
/// Lets the Watcher (or anything else) hand a finished job off to the App: if the App is already
/// running, the job folder is sent over a named pipe and the App brings itself to the foreground;
/// if not, the App is launched with the job folder as its command-line argument.
/// </summary>
public static class AppLaunch
{
    public const string PipeName = "CleanPrintApp.IPC";

    /// <summary>Attempts to notify a running App instance. Returns true if one was listening.</summary>
    public static async Task<bool> TryNotifyRunningInstanceAsync(string jobFolder, int timeoutMs = 400)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(cts.Token);

            await using var writer = new StreamWriter(client) { AutoFlush = true };
            await writer.WriteLineAsync(jobFolder);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Notifies a running App if there is one; otherwise launches a fresh instance with the job folder as its argument.</summary>
    public static async Task NotifyOrLaunchAsync(string appExePath, string jobFolder)
    {
        if (await TryNotifyRunningInstanceAsync(jobFolder))
            return;

        Process.Start(new ProcessStartInfo(appExePath, $"\"{jobFolder}\"")
        {
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// App-side: listens forever for job-folder notifications from the Watcher (or from a second
    /// App instance handing off its command-line argument before exiting). Runs one accept loop
    /// per call; typically started once as a background Task at App startup and left running for
    /// the app's lifetime.
    /// </summary>
    public static async Task ListenForJobsAsync(Action<string> onJobFolderReceived, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server);
                var jobFolder = await reader.ReadLineAsync(ct);
                if (!string.IsNullOrWhiteSpace(jobFolder))
                    onJobFolderReceived(jobFolder);
            }
            catch (OperationCanceledException)
            {
                // shutting down — exit the loop
            }
            catch (IOException)
            {
                // client disconnected mid-write or similar transient issue; just retry
            }
        }
    }
}
