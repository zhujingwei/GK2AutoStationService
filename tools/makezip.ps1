param(
    [Parameter(Mandatory = $true)][string[]]$Source,
    [Parameter(Mandatory = $true)][string[]]$EntryName,
    [Parameter(Mandatory = $true)][string]$Destination
)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if ($Source.Count -ne $EntryName.Count) {
    throw 'Source and EntryName must have the same number of items'
}

if (Test-Path -LiteralPath $Destination) {
    Remove-Item -LiteralPath $Destination -Force
}

$fs = [System.IO.File]::Open($Destination, [System.IO.FileMode]::CreateNew)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        for ($i = 0; $i -lt $Source.Count; $i++) {
            $entry = $archive.CreateEntry($EntryName[$i], [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [System.DateTimeOffset]::new([System.DateTime]::new(2026, 9, 26, 12, 0, 0), [System.TimeSpan]::Zero)
            $stream = $entry.Open()
            try {
                $bytes = [System.IO.File]::ReadAllBytes($Source[$i])
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally {
                $stream.Dispose()
            }
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

$failed = $false
for ($i = 0; $i -lt $Source.Count; $i++) {
    $relative = $EntryName[$i].Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $path = Join-Path $testDir $relative
    if (-not (Test-Path -LiteralPath $path)) {
        'MISSING : ' + $relative
        $failed = $true
        continue
    }

    $sourceHash = (Get-FileHash -LiteralPath $Source[$i] -Algorithm SHA256).Hash
    $extractedHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($sourceHash -eq $extractedHash) {
        'match   : ' + $relative + '  ' + $sourceHash
    }
    else {
        'MISMATCH: ' + $relative
        $failed = $true
    }
}

Remove-Item -LiteralPath $testDir -Recurse -Force
if ($failed) {
    throw 'zip verification failed'
}
'zip verification ok'
