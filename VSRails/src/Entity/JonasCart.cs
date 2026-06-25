using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Datastructures;
namespace VSRails;

/// <summary>
/// The Jonas cart. Finds any block with "rail" in its code underfoot,
/// snaps to the rail axis, and self-propels when a player is riding.
/// Right-click to mount; travel direction is set from the player's look.
/// </summary>
public class JonasCart : EntityAgent, IMountable
{
    // ── IMountable ────────────────────────────────────────────────────────────
    private IMountableSeat[] _seats;

    public IMountableSeat[]  Seats               => _seats;
    public Entity            MountableEntity      => this;
    public Entity            OnEntity             => this;
    public EntityPos         Position             => Pos;
    public double            StepPitch            => 0;
    public bool              AnyMounted()         => _seats[0].Passenger != null;
    public Entity Controller => _seats[0].Passenger;
    public EntityControls    ControllingControls  => _seats[0].Controls;  // seat carries the live rider input while mounted (the player's own Controls go stale)

    // ── Rail state ────────────────────────────────────────────────────────────
    // The simulation runs at Vintage Story's physics rate (Tps = 30). The constants below are
    // EntityMinecart's, authored for Minecraft's 20 Hz, rescaled to our rate so the cart behaves the
    // same while the clock matches the engine: velocities ×(20/Tps), accelerations ×(20/Tps)²,
    // per-tick drag ^(20/Tps). Velocity (_vx/_vz) is therefore blocks per VS tick.
    private const          double Tps    = 30.0;          // Vintage Story physics ticks per second
    private static readonly double McToTps = 20.0 / Tps;  // Minecraft (20 Hz) ticks per VS tick

    private double _vx;   // along-track velocity, X (blocks per VS tick)
    private double _vz;   // along-track velocity, Z (blocks per VS tick)

    private static readonly double MaxSpeed   = 0.4       * McToTps;            // EntityMinecart.getMaximumSpeed()
    private const           double RiddenScale = 0.75;                          // moveAlongTrack reduces speed while ridden
    private static readonly double SlopeAccel = 0.0078125 * McToTps * McToTps;  // 1/128, gravity pull on an ascending rail
    private static readonly double DragEmpty  = Math.Pow(0.96,  McToTps);       // applyDrag(), no passenger
    private static readonly double DragRidden = Math.Pow(0.997, McToTps);       // applyDrag(), with passenger
    private static readonly double PushAccel  = 0.04      * McToTps * McToTps;  // per-tick push from a rider holding "forward"
    private const           double RailTop    = 0.0625;                         // rail surface sits 1/16 above the block
    private static readonly double DerailDrag = Math.Pow(0.96,  McToTps);       // our own horizontal friction once off the rails
    private static readonly double SpeedCap   = 2.0       * McToTps;            // hard projection clamp in moveAlongTrack
    private static readonly double MountKick  = 0.1       * McToTps;            // kick on mount so the cart starts rolling
    private static readonly float YawModelOffset = GameMath.PIHALF;  // model faces +X (east) at yaw 0 -> rotate +90° to point along travel
    private static readonly float SlopePitch     = GameMath.PIHALF / 2f;  // 45°: slope rails rise 1 block per block
    private static readonly float PitchSign      = 1f;  // flip to -1 if the cart tilts the wrong way on slopes
    private static readonly float RollSign       = 1f;  // flip to -1 if east/west slopes tilt the wrong way

    // ── Animation (codes resolve to entries in the entity's client.animations) ──
    private const string AnimMoving      = "moving";       // wheels turning while rolling (loops)
    private const string AnimPunch       = "punch";        // recoil when damaged
    private const string AnimCornerRight = "cornerright";  // turning right through a curve
    private const string AnimCornerLeft  = "cornerleft";   // turning left through a curve
    private const double MoveAnimMinSpeed  = 0.01;         // blocks/MC-tick; above this the wheels spin
    private const float  TurnAnimThreshold = 0.15f;        // yaw change (rad) per tick that counts as a turn
    private const double TurnAnimCooldown  = 0.5;          // seconds before another corner anim may fire
    private static readonly bool SwapTurnAnims = false;    // flip if the right/left corner anims come out reversed

    private bool   _movingAnimOn;
    private float  _prevYaw;
    private double _turnCooldown;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    public override void Initialize(EntityProperties properties, ICoreAPI api, long inChunkIndex3d)
    {
        // Build seats BEFORE base init: a behavior's Initialize may query IMountable.Seats, and a
        // null _seats there would throw during spawn.
        _seats = new IMountableSeat[] { new CartSeat(this) };

        base.Initialize(properties, api, inChunkIndex3d);

        _vx = WatchedAttributes.GetDouble("railVelX", 0);
        _vz = WatchedAttributes.GetDouble("railVelZ", 0);
        _prevYaw = Pos.Yaw;
    }

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);
        if (Api.Side == EnumAppSide.Server)
            TickRail(dt);
        else if (IsControlledByOwnClient())
            PredictClient(dt);   // the rider isn't sent its own mount's UDP position, so follow the published one
    }

    private bool IsControlledByOwnClient()
        => Api is ICoreClientAPI capi && _seats[0].Passenger != null && _seats[0].Passenger == capi.World?.Player?.Entity;

    private void PredictClient(float dt)
    {
        // The engine withholds our own mount's UDP position from us, but the server publishes its
        // authoritative position on WatchedAttributes (TCP, not withheld). Ease toward that real
        // position — interpolated, not guessed — so there's no divergence, even falling off a ledge.
        if (!WatchedAttributes.HasAttribute("railPosX")) return;

        double tx = WatchedAttributes.GetDouble("railPosX");
        double ty = WatchedAttributes.GetDouble("railPosY");
        double tz = WatchedAttributes.GetDouble("railPosZ");
        float  tyaw   = WatchedAttributes.GetFloat("railYaw");
        float  tpitch = WatchedAttributes.GetFloat("railPitch");
        float  troll  = WatchedAttributes.GetFloat("railRoll");

        float f = GameMath.Clamp(dt * 12f, 0f, 1f);
        Pos.X += (tx - Pos.X) * f;
        Pos.Y += (ty - Pos.Y) * f;
        Pos.Z += (tz - Pos.Z) * f;
        Pos.Yaw   += GameMath.AngleRadDistance(Pos.Yaw, tyaw) * f;
        Pos.Pitch += (tpitch - Pos.Pitch) * f;
        Pos.Roll  += (troll  - Pos.Roll)  * f;

        // keep our local ServerPos aligned so interpolateposition doesn't fight the smoothing
        ServerPos.X = Pos.X; ServerPos.Y = Pos.Y; ServerPos.Z = Pos.Z;
        ServerPos.Yaw = Pos.Yaw; ServerPos.Pitch = Pos.Pitch; ServerPos.Roll = Pos.Roll;
    }

    /// <summary>Publish the server's authoritative position+velocity on WatchedAttributes so the
    /// controlling client (which the engine refuses to send mount positions to) can follow it.</summary>
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

    // Play the recoil animation when the cart is hurt (server starts it; it syncs to clients).
    public override bool ReceiveDamage(DamageSource damageSource, float damage)
    {
        bool received = base.ReceiveDamage(damageSource, damage);
        if (received && Api.Side == EnumAppSide.Server) AnimManager?.StartAnimation(AnimPunch);
        return received;
    }

    // ── Rail logic ───────
    private void TickRail(float dt)
    {
        // Dismount on sneak (Shift); we implement IMountable directly, so nothing else triggers it.
        // The rider's live input lands in the seat's Controls while mounted, not the player's own.
        // Zero our velocity so stepping off doesn't fling the now-empty cart away.
        if (_seats[0].Passenger is EntityAgent rider && (_seats[0].Controls.Sneak || rider.Controls.Sneak))
        {
            rider.TryUnmount();
            _vx = 0; _vz = 0;
            Pos.Motion.Set(0, 0, 0);
        }

        // dtFac = how many 20 Hz Minecraft ticks elapsed this frame. Clamp so a lag spike can't
        // fling the cart through a wall.
        double dtFac = GameMath.Clamp(dt, 0.0, 0.1) * Tps;   // VS ticks elapsed (0.1 s anti-tunnel clamp)

        // Like moveAlongTrack: the rail we ride may be at our feet or one block below (top of a
        // slope), and a rail directly below takes priority.
        BlockPos feet = Pos.AsBlockPos;
        BlockPos below = feet.DownCopy();
        BlockPos railPos = IsRailAt(below) ? below : feet;
        string type = RailType(railPos);

        // Convert our rail velocity (blocks per Minecraft tick, 1/20 s) to Vintage Story's Pos.Motion
        // (blocks per 1/60 s): real speed _vx*20 blocks/s == _vx*20/60 in motion units. The original
        // code used _vx*20 (60x too fast), which launched the cart off the end of the track.
        const double McTickToMotion = Tps / 60.0;

        if (type == null)
        {
            // Off the rails (moveDerailedMinecart): keep feeding our HORIZONTAL momentum to the engine
            // every tick, applying our own (gentle) drag so the cart still slows to a stop, and leave
            // the VERTICAL motion to the engine's gravity so the cart arcs off ledges and falls.
            //
            // Re-asserting Motion.X/Z each tick (instead of handing off once and reading it back) is
            // deliberate: the engine's ground friction is very strong, so while the cart rests on the
            // lip of a block it scrubs the horizontal speed to zero before the cart can clear the edge
            // — the cart just stops dead on the block instead of rolling off. Overriding Motion.X/Z
            // bypasses that ground friction; DerailDrag supplies the (gentler) slowdown instead. We do
            // NOT touch Motion.Y, so the engine still applies gravity and landing collisions normally.
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

    /// <summary>Turn the cart to point the way it is travelling. In Vintage Story yaw 0 faces north
    /// (-Z) and the "ahead" vector is (-sin yaw, -cos yaw), so the heading matching our horizontal
    /// velocity (_vx,_vz) is atan2(-_vx,-_vz). Below a tiny speed we leave the yaw alone, so a stopped
    /// cart keeps its facing instead of spinning on rounding noise.</summary>
    private void FaceTravel()
    {
        if (_vx * _vx + _vz * _vz < 1e-6) return;
        Pos.Yaw = (float)Math.Atan2(-_vx, -_vz) + YawModelOffset;
    }

    /// <summary>Loop the wheel "moving" animation whenever the cart is rolling, stop it when at rest.</summary>
    private void UpdateMoveAnim()
    {
        bool moving = _vx * _vx + _vz * _vz > MoveAnimMinSpeed * MoveAnimMinSpeed;
        if (moving && !_movingAnimOn)       { AnimManager?.StartAnimation(AnimMoving); _movingAnimOn = true; }
        else if (!moving && _movingAnimOn)  { AnimManager?.StopAnimation(AnimMoving);  _movingAnimOn = false; }
    }

    /// <summary>Fire a one-shot corner animation when our heading swings (entering/leaving a curve).
    /// dYaw sign chooses right vs left; flip SwapTurnAnims if they come out reversed in game.</summary>
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

    private void MoveAlongTrack(BlockPos pos, string type, double dtFac)
    {
        GetEnds(type, out int e0x, out int e0z, out int e1x, out int e1z,
                out int ascX, out int ascZ, out bool ascending);

        // 1. Re-project our velocity onto the rail direction, keeping its magnitude (moveAlongTrack
        //    aligns motionX/Z to the rail vector aint[1]-aint[0]). For curves this vector is the
        //    diagonal chord between the two exits, so the cart cuts the corner exactly like vanilla.
        double rvx = e1x - e0x;
        double rvz = e1z - e0z;
        double rlen = Math.Sqrt(rvx * rvx + rvz * rvz);
        if (rlen == 0) return;

        if (_vx * rvx + _vz * rvz < 0) { rvx = -rvx; rvz = -rvz; }   // travel-direction sign

        double speed = Math.Sqrt(_vx * _vx + _vz * _vz);
        if (speed > SpeedCap) speed = SpeedCap;
        _vx = speed * rvx / rlen;
        _vz = speed * rvz / rlen;

        // 2. Rider input. Vanilla only nudges a near-stopped cart (it relies on powered rails);
        //    with no powered rails here, holding "forward" accelerates the cart along the track in
        //    whichever direction the rider faces, up to the speed cap below.
        EntityControls controls = ControllingControls;
        if (controls != null && controls.Forward && Controller != null)
        {
            Vec3f look = Controller.Pos.GetViewVector();
            double sign = (look.X * rvx + look.Z * rvz) >= 0 ? 1.0 : -1.0;
            _vx += sign * PushAccel * dtFac * rvx / rlen;
            _vz += sign * PushAccel * dtFac * rvz / rlen;
        }

        // 3. Slope gravity: a constant pull toward the downhill (non-ascending) end, exactly the
        //    ±0.0078125 motion tweak in moveAlongTrack's ASCENDING_* switch.
        if (ascending)
        {
            _vx -= ascX * SlopeAccel * dtFac;
            _vz -= ascZ * SlopeAccel * dtFac;
        }

        // 4. Drag, then the speed cap (reduced while ridden).
        double dragFac = Math.Pow(AnyMounted() ? DragRidden : DragEmpty, dtFac);
        _vx *= dragFac;
        _vz *= dragFac;

        double cap = MaxSpeed * (AnyMounted() ? RiddenScale : 1.0);
        _vx = GameMath.Clamp(_vx, -cap, cap);
        _vz = GameMath.Clamp(_vz, -cap, cap);

        // 5. Snap onto the rail centreline (moveAlongTrack's parametric projection onto the chord
        //    from p0 to p1), then advance along it.
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

        // 6. Ride height: flat rails sit 1/16 above the block; a slope ramps linearly from y to y+1
        //    across the block so the cart meets the flat rail waiting at the higher level.
        Pos.Y = pos.Y + RailTop + SlopeFraction(pos, type);

        // Tilt the cart to lie along the rail. Slope rails climb 1:1 (45°), and the shape renderer
        // applies Pitch/Roll about world axes, so the two slope orientations behave differently:
        //  - North/south slopes use Pitch, which the renderer already flips with travel direction, so a
        //    FIXED tilt taken from the ascending axis reads correctly going both ways.
        //  - East/west slopes use Roll, which the renderer does NOT flip with travel — so here we tilt
        //    by the actual climb direction (sign of _vx toward the high end): nose up uphill, nose down
        //    downhill. (A fixed roll was correct heading west but inverted heading east.)
        float pitch = 0f, roll = 0f;
        if (ascending)
        {
            if (ascZ != 0) pitch = -Math.Sign(ascZ)       * SlopePitch * PitchSign;
            else           roll  =  Math.Sign(_vx * ascX) * SlopePitch * RollSign;
        }
        Pos.Pitch = pitch;
        Pos.Roll  = roll;

        // We drove Pos ourselves; zero the engine motion so controlledphysics doesn't move us again.
        Pos.Motion.Set(0, 0, 0);
    }

    /// <summary>Linear 0..1 height across a slope block, measured from the low edge to the high
    /// (ascending) edge. 0 on flat/curve rails.</summary>
    private double SlopeFraction(BlockPos pos, string type)
    {
        switch (type)
        {
            case "raised_n": return GameMath.Clamp(pos.Z + 1 - Pos.Z, 0, 1); // high edge at north (-Z)
            case "raised_s": return GameMath.Clamp(Pos.Z - pos.Z, 0, 1);     // high edge at south (+Z)
            case "raised_e": return GameMath.Clamp(Pos.X - pos.X, 0, 1);     // high edge at east  (+X)
            case "raised_w": return GameMath.Clamp(pos.X + 1 - Pos.X, 0, 1); // high edge at west  (-X)
            default:         return 0;
        }
    }

    /// <summary>The two cells a rail shape connects (as unit X/Z offsets), plus whether it ascends
    /// and toward which direction. Mirrors the entries of EntityMinecart.MATRIX. Facings: N=(0,-1),
    /// S=(0,1), E=(1,0), W=(-1,0).</summary>
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

    private bool IsRailAt(BlockPos pos) => IsRail(World.BlockAccessor.GetBlock(pos));

    /// <summary>The shape state ("flat_ns", "curved_ne", "raised_e", …) of the rail at <paramref name="pos"/>,
    /// or null if there is no shaped rail there.</summary>
    private string RailType(BlockPos pos)
    {
        Block b = World.BlockAccessor.GetBlock(pos);
        if (!IsRail(b) || b.Variant == null) return null;
        return b.Variant.TryGetValue("type", out string t) ? t : null;
    }

    // ── Interaction ───────────────────────────────────────────────────────────
    public override void OnInteract(EntityAgent byEntity, ItemSlot slot, Vec3d hitPosition, EnumInteractMode mode)
    {
        if (mode == EnumInteractMode.Interact && byEntity is EntityPlayer && Api.Side == EnumAppSide.Server)
        {
            var seat = (CartSeat)_seats[0];
            if (seat.CanMount(byEntity))
            {
                // Give the cart a small kick in the way the rider faces so it starts rolling; from
                // there momentum + the "forward" push (see MoveAlongTrack) carry it along the track.
                var look   = byEntity.Pos.GetViewVector();
                var facing = BlockFacing.FromVector(look.X, 0, look.Z);
                if (facing != null && facing.Axis != EnumAxis.Y)
                {
                    _vx = facing.Normali.X * MountKick;
                    _vz = facing.Normali.Z * MountKick;
                }

                byEntity.TryMount(seat);
                return;
            }
        }
        base.OnInteract(byEntity, slot, hitPosition, mode);
    }

    /// <summary>Rebuilds the seat from the attributes written by CartSeat.MountableToTreeAttributes.
    /// Registered via api.RegisterMountable("JonasCart", …) in VSRails.Start so the engine can restore
    /// the mount on world load and during mounted-state syncs.</summary>
    public static IMountableSeat GetMountable(IWorldAccessor world, TreeAttribute tree)
    {
        return world.GetEntityById(tree.GetLong("entityIdMount")) is JonasCart cart ? cart._seats[0] : null;
    }

    // ── Seat ─────────────────────────────────────────────────────────────────
    private sealed class CartSeat : IMountableSeat
    {
        private readonly JonasCart _cart;
        private readonly Matrixf         _identity = new Matrixf();
        private readonly EntityPos       _seatPos  = new EntityPos();
        private readonly EntityControls  _controls = new EntityControls();

        public CartSeat(JonasCart cart) => _cart = cart;

        public IMountable         MountSupplier          => _cart;
        public Entity             Entity                 => _cart;
        public string             SeatId                 { get; set; } = "main";
        public SeatConfig         Config                 { get; set; }

        public Entity             Passenger              { get; set; }
        public long               PassengerEntityIdForInit { get; set; }

        public EntityPos SeatPosition
        {
            get
            {
                _seatPos.X = _cart.Pos.X;
                _seatPos.Y = _cart.Pos.Y + 0.6;
                _seatPos.Z = _cart.Pos.Z;
                return _seatPos;
            }
        }

        public Vec3f              LocalEyePos            => new Vec3f(0, 0.8f, 0);
        public float              FpHandPitchFollow      => 0f;
        public bool               CanSit                 => Passenger == null;
        public bool               CanControl             => true;
        public bool               DoTeleportOnUnmount    { get; set; } = false;
        public bool               SkipIdleAnimation      => false;
        public EnumMountAngleMode AngleMode               => EnumMountAngleMode.Unaffected;  // free look while riding; Fixate locks the rider's camera to the cart
        private AnimationMetaData _sitAnim;
        public AnimationMetaData  SuggestedAnimation      => _sitAnim ??= MakeSitAnim();
        private AnimationMetaData MakeSitAnim()
        {
            string code = _cart.Properties?.Attributes?["sitAnimation"].AsString(null);
            return string.IsNullOrEmpty(code) ? null
                : new AnimationMetaData { Animation = code, Code = code, AnimationSpeed = 1f, BlendMode = EnumAnimationBlendMode.Average }.Init();
        }
        public Matrixf            RenderTransform         => _identity;
        public EntityControls     Controls               => _controls;  // must be non-null: engine calls Controls.FromInt() during TryMount, before Passenger is assigned

        public bool CanMount(EntityAgent entity)  => Passenger == null && entity is EntityPlayer;
        public bool CanUnmount(EntityAgent entity) => Passenger == entity;

        public void DidMount(EntityAgent entity)  { Passenger = entity; }
        public void DidUnmount(EntityAgent entity) { Passenger = null;  }

        public void MountableToTreeAttributes(TreeAttribute tree)
        {
            // The engine reconstructs the mount from these (see JonasCart.GetMountable, registered in
            // VSRails.Start). "className" MUST be written — otherwise ClassRegistry.GetMountable does a
            // dictionary lookup on a null key and throws while syncing the mounted state.
            tree.SetString("className", "JonasCart");
            tree.SetLong("entityIdMount", _cart.EntityId);
            tree.SetString("seatId", SeatId);
            if (Passenger != null)
                tree.SetLong("passengerEntityId", Passenger.EntityId);
        }
        
        public void MountableFromTreeAttributes(TreeAttribute tree, IWorldAccessor world)
        {
            PassengerEntityIdForInit = tree.GetLong("passengerEntityId");
        }
    }
}
