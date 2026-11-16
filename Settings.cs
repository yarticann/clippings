using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Serialization;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

public sealed class ShelfSettings
{
    public int Display = 1;
    public int Duration = 2;
    public int ReplayDurationSeconds;
    public int FrameRate = 60;
    public int Quality = 2;
    public VideoPreferences Video;
    public bool Cursor;
    public bool Playback = true;
    public string PlaybackDevice = "";
    public int PlaybackLevel = 100;
    public bool Microphone;
    public string MicDevice = "";
    public int MicLevel = 75;
    public bool SaveSound = true;
    public int ClipSoundVolume = 100;
    public string SoundPath = "";
    public string SoundName = "";
    public int Shortcut;
    public uint ShortcutKey;
    public uint ShortcutModifiers;
    public ShelfSettings CloneForSetup() { return (ShelfSettings)MemberwiseClone(); }
    public string ClipFolder;
}

sealed partial class ReplayShelf
{
    static readonly int[] ReplayLengths = { 30, 60, 300 };
    const int MaximumReplaySeconds = (Int32.MaxValue / 60 - 1) * 60;

    static int GetReplaySeconds(ShelfSettings options)
    {
        if (options.ReplayDurationSeconds > 0)
            return Math.Min(MaximumReplaySeconds, options.ReplayDurationSeconds);
        return ReplayLengths[Math.Max(0, Math.Min(ReplayLengths.Length - 1, options.Duration))];
    }

    static int ParseReplayMinutes(string text)
    {
        decimal minutes;
        if (Decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
            CultureInfo.CurrentCulture, out minutes) && minutes <= MaximumReplaySeconds / 60m)
        {
            int seconds = (int)Math.Round(minutes * 60m, MidpointRounding.AwayFromZero);
            if (seconds > 0) return seconds;
        }
        throw new ArgumentException("Enter a positive replay length in minutes. For example, enter " +
            (2.5m).ToString(CultureInfo.CurrentCulture) + " for 2 minutes and 30 seconds.");
    }
    static readonly int[] VideoBitrates = { 10, 16, 25 };
    static readonly string SettingsPath = Path.Combine(Data, "settings.xml");
    static readonly string StartupLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), AppName + ".lnk");
    static readonly string LegacyStartupLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Replay Shelf.lnk");

    static bool StartupEnabled { get { return !PortableMode && (File.Exists(StartupLink) || File.Exists(LegacyStartupLink)); } }
    static readonly XmlSerializer SettingsSerializer = new XmlSerializer(typeof(ShelfSettings));

    static ShelfSettings ReadSettings(string path)
    {
        using (var file = File.OpenRead(path))
        {
            var value = (ShelfSettings)SettingsSerializer.Deserialize(file);
            if (PortableMode)
            {
                value.ClipFolder = ResolvePortablePath(value.ClipFolder);
                value.SoundPath = ResolvePortablePath(value.SoundPath);
            }
            return value;
        }
    }

    static string ResolvePortablePath(string path)
    {
        if (String.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return path;
        string full = Path.GetFullPath(Path.Combine(AppFolder, path));
        return full.StartsWith(AppFolder, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    static string StorePortablePath(string path)
    {
        if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return path;
        string full = Path.GetFullPath(path);
        return full.StartsWith(AppFolder, StringComparison.OrdinalIgnoreCase) ? full.Substring(AppFolder.Length) : full;
    }

    static void WriteSettings(string path, ShelfSettings value)
    {
        if (PortableMode)
        {
            value = value.CloneForSetup();
            value.ClipFolder = StorePortablePath(value.ClipFolder);
            value.SoundPath = StorePortablePath(value.SoundPath);
        }
        string pending = path + ".tmp";
        try
        {
            using (var file = File.Create(pending)) SettingsSerializer.Serialize(file, value);
            if (File.Exists(path)) File.Replace(pending, path, null);
            else File.Move(pending, path);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    ShelfSettings LoadSettings()
    {
        var value = new ShelfSettings { ClipFolder = DefaultClips };
        try
        {
            if (File.Exists(SettingsPath)) value = ReadSettings(SettingsPath);
            else if (File.Exists(Path.Combine(Data, "hotkey.txt")))
                Int32.TryParse(File.ReadAllText(Path.Combine(Data, "hotkey.txt")), out value.Shortcut);
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        value.Display = Math.Max(1, Math.Min(Screen.AllScreens.Length, value.Display));
        value.Duration = Math.Max(0, Math.Min(2, value.Duration));
        value.Quality = Math.Max(0, Math.Min(2, value.Quality));
        value.FrameRate = value.FrameRate == 30 ? 30 : 60;
        value.ClipSoundVolume = Math.Max(0, Math.Min(100, value.ClipSoundVolume));
        value.PlaybackLevel = Math.Max(0, Math.Min(200, value.PlaybackLevel));
        value.MicLevel = Math.Max(0, Math.Min(200, value.MicLevel));

        if (String.IsNullOrWhiteSpace(value.ClipFolder) || !Path.IsPathRooted(value.ClipFolder)) value.ClipFolder = DefaultClips;
        return value;
    }

    static MMDevice FindAudioDevice(DataFlow flow, string id)
    {
        using (var devices = new MMDeviceEnumerator())
        {
            var device = String.IsNullOrEmpty(id) ? devices.GetDefaultAudioEndpoint(flow, Role.Multimedia) : devices.GetDevice(id);
            if (device.State == DeviceState.Active && device.DataFlow == flow) return device;
            device.Dispose();
            throw new IOException("An audio device is unavailable. Choose another device in Settings.");
        }
    }

    sealed class AudioChoice
    {
        public readonly string Id;
        readonly string name;
        public AudioChoice(string id, string label) { Id = id; name = label; }
        public override string ToString() { return name; }
    }

    static ComboBox Choice(object[] options, int index)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 3) };
        box.Items.AddRange(options);
        box.SelectedIndex = index;
        return box;
    }

    static ComboBox AudioDevices(DataFlow flow, string selected)
    {
        var box = Choice(new object[] { new AudioChoice("", "Windows default") }, 0);
        using (var devices = new MMDeviceEnumerator())
        {
            foreach (var device in devices.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    int index = box.Items.Add(new AudioChoice(device.ID, device.FriendlyName));
                    if (device.ID == selected) box.SelectedIndex = index;
                }
            }
        }
        if (!String.IsNullOrEmpty(selected) && box.SelectedIndex == 0)
            box.SelectedIndex = box.Items.Add(new AudioChoice(selected, "Disconnected device"));
        return box;
    }

    static TableLayoutPanel SettingsGroup(TableLayoutPanel layout, string title, int column, int row)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(13, 8, 13, 8), Margin = new Padding(6), BackColor = Surface };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var heading = new Label { Text = title, Font = new Font("Segoe UI", 13, FontStyle.Bold), Dock = DockStyle.Fill };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.Controls.Add(heading, 0, 0);
        panel.SetColumnSpan(heading, 2);
        layout.Controls.Add(panel, column, row);
        return panel;
    }

    static void SettingsRow(TableLayoutPanel group, string label, Control value, int height = 33)
    {
        int row = group.RowStyles.Count;
        group.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        if (label.Length == 0)
        {
            group.Controls.Add(value, 0, row);
            group.SetColumnSpan(value, 2);
        }
        else
        {
            group.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            group.Controls.Add(value, 1, row);
            if (String.IsNullOrEmpty(value.AccessibleName)) value.AccessibleName = label;
        }
    }

    static CheckBox Check(string text, bool enabled)
    {
        return new CheckBox { Text = text, Checked = enabled, Dock = DockStyle.Fill };
    }

    static NumericUpDown Level(int value)
    {
        return new NumericUpDown { Minimum = 0, Maximum = 200, Increment = 5, Value = value, Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 2) };
    }

    static void ChangeStartup(bool enable)
    {
        if (PortableMode) return;
        if (!enable)
        {
            if (File.Exists(StartupLink)) File.Delete(StartupLink);
            if (File.Exists(LegacyStartupLink)) File.Delete(LegacyStartupLink);
            return;
        }
        if (File.Exists(StartupLink) && !File.Exists(LegacyStartupLink)) return;

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        dynamic shell = Activator.CreateInstance(shellType);
        dynamic link = null;
        try
        {
            link = shell.CreateShortcut(StartupLink);
            link.TargetPath = Application.ExecutablePath;
            link.IconLocation = Application.ExecutablePath + ",0";
            link.Arguments = "--tray";
            link.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            link.Save();
            if (File.Exists(LegacyStartupLink)) File.Delete(LegacyStartupLink);
        }
        finally
        {
            if (link != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    static void LayoutSettings(Form form, ScrollViewport viewport, TableLayoutPanel layout, params TableLayoutPanel[] groups)
    {
        foreach (var group in groups)
        {
            // A spacer absorbs spare height instead of stretching the last input or button.
            group.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        }

        bool? compact = null;
        SizeF lastScale = SizeF.Empty;
        int contentHeight = 0;
        viewport.Attach(layout, delegate(Size space)
        {
            SizeF scale = form.CurrentAutoScaleDimensions;
            bool narrow = space.Width < 790 * scale.Width / 96;
            if (narrow == compact && scale == lastScale)
                return new Size(Math.Max(430, space.Width), contentHeight);

            compact = narrow;
            lastScale = scale;
            layout.SuspendLayout();
            layout.ColumnStyles[0].Width = narrow ? 100 : 47;
            layout.ColumnStyles[1].Width = narrow ? 0 : 53;
            for (int row = 0; row < layout.RowStyles.Count; row++) layout.RowStyles[row].Height = 0;
            for (int index = 0; index < groups.Length; index++)
            {
                var group = groups[index];
                int row = narrow ? index : index / 2;
                layout.SetCellPosition(group, new TableLayoutPanelCellPosition(narrow ? 0 : index % 2, row));
                group.TabIndex = index;
                int height = group.Padding.Vertical + group.Margin.Vertical + (int)group.RowStyles.Cast<RowStyle>()
                    .Where(style => style.SizeType == SizeType.Absolute).Sum(style => style.Height);
                layout.RowStyles[row].Height = Math.Max(layout.RowStyles[row].Height, height);
            }
            contentHeight = layout.Padding.Vertical + (int)layout.RowStyles.Cast<RowStyle>().Sum(style => style.Height);
            layout.ResumeLayout();
            return new Size(Math.Max(430, space.Width), contentHeight);
        });
        RevealInputs(viewport, layout);
        form.Shown += delegate
        {
            var working = Screen.FromControl(form).WorkingArea;
            form.Height = Math.Min(form.Height, working.Height - 32);
            viewport.RefreshLayout();
        };
    }

    void OpenSettings()
    {
        try { ShowSettings(); }
        catch (Exception error) { ReportFailure("Settings could not open", error.Message); }
    }

    void ShowSettings()
    {
        using (var form = new Form { Text = AppName + " settings", BackColor = Background, ForeColor = Ink,
            Font = Font, ClientSize = new Size(980, 750), MinimumSize = new Size(460, 420), Icon = Icon, AutoScaleMode = AutoScaleMode.Dpi,
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, MinimizeBox = false })
        using (var micTimer = new System.Windows.Forms.Timer { Interval = 100 })
        {
            form.MaximizeBox = true;
            form.SizeGripStyle = SizeGripStyle.Show;
            var container = new ScrollViewport { Dock = DockStyle.Fill };
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, Height = 690, ColumnCount = 2, RowCount = 4, Padding = new Padding(9, 6, 9, 6) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 53));
            for (int row = 0; row < 4; row++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            form.Controls.Add(container);

            var capture = SettingsGroup(layout, "Capture", 0, 0);
            var displays = MonitorChoices(Screen.AllScreens);
            var display = Choice(displays, Math.Min(settings.Display, displays.Length) - 1);
            var length = new TextBox
            {
                Text = (GetReplaySeconds(settings) / 60m).ToString("0.####", CultureInfo.CurrentCulture),
                AccessibleName = "Replay length in minutes",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 5, 0, 3)
            };
            var cursor = Check("Capture mouse cursor", settings.Cursor);
            SettingsRow(capture, "Monitor", display);
            SettingsRow(capture, "Replay length (min)", length);
            SettingsRow(capture, "", cursor);

            var playbackEnabled = Check("Capture playback audio", settings.Playback);
            var micEnabled = Check("Capture microphone", settings.Microphone);

            var quality = SettingsGroup(layout, "Quality", 0, 1);
            var video = (settings.Video ?? LegacyVideo(settings)).Copy();
            var summary = new Label { Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft };
            Action updateSummary = delegate
            {
                summary.Text = video.Experienced ? "Experienced · " + video.Fps + " FPS · " + video.Codec.ToUpperInvariant()
                    : "Simple · " + new[] { "Auto", "Smaller files", "Balanced", "High quality" }[video.SimplePreset];
            };
            updateSummary();
            var videoButton = new Button { Anchor = AnchorStyles.Left };
            StyleButton(videoButton, "Video settings…", 140);
            videoButton.Click += async delegate
            {
                videoButton.Enabled = false;
                videoButton.Text = "Detecting encoders…";
                try
                {
                    var available = await Encoders.Value;
                    if (form.IsDisposed) return;
                    video = ShowVideo(form, video, available, display.SelectedIndex + 1, ParseReplayMinutes(length.Text), playbackEnabled.Checked || micEnabled.Checked);
                    updateSummary();
                }
                catch (Exception error) { if (!form.IsDisposed) MessageBox.Show(form, error.Message, "Video settings"); }
                finally
                {
                    if (!form.IsDisposed) { videoButton.Enabled = true; videoButton.Text = "Video settings…"; }
                }
            };
            SettingsRow(quality, "", summary, 46);
            SettingsRow(quality, "", videoButton, 43);

            var audio = SettingsGroup(layout, "Audio", 1, 1);
            var playback = AudioDevices(DataFlow.Render, settings.PlaybackDevice);
            var microphone = AudioDevices(DataFlow.Capture, settings.MicDevice);
            var playbackLevel = Level(settings.PlaybackLevel);
            var micLevel = Level(settings.MicLevel);
            SettingsRow(audio, "", playbackEnabled, 36);
            SettingsRow(audio, "Playback device", playback, 38);
            SettingsRow(audio, "Playback level (%)", playbackLevel, 38);
            SettingsRow(audio, "", micEnabled, 36);
            SettingsRow(audio, "Microphone", microphone, 38);
            SettingsRow(audio, "Mic level (%)", micLevel, 38);
            var meter = new ProgressBar { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 14, Margin = new Padding(9, 0, 0, 0), AccessibleName = "Microphone input meter" };
            var preview = new Button();
            StyleButton(preview, "Test mic", 112);
            var meterRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
            meterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
            meterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            preview.Anchor = AnchorStyles.Left;
            meterRow.Controls.Add(preview, 0, 0);
            meterRow.Controls.Add(meter, 1, 0);
            SettingsRow(audio, "", meterRow, 43);
            var listenToMic = Check("Hear my mic (use headphones)", false);
            SettingsRow(audio, "", listenToMic, 36);

            var controls = SettingsGroup(layout, "Hotkeys and storage", 1, 0);
            var key = ReadClipKey(settings);
            var keyButton = new Button { Anchor = AnchorStyles.Left };
            StyleButton(keyButton, key.Label + "  ·  Change", 200);
            keyButton.Click += delegate { key = PickShortcut(form, key); keyButton.Text = key.Label + "  ·  Change"; };
            var sound = Check("Play a sound when a save starts", settings.SaveSound);
            string soundPath = settings.SoundPath;
            string soundName = settings.SoundName;
            string staging = null;
            System.Media.SoundPlayer soundPreview = null;
            var volume = new ResizeTrackBar { Minimum = 0, Maximum = 100, Value = settings.ClipSoundVolume, TickStyle = TickStyle.None,
                SmallChange = 1, LargeChange = 10, Dock = DockStyle.Fill, AccessibleName = "Clip sound volume" };
            var volumeLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Muted };
            var volumeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            volumeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            volumeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
            volumeRow.Controls.Add(volume, 0, 0);
            volumeRow.Controls.Add(volumeLabel, 1, 0);
            Action updateVolume = delegate { volumeLabel.Text = volume.Value + "%"; };
            volume.ValueChanged += delegate { updateVolume(); };
            updateVolume();
            var soundText = new Label { Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            Action updateSoundLabel = delegate
            {
                soundText.Text = String.IsNullOrEmpty(soundPath) ? "Windows default · first 10 s of custom sounds used" : soundName;
            };
            updateSoundLabel();
            var soundButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            var chooseSound = new Button();
            var previewSound = new Button();
            var defaultSound = new Button();
            StyleButton(chooseSound, "Choose sound…", 124);
            StyleButton(previewSound, "Preview", 80);
            StyleButton(defaultSound, "Use default", 97);
            foreach (var button in new[] { chooseSound, previewSound, defaultSound }) soundButtons.Controls.Add(button);
            chooseSound.Click += async delegate
            {
                string input;
                using (var picker = new OpenFileDialog { Title = "Choose clip sound", Filter = SoundFilter, CheckFileExists = true })
                {
                    if (picker.ShowDialog(form) != DialogResult.OK) return;
                    input = picker.FileName;
                }
                chooseSound.Enabled = false;
                defaultSound.Enabled = false;
                try
                {
                    string imported = await System.Threading.Tasks.Task.Run(() => ImportClipSound(input));
                    if (form.IsDisposed || !form.Visible) { DeleteStagedSound(imported); return; }
                    DeleteStagedSound(staging);
                    staging = soundPath = imported;
                    soundName = Path.GetFileName(input);
                    sound.Checked = true;
                    updateSoundLabel();
                }
                catch (Exception error) { ReportFailure("Sound import failed", error.Message); }
                finally
                {
                    if (!form.IsDisposed) { chooseSound.Enabled = true; defaultSound.Enabled = true; }
                }
            };
            previewSound.Click += delegate
            {
                try
                {
                    ReleaseSound(soundPreview);
                    soundPreview = CreateSound(soundPath, volume.Value);
                    if (soundPreview != null) soundPreview.Play();
                }
                catch (Exception error) { ReportFailure("Sound preview failed", error.Message); }
            };
            defaultSound.Click += delegate
            {
                DeleteStagedSound(staging);
                staging = soundPath = soundName = null;
                updateSoundLabel();
            };
            var startup = Check("Open in the tray at Windows sign-in", StartupEnabled);
            var path = new TextBox { Text = settings.ClipFolder, Dock = DockStyle.Fill, AccessibleName = "Clip folder path", Margin = new Padding(0, 5, 0, 0) };
            var browse = new Button();
            StyleButton(browse, "Browse…", 84);
            browse.Anchor = AnchorStyles.Right;
            var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 91));
            path.Dock = DockStyle.None;
            path.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            folderRow.Controls.Add(path, 0, 0);
            folderRow.Controls.Add(browse, 1, 0);
            browse.Click += delegate
            {
                using (var picker = new FolderBrowserDialog { SelectedPath = path.Text })
                    if (picker.ShowDialog(form) == DialogResult.OK) path.Text = picker.SelectedPath;
            };
            SettingsRow(controls, "Save hotkey", keyButton, 39);
            SettingsRow(controls, "", sound);
            SettingsRow(controls, "", soundButtons, 39);
            SettingsRow(controls, "", soundText, 31);
            SettingsRow(controls, "Clip sound volume", volumeRow, 42);
            SettingsRow(controls, "Clip folder", folderRow, 43);
            if (!PortableMode) SettingsRow(controls, "", startup, 38);
            else startup.Dispose();
            LayoutSettings(form, container, layout, capture, controls, quality, audio);

            WasapiCapture micCapture = null;
            MMDevice micDevice = null;
            WasapiOut micOutput = null;
            VolumeSampleProvider monitorGain = null;
            EventHandler<WaveInEventArgs> monitorInput = null;
            Action stopMonitor = delegate
            {
                if (micCapture != null && monitorInput != null) micCapture.DataAvailable -= monitorInput;
                monitorInput = null;
                monitorGain = null;
                var output = micOutput;
                micOutput = null;
                if (output != null) output.Dispose();
            };
            Action startMonitor = delegate
            {
                if (micCapture == null || micOutput != null) return;
                try
                {
                    var format = micCapture.WaveFormat;
                    var extended = format as WaveFormatExtensible;
                    var buffer = new BufferedWaveProvider(extended == null ? format : extended.ToStandardWaveFormat())
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(180),
                        DiscardOnBufferOverflow = true
                    };
                    monitorGain = new VolumeSampleProvider(buffer.ToSampleProvider()) { Volume = (float)micLevel.Value / 100f };
                    var output = new WasapiOut(AudioClientShareMode.Shared, true, 50);
                    micOutput = output;
                    output.Init(new SampleToWaveProvider(monitorGain));
                    output.PlaybackStopped += delegate(object sender, StoppedEventArgs e)
                    {
                        if (micOutput != output) return;
                        stopMonitor();
                        listenToMic.Checked = false;
                        if (e.Exception != null) ReportFailure("Mic monitoring failed", e.Exception.Message);
                    };
                    monitorInput = delegate(object sender, WaveInEventArgs e)
                    {
                        buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    };
                    micCapture.DataAvailable += monitorInput;
                    output.Play();
                }
                catch (Exception error)
                {
                    stopMonitor();
                    listenToMic.Checked = false;
                    ReportFailure("Mic monitoring failed", error.Message);
                }
            };
            listenToMic.CheckedChanged += delegate { if (listenToMic.Checked) startMonitor(); else stopMonitor(); };
            micLevel.ValueChanged += delegate { if (monitorGain != null) monitorGain.Volume = (float)micLevel.Value / 100f; };
            Action stopMeter = delegate
            {
                micTimer.Stop();
                stopMonitor();
                var captureDevice = micCapture;
                micCapture = null;
                if (captureDevice != null) captureDevice.Dispose();
                if (micDevice != null) { micDevice.Dispose(); micDevice = null; }
                meter.Value = 0;
                preview.Text = "Test mic";
            };
            preview.Click += delegate
            {
                if (micTimer.Enabled) { stopMeter(); return; }
                try
                {
                    micDevice = FindAudioDevice(DataFlow.Capture, ((AudioChoice)microphone.SelectedItem).Id);
                    micCapture = new WasapiCapture(micDevice, false, 60);
                    micCapture.RecordingStopped += delegate(object sender, StoppedEventArgs e)
                    {
                        if (!Object.ReferenceEquals(sender, micCapture)) return;
                        stopMeter();
                        if (e.Exception != null) ReportFailure("Microphone test failed", e.Exception.Message);
                    };
                    micCapture.StartRecording();
                    if (listenToMic.Checked) startMonitor();
                    preview.Text = "Stop test";
                    micTimer.Start();
                }
                catch (Exception error) { stopMeter(); ReportFailure("Microphone test failed", error.Message); }
            };
            microphone.SelectedIndexChanged += delegate { stopMeter(); };
            micTimer.Tick += delegate
            {
                try { meter.Value = Math.Min(100, (int)(micDevice.AudioMeterInformation.MasterPeakValue * (float)micLevel.Value)); }
                catch (Exception error) { stopMeter(); ReportFailure("Microphone test failed", error.Message); }
            };

            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(14, 7, 14, 4), FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var save = new Button();
            StyleButton(save, "Save settings", 119);
            var cancel = new Button { DialogResult = DialogResult.Cancel };
            StyleButton(cancel, "Cancel", 78);
            footer.Controls.Add(save);
            footer.Controls.Add(cancel);
            var about = new Button();
            StyleButton(about, "About…", 87);
            about.Click += delegate { video.Idgaf = ShowAbout(form, video.Idgaf); };
            footer.Controls.Add(about);
            var setup = new Button();
            StyleButton(setup, "Setup guide…", 107);
            footer.Controls.Add(setup);
            bool reopenSetup = false;
            setup.Click += delegate { reopenSetup = true; form.DialogResult = DialogResult.Cancel; };
            if (recorder != null)
            {
                var note = new Label { Text = "Restart the buffer to apply capture and audio settings.",
                    Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(17, 0, 0, 0), ForeColor = Muted };
                form.Controls.Add(note);
            }
            form.Controls.Add(footer);
            form.CancelButton = cancel;
            form.AcceptButton = save;
            save.Click += delegate
            {
                try
                {
                    int replaySeconds = ParseReplayMinutes(length.Text);
                    string folder = path.Text.Trim();
                    if (!Path.IsPathRooted(folder) || folder.Length < 3) throw new IOException("Enter a full clip folder path.");
                    folder = Path.GetFullPath(folder);
                    if (folder != Clips || !Directory.Exists(folder)) Directory.CreateDirectory(folder);
                    var update = new ShelfSettings
                    {
                        Display = display.SelectedIndex + 1, Duration = settings.Duration, ReplayDurationSeconds = replaySeconds, Cursor = cursor.Checked,
                        Video = video,
                        Playback = playbackEnabled.Checked, PlaybackDevice = ((AudioChoice)playback.SelectedItem).Id, PlaybackLevel = (int)playbackLevel.Value,
                        Microphone = micEnabled.Checked, MicDevice = ((AudioChoice)microphone.SelectedItem).Id, MicLevel = (int)micLevel.Value,
                        ShortcutKey = key.Code, ShortcutModifiers = key.Modifiers, SaveSound = sound.Checked, ClipSoundVolume = volume.Value, SoundPath = soundPath, SoundName = soundName, ClipFolder = folder
                    };
                    CheckShortcut(key);
                    bool wasEnabled = StartupEnabled;
                    if (!PortableMode) ChangeStartup(startup.Checked);
                    try { WriteSettings(SettingsPath, update); }
                    catch { ChangeStartup(wasEnabled); throw; }
                    string previousSound = settings.SoundPath;
                    settings = update;
                    staging = null;
                    LoadClipSound();
                    if (previousSound != settings.SoundPath) DeleteStagedSound(previousSound);
                    form.DialogResult = DialogResult.OK;
                }
                catch (Exception error) { ReportFailure("Settings could not save", error.Message); }
            };
            try
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    SetShortcut();
                    page = 0;
                    RefreshLibrary();
                }
            }
            finally
            {
                stopMeter();
                ReleaseSound(soundPreview);
                DeleteStagedSound(staging);
            }
            if (reopenSetup) ShowSetupGuide();
        }
    }
}
