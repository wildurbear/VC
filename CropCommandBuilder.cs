using System;
using System.Collections.Generic;
using System.Globalization;

namespace VideoCropper
{
    public enum OutputFormat { Mp4, Webm }
    public enum AudioMode { Keep, Mute, Volume }

    public class CropSettings
    {
        // Source dimensions (from ffprobe)
        public int SourceWidth { get; set; }
        public int SourceHeight { get; set; }

        // Crop, in SOURCE pixels. Leave Width/Height null for "no crop".
        public int? CropWidth { get; set; }
        public int? CropHeight { get; set; }
        public int CropX { get; set; }
        public int CropY { get; set; }

        // Resize. With AspectLock on, set ONE axis and leave the other null;
        // FFmpeg computes the missing one. With it off, set both to stretch.
        public int? ScaleWidth { get; set; }
        public int? ScaleHeight { get; set; }
        public bool AspectLock { get; set; } = true; // false = allow abnormal stretch

        // Trim / cut, in seconds from the start of the source. Null = no trim on that end,
        // i.e. TrimStart null = from the beginning, TrimEnd null = to the end.
        public double? TrimStart { get; set; }
        public double? TrimEnd { get; set; }

        // Audio
        public AudioMode Audio { get; set; } = AudioMode.Keep;
        public double Volume { get; set; } = 1.0; // used when Audio == Volume (1.0 = unchanged)

        // Output
        public OutputFormat Format { get; set; } = OutputFormat.Mp4;
        public bool UseNvenc { get; set; } = true; // ignored for webm (NVENC can't encode VP9)
    }

    public static class CropCommandBuilder
    {
        // Returns an argument LIST (not a string) so paths with spaces never need quoting.
        // Feed it straight into ProcessStartInfo.ArgumentList.
        public static List<string> Build(string inputPath, string outputPath, CropSettings s)
        {
            var args = new List<string> { "-y" }; // overwrite output

            // ---- TRIM / CUT ----
            // -ss before -i = fast, accurate seek when re-encoding (decodes from the nearest
            // keyframe and discards up to the cut point). Express the end as -t DURATION so it
            // stays unambiguous regardless of the input seek.
            double start = Math.Max(0, s.TrimStart ?? 0);
            if (start > 0)
            {
                args.Add("-ss");
                args.Add(start.ToString("0.###", CultureInfo.InvariantCulture));
            }

            args.Add("-i");
            args.Add(inputPath);

            if (s.TrimEnd.HasValue)
            {
                double dur = s.TrimEnd.Value - start;
                if (dur <= 0)
                    throw new ArgumentException("Trim end must be after the trim start.");
                args.Add("-t");
                args.Add(dur.ToString("0.###", CultureInfo.InvariantCulture));
            }

            string vf = BuildVideoFilter(s);
            if (vf.Length > 0)
            {
                args.Add("-vf");
                args.Add(vf);
            }

            // ---- Video codec ----
            if (s.Format == OutputFormat.Webm)
            {
                args.Add("-c:v"); args.Add("libvpx-vp9"); // VP9 = CPU; NVENC doesn't do it
            }
            else
            {
                args.Add("-c:v"); args.Add(s.UseNvenc ? "h264_nvenc" : "libx264");
                args.Add("-pix_fmt"); args.Add("yuv420p"); // broad player compatibility
            }

            // ---- Audio ----
            switch (s.Audio)
            {
                case AudioMode.Mute:
                    args.Add("-an");
                    break;
                case AudioMode.Volume:
                    args.Add("-af");
                    args.Add("volume=" + s.Volume.ToString(CultureInfo.InvariantCulture));
                    AddAudioCodec(args, s.Format);
                    break;
                default: // Keep
                    AddAudioCodec(args, s.Format);
                    break;
            }

            // ---- Machine-readable progress on stdout ----
            args.Add("-progress"); args.Add("pipe:1");
            args.Add("-nostats");

            args.Add(outputPath);
            return args;
        }

        private static void AddAudioCodec(List<string> args, OutputFormat fmt)
        {
            args.Add("-c:a");
            args.Add(fmt == OutputFormat.Webm ? "libopus" : "aac");
        }

        private static string BuildVideoFilter(CropSettings s)
        {
            var parts = new List<string>();

            // ---- CROP ----
            if (s.CropWidth.HasValue && s.CropHeight.HasValue)
            {
                int cw = EnsureEven(s.CropWidth.Value, "Crop width");
                int ch = EnsureEven(s.CropHeight.Value, "Crop height");

                if (s.CropX < 0 || s.CropY < 0 ||
                    s.CropX + cw > s.SourceWidth ||
                    s.CropY + ch > s.SourceHeight)
                {
                    throw new ArgumentException("Crop rectangle falls outside the source video.");
                }

                parts.Add($"crop={cw}:{ch}:{s.CropX}:{s.CropY}");
            }

            // ---- SCALE ----
            if (s.AspectLock)
            {
                // -2 = "auto-compute this axis AND keep it divisible by 2" (no stretch, no odd numbers)
                if (s.ScaleWidth.HasValue && !s.ScaleHeight.HasValue)
                    parts.Add($"scale={EnsureEven(s.ScaleWidth.Value, "Output width")}:-2");
                else if (s.ScaleHeight.HasValue && !s.ScaleWidth.HasValue)
                    parts.Add($"scale=-2:{EnsureEven(s.ScaleHeight.Value, "Output height")}");
                else if (s.ScaleWidth.HasValue && s.ScaleHeight.HasValue)
                    parts.Add($"scale={EnsureEven(s.ScaleWidth.Value, "Output width")}:{EnsureEven(s.ScaleHeight.Value, "Output height")}");
            }
            else if (s.ScaleWidth.HasValue && s.ScaleHeight.HasValue)
            {
                // Stretch: both literal.
                parts.Add($"scale={EnsureEven(s.ScaleWidth.Value, "Output width")}:{EnsureEven(s.ScaleHeight.Value, "Output height")}");
            }

            return string.Join(",", parts);
        }

        private static int EnsureEven(int value, string label)
        {
            if (value <= 0)
                throw new ArgumentException($"{label} must be positive.");
            if (value % 2 != 0)
                throw new ArgumentException($"{label} ({value}) is odd. Video dimensions must be even numbers.");
            return value;
        }
    }
}
