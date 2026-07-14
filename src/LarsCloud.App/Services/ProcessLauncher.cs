using System.Diagnostics;

namespace LarsCloud.Services;

public static class ProcessLauncher
{
    public static void Open(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
}
