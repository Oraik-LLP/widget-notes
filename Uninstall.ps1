[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
$installFolder = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\tagme'))
$expectedParent = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'))
if ([IO.Path]::GetDirectoryName($installFolder) -ne $expectedParent -or [IO.Path]::GetFileName($installFolder) -ne 'tagme') { throw 'Unexpected installation path; removal stopped.' }
if (Get-Process tagme -ErrorAction SilentlyContinue) { throw 'Exit tagme from its tray menu first so your notes are saved.' }
if ($PSCmdlet.ShouldProcess($installFolder, 'Remove tagme program and registration; keep all notebooks and preferences')) {
    $runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Remove-ItemProperty -Path $runPath -Name tagme -ErrorAction SilentlyContinue
    foreach ($shortcutFile in @((Join-Path ([Environment]::GetFolderPath('Desktop')) 'tagme.lnk'), (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\tagme.lnk'))) {
        if (Test-Path -LiteralPath $shortcutFile) { Remove-Item -LiteralPath $shortcutFile -Force }
    }
    $extension = Get-Item 'HKCU:\Software\Classes\.wnote' -ErrorAction SilentlyContinue
    if ($extension -and $extension.GetValue('') -eq 'tagme.Notebook') { $extension.DeleteValue('', $false) }
    Remove-ItemProperty -Path 'HKCU:\Software\Classes\.wnote\OpenWithProgids' -Name 'tagme.Notebook' -ErrorAction SilentlyContinue
    if (Test-Path 'HKCU:\Software\Classes\tagme.Notebook') { Remove-Item -LiteralPath 'HKCU:\Software\Classes\tagme.Notebook' -Recurse -Force }
    if (Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\tagme') { Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\tagme' -Recurse -Force }
    # Exact filenames only: never recursively delete a user-selected directory.
    foreach ($name in @('tagme.exe', 'Uninstall.ps1', 'DotNet-LICENSE.txt', 'DotNet-ThirdPartyNotices.txt')) {
        $target = [IO.Path]::GetFullPath((Join-Path $installFolder $name))
        if ([IO.Path]::GetDirectoryName($target) -ne $installFolder) { throw 'Unexpected target; removal stopped.' }
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
    }
    $noticeFolder = Join-Path $installFolder 'ThirdParty'
    foreach ($notice in @('WPF-UI-LICENSE.md', 'WPF-UI-NOTICES.txt', 'WPF-UI-Abstractions-LICENSE.md')) {
        $target = Join-Path $noticeFolder $notice
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
    }
    if ((Test-Path -LiteralPath $noticeFolder) -and -not (Get-ChildItem -LiteralPath $noticeFolder -Force)) { Remove-Item -LiteralPath $noticeFolder }
    if ((Test-Path -LiteralPath $installFolder) -and -not (Get-ChildItem -LiteralPath $installFolder -Force)) { Remove-Item -LiteralPath $installFolder }
    Write-Host 'tagme removed. All notebooks and preferences were kept.'
}
