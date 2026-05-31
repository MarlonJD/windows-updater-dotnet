using System.Diagnostics;

namespace WindowsUpdater;

public sealed record ProcessLaunchResult(bool Started, int? ProcessId = null, string? Error = null);

public sealed class LauncherCore
{
    private readonly CurrentVersionStore stateStore;

    public LauncherCore(CurrentVersionStore stateStore)
    {
        this.stateStore = stateStore;
    }

    public async Task<ProcessLaunchResult> LaunchActiveVersionAsync(CancellationToken cancellationToken = default)
    {
        var state = await stateStore.ReadOrFallbackAsync(cancellationToken);
        if (state is null)
        {
            return new ProcessLaunchResult(false, Error: "No active version is configured.");
        }

        var executable = stateStore.ResolveExecutablePath(state);
        if (!File.Exists(executable))
        {
            return new ProcessLaunchResult(false, Error: $"Active executable is missing: {executable}");
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
            });

            return process is null
                ? new ProcessLaunchResult(false, Error: "Process.Start returned null.")
                : new ProcessLaunchResult(true, process.Id);
        }
        catch (Exception error)
        {
            return new ProcessLaunchResult(false, Error: error.Message);
        }
    }
}
