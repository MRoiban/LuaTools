param([Parameter(Mandatory=$true)][string]$Version, [Parameter(Mandatory=$true)][string]$Commit, [string]$Directory = 'artifacts')
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_REPOSITORY -cne 'MRoiban/LuaTools' -or $env:GITHUB_REF -cne 'refs/heads/security-hardened') { throw 'Signing is restricted to the fork default branch.' }
if ($Version -notmatch '^1\.4\.[1-9][0-9]*$' -or $Commit -notmatch '^[0-9a-f]{40}$') { throw 'Invalid release identity.' }
$asset = "LuaTools-$Version-win-x64.zip"
$package = Join-Path $Directory $asset
$item = Get-Item -LiteralPath $package
if ($item.Length -le 0 -or $item.Length -gt 268435456) { throw 'Package exceeds updater size policy.' }
$manifest = [ordered]@{ Repository = 'MRoiban/LuaTools'; Version = $Version; Commit = $Commit; Asset = $asset; Size = $item.Length; Sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() }
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Compress))
Add-Type -TypeDefinition @'
using System;
using System.Security.Cryptography;
public static class ForkReleaseSigner {
    public static byte[] Sign(string privatePem, string publicPem, byte[] data) {
        using (var key = ECDsa.Create())
        using (var pinned = ECDsa.Create()) {
            key.ImportFromPem(privatePem);
            pinned.ImportFromPem(publicPem);
            if (key.KeySize != 256 || pinned.KeySize != 256) throw new Exception("Release key must be ECDSA P-256.");
            var signature = key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!pinned.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new Exception("Private release key does not match pinned client key.");
            return signature;
        }
    }
}
'@
try {
    $signature = [ForkReleaseSigner]::Sign($env:RELEASE_SIGNING_KEY, (Get-Content src/LuaToolsGui/Resources/release-signing-public.pem -Raw), $bytes)
    [IO.File]::WriteAllBytes((Join-Path $Directory 'release.json'), $bytes)
    [IO.File]::WriteAllBytes((Join-Path $Directory 'release.sig'), $signature)
} finally { $env:RELEASE_SIGNING_KEY = $null }
