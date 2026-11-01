# Media Foundation oracle run for OneMedia's ASF and MP4 readers.
#
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\run-mf-check.ps1 [-Screenshots] [-Only <substring>]
#
# Needs Windows PowerShell 5.1 (the in-box one, powershell.exe) on Windows 10/11. It compiles MfProbe.cs once with the
# in-box .NET Framework C# compiler, then for each manifest row runs the checks the row names, each in its own process
# with a timeout, so one broken file never stops the run. Everything lands under .\results\.
#
# Checks (manifest column 4, comma separated):
#   mf          Media Foundation probe: source resolver, presentation descriptor, ASF profile, source reader, all samples
#   shell       Shell media properties (System.* property keys and the Explorer "Details" columns)
#   thumb       Explorer's thumbnail for the file (IShellItemImageFactory, thumbnail only, no generic-icon fallback)
#   screenshot  with -Screenshots: Explorer window with the file selected, and the file opened in Photos
#
# Only public, documented Windows APIs are used. Nothing here reads or disassembles any Windows binary.

param(
    [string]$Manifest = (Join-Path $PSScriptRoot 'manifest.tsv'),
    [string]$Results = (Join-Path $PSScriptRoot 'results'),
    [int]$FirstSamples = 20,
    [int]$ProbeTimeoutSec = 90,
    [int]$ThumbTimeoutSec = 30,
    [int]$TotalBudgetMin = 25,
    [switch]$Screenshots,
    [string]$Only = ''
)

$ErrorActionPreference = 'Continue'
$started = Get-Date
$deadline = $started.AddMinutes($TotalBudgetMin)

New-Item -ItemType Directory -Force -Path $Results | Out-Null
foreach ($d in 'mf', 'shell', 'thumbs', 'screenshots') { New-Item -ItemType Directory -Force -Path (Join-Path $Results $d) | Out-Null }
$log = Join-Path $Results 'run.log'
function Log([string]$m) {
    $line = '{0:HH:mm:ss} {1}' -f (Get-Date), $m
    Write-Host $line
    Add-Content -Path $log -Value $line
}

# ---------------------------------------------------------------- environment record
function Get-Env {
    $os = Get-CimInstance Win32_OperatingSystem
    $e = [ordered]@{
        os = $os.Caption; osVersion = $os.Version; osBuild = $os.BuildNumber; architecture = $env:PROCESSOR_ARCHITECTURE
        powershell = $PSVersionTable.PSVersion.ToString(); started = $started.ToString('o')
        interactiveSession = [Environment]::UserInteractive; screenshots = [bool]$Screenshots
    }
    try { $e.displayVersion = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').DisplayVersion } catch { }
    $dlls = [ordered]@{}
    foreach ($n in 'mfplat.dll', 'mf.dll', 'mfreadwrite.dll', 'mfmp4srcsnk.dll', 'mfasfsrcsnk.dll', 'wmvcore.dll', 'windows.media.dll', 'mfsrcsnk.dll', 'shell32.dll') {
        $p = Join-Path $env:WINDIR "System32\$n"
        if (Test-Path $p) { $dlls[$n] = (Get-Item $p).VersionInfo.FileVersion } else { $dlls[$n] = $null }
    }
    $e.mediaDlls = $dlls
    $photos = Get-AppxPackage -Name 'Microsoft.Windows.Photos' -ErrorAction SilentlyContinue | Select-Object -First 1
    $e.photosPackage = if ($photos) { $photos.Version.ToString() } else { $null }
    return $e
}
try { Get-Env | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $Results 'env.json') -Encoding UTF8 } catch { Log "env record failed: $_" }

# ---------------------------------------------------------------- build the probe
$exe = Join-Path $Results 'mfprobe.exe'
$src = Join-Path $PSScriptRoot 'MfProbe.cs'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $exe)) {
    if (Test-Path $csc) {
        Log 'compiling MfProbe.cs with the in-box csc.exe'
        & $csc /nologo /target:exe /platform:anycpu "/out:$exe" /r:System.Drawing.dll $src 2>&1 | Tee-Object -FilePath (Join-Path $Results 'csc.log') | Out-Null
    }
    if (-not (Test-Path $exe)) {
        Log 'csc.exe route failed; trying Add-Type'
        try {
            Add-Type -TypeDefinition (Get-Content -Raw $src) -OutputAssembly $exe -OutputType ConsoleApplication -ReferencedAssemblies System.Drawing
        } catch { Log "Add-Type failed: $_" }
    }
}
$haveProbe = Test-Path $exe
if (-not $haveProbe) { Log 'FATAL: could not build mfprobe.exe; only shell properties will be collected' }

function Invoke-Timed([string[]]$argv, [int]$timeoutSec, [string]$errFile) {
    # Quote each argument for the Win32 command line.
    $line = ($argv | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $p = Start-Process -FilePath $exe -ArgumentList $line -NoNewWindow -PassThru -RedirectStandardError $errFile
    $null = $p.Handle
    if (-not $p.WaitForExit($timeoutSec * 1000)) {
        try { $p.Kill() } catch { }
        return 'timeout'
    }
    return "exit $($p.ExitCode)"
}

# ---------------------------------------------------------------- shell properties
$shellKeys = @(
    'System.Title', 'System.Author', 'System.Comment', 'System.Copyright', 'System.Keywords', 'System.Rating', 'System.Subject',
    'System.Media.Duration', 'System.Media.SubTitle', 'System.Media.Year', 'System.Media.DateEncoded', 'System.Media.CreatorApplication',
    'System.Media.ProtectionType', 'System.Media.ContentDistributor', 'System.Media.Publisher',
    'System.Music.Artist', 'System.Music.AlbumArtist', 'System.Music.AlbumTitle', 'System.Music.Genre', 'System.Music.TrackNumber',
    'System.Music.Composer', 'System.Music.Lyrics',
    'System.Video.FrameWidth', 'System.Video.FrameHeight', 'System.Video.FrameRate', 'System.Video.EncodingBitrate', 'System.Video.TotalBitrate',
    'System.Video.FourCC', 'System.Video.Compression', 'System.Video.StreamName', 'System.Video.Director', 'System.Video.Orientation',
    'System.Audio.Format', 'System.Audio.EncodingBitrate', 'System.Audio.SampleRate', 'System.Audio.ChannelCount', 'System.Audio.SampleSize',
    'System.Audio.StreamNumber', 'System.Audio.IsVariableBitRate', 'System.Audio.Compression',
    'System.DRM.IsProtected', 'System.DRM.DatePlayExpires', 'System.DRM.DescriptionType',
    'System.Kind', 'System.ItemTypeText', 'System.PerceivedType', 'System.ContentType', 'System.Size', 'System.ThumbnailCacheId'
)
function Get-ShellProps([string]$full) {
    $o = [ordered]@{ file = (Split-Path $full -Leaf) }
    try {
        $shell = New-Object -ComObject Shell.Application
        $ns = $shell.NameSpace((Split-Path $full -Parent))
        $item = $ns.ParseName((Split-Path $full -Leaf))
        $props = [ordered]@{}
        foreach ($k in $shellKeys) {
            try {
                $v = $item.ExtendedProperty($k)
                if ($null -ne $v -and "$v" -ne '') { $props[$k] = "$v" }
            } catch { }
        }
        $o.properties = $props
        # What Explorer's Details view labels the columns; labels are localised, so the canonical keys above are the primary record.
        $det = [ordered]@{}
        for ($i = 0; $i -lt 330; $i++) {
            try {
                $label = $ns.GetDetailsOf($null, $i)
                if (-not $label) { continue }
                $val = $ns.GetDetailsOf($item, $i)
                if ($val) { $det["$i $label"] = $val }
            } catch { }
        }
        $o.details = $det
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    } catch { $o.error = "$_" }
    return $o
}

# ---------------------------------------------------------------- screenshots (best effort)
$haveGui = $false
if ($Screenshots) {
    try {
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $haveGui = $true
    } catch { Log "screenshots unavailable: $_" }
}
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

function Show-InExplorer([string]$full, [string]$shot) {
    $before = @(Get-Process explorer -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    Start-Process explorer.exe -ArgumentList "/select,`"$full`""
    Start-Sleep -Seconds 5
    Save-Screen $shot
    # Close only the folder window we opened.
    try {
        $dir = (Split-Path $full -Parent)
        $sh = New-Object -ComObject Shell.Application
        foreach ($w in @($sh.Windows())) {
            try { if ($w.Document.Folder.Self.Path -eq $dir) { $w.Quit() } } catch { }
        }
    } catch { }
}

function Show-InPhotos([string]$full, [string]$shot) {
    $r = [ordered]@{ launched = $false }
    $beforeIds = @(Get-Process -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    try {
        [void][Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
        [void][Windows.System.Launcher, Windows.System, ContentType = WindowsRuntime]
        [void][Windows.System.LauncherOptions, Windows.System, ContentType = WindowsRuntime]
        $sf = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($full)) ([Windows.Storage.StorageFile])
        $opt = New-Object Windows.System.LauncherOptions
        $opt.TargetApplicationPackageFamilyName = 'Microsoft.Windows.Photos_8wekyb3d8bbwe'
        $ok = Await ([Windows.System.Launcher]::LaunchFileAsync($sf, $opt)) ([bool])
        $r.launched = [bool]$ok
        $r.method = 'Launcher.LaunchFileAsync with the Photos package family'
    } catch {
        $r.launchError = "$_"
    }
    Start-Sleep -Seconds 9
    try { Save-Screen $shot } catch { $r.screenshotError = "$_" }
    $new = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $beforeIds -notcontains $_.Id })
    $r.newProcesses = @($new | ForEach-Object { $_.ProcessName } | Sort-Object -Unique)
    $r.windowTitles = @($new | Where-Object { $_.MainWindowTitle } | ForEach-Object { $_.MainWindowTitle })
    foreach ($p in $new) {
        if ($p.ProcessName -match 'Photos|PhotosApp|Microsoft\.Media|Video\.UI|WindowsMedia') { try { $p.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 500; if (-not $p.HasExited) { $p.Kill() } } catch { } }
    }
    return $r
}

# ---------------------------------------------------------------- main loop
function Fmt-Type($t) {
    if (-not $t) { return '?' }
    $sub = if ($t.subtype.fourcc) { $t.subtype.fourcc } elseif ($t.subtype.data1) { $t.subtype.data1 } else { $t.subtype.guid }
    if ($t.major -eq 'audio') { return "audio:$sub/$($t.sampleRate)Hz/$($t.channels)ch" }
    if ($t.major -eq 'video') { return "video:$sub/$($t.width)x$($t.height)" }
    return "$($t.major):$sub"
}

$rows = @()
$lines = Get-Content -Path $Manifest | Where-Object { $_ -and -not $_.StartsWith('#') }
$header = $null
foreach ($l in $lines) {
    $f = $l -split "`t"
    if (-not $header) { $header = $f; continue }
    $rows += [pscustomobject]@{ path = $f[0]; sha256 = $f[1]; kind = $f[2]; checks = ($f[3] -split ','); note = $f[4] }
}
if ($Only) { $rows = @($rows | Where-Object { $_.path -like "*$Only*" }) }
Log "manifest: $($rows.Count) files"

$verdicts = @()
foreach ($row in $rows) {
    $full = Join-Path $PSScriptRoot ($row.path -replace '/', '\')
    $name = ($row.path -replace '/', '__')
    $v = [ordered]@{ file = $row.path; kind = $row.kind }
    if ((Get-Date) -gt $deadline) { $v.status = 'skipped (time budget)'; $verdicts += [pscustomobject]$v; Log "SKIP $($row.path): budget"; continue }
    if (-not (Test-Path $full)) { $v.status = 'missing'; $verdicts += [pscustomobject]$v; Log "MISSING $($row.path)"; continue }
    $hash = (Get-FileHash -Algorithm SHA256 -Path $full).Hash.ToLowerInvariant()
    $v.sha256Match = ($hash -eq $row.sha256.ToLowerInvariant())
    Log "== $($row.path) ($($row.checks -join ','))"

    if ($row.checks -contains 'mf' -and $haveProbe) {
        $out = Join-Path $Results "mf\$name.json"
        $res = Invoke-Timed @('probe', $full, $out, "$FirstSamples") $ProbeTimeoutSec (Join-Path $Results "mf\$name.stderr.txt")
        $v.mfProcess = $res
        if (Test-Path $out) {
            try {
                $j = Get-Content -Raw -Path $out | ConvertFrom-Json
                $v.mfOpens = [bool]$j.opened
                $v.mfSourceResolved = [bool]$j.sourceResolved
                $v.streams = @($j.streams | ForEach-Object { Fmt-Type $_.type }) -join '; '
                $v.streamCount = $j.streamCount
                if ($null -ne $j.duration100ns) { $v.durationSec = [math]::Round([double]$j.duration100ns / 1e7, 3) }
                $v.protected = $j.anyStreamProtected
                $v.samples = @($j.samples.streamTotals | ForEach-Object { "s$($_.readerStream)=$($_.samples)" }) -join ' '
                $v.stoppedBy = $j.samples.stoppedBy
                $v.errors = @($j.errors | ForEach-Object { "$($_.where) $($_.hresult)" }) -join ' | '
            } catch { $v.mfParseError = "$_" }
        } else { $v.mfOpens = $false; $v.errors = "no output ($res)" }
    }

    if ($row.checks -contains 'shell') {
        $sp = Get-ShellProps $full
        $sp | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $Results "shell\$name.json") -Encoding UTF8
        $p = $sp.properties
        if ($p) {
            $v.shellTitle = $p['System.Title']; $v.shellAuthor = $p['System.Author']; $v.shellDuration = $p['System.Media.Duration']
            $v.shellDrm = $p['System.DRM.IsProtected']
        }
    }

    if ($row.checks -contains 'thumb' -and $haveProbe) {
        $png = Join-Path $Results "thumbs\$name.png"
        $res = Invoke-Timed @('thumb', $full, $png) $ThumbTimeoutSec (Join-Path $Results "thumbs\$name.stderr.txt")
        $tj = "$png.json"
        if (Test-Path $tj) {
            try { $t = Get-Content -Raw -Path $tj | ConvertFrom-Json; $v.thumbnail = if ($t.thumbnail) { "$($t.width)x$($t.height)" } else { "none ($($t.getImage))" } } catch { $v.thumbnail = 'parse error' }
        } else { $v.thumbnail = "no result ($res)" }
    }

    if ($row.checks -contains 'screenshot' -and $haveGui) {
        try { Show-InExplorer $full (Join-Path $Results "screenshots\$name.explorer.png") } catch { Log "explorer shot failed: $_" }
        try {
            $ph = Show-InPhotos $full (Join-Path $Results "screenshots\$name.photos.png")
            $ph | ConvertTo-Json -Depth 3 | Set-Content -Path (Join-Path $Results "screenshots\$name.photos.json") -Encoding UTF8
            $v.photos = if ($ph.launched) { 'launched' } else { 'not launched' }
            if ($ph.windowTitles) { $v.photos += ': ' + ($ph.windowTitles -join ' / ') }
        } catch { Log "photos shot failed: $_"; $v.photos = "error: $_" }
    }
    $verdicts += [pscustomobject]$v
}

# ---------------------------------------------------------------- verdict table
$cols = 'file', 'kind', 'sha256Match', 'mfProcess', 'mfOpens', 'streamCount', 'streams', 'durationSec', 'protected', 'samples', 'stoppedBy',
        'shellTitle', 'shellAuthor', 'shellDuration', 'shellDrm', 'thumbnail', 'photos', 'status', 'errors'
$tsv = New-Object System.Collections.Generic.List[string]
$tsv.Add(($cols -join "`t"))
foreach ($v in $verdicts) { $tsv.Add((($cols | ForEach-Object { ("" + $v.$_) -replace "[`t`r`n]", ' ' }) -join "`t")) }
Set-Content -Path (Join-Path $Results 'verdicts.tsv') -Value $tsv -Encoding UTF8
$md = New-Object System.Collections.Generic.List[string]
$md.Add('| ' + ($cols -join ' | ') + ' |'); $md.Add('|' + ((1..$cols.Count | ForEach-Object { '---' }) -join '|') + '|')
foreach ($v in $verdicts) { $md.Add('| ' + (($cols | ForEach-Object { (("" + $v.$_) -replace '\|', '/') -replace "[\r\n]", ' ' }) -join ' | ') + ' |') }
Set-Content -Path (Join-Path $Results 'verdicts.md') -Value $md -Encoding UTF8
Log ("done in {0:N1} min" -f ((Get-Date) - $started).TotalMinutes)
