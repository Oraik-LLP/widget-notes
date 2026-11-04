# WidgetNotes

**WidgetNotes** is a local-first Windows desktop notebook with a compact checklist widget and a full workspace. Version 1.3 adds a captured-backdrop pixel shader with lens refraction and chromatic edge highlights, a refreshed glass notebook icon, and Markdown import for AI-agent outputs.

## Install

1. Download `WidgetNotes-Windows-x64.zip` from a GitHub Release and extract it.
2. Double-click `Install.cmd`.
3. WidgetNotes installs for the current Windows user, creates Start menu and Desktop shortcuts, enables launch at sign-in, and opens the widget.

The installer does not need administrator access or a separately installed .NET runtime. In **⋯ → Settings**, you can turn launch at sign-in off, change the notebook location, and adjust theme, accent, motion, confirmation, and shortcut preferences. Run `Uninstall.ps1` or use **Installed apps → WidgetNotes → Uninstall** to remove the program; notebooks and preferences are kept.

To run without installing, launch `WidgetNotes.exe` from the extracted folder. Startup registration is available in Settings; keep the EXE in the same folder if it is enabled. The app is unsigned, so Windows may show an unknown-publisher prompt.

## Use the widget and workspace

- Add checklist and note pages, edit tasks in place, paste multiline task lists, and undo recent document changes.
- Drag the widget title area to position it. Lock its position from the header; expand into the workspace to search pages, zoom, rearrange blocks, and manage notebooks.
- Add notes, checklists, images, and file attachments. Images are copied into the notebook; other files open in their default Windows apps.
- Choose **⋯ → Import AI / Markdown…** or drop `.md`, `.markdown`, or `.mdown` files onto the app. Saved or exported responses from GPT, Claude, Gemini, Antigravity, Codex, and other agents become new pages; Markdown task checkboxes become editable checklist items. This reads local files you select; it does not connect to agent accounts or read their private app data.
- Export a portable `.wnote` notebook or readable text, print, and save as PDF.
- The app works offline. Notebook content stays in your local `Documents\WidgetNotes` folder by default. Preferences are stored in `%LOCALAPPDATA%\WidgetNotes\settings.json`.
- Close hides the app to the tray. Use the tray icon, the configured hotkey, or **Exit** to leave it. If Explorer's desktop host is unavailable, the widget stays available as a taskbar-visible floating window and retries desktop attachment.

## GitHub builds and releases

The workflow at `.github/workflows/windows-release.yml` builds and packages Windows x64 on pushes and pull requests. Pushing a `v*` tag also publishes the ZIP as a GitHub Release. Workflow artifacts contain `WidgetNotes-Windows-x64.zip` for builds that are not tagged releases.

## Build from source

Requirements: Windows x64 and the .NET 10 SDK.

```powershell
./build.ps1
```

The release ZIP and launchable EXE are written under `dist`. The build regenerates the multi-size Windows icon from `Assets/WidgetNotesIcon.png`, then publishes a self-contained single-file WPF app. The project has no third-party runtime packages. Its refraction shader and capture sample are MIT-licensed adaptations of [WPF-Liquid-Glass-Effect](https://github.com/dragosniamtu/WPF-Liquid-Glass-Effect); notices ship with the installer package. The edge refraction and color separation follow the visual direction of [liquid-glass-react](https://github.com/rdev/liquid-glass-react), adapted to WPF.

To run the existing isolated diagnostics against a build:

```powershell
$selfRoot = Join-Path $env:TEMP "WidgetNotes-self-$([guid]::NewGuid())"
$uiRoot = Join-Path $env:TEMP "WidgetNotes-ui-$([guid]::NewGuid())"
.\dist\package\WidgetNotes.exe --self-test "--test-root=$selfRoot"
.\dist\package\WidgetNotes.exe --ui-test "--test-root=$uiRoot"
```

These diagnostics use separate folders and do not install the app or change startup registration.
