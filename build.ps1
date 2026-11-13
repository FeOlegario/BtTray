# Compila o BtTray usando o compilador C# que já vem com o Windows (.NET Framework 4.8).
# Uso:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force bin | Out-Null

# Gera o ícone do .exe (glifo Bluetooth branco sobre quadrado azul arredondado), PNG dentro de ICO.
if (-not (Test-Path app.ico)) {
    Add-Type -AssemblyName System.Drawing
    $font = 'Segoe Fluent Icons'
    $test = New-Object System.Drawing.Font($font, 10)
    if ($test.Name -ne $font) { $font = 'Segoe MDL2 Assets' }
    $sizes = 16, 24, 32, 48, 256
    $pngs = foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
        $r = $s * 0.22; $d = $r * 2
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $p.AddArc(0, 0, $d, $d, 180, 90); $p.AddArc($s - $d - 1, 0, $d, $d, 270, 90)
        $p.AddArc($s - $d - 1, $s - $d - 1, $d, $d, 0, 90); $p.AddArc(0, $s - $d - 1, $d, $d, 90, 90); $p.CloseFigure()
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 103, 192))), $p)
        $f = New-Object System.Drawing.Font($font, [single]($s * 0.62), [System.Drawing.GraphicsUnit]::Pixel)
        $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
        $g.DrawString([string][char]0xE702, $f, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ($s * 0.03), $s, $s), $sf)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        , $ms.ToArray()
    }
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $pngs[$i].Length
        $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
        $offset += $len
    }
    foreach ($png in $pngs) { $w.Write($png) }
    [System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'app.ico'), $out.ToArray())
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /optimize+ /platform:anycpu /out:bin\BtTray.exe `
    /win32icon:app.ico /win32manifest:app.manifest `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    src\Bluetooth.cs src\Theme.cs src\PopupForm.cs src\Program.cs
if ($LASTEXITCODE -ne 0) { throw "Falha na compilação" }
Get-Item bin\BtTray.exe | Select-Object Name, @{n='KB'; e={[math]::Round($_.Length / 1KB)}}
