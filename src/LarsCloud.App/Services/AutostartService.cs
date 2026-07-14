using Microsoft.Win32;

namespace LarsCloud.Services;

public sealed class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LarsCloud";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LarsCloud.exe");
            key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
        }
        else key.DeleteValue(ValueName, false);
    }
}
