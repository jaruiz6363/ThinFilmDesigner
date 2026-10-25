// ThinFilmHome.cs — where a person's own ThinFilm files live, and the settings kept there.
//
// %APPDATA%\ThinFilm by default; the THINFILM_HOME environment variable moves it, which is what
// the GUI tests do, so that a test run never reads or writes the profile of the person running it.

using System.Text.Json;

namespace ThinFilm.Api;

/// <summary>Settings that belong to the person, not to a design.</summary>
public sealed record AppSettings
{
    /// <summary>The material database to choose from, or null for the default in ThinFilmHome.</summary>
    public string? MaterialDatabasePath { get; init; }
}

public static class ThinFilmHome
{
    public static string Directory =>
        Environment.GetEnvironmentVariable("THINFILM_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ThinFilm");

    public static string SettingsPath => Path.Combine(Directory, "settings.json");

    /// <summary>Where optimisation run logs are written, one file per run: %LOCALAPPDATA%\ThinFilm\logs,
    /// or a logs folder under THINFILM_HOME when that is set.</summary>
    public static string LogDirectory =>
        Environment.GetEnvironmentVariable("THINFILM_HOME") is { Length: > 0 } home
            ? Path.Combine(home, "logs")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThinFilm", "logs");

    /// <summary>The settings, or the defaults when there is no file. A settings file that does not
    /// parse is reported, not ignored: the caller shows the message and carries on with defaults.</summary>
    public static AppSettings LoadSettings(out string? problem)
    {
        problem = null;
        if (!File.Exists(SettingsPath)) return new AppSettings();
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings(); }
        catch (JsonException e)
        {
            problem = $"{SettingsPath} could not be read ({e.Message}); using the defaults";
            return new AppSettings();
        }
    }

    public static void SaveSettings(AppSettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
