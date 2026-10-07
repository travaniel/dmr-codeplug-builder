# Builds the Mac version as .app bundles (Apple Silicon and Intel) and zips them, from Windows or anywhere with the .NET 8 SDK.
#   powershell -File tools\package-mac.ps1            -> dist\W6OZZ-CPS-mac-arm64.zip and dist\W6OZZ-CPS-mac-x64.zip
# The zips are written with Unix permissions (0755) so the app starts after unzipping on a Mac.
# The app isn't signed or notarized: see docs/MAC.md for the first-launch steps on the Mac.
param([string[]]$Runtimes = @('osx-arm64', 'osx-x64'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\Mac\CodeplugBuilder.Mac.csproj'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$appName = 'W6OZZ CPS'
[xml]$csproj = Get-Content $proj
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)

foreach ($rid in $Runtimes) {
    $pub = Join-Path $root "src\Mac\bin\publish\$rid"
    if (Test-Path $pub) { Remove-Item -Recurse -Force $pub }
    dotnet publish $proj -c Release -r $rid --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o $pub
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    # Bundle layout: <name>.app/Contents/{Info.plist, MacOS/<everything published>}
    $stage = Join-Path $root "src\Mac\bin\bundle\$rid"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    $macos = Join-Path $stage "$appName.app\Contents\MacOS"
    New-Item -ItemType Directory -Force $macos | Out-Null
    Copy-Item -Recurse -Force (Join-Path $pub '*') $macos
    $resources = Join-Path $stage "$appName.app\Contents\Resources"
    New-Item -ItemType Directory -Force $resources | Out-Null
    Copy-Item (Join-Path $root 'src\Mac\app.icns') $resources
    # (The same plist is in src/Mac/Info.plist.in, which the macOS CI job uses.)
    $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$appName</string>
  <key>CFBundleDisplayName</key><string>$appName</string>
  <key>CFBundleIdentifier</key><string>com.w6ozz.cps</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleExecutable</key><string>CodeplugBuilderMac</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleIconFile</key><string>app</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
"@
    [IO.File]::WriteAllText((Join-Path $stage "$appName.app\Contents\Info.plist"), $plist, (New-Object Text.UTF8Encoding($false)))

    # Zip with Unix permissions so the executable bit survives (Compress-Archive drops it).
    $zipPath = Join-Path $dist "W6OZZ-CPS-mac-$($rid -replace 'osx-','').zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    $zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        $base = (Resolve-Path $stage).Path.TrimEnd('\') + '\'
        foreach ($f in Get-ChildItem -Recurse -File $stage) {
            $rel = $f.FullName.Substring($base.Length).Replace('\', '/')
            $entry = $zip.CreateEntry($rel, [IO.Compression.CompressionLevel]::Optimal)
            $entry.ExternalAttributes = (0x81ED -shl 16) # -rwxr-xr-x
            $in = [IO.File]::OpenRead($f.FullName)
            $out = $entry.Open()
            $in.CopyTo($out); $out.Dispose(); $in.Dispose()
        }
    } finally { $zip.Dispose() }
    '{0}: {1:N1} MB' -f $zipPath, ((Get-Item $zipPath).Length / 1MB)
}
