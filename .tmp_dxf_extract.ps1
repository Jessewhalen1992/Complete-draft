$files = @(
  'src/AtsBackgroundBuilder/REFERENCE ONLY/expected_clean.dxf',
  'src/AtsBackgroundBuilder/REFERENCE ONLY/actual_bad.dxf',
  'src/AtsBackgroundBuilder/REFERENCE ONLY/actual_after_rollback.dxf',
  'src/AtsBackgroundBuilder/REFERENCE ONLY/newoutput2.dxf'
)
$minX = 317300; $maxX = 320500; $minY = 6032900; $maxY = 6033300
foreach ($file in $files) {
  if (-not (Test-Path $file)) { continue }
  Write-Output ("FILE={0}" -f $file)
  $content = Get-Content $file
  $count = 0
  for ($i = 0; $i -lt $content.Count; $i++) {
    if ($content[$i].Trim() -ne 'LINE') { continue }
    $layer=''; $x1=$null; $y1=$null; $x2=$null; $y2=$null
    for ($j = $i + 1; $j -lt [Math]::Min($i + 80, $content.Count - 1); $j += 2) {
      $code = $content[$j].Trim()
      if ($j + 1 -ge $content.Count) { break }
      $val = $content[$j + 1].Trim()
      if ($code -eq '0') { break }
      switch ($code) {
        '8' { $layer = $val }
        '10' { $x1 = [double]::Parse($val, [Globalization.CultureInfo]::InvariantCulture) }
        '20' { $y1 = [double]::Parse($val, [Globalization.CultureInfo]::InvariantCulture) }
        '11' { $x2 = [double]::Parse($val, [Globalization.CultureInfo]::InvariantCulture) }
        '21' { $y2 = [double]::Parse($val, [Globalization.CultureInfo]::InvariantCulture) }
      }
    }
    if ($null -eq $x1 -or $null -eq $y1 -or $null -eq $x2 -or $null -eq $y2) { continue }
    $inBox = (($x1 -ge $minX -and $x1 -le $maxX -and $y1 -ge $minY -and $y1 -le $maxY) -or ($x2 -ge $minX -and $x2 -le $maxX -and $y2 -ge $minY -and $y2 -le $maxY))
    if (-not $inBox) { continue }
    Write-Output (("{0} {1:N3},{2:N3} -> {3:N3},{4:N3}" -f $layer,$x1,$y1,$x2,$y2))
    $count++
  }
  Write-Output ("COUNT={0}" -f $count)
  Write-Output ''
}
