[CmdletBinding(SupportsShouldProcess)]
param([switch]$NoStartup, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$sourceExe = Join-Path $PSScriptRoot 'WidgetNotes.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) { throw 'Keep Install.ps1 beside WidgetNotes.exe.' }
$installFolder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\WidgetNotes'
$installedExe = Join-Path $installFolder 'WidgetNotes.exe'
$uninstallScript = Join-Path $installFolder 'Uninstall.ps1'
if ($PSCmdlet.ShouldProcess($installFolder, 'Install WidgetNotes for the current Windows user')) {
    $running = Get-Process WidgetNotes -ErrorAction SilentlyContinue
    if ($running) {
        foreach ($process in $running) {
            if ($process.Path -ne $installedExe -and $process.Path -ne $sourceExe) { throw 'Exit the older portable WidgetNotes copy from its tray menu before updating. Your notes will be kept.' }
        }
        if (-not ('WidgetNotesInstallRestartManager' -as [type])) {
            Add-Type @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class WidgetNotesInstallRestartManager {
    [StructLayout(LayoutKind.Sequential)] struct UniqueProcess { public uint Id; public System.Runtime.InteropServices.ComTypes.FILETIME Started; }
    [DllImport("rstrtmgr.dll", CharSet=CharSet.Unicode)] static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet=CharSet.Unicode)] static extern int RmRegisterResources(uint session, uint files, string[] names, uint applications, UniqueProcess[] processes, uint services, string[] serviceNames);
    [DllImport("rstrtmgr.dll")] static extern int RmShutdown(uint session, uint flags, IntPtr callback);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint session);
    public static int Close(int[] processIds) {
        var targets = new UniqueProcess[processIds.Length];
        for (int i=0;i<processIds.Length;i++) {
            var process=Process.GetProcessById(processIds[i]); long time=process.StartTime.ToFileTimeUtc();
            targets[i]=new UniqueProcess { Id=(uint)process.Id, Started=new System.Runtime.InteropServices.ComTypes.FILETIME { dwLowDateTime=unchecked((int)time), dwHighDateTime=unchecked((int)(time>>32)) } };
        }
        uint session; int result=RmStartSession(out session,0,new StringBuilder(33)); if(result!=0)return result;
        try { result=RmRegisterResources(session,0,null,(uint)targets.Length,targets,0,null); return result==0?RmShutdown(session,0,IntPtr.Zero):result; }
        finally { RmEndSession(session); }
    }
}
'@
        }
        $result = [WidgetNotesInstallRestartManager]::Close([int[]]@($running | ForEach-Object Id))
        if ($result -ne 0 -or (Get-Process WidgetNotes -ErrorAction SilentlyContinue)) { throw "WidgetNotes did not complete its graceful save/shutdown (Windows result $result). It was not forced to exit." }
    }
    New-Item -ItemType Directory -Path $installFolder -Force | Out-Null
    Copy-Item -LiteralPath $sourceExe -Destination $installedExe -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $uninstallScript -Force
    foreach ($notice in @('DotNet-LICENSE.txt', 'DotNet-ThirdPartyNotices.txt', 'ThirdParty\WPF-UI-LICENSE.md', 'ThirdParty\WPF-UI-NOTICES.txt', 'ThirdParty\WPF-UI-Abstractions-LICENSE.md')) {
        $noticeSource = Join-Path $PSScriptRoot $notice
        if (-not (Test-Path -LiteralPath $noticeSource)) { continue }
        $noticeTarget = Join-Path $installFolder $notice
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($noticeTarget)) -Force | Out-Null
        Copy-Item -LiteralPath $noticeSource -Destination $noticeTarget -Force
    }
    $shell = New-Object -ComObject WScript.Shell
    foreach ($shortcutFile in @((Join-Path ([Environment]::GetFolderPath('Desktop')) 'WidgetNotes.lnk'), (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\WidgetNotes.lnk'))) {
        $shortcut = $shell.CreateShortcut($shortcutFile)
        $shortcut.TargetPath = $installedExe
        $shortcut.WorkingDirectory = $installFolder
        $shortcut.IconLocation = "$installedExe,0"
        $shortcut.Description = 'Quick checklists and a personal notebook'
        $shortcut.Save()
    }
    # Repair existing WidgetNotes pins/startup shortcuts that still launch an older portable copy.
    $shortcutFolders = @(
        ([Environment]::GetFolderPath('Desktop')),
        ([Environment]::GetFolderPath('StartMenu')),
        (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'),
        ([Environment]::GetFolderPath('Startup'))
    )
    foreach ($shortcutFolder in $shortcutFolders) {
        if (-not (Test-Path -LiteralPath $shortcutFolder)) { continue }
        foreach ($entry in Get-ChildItem -LiteralPath $shortcutFolder -Filter '*.lnk' -File -Recurse -ErrorAction SilentlyContinue) {
            $shortcut = $shell.CreateShortcut($entry.FullName)
            if ([IO.Path]::GetFileName($shortcut.TargetPath) -ine 'WidgetNotes.exe') { continue }
            $shortcut.TargetPath = $installedExe
            $shortcut.WorkingDirectory = $installFolder
            $shortcut.IconLocation = "$installedExe,0"
            $shortcut.Save()
        }
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
    New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value '1.5.1' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installFolder -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name UninstallString -Value ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $uninstallScript + '"') -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
    Write-Host "Installed: $installedExe"
    Write-Host 'Your notebooks are stored separately in Documents\WidgetNotes. Startup can be changed in Settings.'
    if (-not $NoLaunch) { Start-Process -FilePath $installedExe }
}


