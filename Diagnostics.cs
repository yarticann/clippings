using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyVersion("0.56.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.56.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("v0.56")]

sealed partial class ReplayShelf
{
    static readonly string[] DiagnosticEvents =
    {
        "Operation failed", "Microphone test failed", "Mic monitoring failed", "Hotkey unavailable", "Capture could not start", "Capture stop failed", "Capture stopped", "Capture interrupted",
        "Capture restart failed", "Replay unavailable", "Clip save failed", "Clip action failed", "Trim could not open",
        "Trim playback failed", "Trim export failed", "Rename failed", "Settings could not open", "Settings could not save",
        "Sound import failed", "Sound preview failed", "Clip sound unavailable", "Default sound unavailable", "Clip sound failed",
        "Other error"
    };
    static readonly string[] DiagnosticReasons =
    {
        "Disk full", "Access denied", "File locked", "Name already exists", "File missing",
        "Encoder unavailable", "Capture source unavailable", "Buffer not ready", "Timeout", "Invalid input",
        "Unsupported media", "Unclassified"
    };
    static readonly string[] DiagnosticEncoders = { "nvenc", "amf", "qsv", "cpu", "auto", "unknown" };
    static readonly string[] DiagnosticCapture = { "wgc", "desktop-duplication", "not running" };
    static string ErrorLogPath { get { return Path.Combine(Data, "errors.txt"); } }
    static string BuildNumber { get { return "v0.56"; } }

    static string ClassifyError(string detail)
    {
        string lower = (detail ?? "").ToLowerInvariant();
        if (lower.Contains("disk full") || lower.Contains("no space left") || lower.Contains("not enough space")) return "Disk full";
        if (lower.Contains("access is denied") || lower.Contains("permission denied")) return "Access denied";
        if (lower.Contains("being used by another process") || lower.Contains("sharing violation")) return "File locked";
        if (lower.Contains("already exists") || lower.Contains("already has that name")) return "Name already exists";
        if (lower.Contains("file not found") || lower.Contains("no such file") || lower.Contains("cannot find the file")) return "File missing";
        if (lower.Contains("no encoder") || lower.Contains("no capable devices") || lower.Contains("encoder is unavailable")) return "Encoder unavailable";
        if (lower.Contains("dxgi_error_access_lost") || lower.Contains("screen stopped") || lower.Contains("capture item") ||
            lower.Contains("could not attach")) return "Capture source unavailable";
        if (lower.Contains("warming up") || lower.Contains("collecting new footage") || lower.Contains("buffer first") ||
            lower.Contains("no new replay footage") || lower.Contains("fresh footage")) return "Buffer not ready";
        if (lower.Contains("timed out") || lower.Contains("timeout") || lower.Contains("did not open")) return "Timeout";
        if (lower.Contains("invalid argument") || lower.Contains("valid windows filename") || lower.Contains("full clip folder path")) return "Invalid input";
        if (lower.Contains("unsupported") || lower.Contains("invalid data found")) return "Unsupported media";
        return "Unclassified";
    }

    static List<string> ReadDiagnosticEvents()
    {
        var result = new List<string>();
        if (!File.Exists(ErrorLogPath) || new FileInfo(ErrorLogPath).Length > 32768) return result;
        foreach (var line in File.ReadLines(ErrorLogPath))
        {
            var fields = line.Split('|');
            DateTime time;
            if (fields.Length == 5 &&
                DateTime.TryParseExact(fields[0], "yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.None, out time) &&
                DiagnosticEvents.Contains(fields[1]) && DiagnosticReasons.Contains(fields[2]) &&
                DiagnosticEncoders.Contains(fields[3]) && DiagnosticCapture.Contains(fields[4])) result.Add(line);
        }
        return result.Skip(Math.Max(0, result.Count - 100)).ToList();
    }

    void RecordDiagnosticFailure(string title, string detail)
    {
        if (smokeMode) return;
        string category = DiagnosticEvents.Contains(title) ? title : "Other error";
        string encoder = recorder == null ? (settings.Video != null && settings.Video.Experienced ? settings.Video.Encoder : "auto")
            : recorder.DiagnosticEncoder;
        if (!DiagnosticEncoders.Contains(encoder)) encoder = "unknown";
        string capture = recorder == null ? "not running" : recorder.DiagnosticCaptureMethod;
        string line = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "|" +
            category + "|" + ClassifyError(detail) + "|" + encoder + "|" + capture;
        try
        {
            var entries = ReadDiagnosticEvents();
            if (entries.Count == 100) entries.RemoveAt(0);
            entries.Add(line);
            File.WriteAllLines(ErrorLogPath, entries);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    string DiagnosticReport()
    {
        var cfg = settings.Video ?? LegacyVideo(settings);
        string encoder = cfg.Experienced && DiagnosticEncoders.Contains(cfg.Encoder) ? cfg.Encoder : "auto";
        string quality = cfg.Experienced ? "Experienced" :
            new[] { "Auto", "Smaller files", "Balanced", "High quality" }[Math.Max(0, Math.Min(3, cfg.SimplePreset))];
        var header = new List<string>
        {
            "clippings diagnostic report",
            "Build: " + BuildNumber,
            "Mode: " + (PortableMode ? "Portable" : "Installed"),
            "Windows version: " + Environment.OSVersion.Version,
            "Capture preset: " + quality,
            "Encoder preference: " + encoder,
            "Replay length: " + GetReplaySeconds(settings) + " seconds",
            "Playback audio: " + (settings.Playback ? "on" : "off"),
            "Microphone: " + (settings.Microphone ? "on" : "off")
        };
        if (cfg.Experienced)
        {
            header.Add("Custom frame rate: " + cfg.Fps + " fps");
            header.Add("Custom resolution: " + (cfg.Width > 0 && cfg.Height > 0 ? cfg.Width + "x" + cfg.Height : "native"));
        }
        header.Add("");
        header.Add("Recent errors (UTC | operation | reason | encoder | capture method)");
        try
        {
            var events = ReadDiagnosticEvents();
            if (events.Count == 0) header.Add("No errors recorded.");
            else header.AddRange(events.Select(line => line.Replace("|", " | ")));
        }
        catch (IOException) { header.Add("Error history could not be read."); }
        catch (UnauthorizedAccessException) { header.Add("Error history could not be read."); }
        return String.Join(Environment.NewLine, header);
    }

    void ShowDiagnosticLog(Form owner)
    {
        using (var dialog = PreferencesWindow("Share error log", new Size(680, 500)))
        {
            string report = DiagnosticReport();
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(15, 12, 15, 8) };
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.Controls.Add(new Label { Text = "Review the report before sharing it.", Dock = DockStyle.Fill, ForeColor = Muted });
            var reportBox = new TextBox { Text = report, Multiline = true, ReadOnly = true,
                WordWrap = false, AccessibleName = "Diagnostic report" };
            var reportView = new ScrollViewport { Dock = DockStyle.Fill };
            int reportWidth = reportBox.Lines.Max(line => TextRenderer.MeasureText(line, reportBox.Font).Width) + 16;
            int reportHeight = reportBox.Font.Height * (reportBox.Lines.Length + 1) + 12;
            reportView.Attach(reportBox, delegate(Size space) { return new Size(reportWidth, reportHeight); });
            body.Controls.Add(reportView);
            dialog.Controls.Add(body);

            Button copy;
            var footer = DialogFooter(dialog, "Copy report", out copy, "Close");
            var save = new Button();
            StyleButton(save, "Save as .txt…", 128);
            footer.Controls.Add(save);
            var status = new Label { AutoSize = true, Padding = new Padding(5, 9, 5, 0), ForeColor = Muted };
            footer.Controls.Add(status);
            copy.Click += delegate
            {
                try { Clipboard.SetText(report); status.Text = "Copied"; }
                catch (ExternalException) { status.Text = "Clipboard unavailable"; }
            };
            save.Click += delegate
            {
                using (var picker = new SaveFileDialog { FileName = "clippings-error-log.txt", Filter = "Text files (*.txt)|*.txt" })
                {
                    if (picker.ShowDialog(dialog) != DialogResult.OK) return;
                    try { File.WriteAllText(picker.FileName, report); status.Text = "Saved"; }
                    catch (IOException) { status.Text = "Could not save"; }
                    catch (UnauthorizedAccessException) { status.Text = "Could not save"; }
                }
            };
            dialog.ShowDialog(owner);
        }
    }
}
