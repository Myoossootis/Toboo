param()
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
$dependencyPath = Join-Path $repositoryPath 'tmp/portable-deps'
New-Item -ItemType Directory -Force $dependencyPath | Out-Null
$installer = Join-Path $dependencyPath 'vc_redist.x64.exe'
$expectedHash = 'CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B'
if (!(Test-Path -LiteralPath $installer)) {
    Invoke-WebRequest 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $installer -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'Microsoft VC runtime download changed. Review its version and update the pinned SHA256 before packaging.'
}
if ((Get-AuthenticodeSignature -LiteralPath $installer).Status -ne 'Valid') { throw 'Invalid Microsoft signature.' }

# Extract only; never execute the installer or modify the Windows installation.
if (!("PortableCabScanner" -as [type])) {
    Add-Type @'
using System;
using System.IO;
public static class PortableCabScanner {
    public static string[] Extract(string installer, string destination) {
        var bytes = File.ReadAllBytes(installer);
        var result = new System.Collections.Generic.List<string>();
        for (var i = 0; i < bytes.Length - 36; i++) {
            if (bytes[i] != 77 || bytes[i+1] != 83 || bytes[i+2] != 67 || bytes[i+3] != 70
                || bytes[i+24] != 3 || bytes[i+25] != 1) continue;
            var size = BitConverter.ToInt32(bytes, i + 8);
            if (size < 36 || (long)i + size > bytes.Length) continue;
            var path = Path.Combine(destination, "container-" + i + ".cab");
            using (var output = File.Create(path)) output.Write(bytes, i, size);
            result.Add(path);
            i += size - 1;
        }
        return result.ToArray();
    }
}
'@
}
$containers = [PortableCabScanner]::Extract($installer, $dependencyPath)
if ($containers.Count -ne 2) { throw 'Unexpected VC installer container layout.' }
$uiPath = Join-Path $dependencyPath 'ui'
$payloadPath = Join-Path $dependencyPath 'payload'
$minimumPath = Join-Path $dependencyPath 'minimum'
foreach ($folder in @($uiPath, $payloadPath, $minimumPath)) { New-Item -ItemType Directory -Force $folder | Out-Null }
& expand.exe '-F:*' $containers[0] $uiPath | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot extract VC license container.' }
& expand.exe '-F:*' $containers[1] $payloadPath | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot extract VC payload container.' }
[xml]$manifest = Get-Content -LiteralPath (Join-Path $uiPath '0') -Raw
if ($manifest.BurnManifest.Registration.Version -ne '14.44.35211.0') { throw 'Unexpected VC runtime version.' }
$minimumCab = $manifest.BurnManifest.Payload | Where-Object { $_.FilePath -eq 'packages\vcRuntimeMinimum_amd64\cab1.cab' }
& expand.exe '-F:*' (Join-Path $payloadPath $minimumCab.SourcePath) $minimumPath | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot extract app-local VC runtime.' }
Write-Host "Prepared Microsoft VC++ 14.44.35211.0 (x64), SHA256 $expectedHash."
