# WidgetNotes

WidgetNotes 1.5 is a compact Windows desktop widget for checklists, notes, images, and saved AI output. It has no separate application workspace, canvas, or taskbar button. Use its tray icon or configured shortcut to show or hide it.

## Install

Extract `WidgetNotes-Windows-x64.zip` and double-click `Install.cmd`. Setup installs for the current Windows user, creates Desktop and Start menu shortcuts, registers `.wnote` files and an uninstaller, enables launch at sign-in, and opens the widget. No separately installed .NET runtime is needed.

Existing WidgetNotes shortcuts on the Desktop, Start menu, taskbar, and Startup folder are repaired if they target an older executable. The installer keeps notebook data and preferences. Use Installed apps or `Uninstall.ps1` to remove the program.

## Move, resize, and glass

Drag the title area to move the widget and drag its edges to resize it. During either gesture, the live glass is suspended and replaced by a soft solid surface. The live desktop material returns when the gesture finishes. No desktop screenshots are used by the renderer.

The **Lock** button prevents accidental movement and resizing. The widget stays compact and cannot be maximized. Closing it hides it to the tray; **menu → Exit** saves and exits.

The glass integration uses the MIT-licensed [WPF UI FluentWindow](https://github.com/lepoco/wpfui) with the Windows desktop Acrylic backdrop. WPF UI licenses and notices are included in the installer and installed program folder. Windows can simplify transparency according to system performance and accessibility settings.

## Notes and pages

- Add lists, notes, images, and attachments from the widget toolbar. Cards grow with their contents; the widget has a single vertical scrolling area.
- Navigate pages with the footer arrows. Use **menu → Find page…** or Ctrl+F to search notes in a compact dialog.
- Open **menu → Notebooks…** to switch or create notebooks.
- Use **Import Markdown** or drop `.md`, `.markdown`, or `.mdown` files. Saved/exported output from GPT, Claude, Gemini, Antigravity, Codex, and other agents becomes editable pages. Markdown checkboxes become tasks; fenced examples remain text. Import reads selected local files.
- Export portable `.wnote` notebooks or text, print, and save as PDF from the notebook menu.
- **Settings** controls theme, accent, launch at sign-in, shortcut, confirmations, and notebook location. Save and Cancel stay visible while scrolling.

Notebooks default to `Documents\WidgetNotes`. Preferences are in `%LOCALAPPDATA%\WidgetNotes\settings.json`; `settings.runtime.json` records the running version and renderer for troubleshooting. Program updates retain notebook data.

Keyboard shortcuts: Ctrl+N new page, Ctrl+F find a page, Ctrl+S save, Ctrl+Z undo, Ctrl+PageUp/PageDown navigate. The default global show/hide shortcut is Ctrl+Alt+N. If Windows reports that it is already assigned, startup uses Ctrl+Alt+Shift+N and saves that choice in Settings.

## Build and diagnostics

Requires Windows x64 and the .NET 10 SDK. Run `./build.ps1`; a local SDK in `.tools/dotnet-sdk` is also recognized. The script regenerates the multi-size icon and publishes a self-contained EXE and ZIP under `dist`.

Use `--test-root=...` with `--self-test`, `--ui-test`, `--application-test`, or `--benchmark` to run isolated diagnostics. UI checks exercise task controls, Markdown import, automatic card height, page search, menus, settings, and dropdowns. Widget checks cover taskbar suppression, absence of application/canvas controls, gesture pause/resume, and position locking. Run GUI diagnostics with PowerShell `Start-Process -Wait -PassThru` and inspect their text reports.

The GitHub Actions workflow builds Windows x64, runs diagnostics, uploads the ZIP, and publishes a release when a `v*` tag is pushed. No Git remote is configured in this workspace, so local tags have not published a hosted release.

The application is unsigned; Windows may display an unknown-publisher prompt.
