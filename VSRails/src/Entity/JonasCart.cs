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
    public EntityControls    ControllingControls  => (_seats[0].Passenger as EntityAgent)?.Controls;

    // ── Rail state ────────────────────────────────────────────────────────────
    // Velocity is kept in Minecraft's units: blocks per 20 Hz tick (matching the constants below,
    // lifted straight from EntityMinecart). Real movement is dt-corrected via dtFac = dt * 20.
    private double _vx;   // along-track velocity, X (blocks/tick)
    private double _vz;   // along-track velocity, Z (blocks/tick)

    private const double MaxSpeed    = 0.4;        // EntityMinecart.getMaximumSpeed()
    private const double RiddenScale = 0.75;       // moveAlongTrack reduces speed while ridden
    private const double SlopeAccel  = 0.0078125;  // 1/128, gravity pull along an ascending rail
    private const double DragEmpty   = 0.96;       // applyDrag(), no passenger
    private const double DragRidden  = 0.997;      // applyDrag(), with passenger
    private const double PushAccel   = 0.04;       // per-tick push from a rider holding "forward"
    private const double RailTop     = 0.0625;     // rail surface sits 1/16 above the block

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    public override void Initialize(EntityProperties properties, ICoreAPI api, long inChunkIndex3d)
    {
        // Build seats BEFORE base init: a behavior's Initialize may query IMountable.Seats, and a
        // null _seats there would throw during spawn.
        _seats = new IMountableSeat[] { new CartSeat(this) };

        base.Initialize(properties, api, inChunkIndex3d);

        _vx = WatchedAttributes.GetDouble("railVelX", 0);
        _vz = WatchedAttributes.GetDouble("railVelZ", 0);
    }

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);
        if (Api.Side == EnumAppSide.Server)
            TickRail(dt);
    }

    // ── Rail logic (ported from EntityMinecart.onUpdate + moveAlongTrack) ───────
    private void TickRail(float dt)
    {
        // dtFac = how many 20 Hz Minecraft ticks elapsed this frame. Clamp so a lag spike can't
        // fling the cart through a wall.
        double dtFac = GameMath.Clamp(dt * 20.0, 0, 2.0);

        // Like moveAlongTrack: the rail we ride may be at our feet or one block below (top of a
        // slope), and a rail directly below takes priority.
        BlockPos feet = Pos.AsBlockPos;
        BlockPos below = feet.DownCopy();
        BlockPos railPos = IsRailAt(below) ? below : feet;
        string type = RailType(railPos);

        if (type == null)
        {
            // Off the rails (moveDerailedMinecart): hand momentum back to the engine physics so it
            // applies gravity, terrain collision and air drag, and stay in sync for when we re-rail.
            Pos.Motion.X = _vx * 20.0;
            Pos.Motion.Z = _vz * 20.0;
            _vx = Pos.Motion.X / 20.0;
            _vz = Pos.Motion.Z / 20.0;
            return;
        }

        MoveAlongTrack(railPos, type, dtFac);

        WatchedAttributes.SetDouble("railVelX", _vx);
        WatchedAttributes.SetDouble("railVelZ", _vz);
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
        if (speed > 2.0) speed = 2.0;
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
                    _vx = facing.Normali.X * 0.1;
                    _vz = facing.Normali.Z * 0.1;
                }

                byEntity.TryMount(seat);
                return;
            }
        }
        base.OnInteract(byEntity, slot, hitPosition, mode);
    }

    // ── Seat ─────────────────────────────────────────────────────────────────
    private sealed class CartSeat : IMountableSeat
    {
        private readonly JonasCart _cart;
        private readonly Matrixf         _identity = new Matrixf();
        private readonly EntityPos       _seatPos  = new EntityPos();

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
        public EnumMountAngleMode AngleMode               => EnumMountAngleMode.Fixate;
        public AnimationMetaData  SuggestedAnimation      => null;
        public Matrixf            RenderTransform         => _identity;
        public EntityControls     Controls               => (_cart._seats[0].Passenger as EntityAgent)?.Controls;

        public bool CanMount(EntityAgent entity)  => Passenger == null && entity is EntityPlayer;
        public bool CanUnmount(EntityAgent entity) => Passenger == entity;

        public void DidMount(EntityAgent entity)  { Passenger = entity; }
        public void DidUnmount(EntityAgent entity) { Passenger = null;  }

        public void MountableToTreeAttributes(TreeAttribute tree)
        {
            if (Passenger != null)
                tree.SetLong("passengerEntityId", Passenger.EntityId);
        }
        
        public void MountableFromTreeAttributes(TreeAttribute tree, IWorldAccessor world)
        {
            PassengerEntityIdForInit = tree.GetLong("passengerEntityId");
        }
    }
}
