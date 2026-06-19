using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VideoCropper
{
    public record VideoInfo(int Width, int Height, double DurationSeconds);

    public class FfmpegService
    {
        private readonly string _ffmpeg;
        private readonly string _ffprobe;

        // e.g. new FfmpegService(@"C:\FFMPEG\bin\ffmpeg.exe", @"C:\FFMPEG\bin\ffprobe.exe")
        public FfmpegService(string ffmpegPath, string ffprobePath)
        {
            _ffmpeg = ffmpegPath;
            _ffprobe = ffprobePath;
        }

        // ---- Read width/height/duration ----
        public async Task<VideoInfo> ProbeAsync(string inputPath)
        {
            var args = new List<string>
            {
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream=width,height",
                "-show_entries", "format=duration",
                "-of", "json",
                inputPath
            };

            var (stdout, _, _) = await RunCaptureAsync(_ffprobe, args);
            using var doc = JsonDocument.Parse(stdout);

            var stream = doc.RootElement.GetProperty("streams")[0];
            int w = stream.GetProperty("width").GetInt32();
            int h = stream.GetProperty("height").GetInt32();
            double dur = double.Parse(
                doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
                CultureInfo.InvariantCulture);

            return new VideoInfo(w, h, dur);
        }

        // ---- Grab one frame to a temp PNG for the crop canvas ----
        public async Task<string> ExtractFrameAsync(string inputPath, double atSeconds)
        {
            string outPng = Path.Combine(Path.GetTempPath(), $"crop_preview_{Guid.NewGuid():N}.png");

            var args = new List<string>
            {
                "-ss", atSeconds.ToString(CultureInfo.InvariantCulture), // seek before -i = fast
                "-i", inputPath,
                "-frames:v", "1",
                "-q:v", "2",
                "-y", outPng
            };

            await RunCaptureAsync(_ffmpeg, args);
            return outPng; // delete it after you've loaded it into a BitmapImage
        }

        // ---- Run the encode and report progress 0.0 -> 1.0 ----
        public async Task RunWithProgressAsync(
            List<string> args,
            double durationSeconds,
            IProgress<double> progress,
            CancellationToken ct = default)
        {
            var psi = NewPsi(_ffmpeg, args);
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;

                // -progress emits key=value lines. out_time is HH:MM:SS.micro and unambiguous
                // (avoid out_time_ms / out_time_us — their meaning has shifted across FFmpeg builds).
                if (e.Data.StartsWith("out_time=", StringComparison.Ordinal))
                {
                    string ts = e.Data["out_time=".Length..].Trim();
                    if (TimeSpan.TryParse(ts, CultureInfo.InvariantCulture, out var t) && durationSeconds > 0)
                        progress.Report(Math.Clamp(t.TotalSeconds / durationSeconds, 0, 1));
                }
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine(); // drain stderr so the pipe never fills and blocks ffmpeg

            // If the caller cancels, actually stop FFmpeg (kill the whole tree) rather than
            // just abandoning the await and leaving an orphaned encode running.
            using (ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
            }))
            {
                await proc.WaitForExitAsync(ct);
            }

            if (proc.ExitCode != 0)
                throw new Exception($"FFmpeg exited with code {proc.ExitCode}.");

            progress.Report(1.0);
        }

        // ---- helpers ----
        private static ProcessStartInfo NewPsi(string exe, List<string> args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a); // ArgumentList = no manual quoting
            return psi;
        }

        private static async Task<(string stdout, string stderr, int code)> RunCaptureAsync(
            string exe, List<string> args)
        {
            var psi = NewPsi(exe, args);
            using var proc = new Process { StartInfo = psi };
            proc.Start();
            string outStr = await proc.StandardOutput.ReadToEndAsync();
            string errStr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return (outStr, errStr, proc.ExitCode);
        }
    }
}
