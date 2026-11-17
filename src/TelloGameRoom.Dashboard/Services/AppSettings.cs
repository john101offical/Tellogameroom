using System.IO;
using System.Text.Json;
using Serilog;

namespace TelloGameRoom.Dashboard.Services;

/// <summary>Per-Windows-user preferences of the Dashboard, stored in <c>%LocalAppData%\TelloGameRoom\Dashboard</c>.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string? ServerUrl { get; set; }

    public string[] PinnedCertificates { get; set; } = [];

    /// <summary>"en" or "es"; null follows Windows.</summary>
    public string? Language { get; set; }

    /// <summary>"Dark" (default) or "Light".</summary>
    public string Theme { get; set; } = "Dark";

    public string? LastUsername { get; set; }

    public Guid? LastBranchId { get; set; }

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TelloGameRoom", "Dashboard");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }

            // The installer can pre-seed the server address for every user of the PC (/SERVER=...).
            var preset = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TelloGameRoom", "Dashboard", "server.json");
            if (File.Exists(preset))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(preset)) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not read settings; using defaults");
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not save settings");
        }
    }
}
