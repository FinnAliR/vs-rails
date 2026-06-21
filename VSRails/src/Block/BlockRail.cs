using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VSRails
{
    /// <summary>
    /// Handles smart rail placement: straight by default, auto-curves when placed adjacent to existing rails.
    /// Rail variant codes: ns, ew (straight) | ne, nw, se, sw (curves)
    /// </summary>
    public class BlockRail : Block
    {
        private const string ModId = "vsrails";

        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
        {
            if (!CanPlaceBlock(world, byPlayer, blockSel, ref failureCode))
                return false;

            BlockFacing targetFacing = SuggestedHVOrientation(byPlayer, blockSel)[0];

            // Try smart attachment to each adjacent rail
            foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
            {
                if (TryAttachToNeighbor(world, byPlayer, itemstack, blockSel, facing, targetFacing))
                    return true;
            }

            // Default: straight rail aligned to player look direction
            string type = targetFacing.Axis == EnumAxis.Z ? "ns" : "ew";
            Block blockToPlace = GetRailVariant(world, type);
            if (blockToPlace == null)
            {
                failureCode = "missingblock";
                return false;
            }

            blockToPlace.DoPlaceBlock(world, byPlayer, blockSel, itemstack);
            return true;
        }

        /// <summary>
        /// Attempt to place a rail at blockSel that curves to connect with a neighboring rail at toFacing.
        /// May also bend the neighbor rail to form a smooth connection.
        /// </summary>
        private bool TryAttachToNeighbor(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack,
            BlockSelection blockSel, BlockFacing toFacing, BlockFacing targetFacing)
        {
            BlockPos neibPos = blockSel.Position.AddCopy(toFacing);
            Block neibBlock = world.BlockAccessor.GetBlock(neibPos);
            if (neibBlock is not BlockRail) return false;

            string neibType = neibBlock.Variant["type"];
            if (string.IsNullOrEmpty(neibType) || neibType.Length < 2) return false;

            BlockFacing[] neibFacings = GetFacingsFromType(neibType);
            if (neibFacings[0] == null || neibFacings[1] == null) return false;

            // Both ends already connected — don't modify
            if (world.BlockAccessor.GetBlock(neibPos.AddCopy(neibFacings[0])) is BlockRail &&
                world.BlockAccessor.GetBlock(neibPos.AddCopy(neibFacings[1])) is BlockRail)
                return false;

            BlockFacing fromFacing = toFacing.Opposite;
            BlockFacing neibFreeFace = GetOpenEndFacing(neibFacings, world, neibPos);
            if (neibFreeFace == null) return false;

            // Try placing a curve at the current position connecting back to the neighbor
            Block curveHere = GetRailByFacings(world, fromFacing, targetFacing);
            if (curveHere != null && PlaceIfFree(world, byPlayer, curveHere, blockSel.Position, itemstack))
                return true;

            // Bend the neighbor rail to curve toward our placement position
            BlockFacing neibKeepFace = neibFacings[0] == neibFreeFace ? neibFacings[1] : neibFacings[0];
            Block bentNeib = GetRailByFacings(world, neibKeepFace, fromFacing);
            if (bentNeib == null) return false;

            bentNeib.DoPlaceBlock(world, byPlayer,
                new BlockSelection { Position = neibPos, Face = BlockFacing.UP }, null);

            return false;
        }

        private bool PlaceIfFree(IWorldAccessor world, IPlayer byPlayer, Block block, BlockPos pos, ItemStack itemstack)
        {
            string fc = "";
            var sel = new BlockSelection { Position = pos, Face = BlockFacing.UP };
            if (!block.CanPlaceBlock(world, byPlayer, sel, ref fc)) return false;
            block.DoPlaceBlock(world, byPlayer, sel, itemstack);
            return true;
        }

        /// <summary>Get a rail variant that connects two given directions (tries both orderings).</summary>
        private Block GetRailByFacings(IWorldAccessor world, BlockFacing a, BlockFacing b)
        {
            char ca = a.Code[0], cb = b.Code[0];
            return GetRailVariant(world, "" + ca + cb)
                ?? GetRailVariant(world, "" + cb + ca);
        }

        /// <summary>Look up a rail block by its 2-letter type code (ns/ew/ne/nw/se/sw).</summary>
        private static Block GetRailVariant(IWorldAccessor world, string type)
            => world.GetBlock(new AssetLocation(ModId, "rail-" + type));

        private static BlockFacing GetOpenEndFacing(BlockFacing[] facings, IWorldAccessor world, BlockPos pos)
        {
            if (world.BlockAccessor.GetBlock(pos.AddCopy(facings[0])) is not BlockRail) return facings[0];
            if (world.BlockAccessor.GetBlock(pos.AddCopy(facings[1])) is not BlockRail) return facings[1];
            return null;
        }

        private static BlockFacing[] GetFacingsFromType(string type)
        {
            // type is "ns", "ew", "ne", "nw", "se", or "sw" — just two letters
            return new[] { FacingFromLetter(type[0]), FacingFromLetter(type[1]) };
        }

        private static BlockFacing FacingFromLetter(char c) => c switch
        {
            'n' => BlockFacing.NORTH,
            's' => BlockFacing.SOUTH,
            'e' => BlockFacing.EAST,
            'w' => BlockFacing.WEST,
            _ => null
        };
    }
}
