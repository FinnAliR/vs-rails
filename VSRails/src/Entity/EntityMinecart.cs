using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Datastructures;

namespace VSRails;

/// <summary>
/// The rideable minecart: seats a player/creature and (next) carries a small cargo inventory. Adds the
/// mountable seat, mount/dismount, the rider's forward push, and the controlling-client position
/// prediction on top of EntityRailCart's rail movement. Right-click to mount, Shift to dismount.
/// </summary>
public class EntityMinecart : EntityRailCart, IMountable
{
    private IMountableSeat[] _seats;
    private bool _clientSimming;   // controller-client: currently running the local on-rail sim?

    // ---- IMountable ----
    public IMountableSeat[] Seats               => _seats;
    public Entity           MountableEntity      => this;
    public Entity           OnEntity             => this;
    public EntityPos        Position             => Pos;
    public double           StepPitch            => 0;
    public bool             AnyMounted()         => _seats[0].Passenger != null;
    public Entity           Controller           => _seats[0].Passenger;
    public EntityControls   ControllingControls  => _seats[0].Controls;  // seat carries the live rider input while mounted

    protected override bool IsRidden => AnyMounted();

    public override void Initialize(EntityProperties properties, ICoreAPI api, long inChunkIndex3d)
    {
        // Build the seat BEFORE base init: a behavior's Initialize may query IMountable.Seats.
        _seats = new IMountableSeat[] { new CartSeat(this) };
        base.Initialize(properties, api, inChunkIndex3d);
    }

    public override void OnGameTick(float dt)
    {
        if (Api.Side == EnumAppSide.Server) TickDismount();
        base.OnGameTick(dt);   // Entity tick + (server) the base rail movement
        if (Api.Side != EnumAppSide.Server && IsControlledByOwnClient())
            ClientTick(dt);    // the rider isn't sent its own mount's UDP position, so simulate locally
    }

    // Dismount on sneak (Shift). The rider's live input lands in the seat's Controls while mounted, not
    // the player's own; zero our velocity so stepping off doesn't fling the now-empty cart away.
    private void TickDismount()
    {
        if (_seats[0].Passenger is EntityAgent rider && (_seats[0].Controls.Sneak || rider.Controls.Sneak))
        {
            rider.TryUnmount();
            _vx = 0; _vz = 0;
            Pos.Motion.Set(0, 0, 0);
        }
    }

    // Rider's forward push along the rail (no powered rails here, so holding "forward" accelerates us).
    protected override void ApplyRiderPush(double dtFac, double rvx, double rvz, double rlen)
    {
        var controls = ControllingControls;
        if (controls != null && controls.Forward && Controller != null)
        {
            Vec3f look = Controller.Pos.GetViewVector();
            double sign = (look.X * rvx + look.Z * rvz) >= 0 ? 1.0 : -1.0;
            _vx += sign * PushAccel * dtFac * rvx / rlen;
            _vz += sign * PushAccel * dtFac * rvz / rlen;
        }
    }

    private bool IsControlledByOwnClient()
        => Api is ICoreClientAPI capi && _seats[0].Passenger != null && _seats[0].Passenger == capi.World?.Player?.Entity;

    /// <summary>Boat-style local sim for the controlling client. On a rail we run the same rail sim the
    /// server does, seeded once from the server's velocity then owned locally (smooth every tick, no
    /// stutter from sparse syncs), with a gentle pull toward the published position to bound drift. Off
    /// a rail we follow the server's authoritative position (real gravity/collision), so no sideways drift.</summary>
    private void ClientTick(float dt)
    {
        BlockPos feet  = Pos.AsBlockPos;
        BlockPos below = feet.DownCopy();
        BlockPos railPos = IsRailAt(below) ? below : feet;
        string type = RailType(railPos);
        bool haveServer = WatchedAttributes.HasAttribute("railPosX");

        if (type != null)
        {
            if (!_clientSimming)
            {
                _vx = WatchedAttributes.GetDouble("railVelX", _vx);
                _vz = WatchedAttributes.GetDouble("railVelZ", _vz);
                _clientSimming = true;
            }
            MoveAlongTrack(railPos, type, GameMath.Clamp(dt, 0.0, 0.1) * Tps);
            FaceTravel();
            if (haveServer)
            {
                float cf = GameMath.Clamp(dt * 3f, 0f, 1f);
                Pos.X += (WatchedAttributes.GetDouble("railPosX") - Pos.X) * cf;
                Pos.Y += (WatchedAttributes.GetDouble("railPosY") - Pos.Y) * cf;
                Pos.Z += (WatchedAttributes.GetDouble("railPosZ") - Pos.Z) * cf;
            }
        }
        else
        {
            _clientSimming = false;
            if (haveServer)
            {
                float f = GameMath.Clamp(dt * 12f, 0f, 1f);
                Pos.X += (WatchedAttributes.GetDouble("railPosX") - Pos.X) * f;
                Pos.Y += (WatchedAttributes.GetDouble("railPosY") - Pos.Y) * f;
                Pos.Z += (WatchedAttributes.GetDouble("railPosZ") - Pos.Z) * f;
                Pos.Yaw += GameMath.AngleRadDistance(Pos.Yaw, WatchedAttributes.GetFloat("railYaw")) * f;
            }
        }

        ServerPos.X = Pos.X; ServerPos.Y = Pos.Y; ServerPos.Z = Pos.Z;
        ServerPos.Yaw = Pos.Yaw; ServerPos.Pitch = Pos.Pitch; ServerPos.Roll = Pos.Roll;
    }

    public override void OnInteract(EntityAgent byEntity, ItemSlot slot, Vec3d hitPosition, EnumInteractMode mode)
    {
        if (mode == EnumInteractMode.Interact && byEntity is EntityPlayer && Api.Side == EnumAppSide.Server)
        {
            var seat = (CartSeat)_seats[0];
            if (seat.CanMount(byEntity))
            {
                // Small kick the way the rider faces so it starts rolling.
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

    /// <summary>Rebuilds the seat from CartSeat.MountableToTreeAttributes. Registered via
    /// api.RegisterMountable("railcart", EntityMinecart.GetMountable) in VSRails.Start.</summary>
    public static IMountableSeat GetMountable(IWorldAccessor world, TreeAttribute tree)
        => world.GetEntityById(tree.GetLong("entityIdMount")) is EntityMinecart cart ? cart._seats[0] : null;

    // ---- Seat ----
    private sealed class CartSeat : IMountableSeat
    {
        private readonly EntityMinecart  _cart;
        private readonly Matrixf         _identity = new Matrixf();
        private readonly EntityPos       _seatPos  = new EntityPos();
        private readonly EntityControls  _controls = new EntityControls();

        public CartSeat(EntityMinecart cart) => _cart = cart;

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
        public EnumMountAngleMode AngleMode               => EnumMountAngleMode.Unaffected;  // free look while riding
        private AnimationMetaData _sitAnim;
        public AnimationMetaData  SuggestedAnimation      => _sitAnim ??= MakeSitAnim();
        private AnimationMetaData MakeSitAnim()
        {
            string code = _cart.Properties?.Attributes?["sitAnimation"].AsString(null);
            return string.IsNullOrEmpty(code) ? null
                : new AnimationMetaData { Animation = code, Code = code, AnimationSpeed = 1f, BlendMode = EnumAnimationBlendMode.Average }.Init();
        }
        public Matrixf            RenderTransform         => _identity;
        public EntityControls     Controls               => _controls;  // non-null: engine calls Controls.FromInt() during TryMount, before Passenger is set

        public bool CanMount(EntityAgent entity)  => Passenger == null && entity is EntityAgent;  // players + similarly-sized creatures
        public bool CanUnmount(EntityAgent entity) => Passenger == entity;

        public void DidMount(EntityAgent entity)  { Passenger = entity; }
        public void DidUnmount(EntityAgent entity) { Passenger = null;  }

        public void MountableToTreeAttributes(TreeAttribute tree)
        {
            tree.SetString("className", "railcart");   // MUST match RegisterMountable key
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
