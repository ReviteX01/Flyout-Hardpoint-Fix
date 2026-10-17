# Flyout Hardpoint Fire Fix

A lightweight [MelonLoader](https://github.com/LavaGang/MelonLoader) mod for **Flyout** that combines normal missile firing with the ejection force configured on the missile's hardpoint.

In the unmodified game, **Fire Missile** launches a missile without applying the hardpoint's ejection force, while the hardpoint's **Jettison** action ejects it without initiating a normal guided launch. This mod applies the associated hardpoint's ejection impulse when the missile fires, preserving the missile's built-in ignition, staging, aerodynamics, and guidance.

The ejection direction follows the **individual hardpoint**, not the aircraft's up axis or the missile's nose. The same mod works with upward, downward, sideways, angled, inverted, and mirrored installations using a consistent hardpoint-axis convention.

## Requirements and compatibility

- **Flyout 0.2409** (the game build used during development and flight testing)
- **MelonLoader 0.7.3**, IL2CPP, x64
- Windows x64 with the .NET 6 runtime required by MelonLoader
- **.NET 6 SDK** only if compiling from source

Game or MelonLoader updates can change the IL2CPP API and may require a rebuilt or updated mod. Other game versions are not guaranteed to work.

## Installation

1. Install [MelonLoader](https://github.com/LavaGang/MelonLoader/releases) for `Flyout.exe` and launch Flyout once to let it generate the IL2CPP assemblies.
2. Download `FlyoutColdLaunch.dll` from the repository's **Releases** page.
3. Close Flyout, then place the DLL in `<Flyout installation>/Mods/`.
4. Start Flyout. Normal **Fire Missile (F)** now applies the selected missile's hardpoint ejection as well as initiating its usual launch.

Do not keep multiple versions of `FlyoutColdLaunch.dll` installed. To uninstall, remove the DLL from `Mods/`; the optional configuration file can be deleted separately.

## How it works

1. A Harmony patch observes a procedural missile's normal fire operation and identifies the **specific Connector hardpoint** associated with that missile.
2. Before separation, the mod records the connector's configured force and transforms its configured local ejection axis into world space. This orientation is frozen for that launch.
3. Once Flyout has detached the missile, the mod confirms that the target physics body belongs to a uniquely matched missile, then applies the ejection velocity change **once** to both Flyout's procedural missile velocity and the Unity Rigidbody.
4. Flyout continues controlling the missile's flight, including seeker behaviour, motor ignition delay, stages and aerodynamic guidance. The mod does not rotate or steer the missile.

It does **not** issue an additional Jettison command or modify the parent aircraft's physics body.

### Ejection strength

The mod turns the configured hardpoint force into an **instantaneous impulse** using a configurable equivalent duration:

`impulse (N·s) = hardpoint force (N) × ImpulseEquivalentDurationSeconds (s)`

With the default `1` second, a 100 kN hardpoint produces 100,000 N·s, corresponding to a 72.3 m/s velocity change for a 1,382 kg missile. This is an intentional instantaneous ejection approximation, **not** a simulation of the hardpoint applying 100 kN continuously for one second.

## Configuration

On first launch, the mod creates `Flyout/UserData/FlyoutColdLaunch.cfg` with these settings:

```ini
VerboseLogging=true
ForceOverrideNewtons=0
ForceUnitScale=1
AutoDetectKilonewtonStorage=true
ImpulseEquivalentDurationSeconds=1
LaunchDirection=Hardpoint
HardpointAxis=Down
DirectionSchemaVersion=2
MaxLaunchWaitSeconds=4
MaxMatchDistanceMeters=12
MaxDeltaVelocityMetersPerSecond=500
```

| Setting | Description |
| --- | --- |
| `LaunchDirection=Hardpoint` | Uses the selected hardpoint's own orientation (recommended). |
| `HardpointAxis=Down` | Uses the hardpoint's local **−Y axis**, transformed into world space when firing. |
| `ImpulseEquivalentDurationSeconds=1` | Equivalent force duration used to calculate the one-time impulse. |
| `ForceOverrideNewtons=0` | `0` uses each hardpoint's configured force; a positive value overrides it. |
| `ForceUnitScale=1` | Manual multiplier for the stored hardpoint force. |
| `AutoDetectKilonewtonStorage=true` | Converts small positive stored force values to newtons when applicable. |
| `MaxLaunchWaitSeconds=4` | Maximum time to wait for a uniquely identified detached missile. |
| `MaxMatchDistanceMeters=12` | Spatial matching limit for post-release missile identification. |
| `MaxDeltaVelocityMetersPerSecond=500` | Maximum permitted velocity change; larger impulses are rejected. |
| `VerboseLogging=true` | Writes additional launch diagnostics to the MelonLoader log. |

Available `HardpointAxis` values: `Up`, `Down`, `Forward`, `Backward`, `Right`, `Left`. If your hardpoint model uses another local-axis convention, change this setting **once** for that installation; you do not need separate settings for different orientations of the same hardpoint model.

Additional `LaunchDirection` modes (`AircraftUp`, `WorldUp`, `MissileForward`, `ConnectorOutward`) remain available for specialised use, but `Hardpoint` is the recommended default.

**Existing configurations are preserved when updating.** If an older installation already has `HardpointAxis=Up`, edit it to `HardpointAxis=Down` manually, or remove the config file while Flyout is closed to regenerate the defaults. The mod does not overwrite a user's explicit axis choice.

## Building from source

You need Flyout and MelonLoader installed locally, and the **.NET 6 SDK** on Windows. Run Flyout with MelonLoader at least once so its generated IL2CPP reference DLLs exist.

Open PowerShell in the repository root and execute:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -FlyoutDirectory "C:\Program Files (x86)\Steam\steamapps\common\Flyout"
```

Adjust the path to your installation. The build script resolves the game's generated assemblies and MelonLoader dependencies, builds the `net6.0` x64 project in Release configuration, and copies the resulting `FlyoutColdLaunch.dll` to the game's `Mods/` directory **only when compilation succeeds**.

## Limitations

- Supports the **procedural-missile launch path** and requires an identifiable Connector hardpoint with a valid positive force. Standalone missiles or other weapon types are not modified.
- Ambiguous hardpoint-to-missile associations, unavailable detached bodies, invalid force or direction values, and excessive velocity changes are **skipped** rather than guessed.
- `HardpointAxis` is a global local-axis convention. Custom hardpoint models with different conventions may require a different setting; model-specific overrides are not implemented.
- High ejection impulses can cause collisions with closely fitting launch tubes or nearby aircraft structure. Adjust force or equivalent duration for those designs.
- Compatibility with versions other than the listed game/loader combination is unverified.

## Troubleshooting

Check `<Flyout installation>/MelonLoader/Latest.log` for entries from `Flyout_Cold_Launch`:

- `ARMED`: missile-to-hardpoint association was captured before firing.
- `APPLIED`: the impulse was applied to the detached missile. The entry includes force, impulse, direction and velocity change.
- `SKIP`: launch could not be handled safely; the reason is logged.
- `EXPIRED`: a valid detached missile was not uniquely matched within the configured timeout.

A successful `v0.5.1 active` message confirms that the hooks were installed; it does not by itself guarantee that a particular missile's ejection was applied.
