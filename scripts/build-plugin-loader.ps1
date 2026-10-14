param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/plugin-loader'),
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../native/steam-plugin-loader'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $output | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'vswhere not found. Install Visual Studio C++ x64 Build Tools before building this loader.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'Visual Studio C++ x64 Build Tools are unavailable.' }
$vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
$env:PATH = (Split-Path $vswhere) + ';' + $env:PATH
# Capture only the developer environment. Compilation uses native argument arrays,
# not a generated shell command containing caller-controlled output paths.
$environment = & $env:ComSpec /d /c "call `"$vcvars`" >nul && set"
if ($LASTEXITCODE -ne 0) { throw 'Failed to initialize the Visual Studio x64 environment.' }
foreach ($entry in $environment) {
    $split = $entry.IndexOf('=')
    if ($split -gt 0) { [Environment]::SetEnvironmentVariable($entry.Substring(0, $split), $entry.Substring($split + 1), 'Process') }
}
Push-Location $output
try {
    # MSVC resolves forwarder target symbols while producing the proxy import
    # library. Supply an import library describing the installer-owned companion.
    $realExports = @('LIBRARY winmm_real.dll', 'EXPORTS')
    $realExports += Get-Content (Join-Path $source 'winmm.def') | ForEach-Object {
        if ($_ -match '^\s+(\w+)=winmm_real\.\w+\s+@(\d+)$') { '    ' + $Matches[1] }
    }
    [IO.File]::WriteAllLines((Join-Path $output 'winmm-real.def'), $realExports, [Text.Encoding]::ASCII)
    & lib.exe /nologo /machine:x64 /def:winmm-real.def /out:winmm-real.lib
    if ($LASTEXITCODE -ne 0) { throw 'Companion import-library generation failed.' }
    & cl.exe /nologo /std:c++17 /O2 /MT /EHsc /W4 /WX /Brepro /guard:cf /LD `
        (Join-Path $source 'loader.cpp') winmm-real.lib /Foloader.obj /link `
        ('/DEF:' + (Join-Path $source 'winmm.def')) /OUT:winmm.dll /IMPLIB:proxy-imports.lib `
        /Brepro /INCREMENTAL:NO /DYNAMICBASE /NXCOMPAT /CETCOMPAT
    if ($LASTEXITCODE -ne 0) { throw 'Native loader compilation failed.' }
    if ($Test) {
        $expected = @(Get-Content (Join-Path $source 'winmm.def') | ForEach-Object {
            if ($_ -match '^\s+(\w+)=(winmm_real\.\w+)\s+@(\d+)$') { $Matches[3] + ':' + $Matches[1] + ':' + $Matches[2] }
        } | Sort-Object)
        $exportDump = & dumpbin.exe /nologo /exports winmm.dll
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect built loader exports.' }
        $actual = @($exportDump | ForEach-Object {
            if ($_ -match '^\s+(\d+)\s+[0-9A-F]+\s+(\w+) \(forwarded to ([\w.]+)\)') { $Matches[1] + ':' + $Matches[2] + ':' + $Matches[3] }
        } | Sort-Object)
        if ($expected.Count -ne 180 -or $actual.Count -ne 180 -or
            (($expected -join "`n") -cne ($actual -join "`n"))) { throw 'Loader export names, ordinals or forwarders differ from the source ABI.' }
        $systemDump = & dumpbin.exe /nologo /exports (Join-Path $env:SystemRoot 'System32/winmm.dll')
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect native Windows winmm exports.' }
        $systemNames = @($systemDump | ForEach-Object {
            if ($_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\w+)') { $Matches[1] }
        })
        foreach ($entry in $expected) {
            if ($entry.Split(':')[1] -cnotin $systemNames) { throw "Native Windows winmm is missing target: $entry" }
        }
        Write-Output 'Static verification: 180 export names, ordinals and companion forwarders; native Windows targets present.'
        & cl.exe /nologo /std:c++17 /O2 /MT /EHsc /W4 /WX /Brepro `
            (Join-Path $source 'test.cpp') /Fotest.obj /Feloader-tests.exe /link /Brepro /INCREMENTAL:NO
        if ($LASTEXITCODE -ne 0) { throw 'Native loader test compilation failed.' }
        & (Join-Path $output 'loader-tests.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Native loader tests failed.' }
    }
} finally { Pop-Location }
Write-Output "Built x64 loader: $(Join-Path $output 'winmm.dll')"
