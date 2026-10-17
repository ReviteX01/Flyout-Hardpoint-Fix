param([Parameter(Mandatory=$true)][string]$FlyoutDirectory)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $FlyoutDirectory -PathType Container)) { throw "Flyout directory not found: $FlyoutDirectory" }
$il = Join-Path $FlyoutDirectory 'MelonLoader\Il2CppAssemblies'
if (-not (Test-Path $il)) { throw 'Missing generated IL2CPP assemblies. Run Flyout once with MelonLoader.' }
$assemblies = @(Get-ChildItem -Path (Join-Path $FlyoutDirectory 'MelonLoader') -Filter '*.dll' -Recurse -File)
function Get-Assembly([string]$name, [string]$preferred) {
    $p = Join-Path $preferred $name
    if (Test-Path $p) { return $p }
    $p = $assemblies | Where-Object Name -eq $name | Select-Object -First 1 -ExpandProperty FullName
    if (-not $p) { throw "Missing $name. Check MelonLoader installation." }
    return $p
}
$ml = Get-Assembly 'MelonLoader.dll' (Join-Path $FlyoutDirectory 'MelonLoader\net6')
$harmony = Get-Assembly '0Harmony.dll' (Join-Path $FlyoutDirectory 'MelonLoader\net6')
$runtime = Get-Assembly 'Il2CppInterop.Runtime.dll' (Join-Path $FlyoutDirectory 'MelonLoader\net6')
$mscorlib = Get-Assembly 'Il2Cppmscorlib.dll' $il
$game = Get-Assembly 'Assembly-CSharp.dll' $il
$core = Get-Assembly 'UnityEngine.CoreModule.dll' $il
$physics = Get-Assembly 'UnityEngine.PhysicsModule.dll' $il
$input = Get-Assembly 'Unity.InputSystem.dll' $il
$proj = Join-Path $PSScriptRoot 'FlyoutColdLaunch.csproj'
$properties = @(
    "-p:MelonLoaderAssembly=$ml",
    "-p:HarmonyAssembly=$harmony",
    "-p:InteropAssembly=$runtime",
    "-p:Il2CppMscorlibAssembly=$mscorlib",
    "-p:GameAssembly=$game",
    "-p:UnityCoreAssembly=$core",
    "-p:UnityPhysicsAssembly=$physics",
    "-p:UnityInputAssembly=$input"
)
& dotnet build $proj -c Release @properties
if ($LASTEXITCODE -ne 0) { throw 'Build failed, mod was not installed.' }
$src = Join-Path $PSScriptRoot 'bin\Release\net6.0\FlyoutColdLaunch.dll'
$dst = Join-Path $FlyoutDirectory 'Mods\FlyoutColdLaunch.dll'
New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
Copy-Item $src $dst -Force
Write-Host "Installed: $dst"
