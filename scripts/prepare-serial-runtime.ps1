param()
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
$settings = Get-Content -LiteralPath (Join-Path $repositoryPath 'config/serial-runtime.json') -Raw | ConvertFrom-Json
$cache = Join-Path $repositoryPath 'tmp/serial-runtime'
New-Item -ItemType Directory -Force $cache | Out-Null
function Get-VerifiedArchive([string]$Url, [string]$Hash, [string]$Name) {
    $path = Join-Path $cache $Name
    if (!(Test-Path -LiteralPath $path)) { Invoke-WebRequest -Uri $Url -OutFile $path -UseBasicParsing }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Hash) { throw "Serial runtime hash mismatch: $Name" }
    return $path
}
$pythonZip = Get-VerifiedArchive $settings.pythonUrl $settings.pythonSha256 'python-embed.zip'
$wheel = Get-VerifiedArchive $settings.pyserialUrl $settings.pyserialSha256 'pyserial.whl'
$source = Get-VerifiedArchive $settings.pyserialSourceUrl $settings.pyserialSourceSha256 'pyserial-source.tar.gz'
# Always extract from verified archives, never copy a personal Python installation.
$stage = Join-Path $cache ('staged-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $stage | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($pythonZip, $stage)
$site = Join-Path $stage 'Lib/site-packages'
New-Item -ItemType Directory -Force $site | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory($wheel, $site)
# Isolated _pth mode ignores registry/PYTHONPATH/user-site; only owned scripts are importable.
$paths = @($settings.pythonLibrary, '.', 'Lib/site-packages', '../../firmware/tools')
[IO.File]::WriteAllLines((Join-Path $stage $settings.pythonPathFile), $paths, [Text.UTF8Encoding]::new($false))
$licenseScript = "import pathlib,sys,tarfile; t=tarfile.open(sys.argv[1]); pathlib.Path(sys.argv[2]).write_bytes(t.extractfile('pyserial-3.5/LICENSE.txt').read())"
& (Join-Path $stage 'python.exe') -I -B -c $licenseScript $source (Join-Path $stage 'pyserial-LICENSE.txt')
if ($LASTEXITCODE -ne 0) { throw 'Could not extract the pinned pyserial license.' }
Copy-Item -LiteralPath (Join-Path $repositoryPath 'config/serial-runtime.json') -Destination (Join-Path $stage 'runtime-manifest.json')
foreach ($file in @('python.exe', 'python3.dll', 'python314.dll')) {
    if ((Get-AuthenticodeSignature -LiteralPath (Join-Path $stage $file)).Status -ne 'Valid') { throw "Invalid Python signature: $file" }
}
Write-Output $stage
