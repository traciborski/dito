$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This install script is for Dito on Windows.'
}

$dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$rid = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    'X64' { 'win-x64' }
    'Arm64' { 'win-arm64' }
    default { throw 'Dito supports Windows x64 and Arm64.' }
}

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Dito.Windows\Dito.Windows.csproj'
$artifacts = Join-Path $root 'artifacts'
$output = Join-Path $artifacts 'Dito'
$exe = Join-Path $output 'Dito.exe'
$session = (Get-Process -Id $PID).SessionId

$stage = Join-Path $artifacts ".dito-install-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $stage | Out-Null
$publish = Join-Path $stage 'publish'
$previous = Join-Path $stage 'previous'
$installed = $false

try {
    # Publish can skip a DLL if the destination has a newer timestamp, even with an older version.
    & $dotnet publish $project -c Release -r $rid --self-contained false -o $publish
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

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

    if (Test-Path -LiteralPath $output) {
        [IO.Directory]::Move($output, $previous)
    }
    try {
        [IO.Directory]::Move($publish, $output)
        $installed = $true
    }
    catch {
        if (Test-Path -LiteralPath $previous) {
            try {
                [IO.Directory]::Move($previous, $output)
            }
            catch {
                throw "Could not install or restore Dito. The previous build is at '$previous'. $($_.Exception.Message)"
            }
        }
        throw
    }
}
finally {
    if ($installed -or !(Test-Path -LiteralPath $previous)) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction Continue
    }
}

Start-Process -FilePath $exe -WorkingDirectory $output
