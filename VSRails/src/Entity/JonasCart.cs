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
    private Vec3d _travelDir = new Vec3d(0, 0, 1);

    private const float PropelSpeed   = 5f;
    private const float CoastFriction = 0.96f;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    public override void Initialize(EntityProperties properties, ICoreAPI api, long inChunkIndex3d)
    {
        base.Initialize(properties, api, inChunkIndex3d);
        _seats = new IMountableSeat[] { new CartSeat(this) };

        _travelDir.X = WatchedAttributes.GetDouble("railDirX", 0);
        _travelDir.Z = WatchedAttributes.GetDouble("railDirZ", 1);
    }

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);
        if (Api.Side == EnumAppSide.Server)
            TickRail();
    }

    // ── Rail logic ────────────────────────────────────────────────────────────
    private void TickRail()
    {
        BlockPos feetPos = Pos.AsBlockPos;

        Block atFeet    = World.BlockAccessor.GetBlock(feetPos);
        Block underFoot = World.BlockAccessor.GetBlock(feetPos.DownCopy());
        Block rail = IsRail(atFeet) ? atFeet : IsRail(underFoot) ? underFoot : null;

        if (rail == null) return;

        UpdateTravelDir(rail);

        if (_seats[0].Passenger != null)
        {
            Pos.Motion.X = _travelDir.X * PropelSpeed;
            Pos.Motion.Z = _travelDir.Z * PropelSpeed;
        }
        else
        {
            Pos.Motion.X *= CoastFriction;
            Pos.Motion.Z *= CoastFriction;
        }

        double cx = feetPos.X + 0.5;
        double cz = feetPos.Z + 0.5;
        bool travelNs = Math.Abs(_travelDir.Z) >= Math.Abs(_travelDir.X);
        if (travelNs) { Pos.X = cx; Pos.Motion.X = 0; }
        else          { Pos.Z = cz; Pos.Motion.Z = 0; }

        WatchedAttributes.SetDouble("railDirX", _travelDir.X);
        WatchedAttributes.SetDouble("railDirZ", _travelDir.Z);
    }

    private static bool IsRail(Block b)
        => b != null && b.Id != 0 && b.Code?.Path?.Contains("rail") == true;

    private void UpdateTravelDir(Block rail)
    {
        string path = rail.Code.Path;
        if      (HasSuffix(path, "ns")) SetAxis(isNs: true);
        else if (HasSuffix(path, "ew")) SetAxis(isNs: false);
        else if (HasSuffix(path, "ne")) Curve(BlockFacing.NORTH, BlockFacing.EAST);
        else if (HasSuffix(path, "es")) Curve(BlockFacing.EAST,  BlockFacing.SOUTH);
        else if (HasSuffix(path, "sw")) Curve(BlockFacing.SOUTH, BlockFacing.WEST);
        else if (HasSuffix(path, "wn")) Curve(BlockFacing.WEST,  BlockFacing.NORTH);
    }

    private static bool HasSuffix(string path, string suffix)
        => path.EndsWith("-" + suffix) || path.EndsWith("_" + suffix);

    private void SetAxis(bool isNs)
    {
        if (isNs) _travelDir.Set(0, 0, Pos.Motion.Z >= 0 ? 1 : -1);
        else      _travelDir.Set(Pos.Motion.X >= 0 ? 1 : -1, 0, 0);
    }

    private void Curve(BlockFacing exitA, BlockFacing exitB)
    {
        var a  = new Vec3d(exitA.Normali.X, 0, exitA.Normali.Z);
        var b  = new Vec3d(exitB.Normali.X, 0, exitB.Normali.Z);
        double da = Pos.Motion.X * a.X + Pos.Motion.Z * a.Z;
        double db = Pos.Motion.X * b.X + Pos.Motion.Z * b.Z;
        _travelDir = da >= db ? a : b;
    }

    // ── Interaction ───────────────────────────────────────────────────────────
    public override void OnInteract(EntityAgent byEntity, ItemSlot slot, Vec3d hitPosition, EnumInteractMode mode)
    {
        if (mode == EnumInteractMode.Interact && byEntity is EntityPlayer && Api.Side == EnumAppSide.Server)
        {
            var seat = (CartSeat)_seats[0];
            if (seat.CanMount(byEntity))
            {
                var look   = byEntity.Pos.GetViewVector();
                var facing = BlockFacing.FromVector(look.X, 0, look.Z);
                if (facing != null && facing.Axis != EnumAxis.Y)
                    _travelDir.Set(facing.Normali.X, 0, facing.Normali.Z);

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
