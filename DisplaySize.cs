using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed partial class ReplayShelf
{
    [StructLayout(LayoutKind.Sequential)]
    struct DisplayLuid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DeviceRequest
    {
        public int Type;
        public uint Size;
        public DisplayLuid Adapter;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplaySourceName
    {
        public DeviceRequest Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string GdiName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayTargetName
    {
        public DeviceRequest Header;
        public uint Flags;
        public int OutputTechnology;
        public ushort Manufacturer, Product;
        public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DevicePath;
    }

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    struct PreferredDisplayMode
    {
        [FieldOffset(0)] public DeviceRequest Header;
        [FieldOffset(20)] public int Width;
        [FieldOffset(24)] public int Height;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    struct DisplayPath
    {
        [FieldOffset(0)] public DisplayLuid SourceAdapter;
        [FieldOffset(8)] public uint SourceId;
        [FieldOffset(20)] public DisplayLuid TargetAdapter;
        [FieldOffset(28)] public uint TargetId;
        [FieldOffset(40)] public int Rotation;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    struct DisplayMode
    {
        [FieldOffset(0)] public uint Type;
    }

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] DisplayPath[] pathInfo,
        ref uint modes, [Out] DisplayMode[] modeInfo, IntPtr topology);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref DisplaySourceName name);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref PreferredDisplayMode mode);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref DisplayTargetName name);

    static DisplayPath[] ActiveDisplayPaths()
    {
        const uint activePaths = 2;
        for (int retry = 0; retry < 3; retry++)
        {
            uint paths, modes;
            if (GetDisplayConfigBufferSizes(activePaths, out paths, out modes) != 0) break;
            var targets = new DisplayPath[paths];
            var sourceModes = new DisplayMode[modes];
            int result = QueryDisplayConfig(activePaths, ref paths, targets, ref modes, sourceModes, IntPtr.Zero);
            if (result == 0) return targets.Take((int)paths).ToArray();
            if (result != 122) break;
        }
        return new DisplayPath[0];
    }

    static string SourceName(DisplayPath path)
    {
        var source = new DisplaySourceName
        {
            Header = new DeviceRequest
            {
                Type = 1, Size = (uint)Marshal.SizeOf(typeof(DisplaySourceName)), Adapter = path.SourceAdapter, Id = path.SourceId
            }
        };
        return DisplayConfigGetDeviceInfo(ref source) == 0 ? source.GdiName : null;
    }

    static object[] MonitorChoices(Screen[] screens)
    {
        var paths = ActiveDisplayPaths();
        var names = screens.Select(screen =>
        {
            foreach (var path in paths)
            {
                if (!String.Equals(SourceName(path), screen.DeviceName, StringComparison.OrdinalIgnoreCase)) continue;
                var target = new DisplayTargetName
                {
                    Header = new DeviceRequest
                    {
                        Type = 2, Size = (uint)Marshal.SizeOf(typeof(DisplayTargetName)), Adapter = path.TargetAdapter, Id = path.TargetId
                    }
                };
                if (DisplayConfigGetDeviceInfo(ref target) == 0 && !String.IsNullOrWhiteSpace(target.FriendlyName))
                    return target.FriendlyName.Trim();
            }
            return null;
        }).ToArray();
        return screens.Select((screen, i) =>
        {
            const string prefix = @"\\.\DISPLAY";
            string number = screen.DeviceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? screen.DeviceName.Substring(prefix.Length) : (i + 1).ToString();
            if (String.IsNullOrEmpty(names[i])) return (object)("Display " + number);
            return names.Count(name => String.Equals(name, names[i], StringComparison.OrdinalIgnoreCase)) > 1
                ? (object)(names[i] + " · Display " + number) : names[i];
        }).ToArray();
    }

    static Size CaptureDisplaySize(Screen screen)
    {
        Size current = screen.Bounds.Size;
        if ((double)current.Width / current.Height >= 1.5) return current;

        // Keep a widescreen canvas when a fullscreen game changes the desktop mode.
        foreach (var path in ActiveDisplayPaths())
        {
            if (!String.Equals(SourceName(path), screen.DeviceName, StringComparison.OrdinalIgnoreCase)) continue;
            var mode = new PreferredDisplayMode
            {
                Header = new DeviceRequest
                {
                    Type = 3, Size = (uint)Marshal.SizeOf(typeof(PreferredDisplayMode)), Adapter = path.TargetAdapter, Id = path.TargetId
                }
            };
            if (DisplayConfigGetDeviceInfo(ref mode) != 0 || mode.Height <= 0 || mode.Width <= 0) continue;
            var preferred = path.Rotation == 2 || path.Rotation == 4 ? new Size(mode.Height, mode.Width) : new Size(mode.Width, mode.Height);
            if ((double)preferred.Width / preferred.Height >= 1.6) return preferred;
        }
        return current;
    }
}
