param([string]$LogoDir)
$ErrorActionPreference = 'Stop'
function Frame([int]$kind,[int]$phase) {
    $rows = [object[]]::new(8)
    for ($row=0; $row -lt 8; $row++) { $null = ($rows[$row] = [char[]](' ' * 32)) }
    function Dot([int]$x,[int]$y,[char]$ch) {
        if ($x -ge 0 -and $x -lt 32 -and $y -ge 0 -and $y -lt 8) { $null = ($rows[$y][$x] = $ch) }
    }
    $mode = $kind % 5
    if ($mode -eq 0) {
        for ($x=0; $x -lt 32; $x++) { Dot $x (4 + [int][math]::Round((1 + $kind%3) * [math]::Sin(($x+$phase*($kind%4+1))*[math]::PI/(5+$kind%6)))) '~' }
    } elseif ($mode -eq 1) {
        for ($i=0; $i -lt 4+$kind%7; $i++) { $x=($i*7+$phase*(1+$kind%3))%32; $y=($i*3+$phase)%8; Dot $x $y ('.+*#@'[(($i+$phase)%5)]) }
    } elseif ($mode -eq 2) {
        Dot 15 4 '@'; $r=1+(($phase+$kind)%6); Dot (15-$r) 4 '('; Dot (15+$r) 4 ')'; Dot 15 (4-[math]::Min(3,$r)) '|'; Dot 15 (4+[math]::Min(3,$r)) '|'
    } elseif ($mode -eq 3) {
        $x=(($phase*(2+$kind%4))+$kind)%38-3; for($i=0;$i-lt5;$i++){Dot ($x+$i) (2+$kind%4) ('<###>'[$i])}; for($i=0;$i-lt32;$i++){Dot $i (3+$kind%3) '-'}
    } else {
        for($x=1;$x-lt31;$x+=2){$h=1+(($x*($kind+1)+$phase*2)%7);for($y=0;$y-lt$h;$y++){Dot $x (7-$y) ('#*+'[($x+$phase)%3])}}
    }
    return ($rows | ForEach-Object { -join $_ })
}
New-Item -ItemType Directory -Force -Path $LogoDir | Out-Null
for($kind=0;$kind-lt20;$kind++){
    $lines = foreach($phase in 0..9){ Frame $kind $phase }
    if($lines.Count-ne80 -or ($lines|Where-Object{$_.Length-ne32}) -or (($lines-join '')-match '[A-Za-z0-9]')){throw "Validation failed: $kind"}
    $number=82+$kind; $path=Join-Path $LogoDir ('{0}-ascii-loop-{1:D2}.txt' -f $number,($kind+1))
    [IO.File]::WriteAllLines($path,$lines,[Text.Encoding]::ASCII)
}
