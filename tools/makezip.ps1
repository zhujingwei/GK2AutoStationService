param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination,
    [Parameter(Mandatory = $true)][string]$EntryName
)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (Test-Path -LiteralPath $Destination) {
    Remove-Item -LiteralPath $Destination -Force
}

$fs = [System.IO.File]::Open($Destination, [System.IO.FileMode]::CreateNew)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $archive.CreateEntry($EntryName, [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [System.DateTimeOffset]::new([System.DateTime]::new(2026, 9, 26, 12, 0, 0), [System.TimeSpan]::Zero)
        $stream = $entry.Open()
        try {
            $bytes = [System.IO.File]::ReadAllBytes($Source)
            $stream.Write($bytes, 0, $bytes.Length)
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    $fs.Dispose()
}

$zip = [System.IO.Compression.ZipFile]::OpenRead($Destination)
try {
    $zip.Entries | ForEach-Object { '{0}  ({1} bytes)' -f $_.FullName, $_.Length }
}
finally {
    $zip.Dispose()
}

$hash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
$size = (Get-Item -LiteralPath $Destination).Length
'zip size: ' + $size + ' bytes'
'SHA256:   ' + $hash

$testDir = Join-Path (Split-Path -Parent $Destination) '_ziptest'
if (Test-Path -LiteralPath $testDir) {
    Remove-Item -LiteralPath $testDir -Recurse -Force
}

[System.IO.Compression.ZipFile]::ExtractToDirectory($Destination, $testDir)
$extracted = Get-ChildItem -LiteralPath $testDir -Recurse -File
foreach ($f in $extracted) {
    'extracted: ' + $f.FullName.Substring($testDir.Length) + '  ' + $f.Length + ' bytes'
}
'source SHA256:    ' + (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
'extracted SHA256: ' + (Get-FileHash -LiteralPath (Join-Path $testDir 'BepInEx\plugins\GK2AutoStationService.dll') -Algorithm SHA256).Hash
Remove-Item -LiteralPath $testDir -Recurse -Force
