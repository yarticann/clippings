using System;
using System.IO;
using System.Media;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using NAudio.Wave;

[assembly: System.Reflection.AssemblyTitle("clippings")]
[assembly: System.Reflection.AssemblyProduct("clippings")]

sealed partial class ReplayShelf
{
    const string AppName = "clippings";
    const string SoundFilter = "Audio files|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma;*.aiff;*.aif|All files|*.*";

    SoundPlayer clipSound;

    static string ImportClipSound(string source)
    {
        string destination = Path.Combine(Data, "sound-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            RunTool("ffmpeg.exe", "-v error -nostdin -i " + Quote(source) +
                " -map 0:a:0 -vn -t 10 -ac 2 -ar 44100 -c:a pcm_s16le " + Quote(destination), 20000);
            using (var sound = new SoundPlayer(destination)) sound.Load();
            return destination;
        }
        catch
        {
            DeleteStagedSound(destination);
            throw;
        }
    }

    static void VerifySoundImport(string folder)
    {
        foreach (var extension in new[] { "wav", "mp3", "m4a", "aac", "flac" })
        {
            string source = Path.Combine(folder, "tone." + extension);
            RunTool("ffmpeg.exe", "-v error -nostdin -f lavfi -i sine=frequency=440:duration=0.15 " + Quote(source));
            string imported = ImportClipSound(source);
            DeleteStagedSound(imported);
        }
    }

    static void DeleteStagedSound(string path)
    {
        if (String.IsNullOrEmpty(path)) return;
        if (!String.Equals(Path.GetDirectoryName(path), Data, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("sound-", StringComparison.Ordinal) ||
            !Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static SoundPlayer CreateSound(string path, int volume)
    {
        if (volume <= 0) return null;
        if (String.IsNullOrEmpty(path))
        {
            string setting = Registry.GetValue(@"HKEY_CURRENT_USER\AppEvents\Schemes\Apps\.Default\SystemAsterisk\.Current", "", null) as string;
            if (String.IsNullOrEmpty(setting)) return null;
            path = Environment.ExpandEnvironmentVariables(setting);
        }
        if (!File.Exists(path) || new FileInfo(path).Length > 2000000)
            throw new IOException("The selected sound is unavailable.");

        using (var reader = new AudioFileReader(path))
        {
            reader.Volume = volume / 100f;
            var memory = new MemoryStream();
            try
            {
                using (var writer = new WaveFileWriter(memory, new WaveFormat(reader.WaveFormat.SampleRate, 16, reader.WaveFormat.Channels)))
                {
                    var samples = new float[4096];
                    int count;
                    while ((count = reader.Read(samples, 0, samples.Length)) > 0) writer.WriteSamples(samples, 0, count);
                }
                var player = new SoundPlayer(new MemoryStream(memory.ToArray(), false));
                try { player.Load(); }
                catch { ReleaseSound(player); throw; }
                return player;
            }
            finally { memory.Dispose(); }
        }
    }

    static void ReleaseSound(SoundPlayer player)
    {
        if (player == null) return;
        if (player.Stream != null) player.Stream.Dispose();
        player.Dispose();
    }

    void LoadClipSound()
    {
        ReleaseSound(clipSound);
        clipSound = null;
        if (!settings.SaveSound || settings.ClipSoundVolume == 0) return;
        try { clipSound = CreateSound(settings.SoundPath, settings.ClipSoundVolume); }
        catch (Exception error)
        {
            ReportFailure("Clip sound unavailable", error.Message);
            if (!String.IsNullOrEmpty(settings.SoundPath))
                try { clipSound = CreateSound(null, settings.ClipSoundVolume); }
                catch (Exception fallbackError) { ReportFailure("Default sound unavailable", fallbackError.Message); }
        }
    }

    void PlaySaveFeedback()
    {
        if (smokeMode || clipSound == null) return;
        try { clipSound.Play(); }
        catch (Exception error) { ReportFailure("Clip sound failed", error.Message); }
    }

    void ReportFailure(string title, string detail)
    {
        if (IsDisposed) return;
        string message = String.IsNullOrWhiteSpace(detail) ? title : detail.Trim();
        SetStatus(message);
        RecordDiagnosticFailure(title, detail);
        if (smokeMode || !tray.Visible) return;

        // The shell owns placement, Focus Assist and full-screen suppression.
        string banner = message.Length > 245 ? message.Substring(0, 242) + "…" : message;
        tray.ShowBalloonTip(4000, AppName + " · " + title, banner, ToolTipIcon.Warning);
    }
}
