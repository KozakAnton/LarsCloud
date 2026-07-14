namespace LarsCloud.Infrastructure;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LarsCloud");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string TokenFile => Path.Combine(DataDirectory, "auth.dat");
    public static string DatabaseFile => Path.Combine(DataDirectory, "state.db");
    public static string LogsDirectory => Path.Combine(DataDirectory, "Logs");
    public static string LogFile => Path.Combine(LogsDirectory, "larscloud.log");
    public static string UpdatesDirectory => Path.Combine(DataDirectory, "Updates");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(UpdatesDirectory);
    }
}
