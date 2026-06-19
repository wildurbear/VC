using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoCropper
{
    // Small JSON-backed settings store for things that must survive between runs:
    // chiefly where FFmpeg lives. Persisted to %AppData%\VideoCropper\settings.json.
    // No third-party deps (CLAUDE.md): System.Text.Json only.
    public class AppSettings
    {
        // Folder that contains ffmpeg.exe and ffprobe.exe.
        public string FfmpegDir { get; set; } = DefaultFfmpegDir;

        // Remembered output folder for convenience (Likely-next-task #6).
        public string? LastOutputDir { get; set; }

        public const string DefaultFfmpegDir = @"C:\FFMPEG\bin";

        [JsonIgnore]
        public string FfmpegPath => Path.Combine(FfmpegDir, "ffmpeg.exe");

        [JsonIgnore]
        public string FfprobePath => Path.Combine(FfmpegDir, "ffprobe.exe");

        // Both binaries present?
        [JsonIgnore]
        public bool IsFfmpegValid =>
            File.Exists(FfmpegPath) && File.Exists(FfprobePath);

        // ---- persistence ----

        private static string SettingsDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VideoCropper");

        private static string SettingsFile => Path.Combine(SettingsDir, "settings.json");

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string json = File.ReadAllText(SettingsFile);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null) return s;
                }
            }
            catch
            {
                // Corrupt/unreadable settings shouldn't block startup — fall back to defaults.
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
            }
            catch
            {
                // Best-effort; a failed save shouldn't crash the app.
            }
        }
    }
}
