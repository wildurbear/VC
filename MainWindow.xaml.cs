using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace VideoCropper
{
    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings;
        private FfmpegService? _ffmpeg;

        private string? _inputPath;
        private VideoInfo? _info;
        private string? _currentFramePng;

        // Displayed-frame size in device-independent pixels (the canvas size).
        private double _dispW, _dispH;

        // Crop rectangle in display pixels.
        private double _cropX, _cropY, _cropW, _cropH;

        private const double MinCropDisplay = 16;

        private bool _suppressScaleSync;
        private CancellationTokenSource? _cts;
        private bool _isEncoding;

        public MainWindow()
        {
            InitializeComponent();

            _settings = AppSettings.Load();
            FfmpegDirBox.Text = _settings.FfmpegDir;
            RefreshFfmpegService();

            ScrubSlider.ValueChanged += (_, __) =>
                ScrubTimeText.Text = FormatTime(ScrubSlider.Value);
        }

        // ===================== FFmpeg location =====================

        private void RefreshFfmpegService()
        {
            if (_settings.IsFfmpegValid)
            {
                _ffmpeg = new FfmpegService(_settings.FfmpegPath, _settings.FfprobePath);
                FfmpegStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
                FfmpegStatusText.Text = "Found ffmpeg.exe and ffprobe.exe.";
            }
            else
            {
                _ffmpeg = null;
                FfmpegStatusText.Foreground = System.Windows.Media.Brushes.Salmon;
                FfmpegStatusText.Text =
                    "ffmpeg.exe / ffprobe.exe not found in this folder. Set the folder that contains them.";
            }
        }

        private void FfmpegBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select the folder containing ffmpeg.exe and ffprobe.exe",
                InitialDirectory = Directory.Exists(_settings.FfmpegDir)
                    ? _settings.FfmpegDir
                    : AppSettings.DefaultFfmpegDir
            };
            if (dlg.ShowDialog() == true)
            {
                _settings.FfmpegDir = dlg.FolderName;
                _settings.Save();
                FfmpegDirBox.Text = _settings.FfmpegDir;
                RefreshFfmpegService();
            }
        }

        // ===================== Open / drag-drop =====================

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Open a video",
                Filter = "Video files|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v;*.wmv;*.flv;*.mpg;*.mpeg|All files|*.*"
            };
            if (dlg.ShowDialog() == true)
                _ = LoadVideoAsync(dlg.FileName);
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (_isEncoding) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                _ = LoadVideoAsync(files[0]);
            }
        }

        private async Task LoadVideoAsync(string path)
        {
            if (_ffmpeg == null)
            {
                MessageBox.Show(this,
                    "FFmpeg isn't configured yet. Set the FFmpeg folder (it must contain ffmpeg.exe and ffprobe.exe).",
                    "FFmpeg not found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                StatusText.Text = "Reading video…";
                _inputPath = path;
                InputPathText.Text = path;
                InputPathText.Foreground = System.Windows.Media.Brushes.White;

                _info = await _ffmpeg.ProbeAsync(path);

                // Scrub slider spans the duration.
                ScrubSlider.Maximum = Math.Max(0.0, _info.DurationSeconds);
                ScrubSlider.Value = 0;
                ScrubSlider.IsEnabled = _info.DurationSeconds > 0;
                ScrubTimeText.Text = FormatTime(0);

                await LoadFrameAsync(0);
                SuggestOutputPath();
                StatusText.Text = "Ready";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Failed to load video";
                MessageBox.Show(this, ex.Message, "Could not read video",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadFrameAsync(double atSeconds)
        {
            if (_ffmpeg == null || _inputPath == null || _info == null) return;

            string png = await _ffmpeg.ExtractFrameAsync(_inputPath, atSeconds);

            // Load fully into memory so the temp file can be deleted immediately.
            var bmp = new BitmapImage();
            using (var fs = File.OpenRead(png))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = fs;
                bmp.EndInit();
            }
            bmp.Freeze();
            try { File.Delete(png); } catch { /* temp; ignore */ }

            bool first = _currentFramePng == null;
            _currentFramePng = png;

            PreviewImage.Source = bmp;
            PreviewHint.Visibility = Visibility.Collapsed;

            // Fit the frame into the available preview area (only (re)size on first load).
            if (first || _dispW <= 0)
            {
                var host = (FrameworkElement)CropCanvas.Parent;
                double availW = host.ActualWidth - 16;
                double availH = host.ActualHeight - 16;
                if (availW < 50) availW = 720;
                if (availH < 50) availH = 480;

                double ratio = Math.Min(availW / _info.Width, availH / _info.Height);
                if (ratio <= 0 || double.IsInfinity(ratio)) ratio = 1.0;

                _dispW = Math.Round(_info.Width * ratio);
                _dispH = Math.Round(_info.Height * ratio);

                CropCanvas.Width = _dispW;
                CropCanvas.Height = _dispH;
                PreviewImage.Width = _dispW;
                PreviewImage.Height = _dispH;

                ResetCropToFull();
                ShowCropOverlay(true);
            }
        }

        private void ScrubSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _ = LoadFrameAsync(ScrubSlider.Value);
        }

        // ===================== Crop overlay =====================

        private void ShowCropOverlay(bool show)
        {
            var v = show ? Visibility.Visible : Visibility.Collapsed;
            CropRect.Visibility = v;
            MoveThumb.Visibility = v;
            TLThumb.Visibility = v;
            TRThumb.Visibility = v;
            BLThumb.Visibility = v;
            BRThumb.Visibility = v;
        }

        private void ResetCropToFull()
        {
            _cropX = 0; _cropY = 0; _cropW = _dispW; _cropH = _dispH;
            UpdateCropVisual();
        }

        private void ResetCrop_Click(object sender, RoutedEventArgs e)
        {
            if (_dispW > 0) ResetCropToFull();
        }

        private void ApplyCrop(double x, double y, double w, double h)
        {
            w = Math.Max(MinCropDisplay, w);
            h = Math.Max(MinCropDisplay, h);
            x = Math.Clamp(x, 0, Math.Max(0, _dispW - MinCropDisplay));
            y = Math.Clamp(y, 0, Math.Max(0, _dispH - MinCropDisplay));
            w = Math.Min(w, _dispW - x);
            h = Math.Min(h, _dispH - y);

            _cropX = x; _cropY = y; _cropW = w; _cropH = h;
            UpdateCropVisual();
        }

        private void UpdateCropVisual()
        {
            Canvas.SetLeft(CropRect, _cropX);
            Canvas.SetTop(CropRect, _cropY);
            CropRect.Width = _cropW;
            CropRect.Height = _cropH;

            Canvas.SetLeft(MoveThumb, _cropX);
            Canvas.SetTop(MoveThumb, _cropY);
            MoveThumb.Width = _cropW;
            MoveThumb.Height = _cropH;

            PlaceCorner(TLThumb, _cropX, _cropY);
            PlaceCorner(TRThumb, _cropX + _cropW, _cropY);
            PlaceCorner(BLThumb, _cropX, _cropY + _cropH);
            PlaceCorner(BRThumb, _cropX + _cropW, _cropY + _cropH);

            var (sx, sy, sw, sh) = GetSourceCrop();
            CropSizeText.Text = $"{sw} × {sh}";
            CropOffsetText.Text = $"x={sx}, y={sy}";

            // Keep aspect-linked size box(es) consistent with the new crop ratio.
            ResyncScaleBoxes();
        }

        private static void PlaceCorner(Thumb t, double cx, double cy)
        {
            Canvas.SetLeft(t, cx - t.Width / 2);
            Canvas.SetTop(t, cy - t.Height / 2);
        }

        private void MoveThumb_DragDelta(object sender, DragDeltaEventArgs e)
            => ApplyCrop(_cropX + e.HorizontalChange, _cropY + e.VerticalChange, _cropW, _cropH);

        private void TLThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double right = _cropX + _cropW, bottom = _cropY + _cropH;
            double nx = _cropX + e.HorizontalChange, ny = _cropY + e.VerticalChange;
            ApplyCrop(nx, ny, right - nx, bottom - ny);
        }

        private void TRThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double bottom = _cropY + _cropH;
            double ny = _cropY + e.VerticalChange;
            ApplyCrop(_cropX, ny, _cropW + e.HorizontalChange, bottom - ny);
        }

        private void BLThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double right = _cropX + _cropW;
            double nx = _cropX + e.HorizontalChange;
            ApplyCrop(nx, _cropY, right - nx, _cropH + e.VerticalChange);
        }

        private void BRThumb_DragDelta(object sender, DragDeltaEventArgs e)
            => ApplyCrop(_cropX, _cropY, _cropW + e.HorizontalChange, _cropH + e.VerticalChange);

        // Map the on-screen crop rect back to SOURCE pixels: snap even + clamp.
        private (int x, int y, int w, int h) GetSourceCrop()
        {
            if (_info == null || _dispW <= 0 || _dispH <= 0) return (0, 0, 0, 0);

            double sx = _info.Width / _dispW;
            double sy = _info.Height / _dispH;

            int x = (int)Math.Round(_cropX * sx);
            int y = (int)Math.Round(_cropY * sy);
            int w = (int)Math.Round(_cropW * sx);
            int h = (int)Math.Round(_cropH * sy);

            // Even dimensions; offsets stay even too so the crop lands cleanly.
            x = SnapEvenDown(x);
            y = SnapEvenDown(y);
            w = SnapEvenDown(w);
            h = SnapEvenDown(h);

            // Clamp inside the source frame.
            x = Math.Clamp(x, 0, Math.Max(0, _info.Width - 2));
            y = Math.Clamp(y, 0, Math.Max(0, _info.Height - 2));
            if (x + w > _info.Width) w = SnapEvenDown(_info.Width - x);
            if (y + h > _info.Height) h = SnapEvenDown(_info.Height - y);
            w = Math.Max(2, w);
            h = Math.Max(2, h);

            return (x, y, w, h);
        }

        private static int SnapEvenDown(int v) => v - (v % 2);

        private bool IsFullFrameCrop()
        {
            var (x, y, w, h) = GetSourceCrop();
            return x == 0 && y == 0 &&
                   w >= _info!.Width - 1 && h >= _info!.Height - 1;
        }

        // ===================== Resize boxes =====================

        private double CurrentAspect()
        {
            var (_, _, w, h) = GetSourceCrop();
            if (w > 0 && h > 0) return (double)w / h;
            if (_info != null && _info.Height > 0) return (double)_info.Width / _info.Height;
            return 1.0;
        }

        private void AspectLock_Changed(object sender, RoutedEventArgs e) => ResyncScaleBoxes();

        // When locked, keep both size boxes proportional to the crop so the output never stretches.
        private void ResyncScaleBoxes()
        {
            if (!IsLoaded) return;
            if (AspectLockCheck.IsChecked != true) return;
            if (_suppressScaleSync) return;

            // Drive height from width if width has a value; otherwise drive width from height.
            int? w = ParseDim(ScaleWidthBox.Text);
            int? h = ParseDim(ScaleHeightBox.Text);
            double aspect = CurrentAspect();
            if (aspect <= 0) return;

            _suppressScaleSync = true;
            try
            {
                if (w.HasValue)
                    ScaleHeightBox.Text = SnapEvenDown((int)Math.Round(w.Value / aspect)).ToString(CultureInfo.InvariantCulture);
                else if (h.HasValue)
                    ScaleWidthBox.Text = SnapEvenDown((int)Math.Round(h.Value * aspect)).ToString(CultureInfo.InvariantCulture);
            }
            finally { _suppressScaleSync = false; }
        }

        private void ScaleWidthBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressScaleSync || !IsLoaded) return;
            if (AspectLockCheck.IsChecked != true) return;
            int? w = ParseDim(ScaleWidthBox.Text);
            if (!w.HasValue) return;
            double aspect = CurrentAspect();
            if (aspect <= 0) return;
            _suppressScaleSync = true;
            ScaleHeightBox.Text = SnapEvenDown((int)Math.Round(w.Value / aspect)).ToString(CultureInfo.InvariantCulture);
            _suppressScaleSync = false;
        }

        private void ScaleHeightBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressScaleSync || !IsLoaded) return;
            if (AspectLockCheck.IsChecked != true) return;
            int? h = ParseDim(ScaleHeightBox.Text);
            if (!h.HasValue) return;
            double aspect = CurrentAspect();
            if (aspect <= 0) return;
            _suppressScaleSync = true;
            ScaleWidthBox.Text = SnapEvenDown((int)Math.Round(h.Value * aspect)).ToString(CultureInfo.InvariantCulture);
            _suppressScaleSync = false;
        }

        private static int? ParseDim(string? text)
        {
            if (int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0)
                return v;
            return null;
        }

        // ===================== Audio / format =====================

        private void AudioMode_Changed(object sender, RoutedEventArgs e)
        {
            if (VolumeSlider != null)
                VolumeSlider.IsEnabled = AudioVolume.IsChecked == true;
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolumeText != null)
                VolumeText.Text = $"{e.NewValue * 100:0}%";
        }

        private void Format_Changed(object sender, RoutedEventArgs e)
        {
            if (NvencCheck != null)
                NvencCheck.IsEnabled = FormatMp4.IsChecked == true;
            if (IsLoaded) SwapOutputExtension();
        }

        // ===================== Output path =====================

        private string CurrentExt() => FormatWebm.IsChecked == true ? ".webm" : ".mp4";

        private void SuggestOutputPath()
        {
            if (_inputPath == null) return;
            string dir = _settings.LastOutputDir is { } d && Directory.Exists(d)
                ? d
                : Path.GetDirectoryName(_inputPath) ?? "";
            string name = Path.GetFileNameWithoutExtension(_inputPath) + "_cropped" + CurrentExt();
            OutputPathBox.Text = Path.Combine(dir, name);
        }

        private void SwapOutputExtension()
        {
            string cur = OutputPathBox.Text;
            if (string.IsNullOrWhiteSpace(cur)) return;
            try
            {
                string dir = Path.GetDirectoryName(cur) ?? "";
                string name = Path.GetFileNameWithoutExtension(cur);
                OutputPathBox.Text = Path.Combine(dir, name + CurrentExt());
            }
            catch { /* leave as-is on weird paths */ }
        }

        private void OutputBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save output as",
                Filter = "MP4 video|*.mp4|WebM video|*.webm|All files|*.*",
                FilterIndex = FormatWebm.IsChecked == true ? 2 : 1,
                FileName = string.IsNullOrWhiteSpace(OutputPathBox.Text)
                    ? "output" + CurrentExt()
                    : Path.GetFileName(OutputPathBox.Text)
            };
            if (!string.IsNullOrWhiteSpace(OutputPathBox.Text))
            {
                string? d = Path.GetDirectoryName(OutputPathBox.Text);
                if (Directory.Exists(d)) dlg.InitialDirectory = d;
            }
            if (dlg.ShowDialog() == true)
                OutputPathBox.Text = dlg.FileName;
        }

        // ===================== Build settings =====================

        private CropSettings BuildSettings()
        {
            if (_info == null) throw new InvalidOperationException("No video loaded.");

            var s = new CropSettings
            {
                SourceWidth = _info.Width,
                SourceHeight = _info.Height,
                AspectLock = AspectLockCheck.IsChecked == true,
                Format = FormatWebm.IsChecked == true ? OutputFormat.Webm : OutputFormat.Mp4,
                UseNvenc = NvencCheck.IsChecked == true,
            };

            if (!IsFullFrameCrop())
            {
                var (x, y, w, h) = GetSourceCrop();
                s.CropX = x; s.CropY = y; s.CropWidth = w; s.CropHeight = h;
            }

            s.ScaleWidth = ParseDim(ScaleWidthBox.Text);
            s.ScaleHeight = ParseDim(ScaleHeightBox.Text);

            // Aspect lock + both axes filled would stretch unless proportional; the UI keeps them
            // proportional, but to honor the -2 invariant strictly we drop the height and let the
            // builder emit scale=W:-2 whenever a width is present.
            if (s.AspectLock && s.ScaleWidth.HasValue)
                s.ScaleHeight = null;

            if (AudioMute.IsChecked == true) s.Audio = AudioMode.Mute;
            else if (AudioVolume.IsChecked == true) { s.Audio = AudioMode.Volume; s.Volume = VolumeSlider.Value; }
            else s.Audio = AudioMode.Keep;

            return s;
        }

        // ===================== Copy command =====================

        private void CopyCmd_Click(object sender, RoutedEventArgs e)
        {
            if (!Preflight(out string output)) return;
            try
            {
                var args = CropCommandBuilder.Build(_inputPath!, output, BuildSettings());
                string cmd = QuoteArg(_settings.FfmpegPath) + " " + string.Join(" ", args.ConvertAll(QuoteArg));
                Clipboard.SetText(cmd);
                StatusText.Text = "Command copied";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Cannot build command",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string QuoteArg(string a) =>
            a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;

        // ===================== Run / cancel =====================

        private bool Preflight(out string output)
        {
            output = OutputPathBox.Text.Trim();

            if (_ffmpeg == null)
            {
                MessageBox.Show(this, "FFmpeg isn't configured. Set the FFmpeg folder first.",
                    "FFmpeg not found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (_inputPath == null || _info == null)
            {
                MessageBox.Show(this, "Open a video first.", "No video",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(output))
            {
                MessageBox.Show(this, "Choose an output file path.", "No output path",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            if (_isEncoding) return;
            if (!Preflight(out string output)) return;

            if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_inputPath!),
                    StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Output path is the same as the input. Choose a different file.",
                    "Same file", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            System.Collections.Generic.List<string> args;
            try
            {
                args = CropCommandBuilder.Build(_inputPath!, output, BuildSettings());
            }
            catch (Exception ex)
            {
                // This is how odd-number / out-of-bounds builder errors reach the user.
                MessageBox.Show(this, ex.Message, "Invalid settings",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); } catch { }

            _cts = new CancellationTokenSource();
            SetEncodingUi(true);
            var progress = new Progress<double>(p =>
            {
                EncodeProgress.Value = p;
                StatusText.Text = $"Encoding… {p:P0}";
            });

            try
            {
                await _ffmpeg!.RunWithProgressAsync(args, _info!.DurationSeconds, progress, _cts.Token);

                EncodeProgress.Value = 1;
                StatusText.Text = "Done";

                // Remember output folder for next time.
                string? outDir = Path.GetDirectoryName(Path.GetFullPath(output));
                if (Directory.Exists(outDir)) { _settings.LastOutputDir = outDir; _settings.Save(); }

                if (MessageBox.Show(this, "Encode complete.\n\nOpen the output folder?",
                        "Done", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    try
                    {
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{Path.GetFullPath(output)}\"");
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Canceled";
                EncodeProgress.Value = 0;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Failed";
                EncodeProgress.Value = 0;
                MessageBox.Show(this, ex.Message, "Encode failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetEncodingUi(false);
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void SetEncodingUi(bool encoding)
        {
            _isEncoding = encoding;
            RunButton.Visibility = encoding ? Visibility.Collapsed : Visibility.Visible;
            CancelButton.Visibility = encoding ? Visibility.Visible : Visibility.Collapsed;
            OpenButton.IsEnabled = !encoding;
            CopyCmdButton.IsEnabled = !encoding;
        }

        // ===================== helpers =====================

        private static string FormatTime(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
            var t = TimeSpan.FromSeconds(seconds);
            return t.Hours > 0
                ? $"{(int)t.TotalHours:0}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100:0}";
        }
    }
}
