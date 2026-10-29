using Microsoft.Win32;

namespace WiiHub.Core.Config;

/// <summary>
/// "Start with Windows": a per-user entry in HKCU\...\Run, so no administrator rights are needed.
/// The registry is the single source of truth, so the setting also reflects changes made in
/// Task Manager → Startup apps.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Where Task Manager stores "enabled/disabled" for entries of the Run key.</summary>
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public const string ValueName = "Wii Guitar Hub";

    /// <summary>Command-line switch: start with the window minimised.</summary>
    public const string MinimizedArg = "--minimized";

    public sealed record State(bool Enabled, bool Minimized);

    /// <summary>The running exe (also correct for single-file publish builds).</summary>
    private static string? ExePath => Environment.ProcessPath;

    public static State Read()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string command || string.IsNullOrWhiteSpace(command))
                return new State(false, false);

            bool minimized = command.Contains(MinimizedArg, StringComparison.OrdinalIgnoreCase);
            return new State(!IsDisabledInTaskManager(), minimized);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the startup setting: {ex.Message}");
            return new State(false, false);
        }
    }

    /// <summary>Turns "Start with Windows" on or off. Throws when the registry can't be written.</summary>
    public static void Set(bool enabled, bool minimized)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (!enabled)
        {
            run.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Info("Start with Windows: off.");
            return;
        }

        string exe = ExePath ?? throw new InvalidOperationException("The program location is unknown.");
        run.SetValue(ValueName, BuildCommand(exe, minimized), RegistryValueKind.String);

        // If it was switched off in Task Manager, that would silently win; clear it.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);

        Log.Info($"Start with Windows: on{(minimized ? " (minimized)" : "")}.");
    }

    /// <summary>
    /// Call at startup: if the program was moved to another folder, point the startup entry at the
    /// new location (keeping the minimized choice).
    /// </summary>
    public static void RepairPath()
    {
        try
        {
            string? exe = ExePath;
            if (exe is null) return;
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(ValueName) is not string command) return;

            bool minimized = command.Contains(MinimizedArg, StringComparison.OrdinalIgnoreCase);
            string expected = BuildCommand(exe, minimized);
            if (string.Equals(command, expected, StringComparison.OrdinalIgnoreCase)) return;

            run.SetValue(ValueName, expected, RegistryValueKind.String);
            Log.Info("Updated the startup entry to the program's current location.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not update the startup entry: {ex.Message}");
        }
    }

    internal static string BuildCommand(string exe, bool minimized) =>
        minimized ? $"\"{exe}\" {MinimizedArg}" : $"\"{exe}\"";

    private static bool IsDisabledInTaskManager()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        // REG_BINARY, first byte 0x02 = enabled, 0x03 = disabled (odd = disabled).
        return approved?.GetValue(ValueName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
    }
}
