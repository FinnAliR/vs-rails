using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VSRails
{
    /// <summary>
    /// Smart rail block, ported from Minecraft's <c>BlockRailBase</c> placing + updating model and
    /// adapted to this mod's "type" variantgroup states:
    ///   straight : flat_ns, flat_we
    ///   curves   : curved_es, curved_sw, curved_wn, curved_ne
    ///   slopes   : raised_n, raised_s, raised_e, raised_w  (ascending toward that facing)
    ///
    /// Like vanilla Minecraft, all rail logic flows through a small <see cref="Rail"/> helper that
    /// knows which two cells a rail connects, can find rails one block up/down (for slopes), and can
    /// bend a connectable neighbour to meet a new rail. Placement (<see cref="TryPlaceBlock"/>) mirrors
    /// <c>BlockRailBase.Rail.place</c>; dynamic updating (<see cref="OnNeighbourBlockChange"/>) mirrors
    /// <c>BlockRailBase.neighborChanged -&gt; updateState</c> so rails re-curve, straighten or raise
    /// themselves whenever an adjacent rail is added or removed.
    ///
    /// Unlike Minecraft (where rail shape is a blockstate property), each shape here is a separate
    /// block variant, so "set the shape" means swapping to the sibling block resolved via
    /// <see cref="ResolveRail"/> (CodeWithParts), and in-place shape swaps use ExchangeBlock so they
    /// don't churn block entities or cascade neighbour events.
    /// </summary>
    public class BlockRail : Block
    {
        private const string FlatNS = "flat_ns";
        private const string FlatWE = "flat_we";

        // ---------------------------------------------------------------------------------------
        // Placement  (mirrors BlockRailBase.onBlockAdded -> updateDir -> Rail.place)
        // ---------------------------------------------------------------------------------------

        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
        {
            if (!CanPlaceBlock(world, byPlayer, blockSel, ref failureCode))
                return false;

            BlockPos pos = blockSel.Position;

            // Decide the shape purely from connectable neighbours; fall back to the player's facing
            // when nothing connects (this mod snaps a lone rail to the way the player looks).
            BlockFacing playerFacing = SuggestedHVOrientation(byPlayer, blockSel)[0];
            string type = new Rail(this, world, pos).ComputeShape(playerFacing);

            Block toPlace = ResolveRail(world, type);
            if (toPlace == null)
            {
                failureCode = "missingblock";
                return false;
            }

            toPlace.DoPlaceBlock(world, byPlayer, blockSel, itemstack);

            // Let every reachable neighbour (same level + one up/down, for slopes) re-evaluate so it
            // bends to meet the rail we just placed. Mirrors place()'s connectTo loop.
            if (world.Side.IsServer())
                UpdateNeighbours(world, pos);

            return true;
        }

        // ---------------------------------------------------------------------------------------
        // Updating  (mirrors BlockRailBase.neighborChanged -> updateState)
        // ---------------------------------------------------------------------------------------

        public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
        {
            base.OnNeighbourBlockChange(world, pos, neibpos);
            if (world.Side.IsClient()) return;

            // A neighbouring block changed: re-evaluate our own shape from the rails around us.
            new Rail(this, world, pos).UpdateShape();
        }

        /// <summary>Re-evaluate every rail reachable from <paramref name="pos"/> (the four horizontals,
        /// plus one block up and one down each, so slopes connect). Used right after placing a rail.</summary>
        private void UpdateNeighbours(IWorldAccessor world, BlockPos pos)
        {
            for (int i = 0; i < BlockFacing.HORIZONTALS.Length; i++)
            {
                BlockFacing dir = BlockFacing.HORIZONTALS[i];
                for (int dy = -1; dy <= 1; dy++)
                {
                    BlockPos p = pos.AddCopy(dir);
                    if (dy > 0) p.Up(dy);
                    else if (dy < 0) p.Down(-dy);

                    if (world.BlockAccessor.GetBlock(p) is BlockRail)
                        new Rail(this, world, p).UpdateShape();
                }
            }
        }

        /// <summary>Resolve the sibling rail block for a given "type" state via CodeWithParts,
        /// matching vanilla's direct code-segment substitution.</summary>
        internal Block ResolveRail(IWorldAccessor world, string type)
        {
            return type == null ? null : world.GetBlock(CodeWithParts(type));
        }

        // ---------------------------------------------------------------------------------------
        // Rail: the connection model, ported from BlockRailBase.Rail
        // ---------------------------------------------------------------------------------------

        private class Rail
        {
            private readonly BlockRail block;          // resolver for sibling shapes (any rail instance works)
            private readonly IWorldAccessor world;
            private readonly BlockPos pos;
            private string type;                       // current shape state, or null if pos isn't a rail yet
            private List<BlockPos> connected;          // the (up to two) cells this rail joins to

            public Rail(BlockRail block, IWorldAccessor world, BlockPos pos)
            {
                this.block = block;
                this.world = world;
                this.pos = pos;

                if (world.BlockAccessor.GetBlock(pos) is BlockRail rail)
                {
                    this.type = rail.Variant["type"];
                    this.connected = ConnectionsFor(this.type, pos);
                }
                else
                {
                    this.connected = new List<BlockPos>();
                }
            }

            // ---- shape decision (BlockRailBase.Rail.place, non-powered branch) ----------------

            /// <summary>Pick the best shape from connectable neighbours; <paramref name="fallback"/>
            /// orients a lone rail when nothing connects.</summary>
            public string ComputeShape(BlockFacing fallback)
            {
                bool n = HasNeighbourRail(BlockFacing.NORTH);
                bool s = HasNeighbourRail(BlockFacing.SOUTH);
                bool w = HasNeighbourRail(BlockFacing.WEST);
                bool e = HasNeighbourRail(BlockFacing.EAST);

                string t = null;

                if ((n || s) && !w && !e) t = FlatNS;
                if ((w || e) && !n && !s) t = FlatWE;

                // exact two-way curves
                if (s && e && !n && !w) t = "curved_es";
                if (s && w && !n && !e) t = "curved_sw";
                if (n && w && !s && !e) t = "curved_wn";
                if (n && e && !s && !w) t = "curved_ne";

                // 3+/ambiguous junction: prefer a straight, then a curve (mirrors place()'s fallback)
                if (t == null)
                {
                    if (n || s) t = FlatNS;
                    if (w || e) t = FlatWE;
                    if (n && w) t = "curved_wn";
                    if (e && n) t = "curved_ne";
                    if (w && s) t = "curved_sw";
                    if (s && e) t = "curved_es";
                }

                // nothing connects: keep the requested/own orientation
                if (t == null)
                    t = (fallback != null && fallback.Axis == EnumAxis.X) ? FlatWE : FlatNS;

                // ascending overrides: a straight rail climbs toward a rail sitting one block up
                if (t == FlatNS)
                {
                    if (IsRailAt(pos.AddCopy(BlockFacing.NORTH).Up())) t = "raised_n";
                    else if (IsRailAt(pos.AddCopy(BlockFacing.SOUTH).Up())) t = "raised_s";
                }
                else if (t == FlatWE)
                {
                    if (IsRailAt(pos.AddCopy(BlockFacing.EAST).Up())) t = "raised_e";
                    else if (IsRailAt(pos.AddCopy(BlockFacing.WEST).Up())) t = "raised_w";
                }

                return t;
            }

            /// <summary>Recompute this rail's shape and swap to it in place if it changed.
            /// Keeps the rail's current orientation as the fallback so isolated rails don't flip.</summary>
            public void UpdateShape()
            {
                if (type == null) return; // not a rail (anymore)

                // A rail that is already satisfied on both ends is locked, exactly like vanilla's
                // canConnectTo (connectedRails.size() == 2). This stops a new parallel line from
                // re-curving finished straight rails it merely runs alongside. The lock releases as
                // soon as one end stops connecting back (e.g. a neighbour was removed or curved away),
                // letting the rail straighten/re-curve normally.
                if (IsFullyConnected()) return;

                BlockFacing fallback = AxisFallback(type);
                string newType = ComputeShape(fallback);
                if (newType == null || newType == type) return;

                Block nb = block.ResolveRail(world, newType);
                if (nb == null) return;

                world.BlockAccessor.ExchangeBlock(nb.Id, pos);
                type = newType;
                connected = ConnectionsFor(newType, pos);
            }

            // ---- neighbour queries (BlockRailBase.Rail.hasNeighborRail / findRailAt / canConnectTo) ----

            private bool HasNeighbourRail(BlockFacing dir)
            {
                Rail r = FindRailAt(pos.AddCopy(dir));
                if (r == null) return false;
                r.RemoveSoftConnections();
                return r.CanConnectTo(pos);
            }

            /// <summary>Find a rail at <paramref name="at"/>, or one block above/below it (slopes).</summary>
            private Rail FindRailAt(BlockPos at)
            {
                if (world.BlockAccessor.GetBlock(at) is BlockRail) return new Rail(block, world, at);

                BlockPos up = at.UpCopy(1);
                if (world.BlockAccessor.GetBlock(up) is BlockRail) return new Rail(block, world, up);

                BlockPos down = at.DownCopy(1);
                if (world.BlockAccessor.GetBlock(down) is BlockRail) return new Rail(block, world, down);

                return null;
            }

            private bool IsRailAt(BlockPos at) => world.BlockAccessor.GetBlock(at) is BlockRail;

            /// <summary>Connected if not already joined on both ends, or already joined to that cell.</summary>
            private bool CanConnectTo(BlockPos other) => IsConnectedTo(other) || connected.Count != 2;

            /// <summary>True when both ends join to a rail that joins back — i.e. a finished rail that
            /// should never be reshaped just because something was placed next to it.</summary>
            private bool IsFullyConnected()
            {
                if (connected.Count != 2) return false;
                for (int i = 0; i < connected.Count; i++)
                {
                    BlockPos c = connected[i];
                    // Require a rail at the EXACT expected cell (height included) that points back at
                    // exactly this cell. An X/Z-only match would treat a rail one block up as connected,
                    // which would wrongly lock a flat rail that should still tilt up into a slope.
                    if (!(world.BlockAccessor.GetBlock(c) is BlockRail)) return false;
                    if (!new Rail(block, world, c).ContainsExact(pos)) return false;
                }
                return true;
            }

            /// <summary>True if one of this rail's two ends is exactly at <paramref name="other"/>
            /// (full X/Y/Z match), used only by the "finished rail" lock.</summary>
            private bool ContainsExact(BlockPos other)
            {
                for (int i = 0; i < connected.Count; i++)
                {
                    BlockPos c = connected[i];
                    if (c.X == other.X && c.Y == other.Y && c.Z == other.Z) return true;
                }
                return false;
            }

            /// <summary>Compares X/Z only so an ascending connection (stored at .up()) still matches.</summary>
            private bool IsConnectedTo(BlockPos other)
            {
                for (int i = 0; i < connected.Count; i++)
                {
                    BlockPos c = connected[i];
                    if (c.X == other.X && c.Z == other.Z) return true;
                }
                return false;
            }

            /// <summary>Drop connections that no longer point at a rail that points back at us.</summary>
            private void RemoveSoftConnections()
            {
                for (int i = 0; i < connected.Count; i++)
                {
                    Rail r = FindRailAt(connected[i]);
                    if (r != null && r.IsConnectedTo(pos))
                    {
                        connected[i] = r.pos;
                    }
                    else
                    {
                        connected.RemoveAt(i--);
                    }
                }
            }

            // ---- static tables ---------------------------------------------------------------

            /// <summary>The two cells a given shape connects to (ascending ends are stored one block up).</summary>
            private static List<BlockPos> ConnectionsFor(string type, BlockPos pos)
            {
                var list = new List<BlockPos>(2);
                if (type == null) return list;

                switch (type)
                {
                    case FlatNS:
                        list.Add(pos.AddCopy(BlockFacing.NORTH));
                        list.Add(pos.AddCopy(BlockFacing.SOUTH));
                        break;
                    case FlatWE:
                        list.Add(pos.AddCopy(BlockFacing.WEST));
                        list.Add(pos.AddCopy(BlockFacing.EAST));
                        break;
                    case "curved_es":
                        list.Add(pos.AddCopy(BlockFacing.EAST));
                        list.Add(pos.AddCopy(BlockFacing.SOUTH));
                        break;
                    case "curved_sw":
                        list.Add(pos.AddCopy(BlockFacing.WEST));
                        list.Add(pos.AddCopy(BlockFacing.SOUTH));
                        break;
                    case "curved_wn":
                        list.Add(pos.AddCopy(BlockFacing.WEST));
                        list.Add(pos.AddCopy(BlockFacing.NORTH));
                        break;
                    case "curved_ne":
                        list.Add(pos.AddCopy(BlockFacing.EAST));
                        list.Add(pos.AddCopy(BlockFacing.NORTH));
                        break;
                    case "raised_n":
                        list.Add(pos.AddCopy(BlockFacing.NORTH).Up());
                        list.Add(pos.AddCopy(BlockFacing.SOUTH));
                        break;
                    case "raised_s":
                        list.Add(pos.AddCopy(BlockFacing.SOUTH).Up());
                        list.Add(pos.AddCopy(BlockFacing.NORTH));
                        break;
                    case "raised_e":
                        list.Add(pos.AddCopy(BlockFacing.EAST).Up());
                        list.Add(pos.AddCopy(BlockFacing.WEST));
                        break;
                    case "raised_w":
                        list.Add(pos.AddCopy(BlockFacing.WEST).Up());
                        list.Add(pos.AddCopy(BlockFacing.EAST));
                        break;
                }
                return list;
            }

            /// <summary>Orientation to keep when a rail loses all neighbours (so it doesn't flip axis).</summary>
            private static BlockFacing AxisFallback(string type)
            {
                switch (type)
                {
                    case FlatWE:
                    case "raised_e":
                    case "raised_w":
                        return BlockFacing.EAST;   // X axis -> flat_we
                    default:
                        return BlockFacing.NORTH;  // Z axis -> flat_ns
                }
            }
        }
    }
}
