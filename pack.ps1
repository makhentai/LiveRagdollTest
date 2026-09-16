param(
    [string]$Configuration = "Release"
)

dotnet build LiveRagdollTest.csproj -c $Configuration
$outDir = "dist/LiveRagdollTest/BepInEx/plugins"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Copy-Item "bin/$Configuration/net472/LiveRagdollTest.dll" -Destination $outDir -Force

# Root-level docs alongside BepInEx/, matching the convention used by other client-side
# mods in this environment (see _PMCCoopRevive/PMCoopRevive/pack.ps1).
Copy-Item "README.md" -Destination "dist/LiveRagdollTest/README.md" -Force

Compress-Archive -Path "dist/LiveRagdollTest/*" -DestinationPath "dist/LiveRagdollTest.zip" -Force
Write-Host "Packaged to dist/LiveRagdollTest.zip"
