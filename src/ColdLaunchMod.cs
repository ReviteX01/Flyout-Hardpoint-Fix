using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(FlyoutColdLaunch.ColdLaunchMod), "Flyout Hardpoint Fire Fix", "0.5.1", "Community Prototype")]
[assembly: MelonGame("Stonext Games", "Flyout")]

namespace FlyoutColdLaunch;

public sealed class ColdLaunchMod : MelonMod
{
    private static ColdLaunchMod? _instance;
    private const float MaximumForceN = 10_000_000f;
    private const float MinimumSeparationM = 0.005f;
    private readonly Dictionary<int, Pending> _pending = new();
    private readonly HashSet<int> _applied = new();
    private bool _flight;
    private int _generation;
    private int _sequence;
    private float _forceOverrideN;
    private float _scale = 1f;
    private bool _automaticKilonewtonDetection = true;
    private float _maxDeltaV = 500f;
    private float _timeoutSeconds = 4f;
    private float _matchRadius = 12f;
    private bool _verbose = true;
    private float _impulseEquivalentDurationSeconds = 1f;
    private LaunchDirectionMode _directionMode = LaunchDirectionMode.Hardpoint;
    private HardpointAxis _hardpointAxis = HardpointAxis.Down;

    private enum LaunchDirectionMode
    {
        Hardpoint,
        AircraftUp,
        WorldUp,
        ConnectorOutward,
        MissileForward
    }

    private enum HardpointAxis
    {
        Up,
        Down,
        Forward,
        Backward,
        Right,
        Left
    }

    private sealed class Pending
    {
        public int LaunchId;
        public int Generation;
        public int FiredMissileId;
        public int PartId;
        public int PreviousMasterId;
        public int ConnectorId;
        public Vector3 Origin;
        public Vector3 Direction;
        public string DirectionSource = "";
        public float ForceN;
        public float CreatedAt;
        public float Expires;
        public string Label = "";
        public string Association = "";
    }

    private readonly struct Mount
    {
        public readonly Connector Connector;
        public readonly string Relationship;
        public readonly Vector3 Anchor;
        public Mount(Connector c, string relationship, Vector3 anchor)
        {
            Connector = c;
            Relationship = relationship;
            Anchor = anchor;
        }
    }

    public override void OnInitializeMelon()
    {
        _instance = this;
        ReadSettings();
        try
        {
            var fire = AccessTools.Method(typeof(ProcMissile), nameof(ProcMissile.Fire), Type.EmptyTypes);
            var physics = AccessTools.Method(typeof(ProcMissile), nameof(ProcMissile.UpdatePhysics), Type.EmptyTypes);
            if (fire == null || physics == null) throw new MissingMethodException("ProcMissile.Fire or UpdatePhysics missing");
            HarmonyInstance.Patch(fire, prefix: new HarmonyMethod(typeof(ColdLaunchMod), nameof(FirePrefix)));
            HarmonyInstance.Patch(physics, postfix: new HarmonyMethod(typeof(ColdLaunchMod), nameof(PhysicsPostfix)));
            var weaponFire = AccessTools.Method(typeof(WeaponManager), nameof(WeaponManager.Fire), Type.EmptyTypes);
            var systemFire = AccessTools.Method(typeof(WeaponSystem), nameof(WeaponSystem.Fire), new[] { typeof(Weapon) });
            if (weaponFire != null)
            {
                try { HarmonyInstance.Patch(weaponFire, prefix: new HarmonyMethod(typeof(ColdLaunchMod), nameof(WeaponManagerFirePrefix))); }
                catch (Exception ex) { LoggerInstance.Warning("WeaponManager.Fire backup hook failed: " + ex.Message); }
            }
            if (systemFire != null)
            {
                try { HarmonyInstance.Patch(systemFire, prefix: new HarmonyMethod(typeof(ColdLaunchMod), nameof(WeaponSystemFirePrefix))); }
                catch (Exception ex) { LoggerInstance.Warning("WeaponSystem.Fire backup hook failed: " + ex.Message); }
            }
            LoggerInstance.Msg($"v0.5.1 active: ProcMissile.Fire + UpdatePhysics + WeaponManager.Fire({weaponFire != null}) + WeaponSystem.Fire({systemFire != null}); Connector launchers enabled");
        }
        catch (Exception ex)
        {
            LoggerInstance.Error("Unable to install missile hooks: " + ex);
            _instance = null;
        }
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        _flight = sceneName == "PlanetScene2";
        _generation++;
        _pending.Clear();
        _applied.Clear();
        if (_flight) LoggerInstance.Msg("Flight loaded; waiting for ProcMissile.Fire");
    }

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
    {
        if (sceneName != "PlanetScene2") return;
        _flight = false;
        _generation++;
        _pending.Clear();
        _applied.Clear();
    }

    private static void FirePrefix(ProcMissile __instance)
    {
        try { _instance?.Capture(__instance); }
        catch (Exception ex) { _instance?.LoggerInstance.Error("Fire capture exception: " + ex); }
    }

    private static void WeaponManagerFirePrefix(WeaponManager __instance)
    {
        try
        {
            if (_instance == null || !__instance) return;
            _instance.CaptureSelectedWeapon(__instance.SelectedWeapon, "WeaponManager.Fire");
        }
        catch (Exception ex) { _instance?.LoggerInstance.Error("WeaponManager.Fire capture exception: " + ex); }
    }

    private static void WeaponSystemFirePrefix(Weapon __0)
    {
        try { _instance?.CaptureSelectedWeapon(__0, "WeaponSystem.Fire"); }
        catch (Exception ex) { _instance?.LoggerInstance.Error("WeaponSystem.Fire capture exception: " + ex); }
    }

    private void CaptureSelectedWeapon(Weapon? weapon, string source)
    {
        if (!_flight || weapon == null) return;
        var module = weapon.GetModule();
        if (!module) return;
        var missile = module.GetComponent<ProcMissile>();
        if (!missile || module.GetInstanceID() != missile.GetInstanceID()) return;
        if (_verbose) LoggerInstance.Msg($"{source} selected procedural missile id={missile.GetInstanceID()}");
        Capture(missile);
    }

    private static void PhysicsPostfix(ProcMissile __instance)
    {
        try { _instance?.ApplyIfReady(__instance); }
        catch (Exception ex) { _instance?.LoggerInstance.Error("Missile impulse exception: " + ex); }
    }

    private void Capture(ProcMissile missile)
    {
        if (!missile) return;
        var part = missile.Part;
        int missileId = missile.GetInstanceID();
        string label = part ? (part.DisplayName ?? "missile") : "unknown";
        if (_verbose) LoggerInstance.Msg($"FIRE hook missile={missileId} part={label} activeFlight={_flight}");
        if (!_flight || !part) return;
        if (_pending.ContainsKey(missileId) || _applied.Contains(missileId)) return;
        var previousMaster = part.masterRB;
        if (previousMaster && previousMaster.isMissile)
        {
            if (_verbose) LoggerInstance.Warning($"SKIP already-detached missile={label}");
            return;
        }
        if (!TryGetMount(part, out var mount, out string reason))
        {
            LoggerInstance.Warning($"SKIP missile={label} no unambiguous Connector launcher: {reason}");
            return;
        }
        float raw = mount.Connector.force;
        float factor = _scale;
        if (_automaticKilonewtonDetection && _scale <= 1.001f && raw > 0f && raw < 1000f) factor *= 1000f;
        float forceN = _forceOverrideN > 0f ? _forceOverrideN : raw * factor;
        if (!Finite(forceN) || forceN <= 0f || forceN > MaximumForceN)
        {
            LoggerInstance.Warning($"SKIP missile={label} invalid Connector force: raw={raw}, factor={factor}");
            return;
        }
        if (!TryResolveDirection(missile, part, mount, previousMaster, out var direction, out string directionSource))
        {
            LoggerInstance.Warning($"SKIP missile={label} invalid ejection direction mode={_directionMode}");
            return;
        }
        var pending = new Pending
        {
            LaunchId = ++_sequence,
            Generation = _generation,
            FiredMissileId = missileId,
            PartId = part.id,
            PreviousMasterId = previousMaster ? previousMaster.GetInstanceID() : 0,
            ConnectorId = mount.Connector.GetInstanceID(),
            Origin = missile.transform.position,
            Direction = direction,
            DirectionSource = directionSource,
            ForceN = forceN,
            CreatedAt = Time.unscaledTime,
            Expires = Time.unscaledTime + _timeoutSeconds,
            Label = label,
            Association = mount.Relationship
        };
        _pending.Add(missileId, pending);
        LoggerInstance.Msg($"ARMED #{pending.LaunchId} missile={label} part={part.id} Connector={pending.ConnectorId} via={pending.Association} rawForce={raw:F3} scale={factor:F0} forceN={forceN:F0} directionMode={directionSource} direction={direction} hardpointUp={mount.Connector.transform.localToWorldMatrix.MultiplyVector(Vector3.up).normalized} hardpointForward={mount.Connector.transform.localToWorldMatrix.MultiplyVector(Vector3.forward).normalized}");
    }

    private bool TryResolveDirection(ProcMissile missile, Part part, Mount mount, MasterRB sourceBody, out Vector3 direction, out string source)
    {
        direction = Vector3.zero;
        source = _directionMode.ToString();
        switch (_directionMode)
        {
            case LaunchDirectionMode.Hardpoint:
                if (!mount.Connector || !mount.Connector.transform) return false;
                Transform hardpoint = mount.Connector.transform;
                Vector3 localAxis = _hardpointAxis switch
                {
                    HardpointAxis.Up => Vector3.up,
                    HardpointAxis.Down => Vector3.down,
                    HardpointAxis.Forward => Vector3.forward,
                    HardpointAxis.Backward => Vector3.back,
                    HardpointAxis.Right => Vector3.right,
                    HardpointAxis.Left => Vector3.left,
                    _ => Vector3.zero
                };
                direction = hardpoint.localToWorldMatrix.MultiplyVector(localAxis);
                source = $"Hardpoint:{_hardpointAxis}";
                break;
            case LaunchDirectionMode.AircraftUp:
                if (!sourceBody) return false;
                direction = sourceBody.transform.up;
                break;
            case LaunchDirectionMode.WorldUp:
                direction = Vector3.up;
                break;
            case LaunchDirectionMode.MissileForward:
                direction = missile.transform.forward;
                break;
            case LaunchDirectionMode.ConnectorOutward:
                direction = MissileCenter(part) - mount.Anchor;
                if (!VectorFinite(direction) || direction.sqrMagnitude < MinimumSeparationM * MinimumSeparationM)
                    return false;
                break;
            default:
                return false;
        }
        if (!VectorFinite(direction) || direction.sqrMagnitude < 0.0001f) return false;
        direction.Normalize();
        return VectorFinite(direction);
    }

    private static bool TryGetMount(Part missile, out Mount mount, out string reason)
    {
        mount = default;
        reason = "no Connector module on parent chain";
        var seen = new HashSet<int>();
        var parent = missile.parentPart;
        Part child = missile;
        int depth = 0;
        while (parent && depth < 48)
        {
            if (!seen.Add(parent.GetInstanceID())) { reason = "parent cycle"; return false; }
            var connectors = parent.GetComponents<Connector>().Where(c => c && !c.released).ToArray();
            if (connectors.Length > 1)
            {
                reason = $"{connectors.Length} Connectors on ancestor {parent.DisplayName}; ambiguous";
                return false;
            }
            if (connectors.Length == 1)
            {
                if (!TryExactConnectionAnchor(parent, child, out var cpAnchor, out var count))
                {
                    if (count > 1) { reason = "multiple connection points connect same missile branch"; return false; }
                    if (depth > 0) { reason = "deep ancestor Connector has no direct connection to missile branch"; return false; }
                    cpAnchor = connectors[0].transform.position;
                }
                mount = new Mount(connectors[0], $"ancestorDepth={depth + 1},exactConnection={count == 1}", cpAnchor);
                return true;
            }
            child = parent;
            parent = parent.parentPart;
            depth++;
        }
        if (parent) { reason = "parent chain exceeds 48 levels"; return false; }
        var own = missile.GetComponents<Connector>().Where(c => c && !c.released).ToArray();
        if (own.Length > 1) { reason = "multiple Connectors on missile root"; return false; }
        if (own.Length == 1)
        {
            if (!missile.parentPart) { reason = "self Connector with no attached parent"; return false; }
            bool exact = TryExactConnectionAnchor(missile.parentPart, missile, out var selfAnchor, out int count);
            if (count > 1) { reason = "multiple connections to self missile"; return false; }
            if (!exact) selfAnchor = missile.parentPart.transform.position;
            mount = new Mount(own[0], $"selfConnector,exactConnection={exact}", selfAnchor);
            return true;
        }
        return false;
    }

    private static bool TryExactConnectionAnchor(Part owner, Part connectedChild, out Vector3 anchor, out int count)
    {
        anchor = Vector3.zero;
        count = 0;
        var nodes = owner.connectionPoints;
        if (nodes == null) return false;
        foreach (var node in nodes)
        {
            if (!node) continue;
            var connected = node.GetConnectedPart();
            if (!connected || connected.GetInstanceID() != connectedChild.GetInstanceID()) continue;
            anchor = node.transform.position;
            count++;
        }
        return count == 1;
    }

    private static Vector3 MissileCenter(Part part)
    {
        var rs = part.GetComponentsInChildren<Renderer>();
        Bounds bounds = default;
        bool any = false;
        foreach (var r in rs)
        {
            if (!r || !r.enabled) continue;
            if (!any) { bounds = r.bounds; any = true; }
            else bounds.Encapsulate(r.bounds);
        }
        return any ? bounds.center : part.transform.position;
    }

    private void ApplyIfReady(ProcMissile candidate)
    {
        if (!_flight || !candidate || _pending.Count == 0) return;
        int missileId = candidate.GetInstanceID();
        if (_applied.Contains(missileId)) return;
        Part part = candidate.Part;
        if (!part) return;
        MasterRB master = part.masterRB;
        if (!master || !master.isMissile) return;
        Rigidbody body = master.rigidbody;
        if (!body || body.isKinematic || !Finite(body.mass) || body.mass <= 0f) return;
        var nearby = _pending.Values.Where(p =>
            p.Generation == _generation &&
            p.PartId == part.id &&
            (p.PreviousMasterId == 0 || p.PreviousMasterId != master.GetInstanceID()) &&
            (candidate.transform.position - p.Origin).sqrMagnitude <= _matchRadius * _matchRadius).ToArray();
        if (nearby.Length != 1) return;
        var p = nearby[0];
        var matchingBodies = UnityEngine.Object.FindObjectsOfType<ProcMissile>()
            .Where(m => m && m.Part && m.Part.id == p.PartId && m.Part.masterRB &&
                m.Part.masterRB.isMissile &&
                (m.transform.position - p.Origin).sqrMagnitude <= _matchRadius * _matchRadius).ToArray();
        if (matchingBodies.Length != 1 || matchingBodies[0].GetInstanceID() != missileId) return;
        float impulse = p.ForceN * _impulseEquivalentDurationSeconds;
        Vector3 dv = p.Direction * (impulse / body.mass);
        if (!Finite(impulse) || impulse <= 0 || !VectorFinite(dv) || dv.magnitude > _maxDeltaV)
        {
            LoggerInstance.Warning($"SKIP #{p.LaunchId} invalid impulse={impulse} Ns, mass={body.mass} kg, maxDeltaV={_maxDeltaV} m/s");
            _pending.Remove(p.FiredMissileId);
            return;
        }
        var previousBodyVel = body.velocity;
        var previousMissileVel = candidate._vel;
        if (!VectorFinite(previousBodyVel) || !VectorFinite(previousMissileVel)) return;
        body.velocity = previousBodyVel + dv;
        candidate._vel = previousMissileVel + dv;
        _applied.Add(missileId);
        _applied.Add(p.FiredMissileId);
        _pending.Remove(p.FiredMissileId);
        LoggerInstance.Msg($"APPLIED #{p.LaunchId} missile={p.Label} sourceConnector={p.ConnectorId} force={p.ForceN:F0}N effectiveDuration={_impulseEquivalentDurationSeconds:F3}s impulse={impulse:F1}Ns mass={body.mass:F2}kg deltaV={dv.magnitude:F2}m/s directionMode={p.DirectionSource} direction={p.Direction} body={body.GetInstanceID()}");
    }

    public override void OnFixedUpdate()
    {
        if (!_flight || _pending.Count == 0) return;
        try
        {
            float now = Time.unscaledTime;
            if (_pending.Values.Any(p => now - p.CreatedAt > Math.Max(0.08f, Time.fixedDeltaTime * 3f)))
            {
                var active = UnityEngine.Object.FindObjectsOfType<ProcMissile>();
                foreach (var missile in active) ApplyIfReady(missile);
            }
            foreach (var key in _pending.Keys.Where(k => _pending[k].Generation != _generation || now > _pending[k].Expires).ToArray())
            {
                var p = _pending[key];
                LoggerInstance.Warning($"EXPIRED #{p.LaunchId} missile={p.Label} (detached missile with unique ID/body not found within {_timeoutSeconds:F1}s); no impulse applied");
                _pending.Remove(key);
            }
        }
        catch (Exception ex) { LoggerInstance.Error("Cold launch scan exception: " + ex); }
    }

    private void ReadSettings()
    {
        string path = Path.Combine(MelonEnvironment.UserDataDirectory, "FlyoutColdLaunch.cfg");
        if (!File.Exists(path)) File.WriteAllLines(path, new[]
        {
            "VerboseLogging=true",
            "ForceOverrideNewtons=0",
            "ForceUnitScale=1",
            "AutoDetectKilonewtonStorage=true",
            "ImpulseEquivalentDurationSeconds=1",
            "LaunchDirection=Hardpoint",
            "HardpointAxis=Down",
            "DirectionSchemaVersion=2",
            "MaxLaunchWaitSeconds=4",
            "MaxMatchDistanceMeters=12",
            "MaxDeltaVelocityMetersPerSecond=500"
        });
        var configLines = File.ReadAllLines(path).ToList();
        if (!configLines.Any(x => x.TrimStart().StartsWith("DirectionSchemaVersion=", StringComparison.OrdinalIgnoreCase)))
        {
            for (int i = 0; i < configLines.Count; i++)
            {
                if (configLines[i].Trim().Equals("LaunchDirection=AircraftUp", StringComparison.OrdinalIgnoreCase))
                    configLines[i] = "LaunchDirection=Hardpoint";
            }
            if (!configLines.Any(x => x.TrimStart().StartsWith("LaunchDirection=", StringComparison.OrdinalIgnoreCase)))
                configLines.Add("LaunchDirection=Hardpoint");
            configLines.Add("DirectionSchemaVersion=2");
            File.WriteAllLines(path, configLines);
            LoggerInstance.Msg("Upgraded v0.4 direction default to follow the individual hardpoint.");
        }
        foreach (var line in configLines)
        {
            var tokens = line.Split('=', 2);
            if (tokens.Length != 2) continue;
            string key = tokens[0].Trim();
            string raw = tokens[1].Trim();
            if (key == "VerboseLogging" && bool.TryParse(raw, out var v)) { _verbose = v; continue; }
            if (key == "AutoDetectKilonewtonStorage" && bool.TryParse(raw, out var a)) { _automaticKilonewtonDetection = a; continue; }
            if (key == "LaunchDirection")
            {
                if (Enum.TryParse<LaunchDirectionMode>(raw, true, out var mode) && Enum.IsDefined(typeof(LaunchDirectionMode), mode))
                    _directionMode = mode;
                else LoggerInstance.Warning($"Unknown LaunchDirection={raw}; using {_directionMode}");
                continue;
            }
            if (key == "HardpointAxis")
            {
                if (Enum.TryParse<HardpointAxis>(raw, true, out var axis) && Enum.IsDefined(typeof(HardpointAxis), axis))
                    _hardpointAxis = axis;
                else LoggerInstance.Warning($"Unknown HardpointAxis={raw}; using {_hardpointAxis}");
                continue;
            }
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float val) || !Finite(val)) continue;
            switch (key)
            {
                case "ForceOverrideNewtons": if (val >= 0f && val <= MaximumForceN) _forceOverrideN = val; break;
                case "ForceUnitScale": if (val > 0 && val <= 1000) _scale = val; break;
                case "ImpulseEquivalentDurationSeconds": if (val >= 0.001f && val <= 3f) _impulseEquivalentDurationSeconds = val; break;
                case "MaxLaunchWaitSeconds": if (val >= .1f && val <= 20f) _timeoutSeconds = val; break;
                case "MaxMatchDistanceMeters": if (val >= .5f && val <= 100f) _matchRadius = val; break;
                case "MaxDeltaVelocityMetersPerSecond": if (val >= 1f && val <= 5000f) _maxDeltaV = val; break;
            }
        }
        var existing = File.ReadAllLines(path);
        var additions = new List<string>();
        if (!existing.Any(line => line.Split('=', 2)[0].Trim().Equals("ImpulseEquivalentDurationSeconds", StringComparison.OrdinalIgnoreCase)))
            additions.Add("ImpulseEquivalentDurationSeconds=1");
        if (!existing.Any(line => line.Split('=', 2)[0].Trim().Equals("LaunchDirection", StringComparison.OrdinalIgnoreCase)))
            additions.Add("LaunchDirection=Hardpoint");
        if (!existing.Any(line => line.Split('=', 2)[0].Trim().Equals("HardpointAxis", StringComparison.OrdinalIgnoreCase)))
            additions.Add("HardpointAxis=Down");
        if (additions.Count > 0) File.AppendAllLines(path, additions);
        LoggerInstance.Msg($"Ejection settings: equivalentDuration={_impulseEquivalentDurationSeconds:F3}s, direction={_directionMode}, hardpointAxis={_hardpointAxis}, maxDeltaV={_maxDeltaV:F0}m/s");
    }

    private static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    private static bool VectorFinite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
}
