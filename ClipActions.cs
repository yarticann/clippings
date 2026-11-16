using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;

sealed partial class ReplayShelf
{
    [StructLayout(LayoutKind.Sequential)]
    struct RecycleBinInfo
    {
        public int Size;
        public long Bytes;
        public long Count;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string rootPath, ref RecycleBinInfo info);

    ContextMenuStrip ClipMenu(FileInfo file)
    {
        var menu = CreateThemedMenu();
        menu.Items.Add("Rename…", null, delegate { RunClipAction(() => RenameClip(file)); });
        menu.Items.Add("Duplicate", null, async delegate
        {
            menu.Enabled = false;
            try
            {
                await Task.Run(() => DuplicateClip(file.FullName));
                RefreshLibrary();
            }
            catch (Exception error) { ReportFailure("Clip action failed", error.Message); }
            finally { if (!menu.IsDisposed) menu.Enabled = true; }
        });
        menu.Items.Add("Trim…", null, async delegate
        {
            try { await TrimClip(file); }
            catch (Exception error) { ReportFailure("Trim could not open", error.Message); }
        });
        menu.Items.Add("Open file location", null, delegate
        {
            RunClipAction(() => Process.Start("explorer.exe", "/select," + Quote(file.FullName)), false);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete…", null, delegate { RunClipAction(() => DeleteClip(file)); });
        return menu;
    }

    ContextMenuStrip CreateThemedMenu()
    {
        var menu = new ContextMenuStrip { BackColor = Color.FromArgb(19, 12, 29), ForeColor = Ink, ShowImageMargin = false,
            Padding = new Padding(5), Renderer = new ToolStripProfessionalRenderer(new MenuColors()) };
        menu.ItemAdded += delegate(object sender, ToolStripItemEventArgs args)
        {
            var item = args.Item;
            item.ForeColor = Ink;
            if (!(item is ToolStripMenuItem)) return;
            item.Padding = new Padding(12, 7, 12, 7);
            item.Font = Font;
        };
        return menu;
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(19, 12, 29); } }
        public override Color MenuBorder { get { return Color.FromArgb(84, 57, 111); } }
        public override Color MenuItemSelected { get { return Color.FromArgb(58, 34, 83); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(122, 75, 164); } }
        public override Color SeparatorDark { get { return Color.FromArgb(65, 46, 84); } }
        public override Color SeparatorLight { get { return SeparatorDark; } }
    }

    void RunClipAction(Action action, bool refresh = true)
    {
        try
        {
            action();
            if (refresh) RefreshLibrary();
        }
        catch (Exception error) { ReportFailure("Clip action failed", error.Message); }
    }

    static string RenamePath(string source, string name)
    {
        name = name.Trim();
        if (name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
        if (String.IsNullOrWhiteSpace(name) || name.EndsWith(".") || name.EndsWith(" ") || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("Enter a valid Windows filename.");
        string stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem) ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
            throw new IOException("That filename is reserved by Windows.");
        return Path.Combine(Path.GetDirectoryName(source), name + Path.GetExtension(source));
    }

    static void MoveClip(string source, string name)
    {
        string target = RenamePath(source, name);
        if (target == source) return;
        if (!String.Equals(target, source, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(target)) throw new IOException("A clip with that name already exists.");
            File.Move(source, target);
            return;
        }
        string pending = Path.Combine(Path.GetDirectoryName(source), Guid.NewGuid().ToString("N") + ".partial");
        File.Move(source, pending);
        try { File.Move(pending, target); }
        catch { File.Move(pending, source); throw; }
    }

    void RenameClip(FileInfo file)
    {
        using (var dialog = PreferencesWindow("Rename clip", new Size(480, 112)))
        {
            var entry = new TextBox { Left = 17, Top = 15, Width = 446, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Text = Path.GetFileNameWithoutExtension(file.Name), AccessibleName = "Clip name" };
            dialog.Controls.Add(entry);
            dialog.Shown += delegate { entry.Focus(); entry.SelectAll(); };
            Button save;
            DialogFooter(dialog, "Rename", out save);
            save.Click += delegate
            {
                try { MoveClip(file.FullName, entry.Text); dialog.DialogResult = DialogResult.OK; }
                catch (Exception error) { ReportFailure("Rename failed", error.Message); }
            };
            dialog.ShowDialog(this);
        }
    }

    static string NextClipPath(string source, string suffix)
    {
        string dir = Path.GetDirectoryName(source);
        string name = Path.GetFileNameWithoutExtension(source) + suffix;
        string path = Path.Combine(dir, name + ".mp4");
        for (int number = 2; File.Exists(path); number++) path = Path.Combine(dir, name + " " + number + ".mp4");
        return path;
    }

    static string DuplicateClip(string source)
    {
        string pending = Path.Combine(Path.GetDirectoryName(source), Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            File.Copy(source, pending);
            string target = NextClipPath(source, " - copy");
            File.Move(pending, target);
            return target;
        }
        finally { File.Delete(pending); }
    }

    void DeleteClip(FileInfo file)
    {
        var bin = new RecycleBinInfo { Size = Marshal.SizeOf(typeof(RecycleBinInfo)) };
        string root = Path.GetPathRoot(file.FullName);
        bool canRecycle = new DriveInfo(root).DriveType == DriveType.Fixed && SHQueryRecycleBin(root, ref bin) == 0;
        using (var dialog = PreferencesWindow("Delete clip", new Size(560, 158)))
        {
            var name = new Label { Text = file.Name, Left = 16, Top = 16, Width = 528, Height = 26, AutoEllipsis = true, UseMnemonic = false };
            var warning = new Label
            {
                Text = (canRecycle ? "" : "The Recycle Bin is unavailable for this drive. ") + "Permanent deletion cannot be undone.",
                Left = 16, Top = 51, Width = 528, Height = 42, ForeColor = Muted
            };
            dialog.Controls.Add(name);
            dialog.Controls.Add(warning);
            Button recycle;
            var footer = DialogFooter(dialog, "Move to Recycle Bin", out recycle);
            recycle.Width = 159;
            recycle.Enabled = canRecycle;
            recycle.DialogResult = DialogResult.Yes;
            var permanent = new Button { DialogResult = DialogResult.No };
            StyleButton(permanent, "Delete permanently", 157);
            footer.Controls.Add(permanent);
            dialog.AcceptButton = dialog.CancelButton;
            dialog.Shown += delegate { dialog.ActiveControl = (Control)dialog.CancelButton; };
            var choice = dialog.ShowDialog(this);
            if (choice == DialogResult.No) File.Delete(file.FullName);
            else if (choice == DialogResult.Yes)
            {
                try { FileSystem.DeleteFile(file.FullName, UIOption.AllDialogs, RecycleOption.SendToRecycleBin); }
                catch (OperationCanceledException) { }
            }
        }
    }

    static double ClipSeconds(string path)
    {
        string response = RunTool("ffprobe.exe", "-v error -show_entries format=duration -of csv=p=0 " + Quote(path));
        double length;
        if (!Double.TryParse(response.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out length) || Double.IsNaN(length) || length <= 0)
            throw new IOException("Could not read clip duration.");
        return length;
    }

    static Image TrimPreview(string path, decimal position)
    {
        string frame = Path.Combine(Data, "preview-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            RunTool("ffmpeg.exe", "-v error -ss " + position.ToString(CultureInfo.InvariantCulture) + " -i " + Quote(path) +
                " -frames:v 1 -vf scale=540:-2 -q:v 4 -update 1 " + Quote(frame));
            using (var image = Image.FromFile(frame)) return new Bitmap(image);
        }
        finally { File.Delete(frame); }
    }

    sealed class TrimDestination
    {
        public string Path;
        public bool ReplaceOriginal;
    }

    TrimDestination ChooseTrimDestination(Form owner, FileInfo file)
    {
        using (var dialog = PreferencesWindow("Save trimmed clip", new Size(530, 221)))
        {
            var originalName = Path.GetFileNameWithoutExtension(file.Name);
            var copyName = Path.GetFileNameWithoutExtension(NextClipPath(file.FullName, " - trimmed"));
            var entry = new TextBox { Left = 17, Top = 37, Width = 496, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Text = copyName, AccessibleName = "Trimmed clip name" };
            var copy = new RadioButton { Left = 17, Top = 73, Width = 190, Text = "Save as copy", Checked = true };
            var replace = new RadioButton { Left = 17, Top = 103, Width = 210, Text = "Overwrite original" };
            var message = new Label { Left = 17, Top = 138, Width = 496, Height = 30, ForeColor = Muted };
            dialog.Controls.Add(new Label { Text = "Clip name", Left = 17, Top = 13, Width = 100 });
            dialog.Controls.Add(entry);
            dialog.Controls.Add(copy);
            dialog.Controls.Add(replace);
            dialog.Controls.Add(message);
            replace.CheckedChanged += delegate
            {
                if (replace.Checked && entry.Text == copyName) entry.Text = originalName;
                else if (!replace.Checked && entry.Text == originalName) entry.Text = copyName;
            };
            dialog.Shown += delegate { entry.Focus(); entry.SelectAll(); };
            TrimDestination destination = null;
            Button save;
            DialogFooter(dialog, "Save", out save);
            save.Click += delegate
            {
                try
                {
                    string target = RenamePath(file.FullName, entry.Text);
                    bool same = String.Equals(target, file.FullName, StringComparison.OrdinalIgnoreCase);
                    if (same && copy.Checked)
                        throw new IOException("Choose a different name for the copy.");
                    if (!same && File.Exists(target))
                        throw new IOException("A different clip already has that name.");
                    if (replace.Checked && MessageBox.Show(dialog, "Replace “" + file.Name +
                        "” with the trimmed clip? This cannot be undone.", "Overwrite original",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                    destination = new TrimDestination { Path = target, ReplaceOriginal = replace.Checked };
                    dialog.DialogResult = DialogResult.OK;
                }
                catch (Exception error) { message.Text = error.Message; }
            };
            return dialog.ShowDialog(owner) == DialogResult.OK ? destination : null;
        }
    }

    static async Task<string> ExportTrim(string source, decimal start, decimal end, TrimDestination destination = null)
    {
        var available = await Encoders.Value;
        var encoder = available.FirstOrDefault(item => item.Codecs.Contains("h264"));
        if (encoder == null) throw new IOException("No encoder is available for trim export.");
        return await Task.Run(delegate
        {
            string pending = Path.Combine(Path.GetDirectoryName(source), Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                var original = new FileInfo(source);
                long originalLength = original.Length;
                DateTime originalWrite = original.LastWriteTimeUtc;
                RunTool("ffmpeg.exe", "-v error -ss " + start.ToString(CultureInfo.InvariantCulture) + " -i " + Quote(source) +
                    " -t " + (end - start).ToString(CultureInfo.InvariantCulture) + " -map 0:v:0 -map 0:a? -pix_fmt " +
                    (encoder.Id == "qsv" || encoder.Id == "amf" ? "nv12 " : "yuv420p ") +
                    EncodeOptions(encoder, "h264", "Constant quality", 25, 19, "Balanced") +
                    " -c:a aac -b:a 192k -movflags +faststart -f mp4 " + Quote(pending), 600000);
                string output = destination == null ? NextClipPath(source, " - trimmed") : destination.Path;
                if (destination == null || !destination.ReplaceOriginal)
                {
                    File.Move(pending, output);
                    return output;
                }

                string framePacket = RunTool("ffprobe.exe", "-v error -select_streams v:0 -read_intervals %+#1 -show_entries packet=size -of csv=p=0 " + Quote(pending));
                int packetSize;
                if (!Int32.TryParse(framePacket.Trim(), out packetSize) || packetSize <= 0)
                    throw new IOException("The selected range produced no video. The original was preserved.");
                original.Refresh();
                if (!original.Exists || original.Length != originalLength || original.LastWriteTimeUtc != originalWrite)
                    throw new IOException("The original clip changed during export. It was preserved.");

                if (String.Equals(source, output, StringComparison.OrdinalIgnoreCase))
                {
                    File.Replace(pending, source, null);
                    return source;
                }

                // Place the new file first. If removing the original fails, undo the new placement.
                File.Move(pending, output);
                try { File.Delete(source); }
                catch
                {
                    File.Delete(output);
                    throw;
                }
                return output;
            }
            finally { File.Delete(pending); }
        });
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetPreviewWindowStyle(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    static extern int SetPreviewWindowStyle(IntPtr window, int index, int value);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    static extern IntPtr GetParent(IntPtr window);

    async Task TrimClip(FileInfo file)
    {
        double length = await Task.Run(() => ClipSeconds(file.FullName));
        using (var dialog = PreferencesWindow("Trim clip", new Size(654, 522)))
        using (var debounce = new System.Windows.Forms.Timer { Interval = 140 })
        using (var playbackClock = new System.Windows.Forms.Timer { Interval = 100 })
        {
            var picture = new PictureBox { Left = 16, Top = 12, Width = 622, Height = 290, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "Clip preview" };
            dialog.Controls.Add(picture);
            int ticks = Math.Max(1, (int)Math.Min(Int32.MaxValue, Math.Floor(length * 100)));
            var scrub = new ResizeTrackBar { Left = 14, Top = 304, Width = 625, Maximum = ticks, TickStyle = TickStyle.None, AccessibleName = "Preview position" };
            dialog.Controls.Add(scrub);
            decimal max = (decimal)length;
            var start = new NumericUpDown { Left = 83, Top = 393, Width = 102, Maximum = max, DecimalPlaces = 2, Increment = 0.1M, AccessibleName = "Start time in seconds" };
            var end = new NumericUpDown { Left = 250, Top = 393, Width = 102, Maximum = max, Value = max, DecimalPlaces = 2, Increment = 0.1M, AccessibleName = "End time in seconds" };
            dialog.Controls.Add(start);
            dialog.Controls.Add(end);
            var startLabel = new Label { Text = "Start (s)", Left = 17, Width = 65 };
            var endLabel = new Label { Text = "End (s)", Left = 191, Width = 59 };
            dialog.Controls.Add(startLabel);
            dialog.Controls.Add(endLabel);
            var markStart = new Button { Left = 373, Top = 390 };
            var markEnd = new Button { Left = 508, Top = 390 };
            StyleButton(markStart, "Set start here", 124);
            StyleButton(markEnd, "Set end here", 124);
            markStart.Click += delegate { start.Value = Math.Min(max, scrub.Value / 100M); };
            markEnd.Click += delegate { end.Value = scrub.Value == scrub.Maximum ? max : Math.Min(max, scrub.Value / 100M); };
            start.ValueChanged += delegate { scrub.Value = Math.Min(ticks, (int)(start.Value * 100)); };
            end.ValueChanged += delegate { scrub.Value = Math.Min(ticks, (int)(end.Value * 100)); };
            dialog.Controls.Add(markStart);
            dialog.Controls.Add(markEnd);
            var state = new Label { Left = 17, Top = 438, Width = 610, Height = 27, ForeColor = Muted };
            dialog.Controls.Add(state);

            var play = new Button { Left = 16, Top = 350 };
            StyleButton(play, "Play from here", 141);
            dialog.Controls.Add(play);
            Process player = null;
            IntPtr playerWindow = IntPtr.Zero;
            long playerPosition = -1;
            Stopwatch openingPlayer = null;
            string playerFailure = null;
            bool followingPlayer = false;
            bool seekPending = false;
            bool exporting = false;
            bool previewBusy = false;
            int loaded = -1;
            Action request = delegate { debounce.Stop(); debounce.Start(); };
            scrub.ValueChanged += delegate
            {
                if (followingPlayer) return;
                if (player != null) seekPending = true;
                request();
            };

            Action stopPlayback = delegate
            {
                playbackClock.Stop();
                var current = player;
                player = null;
                playerWindow = IntPtr.Zero;
                seekPending = false;
                play.Text = "Play from here";
                if (current == null) return;
                try { if (!current.HasExited) current.Kill(); }
                catch (InvalidOperationException) { }
                finally { current.Dispose(); }
                if (!dialog.IsDisposed) { loaded = -1; request(); }
            };
            playbackClock.Tick += delegate
            {
                if (player == null) return;
                if (player.HasExited)
                {
                    int code = player.ExitCode;
                    stopPlayback();
                    if (code != 0) ReportFailure("Trim playback failed", playerFailure ?? "The preview player stopped.");
                    return;
                }
                if (playerWindow == IntPtr.Zero)
                {
                    player.Refresh();
                    var child = player.MainWindowHandle;
                    if (child != IntPtr.Zero)
                    {
                        int style = GetPreviewWindowStyle(child, -16);
                        SetPreviewWindowStyle(child, -16, (style & ~unchecked((int)0x80CF0000)) | 0x40000000);
                        SetParent(child, picture.Handle);
                        if (GetParent(child) != picture.Handle ||
                            !SetWindowPos(child, IntPtr.Zero, 0, 0, picture.Width, picture.Height, 0x0074))
                        {
                            stopPlayback();
                            ReportFailure("Trim playback failed", "Windows could not attach the preview to Trim.");
                            return;
                        }
                        playerWindow = child;
                    }
                    else if (openingPlayer.ElapsedMilliseconds > 6000)
                    {
                        stopPlayback();
                        ReportFailure("Trim playback failed", "The preview player did not open.");
                        return;
                    }
                }
                long centiseconds = System.Threading.Interlocked.Read(ref playerPosition);
                if (centiseconds < 0 || seekPending || scrub.Capture) return;
                followingPlayer = true;
                try { scrub.Value = (int)Math.Min(ticks, centiseconds); }
                finally { followingPlayer = false; }
                state.Text = "Preview at " + (scrub.Value / 100M).ToString("0.00") + " s";
            };
            Action<decimal> playAt = delegate(decimal position)
            {
                try
                {
                    string ffplay = Path.Combine(ToolFolder, "ffplay.exe");
                    if (!File.Exists(ffplay)) throw new IOException("ffplay.exe is required for in-window playback. Install the full FFmpeg package.");
                    System.Threading.Interlocked.Exchange(ref playerPosition, -1);
                    playerFailure = null;
                    openingPlayer = Stopwatch.StartNew();
                    player = new Process
                    {
                        StartInfo = new ProcessStartInfo(ffplay,
                            "-hide_banner -loglevel error -stats -autoexit -window_title " + Quote("clippings preview") +
                            " -left -32000 -top -32000 -x " + picture.Width + " -y " + picture.Height +
                            " -ss " + position.ToString(CultureInfo.InvariantCulture) + " -i " + Quote(file.FullName))
                        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }
                    };
                    player.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
                    {
                        if (!ReferenceEquals(sender, player) || String.IsNullOrWhiteSpace(args.Data)) return;
                        string line = args.Data.Trim();
                        int space = line.IndexOf(' ');
                        double seconds;
                        if (space > 0 && Double.TryParse(line.Substring(0, space), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                            && !Double.IsInfinity(seconds) && !Double.IsNaN(seconds))
                            System.Threading.Interlocked.Exchange(ref playerPosition, (long)Math.Max(0, seconds * 100));
                        else if (!line.StartsWith("nan", StringComparison.Ordinal)) playerFailure = line;
                    };
                    player.Start();
                    player.BeginErrorReadLine();
                    debounce.Stop();
                    seekPending = false;
                    play.Text = "Stop playback";
                    playbackClock.Start();
                }
                catch (Exception error)
                {
                    stopPlayback();
                    state.Text = error.Message;
                    ReportFailure("Trim playback failed", error.Message);
                }
            };
            play.Click += delegate
            {
                if (player != null) { stopPlayback(); return; }
                playAt(scrub.Value == ticks ? 0 : scrub.Value / 100M);
            };
            dialog.Shown += delegate { request(); };
            debounce.Tick += async delegate
            {
                debounce.Stop();
                if (seekPending && player != null)
                {
                    if (scrub.Capture || (Control.MouseButtons & MouseButtons.Left) != MouseButtons.None) { request(); return; }
                    decimal target = Math.Max(0, Math.Min(max - 0.1M, scrub.Value / 100M));
                    stopPlayback();
                    playAt(target);
                    return;
                }
                if (previewBusy || exporting || player != null || scrub.Value == loaded) return;
                previewBusy = true;
                int requested = scrub.Value;
                try
                {
                    decimal position = Math.Max(0, Math.Min(max - 0.1M, requested / 100M));
                    var frame = await Task.Run(() => TrimPreview(file.FullName, position));
                    if (dialog.IsDisposed || player != null || scrub.Value != requested) frame.Dispose();
                    else
                    {
                        var old = picture.Image;
                        picture.Image = frame;
                        if (old != null) old.Dispose();
                        state.Text = "Preview at " + (requested / 100M).ToString("0.00") + " s";
                    }
                }
                catch (Exception error) { if (!dialog.IsDisposed && !exporting) state.Text = error.Message; }
                finally
                {
                    previewBusy = false;
                    loaded = requested;
                    if (!dialog.IsDisposed && scrub.Value != loaded && !exporting) request();
                }
            };

            Button save;
            DialogFooter(dialog, "Save trim…", out save, minimumBodySize: new Size(390, 330));
            var body = picture.Parent;
            Action layoutPreview = delegate
            {
                bool compact = body.ClientSize.Width < 650;
                int controlsHeight = compact ? 222 : 184;
                int previewWidth = body.ClientSize.Width - 32;
                int previewHeight = body.ClientSize.Height - controlsHeight;
                body.SuspendLayout();
                try
                {
                    picture.SetBounds(16, 12, previewWidth, previewHeight);
                    int bottom = picture.Bottom;
                    scrub.SetBounds(14, bottom + 2, previewWidth + 3, scrub.Height);
                    play.Top = bottom + 48;
                    start.Top = end.Top = bottom + 91;
                    startLabel.Top = endLabel.Top = bottom + 95;
                    markStart.Location = new Point(compact ? 16 : 373, bottom + (compact ? 126 : 88));
                    markEnd.Location = new Point(compact ? 151 : 508, markStart.Top);
                    state.SetBounds(17, bottom + (compact ? 174 : 136), previewWidth, state.Height);
                }
                finally { body.ResumeLayout(false); }

            };
            body.ClientSizeChanged += delegate { layoutPreview(); };
            bool playbackResizeQueued = false;
            picture.SizeChanged += delegate
            {
                if (playerWindow == IntPtr.Zero || playbackResizeQueued) return;
                playbackResizeQueued = true;
                picture.BeginInvoke((Action)delegate
                {
                    playbackResizeQueued = false;
                    if (playerWindow != IntPtr.Zero && !picture.IsDisposed)
                        SetWindowPos(playerWindow, IntPtr.Zero, 0, 0, picture.ClientSize.Width, picture.ClientSize.Height, 0x0014);
                });
            };
            layoutPreview();
            dialog.FormClosing += delegate(object sender, FormClosingEventArgs args) { if (exporting) args.Cancel = true; };
            save.Click += async delegate
            {
                if (end.Value <= start.Value) { state.Text = "End time must be later than start time."; return; }
                stopPlayback();
                var destination = ChooseTrimDestination(dialog, file);
                if (destination == null) return;
                exporting = true;
                play.Enabled = false;
                save.Enabled = false;
                state.Text = "Exporting trimmed copy…";
                try
                {
                    string output = await ExportTrim(file.FullName, start.Value, end.Value, destination);
                    SetStatus("Saved " + Path.GetFileName(output));
                    exporting = false;
                    dialog.DialogResult = DialogResult.OK;
                }
                catch (Exception error) { state.Text = error.Message; ReportFailure("Trim export failed", error.Message); }
                finally { exporting = false; save.Enabled = true; play.Enabled = true; }
            };
            try { if (dialog.ShowDialog(this) == DialogResult.OK) RefreshLibrary(); }
            finally
            {
                stopPlayback();
                if (picture.Image != null) picture.Image.Dispose();
            }
        }
    }
}
