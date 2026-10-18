<#
  Makes Elite Studio's installer: "Elite Studio Setup <version>.exe", the one file a player downloads and runs.

  It runs the checks, builds Studio with the .NET it runs on inside it (so a player needs nothing else installed),
  fetches Microsoft's small WebView2 Runtime installer (run only on a PC that lacks it), and has Inno Setup pack all
  of that into the setup file by the recipe in EliteStudio.iss. The version is the one in Directory.Build.props.

    powershell -ExecutionPolicy Bypass -File installer\Make-Installer.ps1

  -OutputDir   where the setup file goes. By default a folder "Installers" beside this project's folder.
  -SkipTests   leaves the checks out (for a quick try of the installer only, never for a release).
  -Test        makes "Elite Studio (test)" instead: another name and ID, so it installs beside the real one, with no
               shortcuts, and it never closes or deletes anything of the player's real Studio. For trying the installer.

  Needs the .NET SDK (global.json says which) and Inno Setup 6, which is free:
    winget install JRSoftware.InnoSetup --source winget
#>
param(
    [string]$OutputDir = '',
    [switch]$SkipTests,
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutputDir) { $OutputDir = Join-Path (Split-Path $repo -Parent) 'Installers' }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
# dotnet reads global.json (which SDK, which test runner) from the folder it is started in: the project's own.
Push-Location $repo
try {
    # The version: the one in Directory.Build.props, which the program itself carries too.
    [xml]$props = Get-Content -Raw (Join-Path $repo 'Directory.Build.props')
    $version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
    if (-not $version) { throw 'Directory.Build.props has no <Version>.' }

    # Inno Setup's compiler, where winget (for one user) or its own setup (for the PC) put it.
    $iscc = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 is not installed. Install it with:  winget install JRSoftware.InnoSetup --source winget' }

    if (-not $SkipTests) {
        Write-Host "Running the checks..."
        dotnet test (Join-Path $repo 'EliteStudio.slnx')
        if ($LASTEXITCODE -ne 0) { throw 'The checks failed, so no installer was made.' }
    }

    # The program, with .NET inside it, built outside OneDrive (as Directory.Build.props does for every build) so sync never locks it.
    $stage = Join-Path $env:USERPROFILE ('.elite-studio\publish\' + (Split-Path $repo -Leaf))
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    Write-Host "Building Elite Studio $version..."
    dotnet publish (Join-Path $repo 'src\EliteStudio.csproj') -c Release -r win-x64 --self-contained true -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'The build failed, so no installer was made.' }

    # Microsoft's WebView2 Runtime installer (a small download that fetches the rest), kept once fetched. Only Microsoft's own will do.
    $cache = Join-Path $env:USERPROFILE '.elite-studio\cache'
    New-Item -ItemType Directory -Force $cache | Out-Null
    $webView2 = Join-Path $cache 'MicrosoftEdgeWebview2Setup.exe'
    if (-not (Test-Path $webView2)) {
        Write-Host "Fetching Microsoft's WebView2 Runtime installer..."
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $webView2 -UseBasicParsing
    }
    $signature = Get-AuthenticodeSignature $webView2
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        Remove-Item $webView2
        throw "The WebView2 installer fetched is not signed by Microsoft; it was deleted. Run this again."
    }

    # The setup file.
    New-Item -ItemType Directory -Force $OutputDir | Out-Null
    $defines = @("/DAppVersion=$version", "/DAppFiles=$stage", "/DWebView2Setup=$webView2", "/O$OutputDir")
    if ($Test) { $defines += '/DTestBuild' }
    Write-Host "Packing the installer..."
    & $iscc /Qp @defines (Join-Path $PSScriptRoot 'EliteStudio.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup could not make the installer.' }

    $name = if ($Test) { "Elite Studio Test Setup $version.exe" } else { "Elite Studio Setup $version.exe" }
    $setup = Get-Item (Join-Path $OutputDir $name)
    Write-Host ("Made {0} ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB))
}
finally {
    Pop-Location
}
