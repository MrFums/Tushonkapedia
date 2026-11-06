# Builds Tushonkapedia (the server mod and the game plugin), makes the release zip in dist\, and copies the mod into
# your SPT folder.
#   .\build.ps1 -SPTPath D:\SPT
#   .\build.ps1 -SPTPath D:\SPT -NoInstall      (build and zip only)
# The plugin is built against the game's own DLLs, so it needs your SPT folder. Instead of -SPTPath you can put the
# folder's path in a file called spt-path.txt next to this script, or in the SPT_PATH environment variable.
param(
    [string] $SPTPath = '',
    [switch] $NoInstall
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $SPTPath) { $SPTPath = $env:SPT_PATH }
$pathFile = Join-Path $root 'spt-path.txt'
if (-not $SPTPath -and (Test-Path $pathFile)) { $SPTPath = (Get-Content $pathFile -TotalCount 1).Trim() }
if (-not $SPTPath -or -not (Test-Path (Join-Path $SPTPath 'EscapeFromTarkov_Data'))) {
    throw 'Point the script at your SPT folder (the one with EscapeFromTarkov_Data in it): -SPTPath D:\SPT, or put the path in spt-path.txt.'
}
$serverProject = Join-Path $root 'Tushonkapedia.Server\Tushonkapedia.Server.csproj'
$clientProject = Join-Path $root 'Tushonkapedia.Client\Tushonkapedia.Client.csproj'

$version = ([xml](Get-Content $serverProject)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$metadataVersion = Select-String -Path (Join-Path $root 'Tushonkapedia.Server\ModMetadata.cs') -Pattern 'Version Version \{ get; init; \} = new\("(.+)"\)' | ForEach-Object { $_.Matches[0].Groups[1].Value }
if ($metadataVersion -ne $version) {
    throw "Version mismatch: Tushonkapedia.Server.csproj $version, ModMetadata.cs $metadataVersion."
}

# the SDK may be installed per-user, so it may not be on PATH
$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dotnet = if (Test-Path $userDotnet) { $userDotnet } else { 'dotnet' }

& $dotnet build $serverProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }
$serverDll = Join-Path $root 'Tushonkapedia.Server\bin\Release\Tushonkapedia.Server.dll'

# the plugin compiles against the game's own DLLs, from the SPT folder
& $dotnet build $clientProject -c Release --nologo "-p:SPTPath=$SPTPath"
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$clientDll = Join-Path $root 'Tushonkapedia.Client\bin\Release\Tushonkapedia.Client.dll'

# folder and zip laid out like the SPT folder, so the zip can be extracted straight over it
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist "Tushonkapedia-$version"
$mod = Join-Path $stage 'SPT_Runtime\user\mods\Tushonkapedia'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $mod | Out-Null
Copy-Item $serverDll $mod
$plugin = Join-Path $stage 'BepInEx\plugins\Tushonkapedia'
New-Item -ItemType Directory -Force $plugin | Out-Null
Copy-Item $clientDll $plugin
Copy-Item (Join-Path $root 'web') $mod -Recurse
# pictures, map drawings and keys shipped with the mod, so it works with no internet (built by tools\build_pack.py).
# One pack.zip, read in place by the server: extracting thousands of loose files takes ages. Stored, not
# compressed: the pictures are already WebP, and the release zip compresses it anyway.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$packDir = Join-Path $root 'pack'
if (Test-Path $packDir) {
    $packZip = [System.IO.Compression.ZipFile]::Open((Join-Path $mod 'pack.zip'), 'Create')
    try {
        Get-ChildItem $packDir -Recurse -File | ForEach-Object {
            $entry = $_.FullName.Substring($packDir.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($packZip, $_.FullName, $entry, [System.IO.Compression.CompressionLevel]::NoCompression) | Out-Null
        }
    }
    finally {
        $packZip.Dispose()
    }
}
else { Write-Warning 'No pack folder: the mod will need the internet for pictures and map drawings. Run tools\build_pack.py.' }
Copy-Item (Join-Path $root 'LICENSE'), (Join-Path $root 'README.md'), (Join-Path $root 'files\config.json'), (Join-Path $root 'files\keys.json'), (Join-Path $root 'files\bot-notes.json'), (Join-Path $root 'files\box-places.json'), (Join-Path $root 'files\key-places.json'), (Join-Path $root 'files\zone-places.json'), (Join-Path $root 'files\exit-places.json') $mod

$zip = Join-Path $dist "Tushonkapedia-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $entry = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry) | Out-Null
    }
}
finally {
    $archive.Dispose()
}
Write-Host "Packed $zip"
Write-Host "Mod folder: $mod"
Write-Host "SHA-256 Tushonkapedia.Server.dll: $((Get-FileHash $serverDll -Algorithm SHA256).Hash)"

if (-not $NoInstall) {
    if (-not (Test-Path (Join-Path $SPTPath 'SPT_Runtime\SPT.Server.dll'))) {
        throw "$SPTPath doesn't look like an SPT folder (no SPT_Runtime\SPT.Server.dll)."
    }
    $dest = Join-Path $SPTPath 'SPT_Runtime\user\mods\Tushonkapedia'
    New-Item -ItemType Directory -Force $dest | Out-Null

    # The mod used to be called QuestMap. Its settings, saved pictures, map tiles and loot index move to the new
    # folder (unless the new folder has its own already), then the old mod and plugin folders go, so the two
    # don't both load.
    $old = Join-Path $SPTPath 'SPT_Runtime\user\mods\QuestMap'
    if (Test-Path $old) {
        foreach ($name in 'config.json', 'keys.json', 'data', 'portraits') {
            $from = Join-Path $old $name
            if ((Test-Path $from) -and -not (Test-Path (Join-Path $dest $name))) {
                try { Move-Item $from $dest -ErrorAction Stop }
                catch { throw "Couldn't move $name from the old QuestMap folder. Stop the SPT server and build again. ($($_.Exception.Message))" }
            }
        }
        try { Remove-Item $old -Recurse -Force -ErrorAction Stop; Write-Host "Moved your QuestMap settings and data to $dest and removed the old folder" }
        catch { throw "Couldn't remove the old $old. Stop the SPT server and build again. ($($_.Exception.Message))" }
    }
    $oldPlugin = Join-Path $SPTPath 'BepInEx\plugins\QuestMap'
    if (Test-Path $oldPlugin) {
        try { Remove-Item $oldPlugin -Recurse -Force -ErrorAction Stop; Write-Host "Removed the old QuestMap plugin folder" }
        catch { Write-Warning "Couldn't remove $oldPlugin. Close the game and build again. ($($_.Exception.Message))" }
    }
    # your own config.json, keys.json and bot-notes.json are kept; a running server locks the DLL and pack.zip, so
    # unchanged ones are left alone
    $same = @{}
    foreach ($name in 'Tushonkapedia.Server.dll', 'pack.zip') {
        $installed = Join-Path $dest $name
        $built = Join-Path $mod $name
        $same[$name] = (Test-Path $installed) -and (Test-Path $built) -and (Get-FileHash $installed).Hash -eq (Get-FileHash $built).Hash
    }
    Get-ChildItem $mod |
        Where-Object { -not ($_.Name -in 'config.json', 'keys.json', 'bot-notes.json' -and (Test-Path (Join-Path $dest $_.Name))) } |
        Where-Object { -not $same[$_.Name] } |
        ForEach-Object {
            $file = $_
            try { Copy-Item $file.FullName -Destination $dest -Recurse -Force -ErrorAction Stop }
            catch { throw "Couldn't copy $($file.Name) into $dest. Stop the SPT server and build again. ($($_.Exception.Message))" }
        }
    Write-Host "Installed to $dest"

    # a running game locks the plugin
    $pluginDest = Join-Path $SPTPath 'BepInEx\plugins\Tushonkapedia'
    New-Item -ItemType Directory -Force $pluginDest | Out-Null
    try { Copy-Item $clientDll -Destination $pluginDest -Force -ErrorAction Stop; Write-Host "Installed the plugin to $pluginDest" }
    catch { Write-Warning "Couldn't copy the plugin into $pluginDest. Close the game and build again. ($($_.Exception.Message))" }
}
