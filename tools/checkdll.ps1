param([string]$Path, [string]$Needles)
$b = [IO.File]::ReadAllBytes($Path)
$list = $Needles -split '\|'
function Find-Bytes([byte[]]$hay, [byte[]]$needle) {
    $limit = $hay.Length - $needle.Length
    for ($i = 0; $i -le $limit; $i++) {
        if ($hay[$i] -ne $needle[0]) { continue }
        $ok = $true
        for ($j = 1; $j -lt $needle.Length; $j++) {
            if ($hay[$i + $j] -ne $needle[$j]) { $ok = $false; break }
        }
        if ($ok) { return $i }
    }
    return -1
}
foreach ($n in $list) {
    $u16 = [Text.Encoding]::Unicode.GetBytes($n)
    $u8 = [Text.Encoding]::UTF8.GetBytes($n)
    $a = Find-Bytes $b $u16
    $c = Find-Bytes $b $u8
    Write-Output ("u16=" + $a + "  utf8=" + $c + "   " + $n)
}
