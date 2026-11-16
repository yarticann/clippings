# clippings

clippings is a local Windows 11 instant-replay recorder.

1. To install from a release, run the Setup EXE. From a source checkout, run Setup.cmd. Setup uses WinGet to install FFmpeg if needed, downloads NAudio from NuGet, and adds clippings to the Start menu.
2. Open **Settings** to choose a monitor by its Windows display name, replay length in minutes, video settings, audio devices, clip folder, and save shortcut. Take a short clip to confirm the selected display.
3. Select **Start buffer**. Press **Alt+F9** to save by default. In Settings, select **Save hotkey** to record your own key or modifier combination. Windows may reserve some shortcuts. Closing the library sends the app to the system tray. Left-click the tray icon to open clippings. The right-click menu shows **Start buffer** or **Stop buffer** based on capture state.
4. Play clips from the library or select **Open folder**. The default folder is **Replay Shelf** under Windows Videos. A folder change switches the library and destination for new saves.

## Manage clips

Right-click any clip card to rename it, duplicate it, trim it, open its location in File Explorer, or delete it. The delete dialog offers the Recycle Bin where available and a permanent-delete option. Drag the scrubber during playback to seek inside Trim. After choosing a range, **Save trim…** lets you name the result and save a copy or replace the original. Overwrite requires confirmation and exports before replacing the source. Rename preserves the MP4 extension and refuses to overwrite an existing clip.

## Video settings

- **Simple** offers **Auto**, **Smaller files**, **Balanced**, and **High quality**. clippings checks for available NVIDIA NVENC, Intel Quick Sync, AMD AMF, and CPU encoders. Auto prefers hardware H.264, up to 1080p60, with a smaller 720p30 CPU fallback.
- **Experienced** exposes the available encoder and codec, native or custom resolution, frame rate, and bitrate. Custom pixel dimensions must be even. Windows Graphics Capture scales on the GPU. NVIDIA capture passes hardware frames directly to NVENC. Older FFmpeg builds use Desktop Duplication as a fallback.
- **About → Enable IDGAF mode** adds rate control, encoder preset, and keyframe interval to Experienced mode. Constant quality uses the encoder's quality value in place of bitrate.
- The rolling buffer uses disk space. Saving copies completed segments into MP4 without re-encoding. The default one-second segments reduce the wait for a save. Experienced settings can use a longer keyframe interval. When fullscreen capture changes from widescreen to a narrower mode, clippings scales it to fill the recording frame. If capture stalls, clippings restarts the buffer and waits for fresh footage before saving.

Video settings estimates the size per clip from the selected replay length. Bitrate modes show a target size. Constant quality shows a rough range.

## Audio and startup

- Audio defaults to the Windows default playback device, with the mic off. You can set the two devices and levels independently. Enabled sources mix into one stereo AAC track. **Test mic** shows the input level. Turn on **Hear my mic (use headphones)** to hear the selected microphone through your default playback device at the current mic level. Monitoring stops when you stop the test or close Settings.
- Capture, replay length, video, and audio changes take effect the next time you start the buffer. The save shortcut, sound, and destination apply immediately.
- **Open in the tray at Windows sign-in** is opt-in. Capture starts when you select **Start buffer**.

- Use **Choose sound** in Settings to import WAV, MP3, M4A, AAC, FLAC, OGG, Opus, WMA, or AIFF. clippings uses the first ten seconds and keeps a local copy. Preview it at the selected volume, return to the default, or turn off the sound.
- The sound plays when the app accepts the save. Capture, save, and export failures are reported through Windows notifications. Banner display depends on Windows notification and focus settings.

Created by yarticann with ChatGPT. Built with AI assistance.

Use **Settings → Setup guide…** to revisit capture setup or save an optional test clip.

The library footer shows space used by clips in the selected folder, other drive usage, and free space on that drive.

Use **Settings → About → Error log…** to review, copy or export the last 100 error events. The report uses preset categories and does not include raw error messages, clip names, device names, or paths.

App dialogs can be resized. When a window is too small for its controls, dark purple scrollbars keep the rest of the content reachable.

To update a portable copy, exit it and extract the new ZIP over the existing folder. Keep the data and clips folders so settings carry over. The installed updater also preserves settings and skips first-run setup.
