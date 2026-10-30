param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
try {
    function Read-EntryText([string]$Name) {
        $entry = $archive.GetEntry($Name)
        if ($null -eq $entry) { throw "Missing VSIX entry: $Name" }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }

    [xml]$manifest = Read-EntryText 'extension.vsixmanifest'
    if ($manifest.PackageManifest.Metadata.License -ne 'LICENSE') {
        throw 'The VSIX manifest must reference LICENSE.'
    }
    if ((Read-EntryText 'LICENSE') -notmatch '^MIT License') { throw 'MIT license is missing from the VSIX.' }
    if ($archive.Entries | Where-Object { $_.FullName -match '\.pdb$' }) {
        throw 'The release VSIX contains debug symbols that can expose local source paths.'
    }
    if (-not ($manifest.PackageManifest.Assets.Asset | Where-Object {
        $_.Type -eq 'Microsoft.VisualStudio.VsPackage' -and $_.Path -eq 'SLCopilot.pkgdef'
    })) { throw 'The manifest does not register SLCopilot.pkgdef as a VS package.' }

    $registration = Read-EntryText 'SLCopilot.pkgdef'
    $packageSection = [regex]::Match($registration,
        '(?ms)^\[\$RootKey\$\\Packages\\\{3f2b8c1a-5d4e-4f6a-9b7c-1e2d3c4b5a60\}\]\s*(?<values>.*?)(?=^\[|\z)')
    if (-not $packageSection.Success) { throw 'Package registration is missing.' }
    if (-not $packageSection.Groups['values'].Value.Contains('"CodeBase"="$PackageFolder$\SLCopilot.dll"')) {
        throw 'Package registration has no deployed DLL CodeBase. Visual Studio cannot locate the private package using this registration alone.'
    }
    if (-not $registration.Contains('Menus.ctmenu')) { throw 'Menu registration is missing.' }

    foreach ($name in @('SLCopilot.dll', 'SLCopilot.Core.dll')) {
        if ($null -eq $archive.GetEntry($name)) { throw "Missing package assembly: $name" }
    }
    $inputStream = $archive.GetEntry('SLCopilot.dll').Open()
    $buffer = [System.IO.MemoryStream]::new()
    try {
        $inputStream.CopyTo($buffer)
        $assembly = [System.Reflection.Assembly]::Load($buffer.ToArray())
    }
    finally { $inputStream.Dispose(); $buffer.Dispose() }

    $menuFound = $false
    foreach ($name in $assembly.GetManifestResourceNames()) {
        if (-not $name.EndsWith('.resources')) { continue }
        $reader = [System.Resources.ResourceReader]::new($assembly.GetManifestResourceStream($name))
        try {
            $entries = $reader.GetEnumerator()
            while ($entries.MoveNext()) {
                if ($entries.Key -ne 'Menus.ctmenu') { continue }
                # VSCT stores a compressed CFCT command table, not plain UTF-16 labels.
                $menuBytes = [byte[]]$entries.Value
                if ($menuBytes.Length -le 4 -or [System.Text.Encoding]::ASCII.GetString($menuBytes, 0, 4) -ne 'CFCT') {
                    throw 'Menus.ctmenu is not a nonempty compiled command table.'
                }
                $menuFound = $true
            }
        }
        finally { $reader.Dispose() }
    }
    if (-not $menuFound) { throw 'Registered Menus.ctmenu was not found in the package assembly resources.' }
    "PASS: VSIX $($manifest.PackageManifest.Metadata.Identity.Version), package CodeBase, assemblies and registered compiled menu resource."
}
finally { $archive.Dispose() }
