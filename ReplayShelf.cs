using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.CoreAudioApi;

sealed partial class ReplayShelf : Form
{
    static readonly Color Background = Color.FromArgb(31, 17, 48);
    static readonly Color Surface = Color.FromArgb(48, 29, 69);
    static readonly Color Ink = Color.FromArgb(247, 239, 255);
    static readonly Color Muted = Color.FromArgb(195, 176, 215);
    static readonly string AppFolder = AppDomain.CurrentDomain.BaseDirectory;
    static readonly bool PortableMode = File.Exists(Path.Combine(AppFolder, "portable.mode"));
    static readonly string Data = PortableMode ? Path.Combine(AppFolder, "data") :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReplayShelf");
    static readonly string DefaultClips = PortableMode ? Path.Combine(AppFolder, "clips") :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Replay Shelf");
    static readonly string ToolFolder = PortableMode ? Path.Combine(AppFolder, "ffmpeg") :
        File.ReadAllText(Path.Combine(AppFolder, "ffmpeg-path.txt")).Trim();
    static readonly string Ffmpeg = Path.Combine(ToolFolder, "ffmpeg.exe");
    readonly FlowLayoutPanel grid = new FlowLayoutPanel();
    readonly ScrollViewport clipViewport = new ScrollViewport();
    readonly Label status = new Label();
    readonly TextBox search = new TextBox();
    readonly Button arm = new Button();
    readonly Button next = new Button();
    readonly Button previous = new Button();
    readonly Label count = new Label();
    readonly NotifyIcon tray = new NotifyIcon();
    ToolStripItem trayBufferAction;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    Recorder recorder;
    int page;
    int generation;
    bool saving;
    bool starting;
    Task stoppingCapture;
    int recoveryAttempts;
    bool exiting;
    bool hotkeyRegistered;
    bool smokeMode;
    ShelfSettings settings;
    string Clips { get { return settings.ClipFolder; } }
    string testSaveFolder;
    string lastSaved;
    sealed class LibrarySnapshot
    {
        public string Folder;
        public DateTime Stamp;
        public FileInfo[] Files;
    }

    readonly System.Windows.Forms.Timer searchTimer = new System.Windows.Forms.Timer { Interval = 150 };
    LibrarySnapshot librarySnapshot, renderedSnapshot;
    string renderedQuery;
    int renderedPage;
    bool libraryRefreshing, libraryRefreshPending, libraryRescanPending, previewLoading;
    List<Tuple<FileInfo, PictureBox>> pendingPreviews;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LastInputInfo input);

    static bool DesktopRecentlyActive()
    {
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf(typeof(LastInputInfo)) };
        return !GetLastInputInfo(ref input) || unchecked((uint)Environment.TickCount - input.Time) < 5000;
    }

    static readonly Lazy<bool> CanUseGraphicsCapture = new Lazy<bool>(() =>
        RunTool("ffmpeg.exe", "-hide_banner -filters", 6000).Split('\n').Any(line => line.Contains(" gfxcapture ")));


    static string InstanceMutexName()
    {
        const string name = "Local\\ReplayShelf";
        if (!PortableMode) return name;
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return name + "-" + BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                Path.GetFullPath(AppFolder).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
    }

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool first;
        using (var instance = new Mutex(true, InstanceMutexName(), out first))
        {
            if (!first) return;
            using (var app = new ReplayShelf(args.Contains("--smoke") || args.Contains("--smoke-av")))
            {
                if (app.smokeMode)
                {
                    app.Opacity = 0;
                    app.ShowInTaskbar = false;
                    app.Shown += async delegate
                    {
                        int result = await app.SmokeTest(args.Contains("--smoke-av"));
                        Environment.ExitCode = result;
                        app.Exit();
                    };
                }
                if (!app.smokeMode && NeedsStartupGuide(args))
                    app.Shown += delegate { app.BeginInvoke(new Action(app.ShowStartupGuide)); };
                if (args.Contains("--tray") && !app.smokeMode)
                {
                    using (var context = new ApplicationContext())
                    {
                        app.FormClosed += delegate { context.ExitThread(); };
                        app.SetShortcut();
                        Application.Run(context);
                    }
                }
                else Application.Run(app);
            }
        }
    }

    ReplayShelf(bool smoke)
    {
        smokeMode = smoke;
        Directory.CreateDirectory(Data);
        settings = LoadSettings();
        foreach (var stale in Directory.GetDirectories(Data, "buffer-*"))
        {
            try { Directory.Delete(stale, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        DoubleBuffered = true;
        ResizeRedraw = true;
        Text = AppName;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        BackColor = Background;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10);
        MinimumSize = new Size(705, 460);
        ClientSize = new Size(1160, 690);
        StartPosition = FormStartPosition.CenterScreen;

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(22, 14, 22, 14) };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        Controls.Add(shell);
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        header.Controls.Add(new Label { Text = AppName, AutoSize = true, Font = new Font(Font.FontFamily, 23, FontStyle.Bold), Margin = new Padding(0, 0, 20, 0) });
        status.AutoSize = true;
        status.ForeColor = Muted;
        status.Margin = new Padding(0, 14, 0, 0);
        header.Controls.Add(status);
        var buildLabel = new Label
        {
            Text = BuildNumber,
            Font = Font,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(0),
            ForeColor = Muted
        };
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, buildLabel.GetPreferredSize(Size.Empty).Width + 14));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        heading.Controls.Add(header, 0, 0);
        heading.Controls.Add(buildLabel, 1, 0);
        shell.Controls.Add(heading);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        StyleButton(arm, "Start buffer", 110);
        arm.Click += delegate { ToggleCapture(); };
        toolbar.Controls.Add(arm);
        var preferences = new Button();
        StyleButton(preferences, "Settings", 92);
        preferences.Click += delegate { OpenSettings(); };
        toolbar.Controls.Add(preferences);
        search.Width = 165;
        search.BackColor = Surface;
        search.ForeColor = Ink;
        search.AccessibleName = "Search clips";
        search.Margin = new Padding(3, 6, 9, 0);
        search.KeyDown += delegate(object sender, KeyEventArgs args)
        {
            if (args.KeyData != (Keys.Control | Keys.A)) return;
            search.SelectAll();
            args.SuppressKeyPress = true;
        };
        search.TextChanged += delegate { searchTimer.Stop(); searchTimer.Start(); };
        searchTimer.Tick += delegate { searchTimer.Stop(); page = 0; RefreshLibrary(false); };
        toolbar.Controls.Add(new Label { Text = "Search", AutoSize = true, Margin = new Padding(6, 9, 0, 0) });
        toolbar.Controls.Add(search);
        var folder = new Button();
        StyleButton(folder, "Open folder", 110);
        folder.Click += delegate
        {
            try { Directory.CreateDirectory(Clips); Process.Start("explorer.exe", Quote(Clips)); }
            catch (Exception error) { ReportFailure("Operation failed", error.Message); }
        };
        toolbar.Controls.Add(folder);
        shell.Controls.Add(toolbar);

        grid.Resize += delegate { SizeCards(); };
        clipViewport.Dock = DockStyle.Fill;
        clipViewport.Attach(grid, MeasureClipGrid);
        shell.Controls.Add(clipViewport);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        count.AutoSize = true;
        count.ForeColor = Muted;
        count.Margin = new Padding(0, 10, 15, 0);
        footer.Controls.Add(count);
        StyleButton(previous, "Previous", 80);
        StyleButton(next, "Next", 70);
        previous.Click += delegate { page--; RefreshLibrary(false); };
        next.Click += delegate { page++; RefreshLibrary(false); };
        footer.Controls.Add(previous);
        footer.Controls.Add(next);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.Controls.Add(footer, 0, 0);
        storageBar.Dock = DockStyle.Fill;
        storageBar.Margin = new Padding(8, 0, 0, 0);
        bottom.Controls.Add(storageBar, 1, 0);
        shell.Controls.Add(bottom);

        tray.Icon = Icon;
        tray.Text = AppName;
        tray.Visible = true;
        tray.MouseClick += delegate(object sender, MouseEventArgs args) { if (args.Button == MouseButtons.Left) OpenLibrary(); };
        tray.ContextMenuStrip = CreateThemedMenu();
        tray.ContextMenuStrip.Items.Add("Open clip library", null, delegate { OpenLibrary(); });
        tray.ContextMenuStrip.Items.Add("Settings", null, delegate { OpenLibrary(); OpenSettings(); });
        trayBufferAction = tray.ContextMenuStrip.Items.Add("Start buffer", null, delegate { ToggleCapture(); });
        tray.ContextMenuStrip.Opening += delegate { RefreshTrayBufferAction(); };
        tray.ContextMenuStrip.Items.Add("Save replay", null, async delegate { await SaveClip(); });
        tray.ContextMenuStrip.Items.Add("Exit", null, delegate { Exit(); });
        FormClosing += delegate(object sender, FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); ClearCards(); }
        };
        timer.Interval = 1800;
        timer.Tick += async delegate
        {
            var active = recorder;
            if (active != null && !saving && !starting)
            {
                if (active.HasExited || (DesktopRecentlyActive() && active.HasStalled))
                    await RecoverCapture(active);
                else if (active.HasRecentFootage && (DateTime.UtcNow - active.StartedUtc).TotalSeconds > 25)
                    recoveryAttempts = 0;
            }
            if (!libraryRefreshing && !searchTimer.Enabled) RefreshLibrary(false);
            RefreshClipStorage();
            UpdateBackgroundTimer();
        };
        VisibleChanged += delegate { UpdateBackgroundTimer(); };
        Resize += delegate
        {
            if (WindowState == FormWindowState.Minimized && grid.Controls.Count > 0)
            {
                ClearCards();
            }
            UpdateBackgroundTimer();
        };
        if (!smokeMode) LoadClipSound();
        Shown += delegate
        {
            if (!hotkeyRegistered) SetShortcut();
            RefreshLibrary();
        };
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == 1) SaveFromHotkey();
        base.WndProc(ref message);
    }

    void UpdateBackgroundTimer()
    {
        if (exiting || IsDisposed) return;
        RefreshTrayBufferAction();
        timer.Enabled = recorder != null || starting || (Visible && WindowState != FormWindowState.Minimized);
    }

    void RefreshTrayBufferAction()
    {
        if (trayBufferAction == null) return;
        trayBufferAction.Text = recorder == null ? "Start buffer" : "Stop buffer";
        trayBufferAction.Enabled = !starting && !saving && !exiting;
    }

    async void SaveFromHotkey() { await SaveClip(); }

    void SetShortcut()
    {
        if (hotkeyRegistered) UnregisterHotKey(Handle, 1);
        var key = smokeMode ? LegacyKeys[0] : ReadClipKey(settings);
        hotkeyRegistered = RegisterHotKey(Handle, 1, 0x4000 | key.Modifiers, key.Code);
        if (hotkeyRegistered)
        {
            registeredKey = key;
            SetStatus((recorder == null ? "Buffer off" : "Buffer running") + "  ·  " + key.Label + " saves a clip");
        }
        else ReportFailure("Hotkey unavailable", key.Label + " is unavailable. Pick a different shortcut or save from the tray.");
    }

    void OpenLibrary()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        RefreshLibrary(false);
    }

    async void Exit()
    {
        if (exiting) return;
        if (saving) { SetStatus("Wait for the current replay to finish saving."); return; }
        exiting = true;
        timer.Stop();
        searchTimer.Dispose();
        if (hotkeyRegistered) UnregisterHotKey(Handle, 1);
        var active = recorder;
        recorder = null;
        try
        {
            if (stoppingCapture != null) await stoppingCapture;
            else if (active != null) await Task.Run(() => active.Dispose());
        }
        catch (Exception error) { ReportFailure("Capture stop failed", error.Message); }
        tray.Dispose();
        ReleaseSound(clipSound);
        ClearCards();
        storageDetails.Dispose();
        Close();
    }

    void SetStatus(string message)
    {
        if (!IsDisposed) status.Text = message;
    }

    async void ToggleCapture()
    {
        if (exiting || saving || starting) return;
        if (recorder != null)
        {
            var active = recorder;
            starting = true;
            arm.Enabled = false;
            recorder = null;
            SetStatus("Stopping buffer…");
            UpdateBackgroundTimer();
            try
            {
                stoppingCapture = Task.Run(() => active.Dispose());
                await stoppingCapture;
                SetStatus("Buffer off");
            }
            catch (Exception error) { if (!exiting) ReportFailure("Capture stop failed", error.Message); }
            finally
            {
                stoppingCapture = null;
                starting = false;
                if (!IsDisposed)
                {
                    arm.Text = "Start buffer";
                    arm.Enabled = true;
                    UpdateBackgroundTimer();
                }
            }
            return;
        }
        try
        {
            starting = true;
            arm.Enabled = false;
            SetStatus("Checking available encoders…");
            var available = await Encoders.Value;
            bool graphics = await Task.Run(() => CanUseGraphicsCapture.Value);
            if (exiting || IsDisposed) return;
            recoveryAttempts = 0;
            recorder = new Recorder(settings, available, graphics);
            arm.Text = "Stop buffer";
            SetStatus("Buffer running  ·  " + (hotkeyRegistered ? ReadClipKey(settings).Label : "Save from tray"));
        }
        catch (Exception error) { ReportFailure("Capture could not start", error.Message); }
        finally
        {
            starting = false;
            if (!IsDisposed) arm.Enabled = true;
            UpdateBackgroundTimer();
        }
    }

    async Task RecoverCapture(Recorder stalled)
    {
        starting = true;
        arm.Enabled = false;
        recorder = null;
        string reason = stalled.Error;
        bool retry = !stalled.HasExited || stalled.HasRecentFootage || !IsCaptureConfigurationError(reason);
        try
        {
            if (!retry || recoveryAttempts >= 2)
            {
                ReportFailure("Capture stopped", String.IsNullOrEmpty(reason) ? "The screen stopped sending frames." : reason);
                await Task.Run(() => stalled.Dispose());
                return;
            }

            recoveryAttempts++;
            ReportFailure("Capture interrupted", "Restarting the replay buffer. Wait for fresh footage.");
            await Task.Run(() => stalled.Dispose());
            var available = await Encoders.Value;
            if (exiting || IsDisposed) return;
            recorder = new Recorder(settings, available, CanUseGraphicsCapture.Value);
            SetStatus("Buffer restarted · collecting new footage");
        }
        catch (Exception error) { if (!IsDisposed) ReportFailure("Capture restart failed", error.Message); }
        finally
        {
            starting = false;
            if (!IsDisposed)
            {
                arm.Enabled = true;
                arm.Text = recorder == null ? "Start buffer" : "Stop buffer";
                UpdateBackgroundTimer();
            }
        }
    }

    static bool IsCaptureConfigurationError(string message)
    {
        return message.IndexOf("invalid argument", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("option not found", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    async Task SaveClip()
    {
        if (exiting) return;
        if (saving) { SetStatus("A replay is already saving."); return; }
        var active = recorder;
        if (active == null)
        {
            ReportFailure("Replay unavailable", starting ? "The buffer is changing state. Try again in a moment." : "Start the replay buffer first.");
            return;
        }

        string atPress;
        try
        {
            if (active.HasExited || active.HasStalled)
            {
                if (!starting) await RecoverCapture(active);
                return;
            }
            atPress = active.BeginSave();
        }
        catch (Exception error)
        {
            ReportFailure("Replay unavailable", error.Message);
            return;
        }

        saving = true;
        SetStatus("Saving replay…");
        PlaySaveFeedback();
        try
        {
            string file = await active.Save(atPress, active.ReplaySeconds, testSaveFolder ?? Clips);
            lastSaved = file;
            SetStatus("Saved " + Path.GetFileNameWithoutExtension(file));
            if (Visible && !smokeMode) { page = 0; RefreshLibrary(); }
        }
        catch (Exception error) { ReportFailure("Clip save failed", error.Message); }
        finally { saving = false; }
    }

    async Task<int> SmokeTest(bool microphone)
    {
        string report = Path.Combine(Data, "smoke.txt");
        try
        {
            var test = new ShelfSettings
            {
                ClipFolder = DefaultClips, Microphone = microphone, FrameRate = microphone ? 30 : 60,
                Quality = microphone ? 0 : 2, Duration = 0, Cursor = microphone,
                Video = new VideoPreferences { Experienced = microphone, Fps = microphone ? 30 : 60,
                    Width = microphone ? 1280 : 0, Height = microphone ? 720 : 0, Bitrate = 12,
                    Idgaf = microphone, Rate = microphone ? "VBR" : "CBR", Keyframes = microphone ? 3 : 2 }
            };
            string testSettings = Path.Combine(Data, "test-settings.xml");
            WriteSettings(testSettings, test);
            try
            {
                test = ReadSettings(testSettings);
                if (test.FrameRate != (microphone ? 30 : 60) || test.Microphone != microphone ||
                    test.Cursor != microphone || test.Duration != 0 || test.ClipFolder != DefaultClips || test.Video.Idgaf != microphone)
                    throw new IOException("Settings round trip failed.");
            }
            finally { File.Delete(testSettings); }
            var available = await Encoders.Value;
            bool graphics = await Task.Run(() => CanUseGraphicsCapture.Value);
            if (!graphics) throw new IOException("This FFmpeg build has no Windows Graphics Capture filter.");
            recorder = new Recorder(test, available, graphics);
            await Task.Delay(8000);
            testSaveFolder = recorder.WorkFolder;
            var hotkey = Message.Create(Handle, 0x0312, (IntPtr)1, IntPtr.Zero);
            var saveTime = Stopwatch.StartNew();
            WndProc(ref hotkey);
            if (!saving || status.Text != "Saving replay…")
                throw new IOException("The hotkey did not accept the replay immediately. " + status.Text);
            for (int attempt = 0; attempt < 100 && lastSaved == null; attempt++) await Task.Delay(100);
            if (lastSaved == null) throw new IOException(status.Text);
            saveTime.Stop();
            var info = RunTool("ffprobe.exe", "-v error -show_entries stream=codec_name,width,height,r_frame_rate,avg_frame_rate,nb_frames:format=duration " + Quote(lastSaved));
            string thumb = Path.Combine(recorder.WorkFolder, "test.jpg");
            RunTool("ffmpeg.exe", "-v error -ss 1 -i " + Quote(lastSaved) + " -frames:v 1 -vf scale=320:-2 -q:v 5 -update 1 " + Quote(thumb));
            using (var image = Image.FromFile(thumb)) info += "Thumbnail: " + image.Width + "x" + image.Height;
            string copy = DuplicateClip(lastSaved);
            MoveClip(copy, "Smoke renamed");
            string renamed = Path.Combine(recorder.WorkFolder, "Smoke renamed.mp4");
            string trimmed = await ExportTrim(renamed, 0.31M, 1.41M);
            double length = ClipSeconds(trimmed);
            if (!File.Exists(lastSaved) || !File.Exists(renamed) || Math.Abs(length - 1.1) > 0.16)
                throw new IOException("Clip action verification failed.");
            if (!microphone) VerifySoundImport(recorder.WorkFolder);
            string estimateDetail;
            string estimate = ClipSizeEstimate(new CaptureVideo(test, available), 30, true, out estimateDetail);
            if (!estimate.Contains("per 30 s clip")) throw new IOException("Clip size estimate did not use the selected length.");
            File.WriteAllText(report, "Settings and immediate hotkey dispatch passed. Mic capture enabled: " + microphone + Environment.NewLine +
                (microphone ? "" : "WAV, MP3, M4A, AAC and FLAC import passed." + Environment.NewLine) +
                estimate + Environment.NewLine + "Hotkey to MP4: " + saveTime.ElapsedMilliseconds + " ms" + Environment.NewLine +
                "Capture: Windows Graphics Capture" + Environment.NewLine + "Encoders: " + String.Join(", ", available.Select(item => item.Label + " (" + String.Join("/", item.Codecs) + ")")) + Environment.NewLine +
                "Duplicate, rename and trim passed. Trim duration: " + length + Environment.NewLine +
                "Hotkey registered: " + hotkeyRegistered + Environment.NewLine + info);
            return 0;
        }
        catch (Exception error) { File.WriteAllText(report, error.ToString()); return 1; }
    }

    static void StyleButton(Button button, string label, int width)
    {
        button.Text = label;
        button.Width = width;
        button.Height = 31;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(103, 74, 132);
        button.BackColor = Surface;
        button.ForeColor = Ink;
    }

    void ClearCards(bool refreshLayout = true)
    {
        generation++;
        pendingPreviews = null;
        renderedSnapshot = null;
        grid.SuspendLayout();
        while (grid.Controls.Count > 0)
        {
            var card = grid.Controls[0];
            if (card.ContextMenuStrip != null) card.ContextMenuStrip.Dispose();
            var picture = card.Controls.OfType<PictureBox>().FirstOrDefault();
            if (picture != null && picture.Image != null) picture.Image.Dispose();
            foreach (Control child in card.Controls) storageDetails.SetToolTip(child, null);
            card.Dispose();
        }
        grid.ResumeLayout(false);
        if (refreshLayout) clipViewport.RefreshLayout();
    }

    Size MeasureClipGrid(Size viewport)
    {
        int columns = Math.Max(2, Math.Min(5, viewport.Width / 257));
        int width = Math.Max(160, (viewport.Width - 22) / columns - 12);
        int rows = (grid.Controls.Count + columns - 1) / columns;
        return new Size(viewport.Width, rows * (width * 9 / 16 + 75));
    }

    void SizeCards()
    {
        int columns = Math.Max(2, Math.Min(5, grid.ClientSize.Width / 257));
        int width = Math.Max(160, (grid.ClientSize.Width - 22) / columns - 12);
        var size = new Size(width, width * 9 / 16 + 63);
        grid.SuspendLayout();
        try
        {
            foreach (Control card in grid.Controls)
                if (card.Size != size) card.Size = size;
        }
        finally { grid.ResumeLayout(); }
    }

    async void RefreshLibrary(bool rescan = true)
    {
        libraryRescanPending |= rescan;
        if (rescan && !exiting)
        {
            storageRefreshUtc = DateTime.MinValue;
            RefreshClipStorage();
        }
        if (!CanRenderLibrary()) return;

        libraryRefreshPending = true;
        if (libraryRefreshing) return;
        libraryRefreshing = true;
        try
        {
            while (libraryRefreshPending && CanRenderLibrary())
            {
                libraryRefreshPending = false;
                bool force = libraryRescanPending;
                libraryRescanPending = false;
                var cached = librarySnapshot;
                string folder = Clips;
                LibrarySnapshot snapshot;
                try { snapshot = await Task.Run(() => ReadLibrary(folder, cached, force)); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                if (!CanRenderLibrary() || !String.Equals(folder, Clips, StringComparison.OrdinalIgnoreCase)) continue;
                librarySnapshot = snapshot;
                if (!libraryRescanPending && !searchTimer.Enabled)
                    RenderLibrary(snapshot, search.Text.Trim());
            }
        }
        finally { libraryRefreshing = false; }
    }

    bool CanRenderLibrary()
    {
        return !exiting && !IsDisposed && Visible && WindowState != FormWindowState.Minimized;
    }

    static LibrarySnapshot ReadLibrary(string folder, LibrarySnapshot cached, bool force)
    {
        var stamp = Directory.GetLastWriteTimeUtc(folder);
        if (!force && cached != null && cached.Folder == folder && cached.Stamp == stamp) return cached;
        var files = Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles("*.mp4").OrderByDescending(file => file.LastWriteTimeUtc).ToArray()
            : new FileInfo[0];
        return new LibrarySnapshot { Folder = folder, Stamp = stamp, Files = files };
    }

    void RenderLibrary(LibrarySnapshot snapshot, string query)
    {
        if (renderedSnapshot == snapshot && renderedPage == page && renderedQuery == query) return;
        var matches = query.Length == 0 ? snapshot.Files :
            snapshot.Files.Where(file => file.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0).ToArray();
        const int pageSize = 18;
        page = Math.Max(0, Math.Min(page, (matches.Length - 1) / pageSize));
        grid.SuspendLayout();
        ClearCards(false);
        renderedSnapshot = snapshot;
        renderedQuery = query;
        renderedPage = page;
        count.Text = matches.Length == 0 ? "No clips found" :
            String.Format("{0}–{1} of {2} clips", page * pageSize + 1, Math.Min(matches.Length, (page + 1) * pageSize), matches.Length);
        next.Enabled = (page + 1) * pageSize < matches.Length;
        previous.Enabled = page > 0;
        var previews = new List<Tuple<FileInfo, PictureBox>>();
        foreach (var file in matches.Skip(page * pageSize).Take(pageSize))
        {
            var card = new ResizePanel { BackColor = Surface, Margin = new Padding(0, 0, 12, 12) };
            var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Cursor = Cursors.Hand, AccessibleName = "Play " + file.Name };
            var title = new Label { Text = Path.GetFileNameWithoutExtension(file.Name), Dock = DockStyle.Bottom, Height = 29, Padding = new Padding(9, 6, 0, 0), AutoEllipsis = true };
            ulong fileBytes = (ulong)file.Length;
            var detail = new Label { Text = String.Format("{0:g}  ·  {1}", file.LastWriteTime, FormatStorageSize(fileBytes)), Dock = DockStyle.Bottom, Height = 27, ForeColor = Muted, Padding = new Padding(9, 1, 0, 0) };
            storageDetails.SetToolTip(detail, ExactStorageSize(fileBytes));
            picture.MouseClick += delegate(object sender, MouseEventArgs args)
            {
                if (args.Button == MouseButtons.Left) RunClipAction(() => Process.Start(file.FullName), false);
            };
            var menu = ClipMenu(file);
            card.ContextMenuStrip = picture.ContextMenuStrip = title.ContextMenuStrip = detail.ContextMenuStrip = menu;
            card.Controls.Add(picture);
            card.Controls.Add(title);
            card.Controls.Add(detail);
            grid.Controls.Add(card);
            previews.Add(Tuple.Create(file, picture));
        }
        SizeCards();
        grid.ResumeLayout();
        clipViewport.RefreshLayout();
        pendingPreviews = previews;
        LoadPreviews();
    }

    async void LoadPreviews()
    {
        if (previewLoading) return;
        previewLoading = true;
        try
        {
            while (pendingPreviews != null && !IsDisposed)
            {
                var previews = pendingPreviews;
                pendingPreviews = null;
                await LoadPagePreviews(previews, generation);
            }
        }
        finally { previewLoading = false; }
    }

    async Task LoadPagePreviews(List<Tuple<FileInfo, PictureBox>> previews, int revision)
    {
        foreach (var preview in previews)
        {
            if (revision != generation || IsDisposed) break;
            var result = await Task.Run(() => ReadPreview(preview.Item1));
            if (revision != generation || IsDisposed) { if (result.Item1 != null) result.Item1.Dispose(); break; }
            preview.Item2.Image = result.Item1;
            if (result.Item2.Length > 0)
            {
                var picture = preview.Item2;
                picture.Paint += delegate(object sender, PaintEventArgs e)
                {
                    var size = TextRenderer.MeasureText(result.Item2, picture.Font);
                    var badge = new Rectangle(picture.ClientSize.Width - size.Width - 14, picture.ClientSize.Height - size.Height - 12, size.Width + 8, size.Height + 6);
                    using (var shade = new SolidBrush(Color.FromArgb(205, 12, 13, 16))) e.Graphics.FillRectangle(shade, badge);
                    TextRenderer.DrawText(e.Graphics, result.Item2, picture.Font, badge, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                };
                picture.Invalidate();
            }
        }
    }

    static Tuple<Image, string> ReadPreview(FileInfo file)
    {
        try
        {
            string cache = Path.Combine(Data, "thumbnails");
            Directory.CreateDirectory(cache);
            string key;
            using (var hash = System.Security.Cryptography.SHA256.Create())
                key = BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(file.FullName + file.Length + file.LastWriteTimeUtc.Ticks))).Replace("-", "");
            string thumbnail = Path.Combine(cache, key + ".jpg");
            string durationPath = Path.Combine(cache, key + ".txt");
            if (!File.Exists(thumbnail))
            {
                string temporary = Path.Combine(cache, Guid.NewGuid().ToString("N") + ".jpg");
                try
                {
                    RunTool("ffmpeg.exe", "-v error -ss 1 -i " + Quote(file.FullName) + " -frames:v 1 -vf scale=320:-2 -q:v 5 -update 1 " + Quote(temporary), 10000);
                    if (!File.Exists(thumbnail)) File.Move(temporary, thumbnail);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            if (!File.Exists(durationPath))
            {
                string value = RunTool("ffprobe.exe", "-v error -show_entries format=duration -of csv=p=0 " + Quote(file.FullName), 10000);
                File.WriteAllText(durationPath, value);
            }
            double seconds;
            string length = Double.TryParse(File.ReadAllText(durationPath).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds)
                ? String.Format("{0}:{1:00}", (int)seconds / 60, (int)seconds % 60) : "";
            using (var source = Image.FromFile(thumbnail)) return Tuple.Create<Image, string>(new Bitmap(source), length);
        }
        catch { return Tuple.Create<Image, string>(null, ""); }
    }

    static string Quote(string value) { return "\"" + value + "\""; }

    static string RunTool(string name, string arguments, int timeout = 60000)
    {
        using (var process = Process.Start(new ProcessStartInfo(Path.Combine(ToolFolder, name), arguments)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout)) { process.Kill(); throw new IOException(name + " timed out."); }
            if (process.ExitCode != 0) throw new IOException(error.GetAwaiter().GetResult().Trim());
            return output.GetAwaiter().GetResult();
        }
    }

    sealed class Recorder : IDisposable
    {
        readonly Process encoder;
        readonly List<AudioInput> audio = new List<AudioInput>();
        readonly List<string> messages = new List<string>();
        readonly object storageLock = new object();
        readonly System.Threading.Timer cleanup;
        bool disposed;
        public readonly string WorkFolder = Path.Combine(Data, "buffer-" + Guid.NewGuid().ToString("N"));
        public readonly int ReplaySeconds;
        readonly int segmentSeconds;
        readonly bool graphicsCapture;
        public readonly string DiagnosticEncoder;
        public string DiagnosticCaptureMethod { get { return graphicsCapture ? "wgc" : "desktop-duplication"; } }
        public readonly DateTime StartedUtc = DateTime.UtcNow;
        string lastSavedSegment;
        long lastFrame;
        long progressTicks;
        DateTime LastFootageUtc
        {
            get
            {
                long ticks = Interlocked.Read(ref progressTicks);
                return ticks == 0 ? StartedUtc : new DateTime(ticks, DateTimeKind.Utc);
            }
        }
        public bool HasRecentFootage { get { return (DateTime.UtcNow - LastFootageUtc).TotalSeconds < segmentSeconds * 2 + 3; } }
        public bool HasStalled { get { return (DateTime.UtcNow - LastFootageUtc).TotalSeconds >= Math.Max(9, segmentSeconds * 2 + 4); } }
        public bool HasExited { get { return encoder.HasExited; } }
        public string Error { get { lock (messages) return messages.LastOrDefault() ?? ""; } }

        public Recorder(ShelfSettings options, VideoEncoder[] available, bool graphics)
        {
            ReplaySeconds = GetReplaySeconds(options);
            var video = new CaptureVideo(options, available);
            DiagnosticEncoder = video.Encoder.Id;
            segmentSeconds = video.Keyframes;
            graphicsCapture = graphics;
            if (!File.Exists(Ffmpeg)) throw new FileNotFoundException("FFmpeg is missing. Run Setup again.");
            Directory.CreateDirectory(WorkFolder);
            try
            {
                if (options.Playback) audio.Add(new AudioInput(DataFlow.Render, options.PlaybackDevice));
                if (options.Microphone) audio.Add(new AudioInput(DataFlow.Capture, options.MicDevice));
                string source = CaptureSource(options, video, graphicsCapture);
                string arguments = "-hide_banner -loglevel warning -nostats -stats_period 1 -progress pipe:1 -f lavfi -i " + Quote(source) + " ";
                foreach (var input in audio) arguments += input.FfmpegInput;
                arguments += "-map 0:v ";
                string videoFilter = video.Filter;
                if (graphicsCapture)
                    videoFilter = video.Encoder.Id == "nvenc" ? null :
                        "hwdownload,format=bgra,format=" + (video.Encoder.Id == "cpu" ? "yuv420p" : "nv12");
                if (videoFilter != null) arguments += "-vf " + Quote(videoFilter) + " ";
                var gains = new List<int>();
                if (options.Playback) gains.Add(options.PlaybackLevel);
                if (options.Microphone) gains.Add(options.MicLevel);
                if (audio.Count != 0)
                {
                    var filters = gains.Select((gain, index) => String.Format("[{0}:a]aformat=sample_rates=48000:channel_layouts=stereo,volume={1}[a{2}]",
                        index + 1, (gain / 100.0).ToString(System.Globalization.CultureInfo.InvariantCulture), index));
                    string mix = String.Join(";", filters);
                    string inputs = String.Concat(Enumerable.Range(0, audio.Count).Select(index => "[a" + index + "]"));
                    mix += ";" + inputs + (audio.Count == 1 ? "anull" : "amix=inputs=2:normalize=0") + ",alimiter=limit=0.95:level=0[a]";
                    arguments += "-filter_complex " + Quote(mix) + " -map \"[a]\" -c:a aac -b:a 192k ";
                }
                arguments += String.Format("-r {0} -fps_mode cfr {1} -g {2} -force_key_frames expr:gte(t,n_forced*{3}) " +
                    "-f segment -segment_time {3} -segment_time_delta {4} -reset_timestamps 1 -segment_format mpegts {5}",
                    video.Fps, video.Encode, video.Fps * segmentSeconds, segmentSeconds,
                    (0.5 / video.Fps).ToString(System.Globalization.CultureInfo.InvariantCulture), Quote(Path.Combine(WorkFolder, "%09d.ts")));
                encoder = new Process { StartInfo = new ProcessStartInfo(Ffmpeg, arguments)
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true } };
                encoder.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (String.IsNullOrWhiteSpace(e.Data)) return;
                    lock (messages) { messages.Add(e.Data); if (messages.Count > 12) messages.RemoveAt(0); }
                };
                encoder.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    long frame;
                    if (e.Data == null || !e.Data.StartsWith("frame=", StringComparison.Ordinal) ||
                        !Int64.TryParse(e.Data.Substring(6), out frame)) return;
                    if (frame > Interlocked.Read(ref lastFrame))
                    {
                        Interlocked.Exchange(ref lastFrame, frame);
                        Interlocked.Exchange(ref progressTicks, DateTime.UtcNow.Ticks);
                    }
                };
                encoder.Start();
                encoder.BeginOutputReadLine();
                encoder.BeginErrorReadLine();
                cleanup = new System.Threading.Timer(delegate
                {
                    lock (storageLock)
                    {
                        if (disposed) return;
                        try
                        {
                            foreach (var file in Segments().Reverse().Skip((ReplaySeconds + segmentSeconds - 1) / segmentSeconds + 3)) File.Delete(file);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }, null, 5000, 5000);
            }
            catch
            {
                if (encoder != null) encoder.Dispose();
                foreach (var input in audio) input.Dispose();
                Directory.Delete(WorkFolder, true);
                throw;
            }
        }

        static string CaptureSource(ShelfSettings options, CaptureVideo video, bool graphics)
        {
            int index = Math.Min(options.Display - 1, Screen.AllScreens.Length - 1);
            if (!graphics) return String.Format("ddagrab=output_idx={0}:framerate={1}:draw_mouse={2}", index, video.Fps, options.Cursor ? 1 : 0);

            var bounds = Screen.AllScreens[index].Bounds;
            IntPtr monitor = MonitorFromPoint(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), 2);
            string target = monitor == IntPtr.Zero ? "monitor_idx=" + index : "hmonitor=" + unchecked((ulong)monitor.ToInt64());
            string source = "gfxcapture=" + target + ":max_framerate=" + video.Fps + ":capture_cursor=" + (options.Cursor ? 1 : 0) + ":resize_mode=scale";
            if (video.ResizeWidth > 0)
                source += ":width=" + video.ResizeWidth + ":height=" + video.ResizeHeight;
            return source;
        }

        string[] Segments() { return Directory.GetFiles(WorkFolder, "*.ts").OrderBy(path => path, StringComparer.Ordinal).ToArray(); }

        public string BeginSave()
        {
            var files = Segments();
            if (HasExited || files.Length < 2 || new FileInfo(files[files.Length - 2]).Length == 0)
                throw new IOException("The replay buffer is warming up. Try again in a few seconds.");
            return files[files.Length - 1];
        }

        public async Task<string> Save(string atPress, int seconds, string target)
        {
            int segmentNumber = Int32.Parse(Path.GetFileNameWithoutExtension(atPress), System.Globalization.CultureInfo.InvariantCulture);
            string next = Path.Combine(WorkFolder, (segmentNumber + 1).ToString("D9", System.Globalization.CultureInfo.InvariantCulture) + ".ts");
            for (int attempt = 0; attempt < segmentSeconds * 25 + 8 && !HasExited && !File.Exists(next); attempt++)
                await Task.Delay(40);
            return await Task.Run(delegate
            {
                lock (storageLock)
                {
                    var files = Segments();
                    var complete = files.Take(Math.Max(0, files.Length - 1)).Where(file => String.CompareOrdinal(file, atPress) <= 0).Reverse().Take((seconds + segmentSeconds - 1) / segmentSeconds).Reverse().ToArray();
                    if (complete.Length == 0) throw new IOException("The buffer is still warming up. Try again in a few seconds.");
                    string latest = complete[complete.Length - 1];
                    if (HasStalled || latest == lastSavedSegment)
                        throw new IOException("There is no new replay footage yet. Try again in a few seconds.");
                    Directory.CreateDirectory(target);
                    string token = Guid.NewGuid().ToString("N");
                    string list = Path.Combine(WorkFolder, token + ".txt");
                    string pending = Path.Combine(target, token + ".partial");
                    string clip = Path.Combine(target, "Replay " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss-fff") + ".mp4");
                    try
                    {
                        File.WriteAllLines(list, complete.SelectMany(path => new[] { "file '" + Path.GetFileName(path) + "'", "duration " + segmentSeconds }));
                        RunTool("ffmpeg.exe", "-v error -f concat -safe 1 -i " + Quote(list) + " -c copy -f mp4 " + Quote(pending));
                        File.Move(pending, clip);
                        lastSavedSegment = latest;
                        return clip;
                    }
                    finally { File.Delete(list); File.Delete(pending); }
                }
            });
        }

        public void Dispose()
        {
            cleanup.Dispose();
            try
            {
                if (!encoder.HasExited)
                {
                    encoder.StandardInput.WriteLine("q");
                    if (!encoder.WaitForExit(1500)) { encoder.Kill(); encoder.WaitForExit(1500); }
                }
            }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            foreach (var input in audio) input.Dispose();
            encoder.Dispose();
            lock (storageLock)
            {
                disposed = true;
                try { Directory.Delete(WorkFolder, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    sealed class AudioInput : IDisposable
    {
        readonly WasapiCapture source;
        readonly MMDevice device;
        readonly TcpListener listener;
        readonly Task pump;
        readonly object gate = new object();
        readonly Queue<byte[]> queue = new Queue<byte[]>();
        readonly AutoResetEvent ready = new AutoResetEvent(false);
        TcpClient client;
        bool closing;
        public WaveFormat Format { get { return source.WaveFormat; } }
        public int Port { get; private set; }
        public string SampleFormat { get; private set; }

        public string FfmpegInput
        {
            get { return String.Format("-thread_queue_size 128 -probesize 32 -analyzeduration 0 -f {0} -ar {1} -ac {2} -i tcp://127.0.0.1:{3} ",
                SampleFormat, Format.SampleRate, Format.Channels, Port); }
        }

        public AudioInput(DataFlow flow, string id)
        {
            device = FindAudioDevice(flow, id);
            try { source = flow == DataFlow.Render ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, false, 60); }
            catch { device.Dispose(); throw; }
            var extended = Format as WaveFormatExtensible;
            var standard = extended == null ? Format : extended.ToStandardWaveFormat();
            if (standard.Encoding == WaveFormatEncoding.IeeeFloat && standard.BitsPerSample == 32) SampleFormat = "f32le";
            else if (standard.Encoding == WaveFormatEncoding.Pcm && new[] { 16, 24, 32 }.Contains(standard.BitsPerSample))
                SampleFormat = "s" + standard.BitsPerSample + "le";
            else
            {
                source.Dispose();
                device.Dispose();
                throw new IOException("An audio device uses an unsupported format.");
            }
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            source.DataAvailable += delegate(object sender, WaveInEventArgs e)
            {
                if (e.BytesRecorded == 0) return;
                var copy = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
                lock (gate)
                {
                    if (closing) return;
                    queue.Enqueue(copy);
                    while (queue.Count > 25) queue.Dequeue();
                    ready.Set();
                }
            };
            pump = Task.Run((Action)SendAudio);
        }

        void SendAudio()
        {
            try
            {
                var accepted = listener.AcceptTcpClient();
                lock (gate) { if (closing) { accepted.Close(); return; } client = accepted; }
                accepted.SendTimeout = 3000;
                var stream = accepted.GetStream();
                source.StartRecording();
                var watch = Stopwatch.StartNew();
                long sent = 0;
                int block = Format.BlockAlign;
                int bytesPerSecond = Format.AverageBytesPerSecond;
                var silence = new byte[bytesPerSecond / 10 / block * block];
                while (true)
                {
                    byte[] data = null;
                    lock (gate)
                    {
                        if (closing) return;
                        if (queue.Count > 0) data = queue.Dequeue();
                    }
                    long target = Math.Max(0, watch.ElapsedMilliseconds - 100) * bytesPerSecond / 1000 / block * block;
                    if (data != null) { stream.Write(data, 0, data.Length); sent += data.Length; }
                    else if (target - sent >= silence.Length) { stream.Write(silence, 0, silence.Length); sent += silence.Length; }
                    else
                    {
                        long due = (sent + silence.Length) * 1000 / bytesPerSecond + 100;
                        ready.WaitOne((int)Math.Max(1, Math.Min(100, due - watch.ElapsedMilliseconds)));
                    }
                }
            }
            catch (SocketException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            finally { try { source.StopRecording(); } catch (InvalidOperationException) { } }
        }

        public void Dispose()
        {
            lock (gate)
            {
                closing = true;
                if (client != null) client.Close();
                ready.Set();
            }
            listener.Stop();
            bool stopped = pump.Wait(3500);
            source.Dispose();
            device.Dispose();
            if (stopped) ready.Dispose();
        }
    }
}
