param(
    [string]$Version = "1.0.0",
    [string]$GoogleClientId = "",
    [string]$GoogleClientSecret = "",
    [string]$GitHubOwner = "",
    [string]$GitHubRepository = "LarsCloud",
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$ExeOnly
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "src\LarsCloud.App\LarsCloud.App.csproj"
$publish = Join-Path $root "artifacts\publish"
$installerOut = Join-Path $root "artifacts\installer"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 8 SDK не знайдено. Установіть його з https://dotnet.microsoft.com/download/dotnet/8.0"
}

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
if (Test-Path $installerOut) { Remove-Item $installerOut -Recurse -Force }
New-Item $publish -ItemType Directory -Force | Out-Null
New-Item $installerOut -ItemType Directory -Force | Out-Null

Push-Location $root
try {
    dotnet restore "LarsCloud.sln"
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore завершився з помилкою." }

    if (-not $SkipTests) {
        dotnet test "LarsCloud.sln" -c $Configuration --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Тести не пройшли." }
    }

    dotnet publish $project -c $Configuration -r win-x64 --self-contained true --no-restore `
        -p:Version=$Version -p:AssemblyVersion="$Version.0" -p:FileVersion="$Version.0" `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publish
    if ($LASTEXITCODE -ne 0) { throw "Збирання LarsCloud.exe завершилося з помилкою." }

    $Version | Set-Content (Join-Path $publish "VERSION") -Encoding ascii

    $configPath = Join-Path $publish "appsettings.json"
    $config = Get-Content $configPath -Raw | ConvertFrom-Json
    if ($GoogleClientId) { $config.GoogleClientId = $GoogleClientId }
    if ($GoogleClientSecret) { $config.GoogleClientSecret = $GoogleClientSecret }
    if ($GitHubOwner) {
        $config.GitHubOwner = $GitHubOwner
        $config.PrivacyPolicyUrl = "https://github.com/$GitHubOwner/$GitHubRepository/blob/main/PRIVACY.md"
    }
    if ($GitHubRepository) { $config.GitHubRepository = $GitHubRepository }
    $config | ConvertTo-Json -Depth 10 | Set-Content $configPath -Encoding utf8

    if ($ExeOnly) {
        Write-Host "Готово: $publish\LarsCloud.exe" -ForegroundColor Green
        exit 0
    }

    $isccCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )
    $iscc = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) {
        $isccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($isccCommand) { $iscc = $isccCommand.Source }
    }
    if (-not $iscc) { throw "Inno Setup 6 не знайдено. Установіть його з https://jrsoftware.org/isdl.php" }

    & $iscc (Join-Path $root "installer\LarsCloud.iss")
    if ($LASTEXITCODE -ne 0) { throw "Створення Installer завершилося з помилкою." }

    $setup = Join-Path $installerOut "LarsCloud_Setup.exe"
    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  LarsCloud_Setup.exe" | Set-Content "$setup.sha256" -Encoding ascii
    Write-Host "Готово: $setup" -ForegroundColor Green
    Write-Host "SHA-256: $hash" -ForegroundColor Green
}
finally {
    Pop-Location
}
