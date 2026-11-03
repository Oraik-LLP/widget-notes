[CmdletBinding(SupportsShouldProcess)]
param([switch]$NoStartup, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$sourceExe = Join-Path $PSScriptRoot 'WidgetNotes.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) { throw 'Keep Install.ps1 beside WidgetNotes.exe.' }
$installFolder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\WidgetNotes'
$installedExe = Join-Path $installFolder 'WidgetNotes.exe'
$uninstallScript = Join-Path $installFolder 'Uninstall.ps1'
$running = Get-Process WidgetNotes -ErrorAction SilentlyContinue
if ($running) { throw 'Exit WidgetNotes using its tray menu before installing or updating. Your notes will be kept.' }
if ($PSCmdlet.ShouldProcess($installFolder, 'Install WidgetNotes for the current Windows user')) {
    New-Item -ItemType Directory -Path $installFolder -Force | Out-Null
    Copy-Item -LiteralPath $sourceExe -Destination $installedExe -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $uninstallScript -Force
    $shell = New-Object -ComObject WScript.Shell
    foreach ($shortcutFile in @((Join-Path ([Environment]::GetFolderPath('Desktop')) 'WidgetNotes.lnk'), (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\WidgetNotes.lnk'))) {
        $shortcut = $shell.CreateShortcut($shortcutFile)
        $shortcut.TargetPath = $installedExe
        $shortcut.WorkingDirectory = $installFolder
        $shortcut.IconLocation = "$installedExe,0"
        $shortcut.Description = 'Quick checklists and a personal notebook'
        $shortcut.Save()
    }
    $runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $runPath -Force | Out-Null
    if ($NoStartup) { Remove-ItemProperty -Path $runPath -Name WidgetNotes -ErrorAction SilentlyContinue }
    else { New-ItemProperty -Path $runPath -Name WidgetNotes -Value ('"' + $installedExe + '" --startup') -PropertyType String -Force | Out-Null }
    $classPath = 'HKCU:\Software\Classes\WidgetNotes.Notebook'
    New-Item -Path "$classPath\shell\open\command" -Force | Out-Null
    Set-Item -Path $classPath -Value 'WidgetNotes portable notebook'
    Set-Item -Path "$classPath\shell\open\command" -Value ('"' + $installedExe + '" "%1"')
    New-Item -Path 'HKCU:\Software\Classes\.wnote\OpenWithProgids' -Force | Out-Null
    New-ItemProperty -Path 'HKCU:\Software\Classes\.wnote\OpenWithProgids' -Name 'WidgetNotes.Notebook' -Value '' -PropertyType String -Force | Out-Null
    # Claim the default only if no other app owns the extension.
    $extension = Get-Item 'HKCU:\Software\Classes\.wnote'
    if (-not $extension.GetValue('')) { Set-Item -Path 'HKCU:\Software\Classes\.wnote' -Value 'WidgetNotes.Notebook' }
    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WidgetNotes'
    New-Item -Path $uninstallKey -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'WidgetNotes' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value '1.2.0' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installFolder -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name UninstallString -Value ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $uninstallScript + '"') -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
    Write-Host "Installed: $installedExe"
    Write-Host 'Your notebooks are stored separately in Documents\WidgetNotes. Startup can be changed in Settings.'
    if (-not $NoLaunch) { Start-Process -FilePath $installedExe -WindowStyle Hidden }
}


