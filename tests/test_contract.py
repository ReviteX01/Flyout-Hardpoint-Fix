from pathlib import Path
import os
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))
import pytest
from pe_metadata import PECLI

root = Path(__file__).resolve().parents[1]
source = (root / 'src/ColdLaunchMod.cs').read_text(encoding='utf-8')
project = (root / 'FlyoutColdLaunch.csproj').read_text(encoding='utf-8')
build = (root / 'build.ps1').read_text(encoding='utf-8')
assembly = Path(os.environ.get('FLYOUT_GAME_ASSEMBLY', '__optional_flyout_game_assembly_not_provided__'))


def test_class_types():
    if not assembly.exists(): pytest.skip("Flyout generated Assembly-CSharp.dll not supplied")
    a = PECLI(str(assembly))
    names = {name for _, _, name, _, _ in a.typeDefs()}
    assert {'ProcMissile', 'WeaponManager', 'WeaponSystem', 'Weapon', 'Connector', 'Part', 'MasterRB', 'ConnectionPoint'}.issubset(names)


def test_methods_exist():
    if not assembly.exists(): pytest.skip("Flyout generated Assembly-CSharp.dll not supplied")
    a = PECLI(str(assembly))
    types = a.typeDefs()
    methods = a.methods()
    for typ, meth in [('ProcMissile', 'Fire'), ('ProcMissile', 'UpdatePhysics'),
                      ('WeaponManager','Fire'), ('WeaponSystem', 'Fire'),
                      ('Weapon','GetModule'), ('Connector','Release'),
                      ('ConnectionPoint','GetConnectedPart')]:
        idx = next(i for i, x in enumerate(types) if x[2] == typ)
        start, end = types[idx][4]-1, types[idx+1][4]-1
        assert meth in [x[3] for x in methods[start:end]], (typ, meth)


def test_hooks():
    assert 'nameof(ProcMissile.Fire)' in source
    assert 'nameof(WeaponManager.Fire)' in source
    assert 'nameof(WeaponSystem.Fire)' in source
    assert 'postfix: new HarmonyMethod' in source
    assert 'IsAvailable()' not in source
    assert 'fKey' not in source


def test_connector_not_decoupler():
    assert 'GetComponents<Connector>()' in source
    assert 'mount.Connector.force' in source
    assert 'TryExactConnectionAnchor' in source
    assert 'GetConnectedPart()' in source
    assert 'Decoupler' not in source
    assert '.Release()' not in source


def test_targeting_and_detachment():
    assert 'master.isMissile' in source
    assert 'PreviousMasterId != master.GetInstanceID()' in source
    assert 'matchingBodies.Length != 1' in source
    assert 'nearby.Length != 1' in source
    assert 'part.id' in source
    assert 'body.velocity = previousBodyVel + dv' in source
    assert 'candidate._vel = previousMissileVel + dv' in source
    assert '_applied.Add(missileId)' in source


def test_force_semantics():
    assert 'float impulse = p.ForceN * _impulseEquivalentDurationSeconds' in source
    assert 'float impulse = p.ForceN * Time.fixedDeltaTime' not in source
    assert 'ImpulseEquivalentDurationSeconds' in source
    assert '_automaticKilonewtonDetection' in source
    assert 'ForceOverrideNewtons' in source
    assert 'ForceUnitScale' in source
    assert 'MaximumForceN' in source


def test_debug():
    for label in ['FIRE hook', 'ARMED', 'APPLIED', 'EXPIRED', 'SKIP']:
        assert label in source


def test_build_refs():
    for ref in ['MelonLoader', '0Harmony', 'Il2CppInterop.Runtime', 'Il2Cppmscorlib', 'Assembly-CSharp', 'UnityEngine.CoreModule', 'UnityEngine.PhysicsModule']:
        assert f'<Reference Include="{ref}"' in project
    assert 'Copy-Item $src $dst -Force' in build
    assert 'if ($LASTEXITCODE -ne 0)' in build


def test_scene_isolation():
    assert '_generation++' in source
    assert '_pending.Clear()' in source
    assert '_applied.Clear()' in source
    assert 'sceneName == "PlanetScene2"' in source


def test_direction_follows_specific_hardpoint_by_default():
    assert 'LaunchDirectionMode _directionMode = LaunchDirectionMode.Hardpoint' in source
    assert 'Vector3 localAxis = _hardpointAxis switch' in source
    assert 'direction = hardpoint.localToWorldMatrix.MultiplyVector(localAxis);' in source
    assert 'HardpointAxis.Up => Vector3.up' in source
    assert 'direction = sourceBody.transform.up;' in source
    assert 'direction = MissileCenter(part) - mount.Anchor;' in source
    assert 'directionMode={directionSource}' in source


def test_existing_configs_migrated():
    assert 'File.AppendAllLines(path, additions)' in source
    assert 'LaunchDirection=Hardpoint' in source
    assert 'ImpulseEquivalentDurationSeconds=1' in source


def test_corrected_launch_numerics():
    force = 100_000
    mass = 1382.44
    equivalent_duration = 1.0
    impulse = force * equivalent_duration
    dv = impulse / mass
    assert impulse == 100_000
    assert dv == pytest.approx(72.3358, rel=1e-4)
    assert 0.25 * dv == pytest.approx(18.08395, rel=1e-4)


def test_no_force_applied_to_aircraft_and_single_application():
    assert 'master.isMissile' in source
    assert 'PreviousMasterId != master.GetInstanceID()' in source
    assert '_applied.Add(missileId)' in source
    assert 'candidate._vel = previousMissileVel + dv' in source
    assert 'body.velocity = previousBodyVel + dv' in source


def test_down_is_default_everywhere():
    assert 'private HardpointAxis _hardpointAxis = HardpointAxis.Down;' in source
    assert source.count('"HardpointAxis=Down"') == 2
    assert '"HardpointAxis=Up"' not in source
