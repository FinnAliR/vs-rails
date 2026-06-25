using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VSRails;

/// <summary>
/// Base rail cart - the shared tech both the minecart and the smart cart use. Finds any block with
/// "rail" in its code underfoot, snaps to the rail axis, follows the track (curves + 1:1 slopes),
/// derails off open ends, tilts to match the rail, plays the wheel/corner/hurt animations and publishes
/// its state for clients. It is intentionally NOT mountable - seating + stowage live on EntityMinecart.
/// A rider or AI drives it by setting velocity and overriding the rider hooks (IsRidden, ApplyRiderPush).
/// </summary>
public class EntityRailCart : EntityAgent
{
    // The sim runs at Vintage Story's physics rate (Tps = 30). Constants come from EntityMinecart
    // (authored for Minecraft's 20 Hz) rescaled to our rate: velocities *(20/Tps), accel *(20/Tps)^2,
    // per-tick drag ^(20/Tps). Velocity (_vx/_vz) is blocks per VS tick.
    protected const          double Tps     = 30.0;
    private  static readonly double McToTps = 20.0 / Tps;

    protected double _vx;   // along-track velocity, X (blocks per VS tick)
    protected double _vz;   // along-track velocity, Z (blocks per VS tick)

    private  static readonly double MaxSpeed    = 0.4       * McToTps;
    private  const           double RiddenScale = 0.75;
    private  static readonly double SlopeAccel  = 0.0078125 * McToTps * McToTps;
    private  static readonly double DragEmpty   = Math.Pow(0.96,  McToTps);
    private  static readonly double DragRidden  = Math.Pow(0.997, McToTps);
    protected static readonly double PushAccel  = 0.04      * McToTps * McToTps;
    private  const           double RailTop     = 0.0625;
    private  static readonly double DerailDrag  = Math.Pow(0.96,  McToTps);
    private  static readonly double SpeedCap    = 2.0       * McToTps;
    protected static readonly double MountKick  = 0.1       * McToTps;
    private  static readonly float  SlopePitch  = GameMath.PIHALF / 2f;  // 45deg: slope rails rise 1 block per block
    private  static readonly float  PitchSign   = 1f;  // flip to -1 if the cart tilts the wrong way on N/S slopes
    private  static readonly float  RollSign    = 1f;  // flip to -1 if E/W slopes tilt the wrong way

    // Per-model facing fix (degrees) from the entity "yawOffsetDeg" attribute: the model's authored
    // forward vs our travel yaw. The minecart model faces east, so its JSON sets 90.
    protected float YawOffset;

    private const string AnimMoving      = "moving";       // wheels turning while rolling (loops)
    private const string AnimPunch       = "punch";        // recoil when damaged
    private const string AnimCornerRight = "cornerright";  // turning right through a curve
    private const string AnimCornerLeft  = "cornerleft";   // turning left through a curve
    private const double MoveAnimMinSpeed  = 0.01;
    private const float  TurnAnimThreshold = 0.15f;
    private const double TurnAnimCooldown  = 0.5;
    private static readonly bool SwapTurnAnims = false;    // flip if the right/left corner anims are reversed

    private bool   _movingAnimOn;
    private float  _prevYaw;
    private double _turnCooldown;

    // ---- Rider hooks (EntityMinecart overrides these) ----
    /// <summary>True while a speed/drag-scaling rider is aboard. A bare rail cart is never "ridden".</summary>
    protected virtual bool IsRidden => false;
    /// <summary>Apply a rider's forward push along the rail direction. No-op on the base (no seat).</summary>
    protected virtual void ApplyRiderPush(double dtFac, double rvx, double rvz, double rlen) { }

    public override void Initialize(EntityProperties properties, ICoreAPI api, long inChunkIndex3d)
    {
        base.Initialize(properties, api, inChunkIndex3d);
        _vx = WatchedAttributes.GetDouble("railVelX", 0);
        _vz = WatchedAttributes.GetDouble("railVelZ", 0);
        _prevYaw = Pos.Yaw;
        YawOffset = (properties.Attributes?["yawOffsetDeg"].AsFloat(0) ?? 0f) * GameMath.DEG2RAD;
    }

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);
        if (Api.Side == EnumAppSide.Server)
            TickRail(dt);
    }

    // Play the recoil animation when hurt (server starts it; it syncs to clients).
    public override bool ReceiveDamage(DamageSource damageSource, float damage)
    {
        bool received = base.ReceiveDamage(damageSource, damage);
        if (received && Api.Side == EnumAppSide.Server) AnimManager?.StartAnimation(AnimPunch);
        return received;
    }

    // ---- Rail logic (server) ----
    private void TickRail(float dt)
    {
        double dtFac = GameMath.Clamp(dt, 0.0, 0.1) * Tps;   // VS ticks elapsed (0.1 s anti-tunnel clamp)

        BlockPos feet = Pos.AsBlockPos;
        BlockPos below = feet.DownCopy();
        BlockPos railPos = IsRailAt(below) ? below : feet;
        string type = RailType(railPos);

        const double McTickToMotion = Tps / 60.0;   // blocks per VS tick -> Pos.Motion (blocks per 1/60 s)

        if (type == null)
        {
            // Off the rails: keep feeding horizontal momentum to the engine each tick (overriding its
            // strong ground friction so the cart rolls off lips) with our own gentler drag, and leave
            // the vertical motion to the engine's gravity so it arcs off ledges and falls.
            double drag = Math.Pow(DerailDrag, dtFac);
            _vx *= drag;
            _vz *= drag;
            Pos.Motion.X = _vx * McTickToMotion;
            Pos.Motion.Z = _vz * McTickToMotion;
            FaceTravel();
            UpdateMoveAnim();
            WriteNetState();
            return;
        }

        MoveAlongTrack(railPos, type, dtFac);
        FaceTravel();
        UpdateMoveAnim();
        UpdateTurnAnim(dt);
        WriteNetState();
    }

    /// <summary>Publish our authoritative position+velocity on WatchedAttributes (TCP, not withheld
    /// from a rider) so a controlling client can follow it.</summary>
    private void WriteNetState()
    {
        WatchedAttributes.SetDouble("railVelX", _vx);
        WatchedAttributes.SetDouble("railVelZ", _vz);
        WatchedAttributes.SetDouble("railPosX", Pos.X);
        WatchedAttributes.SetDouble("railPosY", Pos.Y);
        WatchedAttributes.SetDouble("railPosZ", Pos.Z);
        WatchedAttributes.SetFloat("railYaw", Pos.Yaw);
        WatchedAttributes.SetFloat("railPitch", Pos.Pitch);
        WatchedAttributes.SetFloat("railRoll", Pos.Roll);
    }

    /// <summary>Point the cart the way it is travelling (+ the model's YawOffset). Left alone below a
    /// tiny speed so a stopped cart keeps its facing.</summary>
    protected void FaceTravel()
    {
        if (_vx * _vx + _vz * _vz < 1e-6) return;
        Pos.Yaw = (float)Math.Atan2(-_vx, -_vz) + YawOffset;
    }

    private void UpdateMoveAnim()
    {
        bool moving = _vx * _vx + _vz * _vz > MoveAnimMinSpeed * MoveAnimMinSpeed;
        if (moving && !_movingAnimOn)      { AnimManager?.StartAnimation(AnimMoving); _movingAnimOn = true; }
        else if (!moving && _movingAnimOn) { AnimManager?.StopAnimation(AnimMoving);  _movingAnimOn = false; }
    }

    private void UpdateTurnAnim(float dt)
    {
        if (_turnCooldown > 0) _turnCooldown -= dt;
        float dYaw = GameMath.AngleRadDistance(_prevYaw, Pos.Yaw);
        _prevYaw = Pos.Yaw;
        if (_turnCooldown <= 0 && Math.Abs(dYaw) > TurnAnimThreshold)
        {
            bool right = dYaw < 0;
            if (SwapTurnAnims) right = !right;
            AnimManager?.StartAnimation(right ? AnimCornerRight : AnimCornerLeft);
            _turnCooldown = TurnAnimCooldown;
        }
    }

    /// <summary>Advance one tick along a shaped rail: re-project velocity onto the rail, let a rider push,
    /// apply slope gravity, drag and the speed cap, snap to the rail centreline, advance, set ride height
    /// and slope tilt. Protected so the controlling client can run the same sim locally.</summary>
    protected void MoveAlongTrack(BlockPos pos, string type, double dtFac)
    {
        GetEnds(type, out int e0x, out int e0z, out int e1x, out int e1z,
                out int ascX, out int ascZ, out bool ascending);

        double rvx = e1x - e0x;
        double rvz = e1z - e0z;
        double rlen = Math.Sqrt(rvx * rvx + rvz * rvz);
        if (rlen == 0) return;

        if (_vx * rvx + _vz * rvz < 0) { rvx = -rvx; rvz = -rvz; }   // travel-direction sign

        double speed = Math.Sqrt(_vx * _vx + _vz * _vz);
        if (speed > SpeedCap) speed = SpeedCap;
        _vx = speed * rvx / rlen;
        _vz = speed * rvz / rlen;

        ApplyRiderPush(dtFac, rvx, rvz, rlen);

        if (ascending)
        {
            _vx -= ascX * SlopeAccel * dtFac;
            _vz -= ascZ * SlopeAccel * dtFac;
        }

        double dragFac = Math.Pow(IsRidden ? DragRidden : DragEmpty, dtFac);
        _vx *= dragFac;
        _vz *= dragFac;

        double cap = MaxSpeed * (IsRidden ? RiddenScale : 1.0);
        _vx = GameMath.Clamp(_vx, -cap, cap);
        _vz = GameMath.Clamp(_vz, -cap, cap);

        double cx = pos.X + 0.5, cz = pos.Z + 0.5;
        double p0x = cx + e0x * 0.5, p0z = cz + e0z * 0.5;
        double p1x = cx + e1x * 0.5, p1z = cz + e1z * 0.5;
        double dlx = p1x - p0x, dlz = p1z - p0z;

        if (dlx == 0) Pos.X = cx;
        else if (dlz == 0) Pos.Z = cz;
        else
        {
            double t = ((Pos.X - p0x) * dlx + (Pos.Z - p0z) * dlz) * 2.0;
            Pos.X = p0x + dlx * t;
            Pos.Z = p0z + dlz * t;
        }

        Pos.X += _vx * dtFac;
        Pos.Z += _vz * dtFac;

        Pos.Y = pos.Y + RailTop + SlopeFraction(pos, type);

        // Slope tilt: N/S slopes use Pitch (renderer flips it with travel, so a fixed sign reads right
        // both ways); E/W slopes use Roll keyed to the actual climb direction.
        float pitch = 0f, roll = 0f;
        if (ascending)
        {
            if (ascZ != 0) pitch = -Math.Sign(ascZ)       * SlopePitch * PitchSign;
            else           roll  =  Math.Sign(_vx * ascX) * SlopePitch * RollSign;
        }
        Pos.Pitch = pitch;
        Pos.Roll  = roll;

        Pos.Motion.Set(0, 0, 0);   // we drove Pos ourselves; don't let controlledphysics move us again
    }

    private double SlopeFraction(BlockPos pos, string type)
    {
        switch (type)
        {
            case "raised_n": return GameMath.Clamp(pos.Z + 1 - Pos.Z, 0, 1);
            case "raised_s": return GameMath.Clamp(Pos.Z - pos.Z, 0, 1);
            case "raised_e": return GameMath.Clamp(Pos.X - pos.X, 0, 1);
            case "raised_w": return GameMath.Clamp(pos.X + 1 - Pos.X, 0, 1);
            default:         return 0;
        }
    }

    private static void GetEnds(string type, out int e0x, out int e0z, out int e1x, out int e1z,
                                out int ascX, out int ascZ, out bool ascending)
    {
        ascX = 0; ascZ = 0; ascending = false;
        switch (type)
        {
            case "flat_we":   e0x = -1; e0z = 0; e1x = 1; e1z = 0; return;
            case "curved_ne": e0x = 0; e0z = -1; e1x = 1; e1z = 0; return;
            case "curved_es": e0x = 1; e0z = 0; e1x = 0; e1z = 1; return;
            case "curved_sw": e0x = 0; e0z = 1; e1x = -1; e1z = 0; return;
            case "curved_wn": e0x = -1; e0z = 0; e1x = 0; e1z = -1; return;
            case "raised_n":  e0x = 0; e0z = -1; e1x = 0; e1z = 1; ascending = true; ascZ = -1; return;
            case "raised_s":  e0x = 0; e0z = -1; e1x = 0; e1z = 1; ascending = true; ascZ = 1; return;
            case "raised_e":  e0x = -1; e0z = 0; e1x = 1; e1z = 0; ascending = true; ascX = 1; return;
            case "raised_w":  e0x = -1; e0z = 0; e1x = 1; e1z = 0; ascending = true; ascX = -1; return;
            default:          e0x = 0; e0z = -1; e1x = 0; e1z = 1; return;   // flat_ns
        }
    }

    private static bool IsRail(Block b)
        => b != null && b.Id != 0 && b.Code?.Path?.Contains("rail") == true;

    protected bool IsRailAt(BlockPos pos) => IsRail(World.BlockAccessor.GetBlock(pos));

    protected string RailType(BlockPos pos)
    {
        Block b = World.BlockAccessor.GetBlock(pos);
        if (!IsRail(b) || b.Variant == null) return null;
        return b.Variant.TryGetValue("type", out string t) ? t : null;
    }
}
