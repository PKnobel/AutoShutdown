<#
  Baut dist\AutoShutdown.exe mit dem in Windows enthaltenen C#-Compiler (.NET Framework 4.8, WPF).
  Es wird weder Visual Studio noch ein .NET SDK benoetigt.

  Aufruf:
    powershell -ExecutionPolicy Bypass -File build.ps1

  Optional signieren (Code-Signing-Zertifikat im Speicher "Eigene Zertifikate"):
    powershell -ExecutionPolicy Bypass -File build.ps1 -CertificateThumbprint <Fingerabdruck>
#>
param(
    [string]$CertificateThumbprint,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
Add-Type -AssemblyName System.Drawing

$src = Join-Path $PSScriptRoot 'src'
$obj = Join-Path $PSScriptRoot 'obj'
$dist = Join-Path $PSScriptRoot 'dist'
$exe = Join-Path $dist 'AutoShutdown.exe'
New-Item -ItemType Directory -Force $obj, $dist | Out-Null

# --- Icon zeichnen (roter Kreis mit weissem Power-Symbol) ---
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(196, 43, 28))), 0, 0, $s - 0.5, $s - 0.5)

    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.6, $s * 0.1))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $c = $s / 2; $r = $s * 0.25
    $g.DrawArc($pen, $c - $r, $c - $r + $s * 0.02, 2 * $r, 2 * $r, 300, 300)
    $g.DrawLine($pen, $c, $c - $r * 1.2, $c, $c - $r * 0.1)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# --- ICO-Datei schreiben (PNG-Eintraege) ---
$ico = Join-Path $obj 'app.ico'
$fs = [System.IO.File]::Create($ico)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $images[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256))   # 256 wird als 0 kodiert
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $w.Write($data) }
$w.Close()

# --- Kompilieren ---
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path "$fw\csc.exe")) { $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }

& "$fw\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 `
    "/win32icon:$ico" "/win32manifest:$src\app.manifest" `
    "/resource:$src\MainWindow.xaml,AutoShutdown.MainWindow.xaml" `
    "/r:$fw\WPF\PresentationFramework.dll" "/r:$fw\WPF\PresentationCore.dll" `
    "/r:$fw\WPF\WindowsBase.dll" "/r:$fw\System.Xaml.dll" `
    "/out:$exe" "$src\AutoShutdown.cs"
if ($LASTEXITCODE -ne 0) { throw "Kompilieren fehlgeschlagen ($LASTEXITCODE)" }

# --- Optional signieren (mit Zeitstempel, damit die Signatur nach Ablauf des Zertifikats gueltig bleibt) ---
if ($CertificateThumbprint) {
    $cert = Get-ChildItem "Cert:\CurrentUser\My\$CertificateThumbprint" -CodeSigningCert
    if (-not $cert) { throw "Code-Signing-Zertifikat $CertificateThumbprint nicht gefunden" }
    $sig = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $TimestampServer
    if ($sig.Status -ne 'Valid') { throw "Signieren fehlgeschlagen: $($sig.StatusMessage)" }
    Write-Host "Signiert von: $($cert.Subject)"
}

# --- Pruefsumme, damit Nutzer die heruntergeladene Datei pruefen koennen ---
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText("$exe.sha256", "$hash  AutoShutdown.exe`n")

Write-Host "Fertig: $exe"
Write-Host "SHA256: $hash"
