using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

sealed partial class ReplayShelf
{
    readonly ClipStorageBar storageBar = new ClipStorageBar();
    DateTime storageRefreshUtc;
    string storageFolder;
    bool storageReading;

    async void RefreshClipStorage()
    {
        if (smokeMode || exiting || storageReading || !Visible || WindowState == FormWindowState.Minimized) return;
        string folder = Clips;
        if (folder == storageFolder && DateTime.UtcNow < storageRefreshUtc) return;
        storageReading = true;
        storageFolder = folder;
        storageRefreshUtc = DateTime.UtcNow.AddSeconds(15);
        try
        {
            var usage = await Task.Run(() => ClipStorageBar.Read(folder));
            if (!IsDisposed && folder == Clips)
            {
                storageBar.UpdateUsage(usage);
                storageDetails.SetToolTip(storageBar, usage == null ? null :
                    "Clips: " + ExactStorageSize(usage.Clips) + Environment.NewLine +
                    "Other: " + ExactStorageSize(usage.Other) + Environment.NewLine +
                    "Free: " + ExactStorageSize(usage.Free));
            }
        }
        finally { storageReading = false; }
    }

    static readonly string[] StorageUnits = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
    readonly ToolTip storageDetails = new ToolTip();

    static string FormatStorageSize(ulong bytes)
    {
        decimal value = bytes;
        int unit = 0;
        while (unit < StorageUnits.Length - 1 && value >= 1000)
        {
            value /= 1000;
            unit++;
        }
        value = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        if (value >= 1000 && unit < StorageUnits.Length - 1)
        {
            unit++;
            value /= 1000;
        }
        return value.ToString("0.#") + " " + StorageUnits[unit];
    }

    static string ExactStorageSize(ulong bytes) { return bytes.ToString("N0") + " bytes"; }

    sealed class ClipStorageBar : Control
    {
        static readonly Color ClipsColor = Color.FromArgb(157, 85, 245);
        static readonly Color OtherColor = Color.FromArgb(105, 84, 134);
        static readonly Color FreeColor = Color.FromArgb(57, 43, 76);
        Usage usage;
        public ClipStorageBar()
        {
            DoubleBuffered = true;
            AccessibleName = "Clip drive storage";
        }

        public sealed class Usage
        {
            public string Drive;
            public ulong Clips, Other, Free, Total;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);

        public static Usage Read(string folder)
        {
            try
            {
                string root = Path.GetPathRoot(folder);
                ulong available, total, free;
                string probe = Directory.Exists(folder) ? folder : root;
                if (String.IsNullOrEmpty(probe) || !GetDiskFreeSpaceEx(probe, out available, out total, out free) || total == 0)
                    return null;

                ulong clips = 0;
                if (Directory.Exists(folder))
                    foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.mp4"))
                    {
                        try { clips += (ulong)file.Length; }
                        catch (FileNotFoundException) { }
                    }
                available = Math.Min(available, total);
                clips = Math.Min(clips, total - available);
                return new Usage { Drive = root.TrimEnd(Path.DirectorySeparatorChar), Clips = clips,
                    Other = total - available - clips, Free = available, Total = total };
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
        }

        public void UpdateUsage(Usage value)
        {
            usage = value;
            AccessibleDescription = value == null ? "Clip drive storage unavailable" :
                value.Drive + " · Clips " + FormatStorageSize(value.Clips) + ", Other " + FormatStorageSize(value.Other) + ", Free " + FormatStorageSize(value.Free);
            Invalidate();
        }

        static void TextAt(Graphics graphics, string text, Font font, Rectangle bounds, Color color, TextFormatFlags align)
        {
            TextRenderer.DrawText(graphics, text, font, bounds, color,
                align | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int width = ClientSize.Width;
            if (width <= 0) return;
            var g = e.Graphics;
            var value = usage;
            TextAt(g, value == null ? "Clip drive storage unavailable" : "Clip drive " + value.Drive,
                Font, new Rectangle(0, 2, width, 21), Ink, TextFormatFlags.Right);
            if (value == null) return;

            int clipEnd = (int)Math.Round(width * (double)value.Clips / value.Total);
            int usedEnd = (int)Math.Round(width * (double)(value.Clips + value.Other) / value.Total);
            DrawSegment(g, ClipsColor, 0, clipEnd);
            DrawSegment(g, OtherColor, clipEnd, usedEnd - clipEnd);
            DrawSegment(g, FreeColor, usedEnd, width - usedEnd);

            Legend(g, "Clips", value.Clips, ClipsColor, 0, width / 3);
            Legend(g, "Other", value.Other, OtherColor, width / 3, width / 3);
            Legend(g, "Free", value.Free, FreeColor, 2 * (width / 3), width - 2 * (width / 3));
        }

        static void DrawSegment(Graphics graphics, Color color, int x, int width)
        {
            if (width <= 0) return;
            using (var brush = new SolidBrush(color)) graphics.FillRectangle(brush, x, 25, width, 8);
        }

        void Legend(Graphics graphics, string label, ulong bytes, Color color, int left, int width)
        {
            using (var brush = new SolidBrush(color)) graphics.FillRectangle(brush, left, 45, 7, 7);
            TextAt(graphics, label + " " + FormatStorageSize(bytes), Font, new Rectangle(left + 11, 39, Math.Max(0, width - 13), 21),
                Muted, TextFormatFlags.Left);
        }
    }
}
