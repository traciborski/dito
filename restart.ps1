$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Dito currently runs only on Windows.'
}

$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$rid = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    'X64' { 'win-x64' }
    'Arm64' { 'win-arm64' }
    default { throw 'Dito supports Windows x64 and Arm64.' }
}

$project = Join-Path $PSScriptRoot 'src\Dito\Dito.csproj'
$output = Join-Path $PSScriptRoot 'artifacts\Dito'
$exe = Join-Path $output 'Dito.exe'
$session = (Get-Process -Id $PID).SessionId

foreach ($process in Get-Process -Name Dito -ErrorAction SilentlyContinue) {
    if ($process.SessionId -ne $session -or $process.HasExited -or
        ($process.Path -ne $exe -and $process.FileVersionInfo.OriginalFilename -ne 'Dito.dll')) {
        continue
    }

    Write-Host "Stopping Dito (PID $($process.Id))..."
    Stop-Process -Id $process.Id
    if (!$process.WaitForExit(10000)) {
        throw "Dito (PID $($process.Id)) did not stop."
    }
}

& $dotnet publish $project -c Release -r $rid --self-contained false -o $output
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Start-Process -FilePath $exe -WorkingDirectory $output
