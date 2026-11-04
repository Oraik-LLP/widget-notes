# WidgetNotes

WidgetNotes 1.4 is a Windows notebook application with an optional compact view. It uses the Windows compositor for live translucent glass, rounded window chrome, and matching menus, settings, dropdowns, checkboxes, and dialogs.

## Install and launch

Extract `WidgetNotes-Windows-x64.zip`, then double-click `Install.cmd`. Setup installs for the current Windows user, adds Desktop and Start menu shortcuts, registers the uninstaller and `.wnote` files, enables launch at sign-in, and opens the application. No administrator account or separately installed .NET runtime is required.

The app opens in the full workspace by default. Use **Compact** in the title bar for a smaller window and **Workspace** to return. Both views are ordinary Windows application windows with taskbar presence. Drag the title bar to move, resize from the edges, or use minimize/maximize/close. Closing saves and exits; the tray menu can hide or reveal the window while it is running.

The previous screenshot-based glass renderer and Explorer desktop attachment have been removed. Backgrounds are composed by Windows without copying desktop icons or hiding the app to refresh. During interactive dragging and resizing the material switches to a lighter compositor blur, then restores acrylic when the gesture ends. Windows may simplify transparency under system performance or accessibility conditions.

## Notebook features

- Create checklist and note pages, edit tasks in place, paste multiline lists, search pages, undo changes, and rearrange or resize blocks in the workspace.
- Add images and file attachments. Files are copied into the notebook; attachments open in their default Windows app.
- Use **Import Markdown** or **menu → Import AI / Markdown…**, or drop `.md`, `.markdown`, or `.mdown` files. Saved or exported output from GPT, Claude, Gemini, Antigravity, Codex, and other agents becomes editable pages. Markdown checkboxes become task items; fenced examples remain text. Import reads the local files selected by the user.
- Export a portable `.wnote` notebook or plain text, print, and save as PDF.
- Open **Settings** from the sidebar or notebook menu for theme, accent, startup, shortcut, confirmation, and storage preferences. Save and Cancel stay visible while scrolling.

Notebooks are stored in `Documents\WidgetNotes` by default. Preferences are stored in `%LOCALAPPDATA%\WidgetNotes\settings.json`. The installer and uninstaller keep notebook data. The adjacent `settings.runtime.json` records the running version, renderer, and application mode for troubleshooting.

Keyboard shortcuts: Ctrl+N new page, Ctrl+F search, Ctrl+S save, Ctrl+Z undo, Ctrl+PageUp/PageDown navigate. The configurable global show/hide shortcut defaults to Ctrl+Alt+N.

## Build and diagnostics

Requires Windows x64 and the .NET 10 SDK. Run `./build.ps1`; the script also recognizes a local SDK in `.tools/dotnet-sdk`. It regenerates the multi-size app icon and publishes a self-contained EXE and ZIP under `dist`. The runtime uses .NET and Windows APIs, without third-party application packages.

The app supports isolated diagnostics using `--test-root=...` with `--self-test`, `--ui-test`, `--application-test`, or `--benchmark`. UI diagnostics render both themes, the actual notebook menu, settings dialog, and dropdown. Application diagnostics verify top-level windows and compositor behavior during move/resize. Use PowerShell `Start-Process -Wait -PassThru` when running the GUI executable from scripts, and inspect the resulting diagnostic report.

`--backdrop-test --test-root=...` is an optional display-level diagnostic that compares a test surface behind the window. It refuses to measure another application's pixels when the test window is covered.

## GitHub

`.github/workflows/windows-release.yml` builds Windows x64, runs diagnostics, and uploads the installer ZIP. Pushing a `v*` tag publishes a GitHub Release. This workspace has no Git remote configured; local tags do not publish a hosted release.

The application is unsigned. Windows may display an unknown-publisher prompt until a signing certificate is provided.
