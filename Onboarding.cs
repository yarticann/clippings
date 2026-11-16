using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Diagnostics;

sealed partial class ReplayShelf
{
    struct ClipKey
    {
        public readonly uint Code, Modifiers;
        public ClipKey(uint code, uint modifiers) { Code = code; Modifiers = modifiers; }
        public string Label
        {
            get
            {
                string key = new KeysConverter().ConvertToInvariantString((Keys)Code);
                return ((Modifiers & 2) != 0 ? "Ctrl+" : "") + ((Modifiers & 1) != 0 ? "Alt+" : "") +
                    ((Modifiers & 4) != 0 ? "Shift+" : "") + ((Modifiers & 8) != 0 ? "Win+" : "") + key;
            }
        }
        public bool Same(ClipKey other) { return Code == other.Code && Modifiers == other.Modifiers; }
    }

    static readonly ClipKey[] LegacyKeys = { new ClipKey(120, 1), new ClipKey(120, 3), new ClipKey(120, 6), new ClipKey(121, 1) };
    ClipKey registeredKey;
    static ClipKey ReadClipKey(ShelfSettings value)
    {
        if (value.ShortcutKey >= 8 && value.ShortcutKey <= 254 && value.ShortcutModifiers <= 15)
            return new ClipKey(value.ShortcutKey, value.ShortcutModifiers);
        return LegacyKeys[Math.Max(0, Math.Min(LegacyKeys.Length - 1, value.Shortcut))];
    }

    [DllImport("user32.dll")]
    static extern short GetKeyState(int key);

    sealed class KeyCaptureForm : Form
    {
        readonly Action<ClipKey> capture;
        public KeyCaptureForm(Action<ClipKey> captureKey) { capture = captureKey; }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            if (key == Keys.ShiftKey || key == Keys.ControlKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin)
                return true;
            uint modifiers = (uint)(((keyData & Keys.Alt) != 0 ? 1 : 0) |
                ((keyData & Keys.Control) != 0 ? 2 : 0) | ((keyData & Keys.Shift) != 0 ? 4 : 0) |
                ((GetKeyState(91) < 0 || GetKeyState(92) < 0) ? 8 : 0));
            if ((int)key >= 8 && (int)key <= 254) capture(new ClipKey((uint)key, modifiers));
            return true;
        }
    }

    void CheckShortcut(ClipKey key)
    {
        if (key.Code == 123) throw new IOException("Windows reserves F12. Choose another key.");
        if (hotkeyRegistered && key.Same(registeredKey)) return;
        if (!RegisterHotKey(Handle, 2, 0x4000 | key.Modifiers, key.Code))
            throw new IOException(key.Label + " is reserved by Windows or in use by another app.");
        UnregisterHotKey(Handle, 2);
    }

    ClipKey PickShortcut(Form owner, ClipKey existing)
    {
        ClipKey selected = existing;
        bool suspended = hotkeyRegistered;
        if (suspended)
        {
            UnregisterHotKey(Handle, 1);
            hotkeyRegistered = false;
        }
        try
        {
            var readout = new Label { Left = 17, Top = 15, Width = 436, Height = 61, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Text = "Press your new save shortcut.", ForeColor = Ink };
            using (var picker = new KeyCaptureForm(delegate(ClipKey key) { selected = key; readout.Text = key.Label; }))
            {
                picker.Text = "Set save hotkey";
                picker.ClientSize = new Size(470, 133);
                EnableDialogResize(picker);
                picker.StartPosition = FormStartPosition.CenterParent;
                picker.BackColor = Background;
                picker.Font = Font;
                picker.Icon = Icon;
                picker.MinimizeBox = picker.ShowInTaskbar = false;
                picker.Controls.Add(readout);
                Button use;
                DialogFooter(picker, "Use shortcut", out use);
                use.Click += delegate
                {
                    try { CheckShortcut(selected); picker.DialogResult = DialogResult.OK; }
                    catch (IOException error) { readout.Text = error.Message; }
                };
                return picker.ShowDialog(owner) == DialogResult.OK ? selected : existing;
            }
        }
        finally { if (suspended) SetShortcut(); }
    }

    static string SetupSeenPath { get { return Path.Combine(Data, "setup-seen"); } }

    static bool NeedsStartupGuide(string[] args)
    {
        if (!PortableMode && !Array.Exists(args, value => value == "--setup-guide")) return false;
        return !File.Exists(SettingsPath) && !File.Exists(SetupSeenPath) &&
            !File.Exists(Path.Combine(Data, "hotkey.txt"));
    }

    void ShowStartupGuide()
    {
        ShowSetupGuide();
        try { File.WriteAllText(SetupSeenPath, "seen"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    void ShowSetupGuide()
    {
        using (var wizard = PreferencesWindow("clippings setup", new Size(660, 345)))
        using (var testTimer = new System.Windows.Forms.Timer { Interval = 220 })
        {
            ClipKey key = ReadClipKey(settings);
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(18) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var screen = Choice(MonitorChoices(Screen.AllScreens), settings.Display - 1);
            var path = new TextBox { Text = Clips, Dock = DockStyle.Fill };
            var location = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            path.Dock = DockStyle.None;
            path.Width = 330;
            var browse = new Button();
            StyleButton(browse, "Browse…", 95);
            browse.Click += delegate
            {
                using (var chooser = new FolderBrowserDialog { SelectedPath = path.Text })
                    if (chooser.ShowDialog(wizard) == DialogResult.OK) path.Text = chooser.SelectedPath;
            };
            location.Controls.Add(path); location.Controls.Add(browse);
            var bind = new Button { Anchor = AnchorStyles.Left };
            StyleButton(bind, key.Label + "  ·  Change", 230);
            bind.Click += delegate { key = PickShortcut(wizard, key); bind.Text = key.Label + "  ·  Change"; };
            var quality = (settings.Video ?? LegacyVideo(settings)).Copy();
            var preset = Choice(new object[] { "Keep current quality", "Auto (recommended)" }, (quality.Experienced || quality.SimplePreset != 0) ? 0 : 1);
            var hint = new Label { Text = "Choose where clippings records and saves replays.", Dock = DockStyle.Fill, ForeColor = Muted };
            SettingsRow(table, "", hint, 39);
            SettingsRow(table, "Monitor", screen, 39);
            SettingsRow(table, "Clip folder", location, 42);
            SettingsRow(table, "Save hotkey", bind, 41);
            SettingsRow(table, "Quality", preset, 39);
            var statusLine = new Label { Dock = DockStyle.Fill, ForeColor = Muted };
            SettingsRow(table, "", statusLine, 60);
            wizard.Controls.Add(table);
            Button nextStep;
            var footer = DialogFooter(wizard, "Save and continue", out nextStep);
            var startTest = new Button { Visible = false };
            var watch = new Button { Visible = false };
            StyleButton(startTest, "Start test", 103);
            StyleButton(watch, "Play test clip", 111);
            footer.Controls.Add(startTest); footer.Controls.Add(watch);
            bool configured = false;
            bool startedHere = false;
            string priorClip = lastSaved;
            nextStep.Click += delegate
            {
                if (configured) { if (!saving && !starting) wizard.DialogResult = DialogResult.OK; return; }
                try
                {
                    CheckShortcut(key);
                    string folder = path.Text.Trim();
                    if (!Path.IsPathRooted(folder) || folder.Length < 3) throw new IOException("Choose a full clip folder path.");
                    folder = Path.GetFullPath(folder);
                    Directory.CreateDirectory(folder);
                    var updated = (ShelfSettings)settings.CloneForSetup();
                    updated.Display = screen.SelectedIndex + 1;
                    updated.ClipFolder = folder;
                    updated.ShortcutKey = key.Code;
                    updated.ShortcutModifiers = key.Modifiers;
                    if (preset.SelectedIndex == 1) updated.Video = new VideoPreferences();
                    WriteSettings(SettingsPath, updated);
                    settings = updated;
                    SetShortcut();
                    page = 0;
                    RefreshLibrary();
                    configured = true;
                    screen.Enabled = path.Enabled = browse.Enabled = bind.Enabled = preset.Enabled = false;
                    nextStep.Text = "Finish";
                    startTest.Visible = true;
                    hint.Text = "Setup saved. You can record a test clip or finish now.";
                    statusLine.Text = recorder == null ? "Start the test, then press " + key.Label + "." :
                        "The existing buffer is still running. Press " + key.Label + " to test saving. New capture settings apply after a restart.";
                }
                catch (Exception error) { statusLine.Text = error.Message; }
            };
            startTest.Click += delegate
            {
                if (recorder == null && !starting) { startedHere = true; ToggleCapture(); }
                startTest.Enabled = false;
                priorClip = lastSaved;
                testTimer.Start();
            };
            watch.Click += delegate { if (lastSaved != null) RunClipAction(() => Process.Start(lastSaved), false); };
            testTimer.Tick += delegate
            {
                if (lastSaved != null && lastSaved != priorClip)
                {
                    hint.Text = "Test clip saved.";
                    statusLine.Text = lastSaved;
                    watch.Visible = true;
                    testTimer.Stop();
                }
                else statusLine.Text = (starting || saving) ? status.Text : status.Text + " · Press " + key.Label + " to save.";
            };
            wizard.FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (startedHere && (starting || saving)) { e.Cancel = true; return; }
                if (startedHere && recorder != null) ToggleCapture();
            };
            wizard.ShowDialog(this);
        }
    }
}
