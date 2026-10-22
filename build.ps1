param([switch]$Run)
$ErrorActionPreference = 'Stop'
$taskDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($taskDotnet) {
    $taskDotnet = $taskDotnet.Source
    if (-not (& $taskDotnet --list-sdks | Where-Object { $_ -match '^10\.' })) { $taskDotnet = $null }
}
if (-not $taskDotnet) { $taskDotnet = Join-Path $env:LOCALAPPDATA 'SharmBuild\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $taskDotnet)) { throw 'Install the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0' }
& $taskDotnet publish "$PSScriptRoot\Sharm.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "$PSScriptRoot\release"
if ($LASTEXITCODE -ne 0) { throw 'Sharm build failed.' }
if ($Run) { Start-Process -FilePath "$PSScriptRoot\release\Sharm.exe" }
