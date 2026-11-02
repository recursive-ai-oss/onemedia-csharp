# Reads the dates Windows shows for each file in a folder, and screenshots Photos with its information pane open.
#
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\read-dates.ps1 -Folder C:\datecheck\files [-Screenshots] [-Out C:\datecheck\results]
#
# Run it in an interactive desktop session (a scheduled task with /IT works) with the in-box Windows PowerShell 5.1. It reads, per file, the Shell
# properties Explorer's columns are built from (System.Media.DateEncoded, System.ItemDate, System.Photo.DateTaken, System.Media.DateReleased,
# System.DateCreated, System.DateModified) as Explorer would display them in the local time zone, the same values as UTC, and every Explorer
# "Details" column whose name contains "date", "created", "taken", "encoded" or "recorded". Nothing is installed and only $Out is written.
param(
    [Parameter(Mandatory = $true)][string]$Folder,
    [string]$Out = (Join-Path (Split-Path $Folder -Parent) 'results'),
    [switch]$Screenshots,
    [string]$Only = ''
)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$props = 'System.Media.DateEncoded', 'System.ItemDate', 'System.Photo.DateTaken', 'System.Media.DateReleased', 'System.DateCreated', 'System.DateModified', 'System.Title'
$sh = New-Object -ComObject Shell.Application
$ns = $sh.Namespace((Resolve-Path $Folder).Path)

Add-Type -AssemblyName System.Windows.Forms, System.Drawing, System.Runtime.WindowsRuntime
function Save-Screen([string]$path) {
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

$asTask = $null
function Await($op, [type]$type) {
    if (-not $script:asTask) {
        $script:asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
            Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
    }
    $t = $script:asTask.MakeGenericMethod($type).Invoke($null, @($op))
    [void]$t.Wait(30000)
    return $t.Result
}

function Show-InPhotos([string]$full, [string]$shotBase) {
    $r = [ordered]@{ launched = $false }
    try {
        [void][Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
        [void][Windows.System.Launcher, Windows.System, ContentType = WindowsRuntime]
        [void][Windows.System.LauncherOptions, Windows.System, ContentType = WindowsRuntime]
        $sf = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($full)) ([Windows.Storage.StorageFile])
        $opt = New-Object Windows.System.LauncherOptions
        $opt.TargetApplicationPackageFamilyName = 'Microsoft.Windows.Photos_8wekyb3d8bbwe'
        $r.launched = [bool](Await ([Windows.System.Launcher]::LaunchFileAsync($sf, $opt)) ([bool]))
    } catch { $r.launchError = "$_" }
    Start-Sleep -Seconds 10
    try { Save-Screen "$shotBase.photos.png" } catch { $r.screenshotError = "$_" }
    # Open the information pane by its automation name (the viewer's Info button); fall back to Ctrl+I.
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $win = $null
        foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($w.Current.Name -like "$([IO.Path]::GetFileName($full))*") { $win = $w; break }
        }
        $done = $false
        if ($win) {
            foreach ($n in 'Info', 'File info', 'Information') {
                $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $n
                $btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
                if ($btn) {
                    $r.infoButton = $n
                    $p = $null
                    if ($btn.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); $done = $true; $r.infoVia = 'invoke' }
                    elseif ($btn.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Toggle(); $done = $true; $r.infoVia = 'toggle' }
                    else {
                        # No pattern: click the button's centre with the mouse.
                        $pt = $btn.GetClickablePoint()
                        Add-Type -Namespace Win32 -Name Mouse -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, int e);'
                        [Win32.Mouse]::SetCursorPos([int]$pt.X, [int]$pt.Y) | Out-Null
                        [Win32.Mouse]::mouse_event(2, 0, 0, 0, 0); [Win32.Mouse]::mouse_event(4, 0, 0, 0, 0)
                        $done = $true; $r.infoVia = 'click'
                    }
                    break
                }
            }
        }
        if (-not $done) { [System.Windows.Forms.SendKeys]::SendWait('^i'); $r.infoButton = 'keys' }
        Start-Sleep -Seconds 3
        Save-Screen "$shotBase.photos-info.png"
    } catch { $r.infoError = "$_" }
    # Close every Photos process so the next window opens in the same place.
    $r.processes = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'Photos' } | ForEach-Object { $_.ProcessName } | Sort-Object -Unique)
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'Photos' } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    return $r
}

$photosPackage = Get-AppxPackage -Name 'Microsoft.Windows.Photos' -ErrorAction SilentlyContinue | Select-Object -First 1
$tz = Get-TimeZone
$rows = @()
foreach ($f in (Get-ChildItem -File $Folder | Sort-Object Name)) {
    if ($Only -and $f.Name -notlike "*$Only*") { continue }
    $item = $ns.ParseName($f.Name)
    $v = [ordered]@{ file = $f.Name; properties = [ordered]@{}; columns = [ordered]@{} }
    foreach ($p in $props) {
        try {
            $x = $item.ExtendedProperty($p)
            if ($null -eq $x) { $v.properties[$p] = $null }
            elseif ($x -is [datetime]) { $v.properties[$p] = [ordered]@{ local = $x.ToString('yyyy-MM-dd HH:mm:ss'); utc = $x.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') } }
            else { $v.properties[$p] = "$x" }
        } catch { $v.properties[$p] = "error: $_" }
    }
    for ($i = 0; $i -lt 320; $i++) {
        $name = $ns.GetDetailsOf($null, $i)
        if (-not $name) { continue }
        if ($name -match 'date|created|taken|encoded|recorded|release') {
            $val = $ns.GetDetailsOf($item, $i)
            if ($val) { $v.columns[$name] = ($val -replace '[\u200e\u200f]', '') }
        }
    }
    if ($Screenshots) { $v.photos = Show-InPhotos $f.FullName (Join-Path $Out $f.Name) }
    $rows += [pscustomobject]$v
    Write-Output "done $($f.Name)"
}
[pscustomobject]@{ photosPackage = $(if ($photosPackage) { $photosPackage.Version.ToString() } else { $null }); timeZone = $tz.Id; utcOffset = $tz.BaseUtcOffset.ToString(); os = (Get-CimInstance Win32_OperatingSystem).Caption + ' ' + (Get-CimInstance Win32_OperatingSystem).Version; files = $rows } |
    ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $Out 'dates.json') -Encoding UTF8
Write-Output 'finished'
