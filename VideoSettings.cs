using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Forms;

public sealed class VideoPreferences
{
    public bool Experienced;
    public bool Idgaf;
    public int SimplePreset;
    public string Encoder = "auto";
    public string Codec = "h264";
    public int Width;
    public int Height;
    public int Fps = 60;
    public int Bitrate = 25;
    public int ConstantQuality = 23;
    public string Rate = "CBR";
    public string Preset = "Balanced";
    public int Keyframes = 2;

    public VideoPreferences Copy() { return (VideoPreferences)MemberwiseClone(); }
}

sealed partial class ReplayShelf
{
    sealed class VideoEncoder
    {
        public readonly string Id;
        public readonly string Label;
        public readonly string[] Codecs;
        public VideoEncoder(string id, string label, params string[] codecs)
        {
            Id = id;
            Label = label;
            Codecs = codecs;
        }
        public override string ToString() { return Label; }
        public string Name(string codec) { return Id == "cpu" ? "libx264" : codec + "_" + Id; }
    }

    static readonly VideoEncoder Automatic = new VideoEncoder("auto", "Auto");
    static readonly VideoEncoder[] EncoderCandidates =
    {
        new VideoEncoder("nvenc", "NVIDIA NVENC", "h264", "hevc"),
        new VideoEncoder("qsv", "Intel Quick Sync", "h264", "hevc"),
        new VideoEncoder("amf", "AMD AMF", "h264", "hevc"),
        new VideoEncoder("cpu", "CPU (x264)", "h264")
    };
    static readonly Lazy<Task<VideoEncoder[]>> Encoders =
        new Lazy<Task<VideoEncoder[]>>(() => Task.Run(() =>
        {
            var result = new List<VideoEncoder>();
            var hardware = InstalledEncoderFamilies();
            foreach (var candidate in EncoderCandidates.Where(item => item.Id == "cpu" || hardware.Count == 0 || hardware.Contains(item.Id)))
            {
                var usable = new List<string>();
                foreach (string codec in candidate.Codecs)
                {
                    try
                    {
                        RunTool("ffmpeg.exe", "-v error -nostdin -f lavfi -i color=s=256x144:r=30 -frames:v 1 " +
                            EncodeOptions(candidate, codec, "CBR", 10, 23, "Balanced") + " -f null NUL", 4000);
                        usable.Add(codec);
                    }
                    catch (IOException) { }
                }
                if (usable.Count > 0) result.Add(new VideoEncoder(candidate.Id, candidate.Label, usable.ToArray()));
            }
            return result.ToArray();
        }));

    static HashSet<string> InstalledEncoderFamilies()
    {
        var families = new HashSet<string>();
        try
        {
            using (var search = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_VideoController"))
            using (var adapters = search.Get())
            {
                foreach (ManagementObject adapter in adapters)
                {
                    using (adapter)
                    {
                        var id = Convert.ToString(adapter["PNPDeviceID"]);
                        if (id.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0) families.Add("nvenc");
                        if (id.IndexOf("VEN_8086", StringComparison.OrdinalIgnoreCase) >= 0) families.Add("qsv");
                        if (id.IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0) families.Add("amf");
                    }
                }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        return families;
    }

    static string EncodeOptions(VideoEncoder encoder, string codec, string rate, int bitrate, int quality, string preset)
    {
        string options = "-c:v " + encoder.Name(codec) + " ";
        string family = encoder.Id;
        if (family == "nvenc")
            options += "-preset " + (preset == "Fast" ? "p2" : preset == "Quality" ? "p6" : "p4") +
                " -rc " + (rate == "Constant quality" ? "constqp" : rate.ToLowerInvariant()) + " -forced-idr 1 ";
        if (family == "qsv")
            options += "-preset " + (preset == "Fast" ? "veryfast" : preset == "Quality" ? "slow" : "fast") + " -forced_idr 1 ";
        if (family == "amf")
            options += "-quality " + (preset == "Fast" ? "speed" : preset == "Quality" ? "quality" : "balanced") +
                " -rc " + (rate == "Constant quality" ? "cqp" : rate == "VBR" ? "vbr_peak" : "cbr") + " ";
        if (family == "cpu")
            options += "-preset " + (preset == "Fast" ? "ultrafast" : preset == "Quality" ? "fast" : "veryfast") + " -forced-idr 1 ";
        if (rate == "Constant quality")
        {
            if (family == "nvenc") return options + "-qp " + quality + " ";
            if (family == "qsv") return options + "-global_quality " + quality + " ";
            if (family == "amf") return options + "-qp_i " + quality + " -qp_p " + quality + " ";
            return options + "-crf " + quality + " ";
        }
        int peak = rate == "CBR" ? bitrate : bitrate * 3 / 2;
        if (family == "cpu" && rate == "CBR") options += "-x264-params nal-hrd=cbr ";
        return options + String.Format("-b:v {0}M -maxrate {1}M -bufsize {1}M ", bitrate, peak);
    }

    sealed class CaptureVideo
    {
        public readonly VideoEncoder Encoder;
        public readonly int Fps;
        public readonly int Keyframes;
        public readonly int Bitrate;
        public readonly int Width;
        public readonly int Height;
        public readonly string Rate;
        public readonly int Quality;
        public readonly string Filter;
        public readonly int ResizeWidth;
        public readonly int ResizeHeight;
        public readonly string Encode;
        public CaptureVideo(ShelfSettings settings, VideoEncoder[] available)
        {
            var video = settings.Video ?? LegacyVideo(settings);
            bool custom = video.Experienced;
            Encoder = available.FirstOrDefault(item => custom && item.Id == video.Encoder)
                ?? (custom && video.Encoder != "auto" ? null : available.FirstOrDefault(item => item.Codecs.Contains(custom ? video.Codec : "h264")));
            if (Encoder == null) throw new IOException("The selected encoder is unavailable. Open Video settings and choose another encoder.");
            string codec = custom ? video.Codec : "h264";
            if (!Encoder.Codecs.Contains(codec)) throw new IOException("The selected encoder cannot encode this codec. Open Video settings.");
            var display = Screen.AllScreens[Math.Min(settings.Display - 1, Screen.AllScreens.Length - 1)];
            var monitor = display.Bounds;
            var canvas = CaptureDisplaySize(display);
            bool cpu = Encoder.Id == "cpu";
            int simple = video.SimplePreset;
            Fps = custom ? video.Fps : cpu || simple == 1 ? 30 : 60;
            int presetBitrate = simple == 1 ? 10 : simple == 3 ? 25 : simple == 2 ? 16 : cpu ? 8 : 20;
            double scale = Math.Min(1, Math.Min((cpu || simple == 1 ? 1280.0 : 1920.0) / canvas.Width,
                (cpu || simple == 1 ? 720.0 : 1080.0) / canvas.Height));
            int width = custom && video.Width > 0 ? video.Width : (int)(canvas.Width * (custom ? 1 : scale) / 2) * 2;
            int height = custom && video.Height > 0 ? video.Height : (int)(canvas.Height * (custom ? 1 : scale) / 2) * 2;
            bool resize = width != monitor.Width || height != monitor.Height;
            if (resize)
            {
                ResizeWidth = width;
                ResizeHeight = height;
                Filter = "hwdownload,format=bgra,scale=" + width + ":" + height +
                    ",setsar=1,format=" + (cpu ? "yuv420p" : "nv12");
            }
            else if (Encoder.Id != "nvenc")
                Filter = "hwdownload,format=bgra,format=" + (cpu ? "yuv420p" : "nv12");
            Keyframes = custom && video.Idgaf ? video.Keyframes : 1;
            Rate = custom && video.Idgaf ? video.Rate : "CBR";
            Bitrate = custom ? video.Bitrate : presetBitrate;
            Width = ResizeWidth > 0 ? ResizeWidth : monitor.Width;
            Height = ResizeHeight > 0 ? ResizeHeight : monitor.Height;
            Quality = video.ConstantQuality;
            string preset = custom && video.Idgaf ? video.Preset : cpu ? "Fast" : "Balanced";
            Encode = EncodeOptions(Encoder, codec, Rate, Bitrate, Quality, preset);
        }
    }

    static string ClipSizeEstimate(CaptureVideo video, int seconds, bool audio, out string detail)
    {
        double sound = audio ? 0.192 : 0;
        const double overhead = 1.01;
        double multiplier = seconds / 8.0 * overhead * 1000000;
        if (video.Rate != "Constant quality")
        {
            ulong bytes = (ulong)Math.Round((video.Bitrate + sound) * multiplier);
            detail = "About " + ExactStorageSize(bytes);
            return String.Format("Estimated size · {0} per {1} clip at {2} Mbps. Actual size varies.",
                FormatStorageSize(bytes), DurationLabel(seconds), video.Bitrate);
        }

        double center = 18 * (video.Width * (double)video.Height / (1920 * 1080)) * (video.Fps / 60.0) *
            Math.Pow(2, (23 - video.Quality) / 6.0);
        ulong low = (ulong)Math.Round((Math.Max(0.25, center / 2) + sound) * multiplier);
        ulong high = (ulong)Math.Round((Math.Max(0.75, center * 2) + sound) * multiplier);
        detail = "About " + ExactStorageSize(low) + " to " + ExactStorageSize(high);
        return String.Format("Estimated size · {0}–{1} per {2} clip. Motion and detail can change it substantially.",
            FormatStorageSize(low), FormatStorageSize(high), DurationLabel(seconds));
    }

    static string DurationLabel(int seconds)
    {
        if (seconds < 60) return seconds + " s";
        int remainder = seconds % 60;
        return (seconds / 60) + " min" + (remainder == 0 ? "" : " " + remainder + " s");
    }

    static VideoPreferences LegacyVideo(ShelfSettings current)
    {
        if (current.Quality == 2 && current.FrameRate == 60) return new VideoPreferences();
        return new VideoPreferences
        {
            Experienced = true, Fps = current.FrameRate, Bitrate = VideoBitrates[current.Quality]
        };
    }

    static NumericUpDown VideoNumber(int value, int minimum, int maximum, int increment = 1)
    {
        return new NumericUpDown { Minimum = minimum, Maximum = maximum, Increment = increment,
            Value = Math.Max(minimum, Math.Min(maximum, value)), Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 2) };
    }

    Form PreferencesWindow(string title, Size size)
    {
        var dialog = new Form { Text = title, BackColor = Background, ForeColor = Ink, Font = Font,
            ClientSize = size, Icon = Icon, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
            MinimizeBox = false };
        EnableDialogResize(dialog);
        return dialog;
    }

    static FlowLayoutPanel DialogFooter(Form form, string saveLabel, out Button save, string cancelLabel = "Cancel", Size? minimumBodySize = null)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(9, 5, 9, 2),
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        save = new Button();
        StyleButton(save, saveLabel, 133);
        var cancel = new Button { DialogResult = DialogResult.Cancel };
        StyleButton(cancel, cancelLabel, 90);
        bar.Controls.Add(save);
        bar.Controls.Add(cancel);
        form.CancelButton = cancel;
        form.AcceptButton = save;
        WrapDialogBody(form, bar.Height, minimumBodySize);
        form.Controls.Add(bar);
        bar.Layout += delegate
        {
            int width = bar.Padding.Horizontal + bar.Controls.Cast<Control>().Sum(control => control.Width + control.Margin.Horizontal) + 24;
            int minimum = Math.Max(325, width);
            if (form.MinimumSize.Width != minimum) form.MinimumSize = new Size(minimum, form.MinimumSize.Height);
        };
        return bar;
    }

    bool ShowAbout(Form owner, bool enabled)
    {
        using (var dialog = PreferencesWindow("About " + AppName, new Size(510, 235)))
        {
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(19), ColumnCount = 1, RowCount = 4 };
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            body.Controls.Add(new Label { Text = AppName + "  ·  Build " + BuildNumber, Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Fill });
            var toggle = Check("Enable IDGAF mode", enabled);
            body.RowStyles.Insert(1, new RowStyle(SizeType.Absolute, 48));
            body.Controls.Add(new Label { Text = "Created by yarticann with ChatGPT. Built with AI assistance.",
                Dock = DockStyle.Fill, ForeColor = Ink });
            body.Controls.Add(toggle);
            body.Controls.Add(new Label { Text = "Shows rate control, encoder preset, and keyframe interval in Experienced mode.", Dock = DockStyle.Fill, ForeColor = Muted });
            dialog.Controls.Add(body);
            Button save;
            var footer = DialogFooter(dialog, "Apply", out save);
            var diagnostics = new Button();
            StyleButton(diagnostics, "Error log…", 110);
            diagnostics.Click += delegate { ShowDiagnosticLog(dialog); };
            footer.Controls.Add(diagnostics);
            save.DialogResult = DialogResult.OK;
            return dialog.ShowDialog(owner) == DialogResult.OK ? toggle.Checked : enabled;
        }
    }

    VideoPreferences ShowVideo(Form owner, VideoPreferences video, VideoEncoder[] available, int monitor, int seconds, bool audio)
    {
        using (var dialog = PreferencesWindow("Video settings", new Size(610, 494)))
        using (var estimateDetails = new ToolTip())
        {
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 12, 20, 6), ColumnCount = 2 };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            dialog.Controls.Add(body);
            var mode = Choice(new object[] { "Simple", "Experienced" }, video.Experienced ? 1 : 0);
            var simple = Choice(new object[] { "Auto", "Smaller files", "Balanced", "High quality" }, video.SimplePreset);
            var recommendation = new Label { Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft };
            var choices = new[] { Automatic }.Concat(available).ToArray();
            int index = Array.FindIndex(choices, item => item.Id == video.Encoder);
            var encoder = Choice(choices, Math.Max(0, index));
            var codec = Choice(new object[0], -1);
            var native = Check("Use native monitor resolution", video.Width == 0 || video.Height == 0);
            var bounds = CaptureDisplaySize(Screen.AllScreens[Math.Min(monitor - 1, Screen.AllScreens.Length - 1)]);
            var width = VideoNumber(video.Width > 0 ? video.Width : bounds.Width, 320, 7680, 2);
            var height = VideoNumber(video.Height > 0 ? video.Height : bounds.Height, 240, 4320, 2);
            var resolution = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
            resolution.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            resolution.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
            resolution.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            width.AccessibleName = "Video width";
            height.AccessibleName = "Video height";
            resolution.Controls.Add(width, 0, 0);
            resolution.Controls.Add(new Label { Text = "×", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter }, 1, 0);
            resolution.Controls.Add(height, 2, 0);
            var fps = VideoNumber(video.Fps, 15, 120);
            var bitrate = VideoNumber(video.Bitrate, 2, 150);
            var qp = VideoNumber(video.ConstantQuality, 1, 51);
            string[] rateValues = { "CBR", "VBR", "Constant quality" };
            var rate = Choice(rateValues, Math.Max(0, Array.IndexOf(rateValues, video.Rate)));
            string[] presets = { "Fast", "Balanced", "Quality" };
            var preset = Choice(presets, Math.Max(0, Array.IndexOf(presets, video.Preset)));
            var keyframes = VideoNumber(video.Keyframes, 1, 10);
            var qualityValue = new Panel { Dock = DockStyle.Fill };
            qualityValue.Controls.Add(bitrate);
            qualityValue.Controls.Add(qp);
            SettingsRow(body, "Mode", mode);
            SettingsRow(body, "Preset", simple);
            SettingsRow(body, "", recommendation, 51);
            SettingsRow(body, "Encoder", encoder);
            SettingsRow(body, "Codec", codec);
            SettingsRow(body, "", native);
            SettingsRow(body, "Resolution", resolution);
            SettingsRow(body, "Frames per second", fps);
            SettingsRow(body, "Bitrate (Mbps)", qualityValue);
            SettingsRow(body, "Rate control", rate);
            SettingsRow(body, "Encoder preset", preset);
            SettingsRow(body, "Keyframe interval (s)", keyframes);
            var estimate = new Label { BackColor = Surface, ForeColor = Ink, Dock = DockStyle.Fill,
                Padding = new Padding(9, 0, 9, 0), TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Estimated clip size" };
            SettingsRow(body, "", estimate, 51);
            var qualityLabel = (Label)body.GetControlFromPosition(0, 8);
            bitrate.AccessibleName = "Bitrate in megabits per second";
            qp.AccessibleName = "Constant quality value";

            Func<VideoPreferences> draft = delegate
            {
                return new VideoPreferences
                {
                    Experienced = mode.SelectedIndex == 1, Idgaf = video.Idgaf, SimplePreset = simple.SelectedIndex,
                    Encoder = ((VideoEncoder)encoder.SelectedItem).Id, Codec = codec.Text.StartsWith("HEVC") ? "hevc" : "h264",
                    Width = native.Checked ? 0 : (int)width.Value, Height = native.Checked ? 0 : (int)height.Value,
                    Fps = (int)fps.Value, Bitrate = (int)bitrate.Value, ConstantQuality = (int)qp.Value,
                    Rate = rateValues[rate.SelectedIndex], Preset = presets[preset.SelectedIndex], Keyframes = (int)keyframes.Value
                };
            };
            Action updateEstimate = delegate
            {
                string detail = null;
                try { estimate.Text = ClipSizeEstimate(new CaptureVideo(new ShelfSettings { Display = monitor, Video = draft() }, available), seconds, audio, out detail); }
                catch (IOException) { estimate.Text = "Estimated size unavailable for this encoder."; }
                estimateDetails.SetToolTip(estimate, detail);
            };

            Action update = delegate
            {
                bool custom = mode.SelectedIndex == 1;
                bool qualityMode = custom && video.Idgaf && rate.SelectedIndex == 2;
                qualityLabel.Text = qualityMode ? "Quality value" : "Bitrate (Mbps)";
                bitrate.Visible = !qualityMode;
                qp.Visible = qualityMode;
                int shownHeight = 12;
                for (int row = 0; row < body.RowStyles.Count; row++)
                {
                    bool show = row == 0 || row == 12 || (custom ? row >= 3 && (row <= 8 || video.Idgaf) && (row != 6 || !native.Checked) : row <= 2);
                    int rowHeight = show ? row == 2 || row == 12 ? 51 : 33 : 0;
                    body.RowStyles[row].Height = rowHeight;
                    shownHeight += rowHeight;
                    foreach (Control control in body.Controls)
                        if (body.GetRow(control) == row) control.Visible = show;
                }
                var viewport = body.Parent.Parent.Parent as ScrollViewport;
                if (viewport != null)
                {
                    viewport.Tag = new Size(440, shownHeight + 6);
                    viewport.RefreshLayout();
                }
                var preferred = available.FirstOrDefault(item => item.Codecs.Contains("h264"));
                if (preferred == null) recommendation.Text = "No compatible encoder initialized.";
                else
                {
                    var plan = new CaptureVideo(new ShelfSettings { Display = monitor, Video = new VideoPreferences { SimplePreset = simple.SelectedIndex } }, available);
                    recommendation.Text = preferred.Label + " · H.264 · " + plan.Fps + " FPS. " +
                        (preferred.Id == "cpu" || simple.SelectedIndex == 1 ? "Up to 720p." : "Up to 1080p.");
                }
                updateEstimate();
            };
            Action<string> updateCodecs = delegate(string selected)
            {
                var choice = (VideoEncoder)encoder.SelectedItem;
                var resolved = choice.Id == "auto" ? available.FirstOrDefault(item => item.Codecs.Contains("h264")) ?? available.FirstOrDefault() : choice;
                codec.Items.Clear();
                if (resolved != null)
                {
                    foreach (string item in resolved.Codecs) codec.Items.Add(item == "h264" ? "H.264" : "HEVC (H.265)");
                    codec.SelectedIndex = selected == "hevc" && resolved.Codecs.Contains("hevc") ? Array.IndexOf(resolved.Codecs, "hevc") : 0;
                }
            };
            updateCodecs(video.Codec);
            encoder.SelectedIndexChanged += delegate { updateCodecs(codec.Text.StartsWith("HEVC") ? "hevc" : "h264"); updateEstimate(); };
            codec.SelectedIndexChanged += delegate { updateEstimate(); };
            foreach (var setting in new[] { fps, width, height, bitrate, qp })
                setting.ValueChanged += delegate { updateEstimate(); };
            mode.SelectedIndexChanged += delegate { update(); };
            native.CheckedChanged += delegate { update(); };
            simple.SelectedIndexChanged += delegate { update(); };
            rate.SelectedIndexChanged += delegate { update(); };
            Button save;
            DialogFooter(dialog, "Apply", out save);
            save.Enabled = available.Length != 0;
            save.Click += delegate
            {
                if (mode.SelectedIndex == 1 && !native.Checked && ((int)width.Value % 2 != 0 || (int)height.Value % 2 != 0))
                {
                    MessageBox.Show(dialog, "Width and height must be even numbers.", "Video settings");
                    return;
                }
                dialog.DialogResult = DialogResult.OK;
            };
            update();
            return dialog.ShowDialog(owner) == DialogResult.OK ? draft() : video;
        }
    }
}
