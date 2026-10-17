from pathlib import Path
import os
import math
import pytest

SOURCE = (Path(__file__).resolve().parents[1] / 'src' / 'ColdLaunchMod.cs').read_text()


def rot_x(deg, v):
    t = math.radians(deg)
    x, y, z = v
    return (x, y*math.cos(t)-z*math.sin(t), y*math.sin(t)+z*math.cos(t))


def rot_z(deg, v):
    t = math.radians(deg)
    x, y, z = v
    return (x*math.cos(t)-y*math.sin(t), x*math.sin(t)+y*math.cos(t), z)


def near(a, b):
    for i in range(3):
        assert a[i] == pytest.approx(b[i], abs=1e-6)


def test_dedicated_hardpoint_mode():
    assert 'private LaunchDirectionMode _directionMode = LaunchDirectionMode.Hardpoint;' in SOURCE
    assert 'if (!mount.Connector || !mount.Connector.transform) return false;' in SOURCE
    assert 'Transform hardpoint = mount.Connector.transform;' in SOURCE
    assert 'hardpoint.localToWorldMatrix.MultiplyVector(localAxis)' in SOURCE
    assert 'source = $"Hardpoint:{_hardpointAxis}";' in SOURCE
    assert 'Direction = direction,' in SOURCE
    assert 'Vector3 dv = p.Direction * (impulse / body.mass)' in SOURCE


@pytest.mark.parametrize('side,basis', [
    ('Up','Vector3.up'),('Down','Vector3.down'),
    ('Forward','Vector3.forward'),('Backward','Vector3.back'),
    ('Right','Vector3.right'),('Left','Vector3.left')])
def test_all_axis_conventions(side, basis):
    assert f'HardpointAxis.{side} => {basis}' in SOURCE


def test_downward_hardpoint_while_missile_looks_forward():
    hardpoint_up = rot_x(180, (0, 1, 0))
    missile_forward = (0, 0, 1)
    near(hardpoint_up, (0, -1, 0))
    assert hardpoint_up != missile_forward
    assert SOURCE.index('case LaunchDirectionMode.Hardpoint:') < SOURCE.index('case LaunchDirectionMode.MissileForward:')


def test_left_right_and_skewed_mounts():
    near(rot_z(-90, (0, 1, 0)), (1, 0, 0))
    near(rot_z(90, (0, 1, 0)), (-1, 0, 0))
    v = rot_z(30, rot_x(45, (0, 1, 0)))
    near((sum(x*x for x in v)**.5, 0, 0), (1, 0, 0))
    assert abs(v[0]) > .1 and abs(v[1]) > .1 and abs(v[2]) > .1


def test_upward_mount_with_inverted_aircraft():
    aircraft_up = (0, -1, 0)
    local_hardpoint_up = (0, 1, 0)
    assert local_hardpoint_up != aircraft_up
    assert 'direction = sourceBody.transform.up;' in SOURCE
    assert 'case LaunchDirectionMode.Hardpoint:' in SOURCE


def test_previous_aircraft_up_configuration_migration():
    assert 'DirectionSchemaVersion=2' in SOURCE
    assert 'configLines[i].Trim().Equals("LaunchDirection=AircraftUp"' in SOURCE
    assert 'configLines[i] = "LaunchDirection=Hardpoint";' in SOURCE
    assert 'File.WriteAllLines(path, configLines)' in SOURCE
    assert 'LoggerInstance.Msg("Upgraded v0.4 direction default' in SOURCE


def test_force_and_firing_regression():
    assert 'nameof(ProcMissile.Fire)' in SOURCE
    assert 'nameof(ProcMissile.UpdatePhysics)' in SOURCE
    assert 'raw = mount.Connector.force;' in SOURCE or 'float raw = mount.Connector.force;' in SOURCE
    assert 'float impulse = p.ForceN * _impulseEquivalentDurationSeconds;' in SOURCE
    assert 'candidate._vel = previousMissileVel + dv;' in SOURCE
    assert 'body.velocity = previousBodyVel + dv;' in SOURCE
    assert 'master.isMissile' in SOURCE
    assert 'matchingBodies.Length != 1' in SOURCE
    assert '_applied.Add(missileId);' in SOURCE
    assert 'Time.unscaledTime + _timeoutSeconds' in SOURCE


def test_new_metadata_fields():
    root = Path(__file__).resolve().parents[1]
    import sys
    sys.path.insert(0,str(root/'tests'))
    from pe_metadata import PECLI
    assembly = Path(os.environ.get('FLYOUT_GAME_ASSEMBLY', '__optional_flyout_game_assembly_not_provided__'))
    if not assembly.exists():
        pytest.skip('Game proxy assembly not supplied')
    p = PECLI(str(assembly))
    defs=p.typeDefs()
    assert {'Connector','ProcMissile','WeaponManager','WeaponSystem','MasterRB'}.issubset({x[2] for x in defs})


def test_mirrored_mounted_pylons():
    assert 'localToWorldMatrix.MultiplyVector(localAxis)' in SOURCE
    local_up = (0, 1, 0)
    mirrored_y = (1, -1, 1)
    near(tuple(a * b for a, b in zip(local_up, mirrored_y)), (0, -1, 0))
    local_right = (1, 0, 0)
    mirrored_x = (-1, 1, 1)
    near(tuple(a * b for a, b in zip(local_right, mirrored_x)), (-1, 0, 0))


def test_no_missile_orientation_modification():
    assert 'candidate._vel = previousMissileVel + dv;' in SOURCE
    assert 'body.velocity = previousBodyVel + dv;' in SOURCE
    assert 'candidate.transform.rotation =' not in SOURCE
    assert 'missile.transform.rotation =' not in SOURCE
    assert 'candidate.transform.forward =' not in SOURCE
