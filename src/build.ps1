# Build PCL-Rebuilder.exe from source. Requires .NET Framework 4.8 and a Roslyn csc.exe.
# dnlib.dll is expected at ..\_lib\dnlib.dll (nuget: dnlib -> lib\net45\dnlib.dll).
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$lib = Join-Path (Split-Path $src -Parent) '_lib\dnlib.dll'
$out = Join-Path (Split-Path $src -Parent) 'PCL-Rebuilder.exe'
if (-not (Test-Path -LiteralPath $lib)) { Write-Host "[FAIL] dnlib.dll not found: $lib" -ForegroundColor Red; exit 1 }

function Find-Csc {
    if ($env:CSC_PATH -and (Test-Path -LiteralPath $env:CSC_PATH)) { return $env:CSC_PATH }
    $c = Get-Command csc.exe -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $m = & $vswhere -latest -find 'MSBuild\**\Bin\Roslyn\csc.exe' 2>$null | Select-Object -First 1
        if ($m -and (Test-Path -LiteralPath $m)) { return $m }
    }
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $base) { continue }
        $hit = Get-ChildItem -Path $base -Directory -ErrorAction SilentlyContinue |
               ForEach-Object { Join-Path $_.FullName '*\MSBuild\Current\Bin\Roslyn\csc.exe' } |
               ForEach-Object { Get-ChildItem -Path $_ -ErrorAction SilentlyContinue } |
               Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

$csc = Find-Csc
if (-not $csc) { Write-Host '[FAIL] csc.exe not found. Install VS with the .NET desktop workload, or set CSC_PATH.' -ForegroundColor Red; exit 1 }
Write-Host "csc  : $csc"
Write-Host "out  : $out"
Write-Host ''
& $csc /nologo /target:exe /platform:anycpu /optimize+ /debug- /out:$out /r:$lib /resource:"$lib,dnlib.dll" `
    (Join-Path $src 'PCL-Rebuilder.cs') (Join-Path $src 'Bootstrap.cs') `
    (Join-Path $src 'ImportPackPatch.cs') (Join-Path $src 'DispatchPatch.cs')
if ($LASTEXITCODE -ne 0) { Write-Host '[FAIL] build failed.' -ForegroundColor Red; exit 1 }
Write-Host "[OK] built $out" -ForegroundColor Green