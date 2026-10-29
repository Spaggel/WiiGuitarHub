using System.Text.Json;
using System.Text.Json.Serialization;
using WiiHub.Core.Input;
using WiiHub.Core.Mapping;

namespace WiiHub.Core.Config;

/// <summary>What a controller shows up as on the PC.</summary>
public enum OutputMode
{
    Xbox360,
    Keyboard,
    Both,
    Disabled,
}

public enum AccelAxis { X, Y, Z }

/// <summary>
/// Guitar tilt (Star Power) is measured with the Wiimote's accelerometer, because the Wiimote
/// sits inside the guitar body. Calibrate by recording "rest" and "tilted" readings.
/// </summary>
public sealed class TiltSettings
{
    public AccelAxis Axis { get; set; } = AccelAxis.Y;
    public double RestG { get; set; }
    public double TiltedG { get; set; } = 0.7;

    /// <summary>
    /// Before calibration the direction is unknown, so tilting either way counts.
    /// After calibration only the recorded direction counts.
    /// </summary>
    public bool Calibrated { get; set; }

    /// <summary>Fraction of the way from rest to tilted at which tilt is "pressed".</summary>
    public double Threshold { get; set; } = 0.5;

    public TiltSettings Clone() => (TiltSettings)MemberwiseClone();
}

public sealed record Binding(InputId Source, int Target, bool Invert = false);

/// <summary>Bindings for one (extension, output) combination.</summary>
public sealed class MappingSet
{
    public DeviceKind Device { get; set; }
    public OutputKind Output { get; set; }
    public List<Binding> Bindings { get; set; } = [];
}

public sealed class ControllerConfig
{
    /// <summary>Bluetooth MAC (or device path) — the identity that survives reconnects.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Wiimote";
    public int PlayerIndex { get; set; } = 1;
    public OutputMode Output { get; set; } = OutputMode.Xbox360;
    public bool ForwardRumble { get; set; } = true;

    /// <summary>
    /// Keep the virtual Xbox controller plugged in while the Wiimote is away (e.g. battery swap),
    /// so the game keeps the same player slot.
    /// </summary>
    public bool KeepVirtualPadWhenDisconnected { get; set; } = true;

    /// <summary>GHWT touch bar acts as extra fret buttons (only through the Touch* inputs' bindings).</summary>
    public bool TouchBarEnabled { get; set; } = true;

    public TiltSettings Tilt { get; set; } = new();
    public List<MappingSet> Mappings { get; set; } = [];
    public DateTime LastSeen { get; set; }

    /// <summary>Returns the binding set for the combination, creating it from defaults if missing.</summary>
    public MappingSet GetMapping(DeviceKind device, OutputKind output)
    {
        var set = Mappings.FirstOrDefault(m => m.Device == device && m.Output == output);
        if (set is null)
        {
            set = new MappingSet { Device = device, Output = output, Bindings = DefaultMappings.Create(device, output) };
            Mappings.Add(set);
        }
        return set;
    }
}

public sealed class AppConfig
{
    public List<ControllerConfig> Controllers { get; set; } = [];
    public bool ShowDebugLog { get; set; }

    /// <summary>Turn off Wiimotes nobody has touched for this many minutes. 0 = never.</summary>
    public int AutoOffMinutes { get; set; } = 10;

    /// <summary>Minimising the window hides it to the notification area (tray).</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>The window's close button hides it to the tray instead of exiting.</summary>
    public bool CloseToTray { get; set; }
}

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _sync = new();

    public ConfigStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WiiHub", "config.json");
    }

    public string Path { get; }

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path), Options) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {Path}: {ex.Message}. Starting with defaults.");
            try { File.Copy(Path, Path + ".broken", overwrite: true); } catch { /* ignore */ }
        }
        return new AppConfig();
    }

    public void Save(AppConfig config)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                string json = JsonSerializer.Serialize(config, Options);
                string tmp = Path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, Path, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error($"Could not save settings: {ex.Message}");
            }
        }
    }

    public static string Serialize(AppConfig config) => JsonSerializer.Serialize(config, Options);
    public static AppConfig? Deserialize(string json) => JsonSerializer.Deserialize<AppConfig>(json, Options);
}
