param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld',
    [string]$HarmonyPath = 'C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll',
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$gameAssembly = Join-Path $GamePath 'Assembly-CSharp.dll'
foreach ($file in @($gameAssembly, (Join-Path $GamePath 'UnityStubs.dll'), $HarmonyPath)) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing required assembly: $file" }
}
$artifacts = Join-Path $root 'artifacts/waylith'
$referenceDirectory = New-Item -ItemType Directory -Force (Join-Path $artifacts 'references')
$reference = Join-Path $referenceDirectory.FullName 'Assembly-CSharp.dll'
& $Dotnet run --project (Join-Path $root 'Tools/WaylithReference') -c Release -p:UseSharedCompilation=false -- $gameAssembly $reference
if ($LASTEXITCODE -ne 0) { throw 'Failed to generate the Waylith compile-time reference.' }
& $Dotnet build (Join-Path $root 'Source/RimBridgeServer.Waylith.csproj') -c Release -p:UseSharedCompilation=false -p:BaseIntermediateOutputPath=obj/Waylith/ "-p:GamePath=$GamePath" "-p:HarmonyPath=$HarmonyPath" "-p:WaylithReferencePath=$reference"
if ($LASTEXITCODE -ne 0) { throw 'RimBridgeServer Waylith build failed.' }
$package = Join-Path $artifacts 'RimBridgeServer'
$assemblies = New-Item -ItemType Directory -Force (Join-Path $package '1.7/Assemblies')
$about = New-Item -ItemType Directory -Force (Join-Path $package 'About')
Copy-Item -LiteralPath (Join-Path $root 'About/About.xml'),(Join-Path $root 'About/Preview.png') -Destination $about.FullName -Force
Copy-Item -LiteralPath (Join-Path $root 'LoadFolders.xml'),(Join-Path $root 'LICENSE') -Destination $package -Force
Copy-Item -Path (Join-Path $root '1.6/Assemblies/*.dll') -Destination $assemblies.FullName -Force
Copy-Item -LiteralPath (Join-Path $artifacts 'compiled/net10.0/RimBridgeServer.dll') -Destination $assemblies.FullName -Force
[xml]$meta = Get-Content -LiteralPath (Join-Path $about.FullName 'About.xml') -Raw
$supported = $meta.SelectSingleNode("/ModMetaData/supportedVersions")
$supported.RemoveAll()
$version = $meta.CreateElement('li')
$version.InnerText = '1.7'
$null = $supported.AppendChild($version)
$meta.Save((Join-Path $about.FullName 'About.xml'))
Write-Output $package

