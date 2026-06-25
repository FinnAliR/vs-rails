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
    /// bend a connectable neighbour to meet a new rail.
    ///
    /// Update model (faithful to <c>BlockRailBase.Rail.place</c>): shapes change ONLY when a rail is
    /// placed. The freshly placed rail picks its own shape by scanning connectable neighbours
    /// (<see cref="Rail.ComputeShape"/>), then runs the same cascade vanilla does at the end of
    /// <c>place()</c> — it asks each of the (up to two) rails it actually connects to, to bend toward
    /// it via <see cref="Rail.ConnectTo"/>. <c>ConnectTo</c> recomputes a neighbour's shape from that
    /// neighbour's OWN committed connection list (not a fresh world scan), so finished track running
    /// alongside is never reshaped, and a flat rail that should climb becomes a slope rather than a
    /// flat corner. Like vanilla, a settled regular rail does NOT re-evaluate itself just because an
    /// adjacent block changed; it only drops when it loses support (handled by the Unstable behavior).
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

        // Right-clicking a rail with a wrench cycles it through the rail shapes, so players can force a
        // specific shape (a corner or a slope) instead of relying on the auto-shaping at placement.
        private static readonly string[] WrenchCycle =
        {
            "flat_ns", "flat_we",
            "curved_ne", "curved_es", "curved_sw", "curved_wn",
            "raised_n", "raised_e", "raised_s", "raised_w"
        };

        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            ItemStack held = byPlayer?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            if (held?.Collectible?.Code?.Path?.Contains("wrench") == true)
            {
                if (world.Side == EnumAppSide.Server)
                {
                    int idx = System.Array.IndexOf(WrenchCycle, Variant["type"]);
                    string next = WrenchCycle[(idx + 1) % WrenchCycle.Length];   // idx -1 -> flat_ns; wraps round
                    Block nb = ResolveRail(world, next);
                    if (nb != null) world.BlockAccessor.ExchangeBlock(nb.Id, blockSel.Position);
                }
                return true;   // consume the interaction so the wrench does nothing else
            }
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

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

            // Mirror the final loop of BlockRailBase.Rail.place(): tell each rail we actually connect
            // to, to bend toward us via ConnectTo. We deliberately do NOT rescan unrelated rails, so
            // a finished slope or line merely running past is never reshaped just for being adjacent.
            if (world.Side.IsServer())
                new Rail(this, world, pos).ConnectNeighbours();

            return true;
        }

        // ---------------------------------------------------------------------------------------
        // Updating  (mirrors BlockRailBase.neighborChanged)
        // ---------------------------------------------------------------------------------------

        // Vanilla regular rails do NOT recompute their shape when an adjacent block changes — that is
        // why a finished slope/curve keeps its shape and never silently re-curves. They only need to
        // drop when they lose the solid block beneath them, which the Unstable behavior already does
        // via the base handler. So there is intentionally no shape re-evaluation here; all reshaping
        // happens through the placement cascade (TryPlaceBlock -> ConnectNeighbours -> ConnectTo).

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

            /// <summary>Mirror the cascade at the end of <c>BlockRailBase.Rail.place()</c>: for each
            /// cell this rail connects to, find the rail there (incl. one up/down for slopes), prune
            /// its stale connections, and—if it can still take another connection—ask it to bend
            /// toward us via <see cref="ConnectTo"/>. This is the ONLY path that reshapes neighbours.</summary>
            public void ConnectNeighbours()
            {
                if (type == null) return;
                for (int i = 0; i < connected.Count; i++)
                {
                    Rail r = FindRailAt(connected[i]);
                    if (r == null) continue;
                    r.RemoveSoftConnections();
                    if (r.CanConnectTo(pos)) r.ConnectTo(this);
                }
            }

            /// <summary>Port of <c>BlockRailBase.Rail.connectTo</c> (func_150645_c). Adds
            /// <paramref name="other"/> as a connection, then re-derives this rail's shape from its OWN
            /// connection list (X/Z matches via <see cref="IsConnectedTo"/>) rather than a fresh world
            /// scan. Because the decision is driven by committed connections, a straight run that
            /// should climb resolves to a slope instead of being pulled into a flat corner.</summary>
            private void ConnectTo(Rail other)
            {
                if (type == null) return;
                connected.Add(other.pos);

                bool n = IsConnectedTo(pos.AddCopy(BlockFacing.NORTH));
                bool s = IsConnectedTo(pos.AddCopy(BlockFacing.SOUTH));
                bool w = IsConnectedTo(pos.AddCopy(BlockFacing.WEST));
                bool e = IsConnectedTo(pos.AddCopy(BlockFacing.EAST));

                string t = null;
                if (n || s) t = FlatNS;
                if (w || e) t = FlatWE;

                if (s && e && !n && !w) t = "curved_es";
                if (s && w && !n && !e) t = "curved_sw";
                if (n && w && !s && !e) t = "curved_wn";
                if (n && e && !s && !w) t = "curved_ne";

                // straight rails climb toward a rail sitting one block up (vanilla ASCENDING_*)
                if (t == FlatNS)
                {
                    if (IsRailAt(pos.AddCopy(BlockFacing.NORTH).Up())) t = "raised_n";
                    if (IsRailAt(pos.AddCopy(BlockFacing.SOUTH).Up())) t = "raised_s";
                }
                else if (t == FlatWE)
                {
                    if (IsRailAt(pos.AddCopy(BlockFacing.EAST).Up())) t = "raised_e";
                    if (IsRailAt(pos.AddCopy(BlockFacing.WEST).Up())) t = "raised_w";
                }

                if (t == null) t = FlatNS;
                if (t == type) return;

                Block nb = block.ResolveRail(world, t);
                if (nb == null) return;

                world.BlockAccessor.ExchangeBlock(nb.Id, pos);
                type = t;
                connected = ConnectionsFor(t, pos);
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
        }
    }
}
