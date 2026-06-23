using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VSRails
{
    /// <summary>
    /// Handles smart rail placement: straight by default, auto-curves when placed adjacent to existing rails.
    /// Mirrors vanilla BlockRails (Vintagestory.GameContent) placement logic, adapted to this mod's
    /// "type" variantgroup states: flat_ns, flat_we (straight) | curved_es, curved_sw, curved_wn, curved_ne (curves) | raised_ns, raised_we (raised straight). Uses CodeWithParts, matching vanilla, instead
    /// CodeWithParts does direct code-segment substitution and does not depend on the variantgroup dictionary resolving "type" at runtime.
    /// </summary>
    public class BlockRail : Block
    {
        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
        {
            if (!CanPlaceBlock(world, byPlayer, blockSel, ref failureCode))
                return false;

            // 1. PURE neighbor-driven logic first (no player input involved)
            for (int i = 0; i < BlockFacing.HORIZONTALS.Length; i++)
            {
                BlockFacing facing = BlockFacing.HORIZONTALS[i];

                if (TryAttachPlaceToHorizontal(world, byPlayer, blockSel.Position, facing))
                    return true;
            }

            if (TryAttachSlope(world, byPlayer, itemstack, blockSel.Position))
                return true;

            TryAttachSlopeUpdateNeighbor(world, byPlayer, blockSel.Position);

            // 2. ONLY NOW use player direction
            BlockFacing playerFacing = SuggestedHVOrientation(byPlayer, blockSel)[0];

            Block blockToPlace =
                playerFacing.Axis == EnumAxis.Z
                    ? world.GetBlock(CodeWithParts("flat_ns"))
                    : world.GetBlock(CodeWithParts("flat_we"));

            if (blockToPlace == null)
            {
                failureCode = "missingblock";
                return false;
            }

            blockToPlace.DoPlaceBlock(world, byPlayer, blockSel, itemstack);
            return true;
        }

        /// <summary>
        /// Checks each horizontal direction for a rail one block up or one block down from
        /// position. If found one block up in direction D, places a raised_D slope here (high
        /// end faces D, toward the elevated neighbor). If found one block down in direction D,
        /// places a raised_(opposite of D) slope here (this is the upper end, sloping down
        /// toward D). Only fires when no same-plane neighbor was found.
        /// </summary>
        private bool TryAttachSlope(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockPos position)
        {
            for (int i = 0; i < BlockFacing.HORIZONTALS.Length; i++)
            {
                BlockFacing dir = BlockFacing.HORIZONTALS[i];

                BlockPos upPos = position.AddCopy(dir).Up();
                if (world.BlockAccessor.GetBlock(upPos) is BlockRail)
                {
                    Block slope = world.GetBlock(CodeWithParts("raised_" + dir.Code[0]));
                    if (slope != null)
                    {
                        slope.DoPlaceBlock(world, byPlayer,
                            new BlockSelection { Position = position, Face = BlockFacing.UP },
                            itemstack);

                        return true;
                    }
                }
            }

            return false;
        }
        
        /// <summary>
        /// Checks each horizontal direction for an existing flat rail one block down and one
        /// block back from position. If found in direction D, that flat rail's open end faces
        /// toward this position — convert it from flat to raised_D, climbing up to meet the
        /// new block here. Does not affect placement at `position` itself.
        /// </summary>
        private void TryAttachSlopeUpdateNeighbor(IWorldAccessor world, IPlayer byPlayer, BlockPos position)
        {
            for (int i = 0; i < BlockFacing.HORIZONTALS.Length; i++)
            {
                BlockFacing dir = BlockFacing.HORIZONTALS[i];

                BlockPos belowPos = position.AddCopy(dir.Opposite).Down();
                Block neibBlock = world.BlockAccessor.GetBlock(belowPos);
                if (neibBlock is not BlockRail) continue;

                string neibType = neibBlock.Variant["type"];
                bool isNS = dir.Axis == EnumAxis.Z;
                if (isNS && neibType != "flat_ns") continue;
                if (!isNS && neibType != "flat_we") continue;

                Block slope = world.GetBlock(CodeWithParts("raised_" + dir.Code[0]));
                if (slope == null) continue;

                slope.DoPlaceBlock(world, byPlayer,
                    new BlockSelection { Position = belowPos, Face = BlockFacing.UP },
                    null);

                return;
            }
        }

        /// <summary>
        /// Attempt to place a rail at position that curves to connect with a neighboring rail at toFacing.
        /// May also bend the neighbor rail to form a smooth connection.
        /// </summary>
        private bool TryAttachPlaceToHorizontal(IWorldAccessor world, IPlayer byPlayer, BlockPos position, BlockFacing toFacing)
        {
            BlockPos neibPos = position.AddCopy(toFacing);
            Block neibBlock = world.BlockAccessor.GetBlock(neibPos);
            if (neibBlock is not BlockRail) return false;

            // Sloped rails are derived-only and never curve or get bent into curves.
            string neibType = neibBlock.Variant["type"];
            if (neibType?.StartsWith("raised_") == true) return false;

            BlockFacing fromFacing = toFacing.Opposite;
            BlockFacing[] neibDirFacings = GetFacingsFromType(neibType);
            if (neibDirFacings == null || neibDirFacings[0] == null || neibDirFacings[1] == null) return false;

            // Already attached on both ends, do default placement behavior
            if (world.BlockAccessor.GetBlock(neibPos.AddCopy(neibDirFacings[0])) is BlockRail &&
                world.BlockAccessor.GetBlock(neibPos.AddCopy(neibDirFacings[1])) is BlockRail)
            {
                return false;
            }

            BlockFacing neibFreeFace = GetOpenEndedFace(neibDirFacings, world, position.AddCopy(toFacing));
            // Already fully attached, don't bend rail
            if (neibFreeFace == null) return false;

            Block blockToPlace = GetRailBlock(world, "curved_", toFacing, fromFacing);

            if (blockToPlace != null)
            {
                if (!PlaceIfSuitable(world, byPlayer, blockToPlace, position))
                {
                    return false;
                }
                return true;
            }

            string dirs = neibType.Split('_')[1];
            BlockFacing neibKeepFace = (dirs[0] == neibFreeFace.Code[0]) ? BlockFacing.FromFirstLetter(dirs[1]) : BlockFacing.FromFirstLetter(dirs[0]);
            Block block = GetRailBlock(world, "curved_", neibKeepFace, fromFacing);
            if (block == null) return false;

            block.DoPlaceBlock(world, byPlayer, new BlockSelection { Position = position.AddCopy(toFacing), Face = BlockFacing.UP }, null);

            return false;
        }

        private bool PlaceIfSuitable(IWorldAccessor world, IPlayer byPlayer, Block block, BlockPos pos)
        {
            string failureCode = "";
            BlockSelection blockSel = new BlockSelection { Position = pos, Face = BlockFacing.UP };
            if (block.CanPlaceBlock(world, byPlayer, blockSel, ref failureCode))
            {
                block.DoPlaceBlock(world, byPlayer, blockSel, null);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Look up a curved rail variant connecting two facings (tries both letter orderings),
        /// using CodeWithParts to match vanilla's direct code-segment substitution.
        /// </summary>
        private Block GetRailBlock(IWorldAccessor world, string prefix, BlockFacing dir0, BlockFacing dir1)
        {
            Block block = world.GetBlock(CodeWithParts(prefix + dir0.Code[0] + dir1.Code[0]));
            if (block != null) return block;

            return world.GetBlock(CodeWithParts(prefix + dir1.Code[0] + dir0.Code[0]));
        }

        private BlockFacing GetOpenEndedFace(BlockFacing[] dirFacings, IWorldAccessor world, BlockPos blockPos)
        {
            Block block = world.BlockAccessor.GetBlock(blockPos.AddCopy(dirFacings[0]));
            if (block is not BlockRail) return dirFacings[0];

            block = world.BlockAccessor.GetBlock(blockPos.AddCopy(dirFacings[1]));
            if (block is not BlockRail) return dirFacings[1];

            return null;
        }

        /// <summary>
        /// Extract the two BlockFacings implied by a "type" variant state, e.g. "flat_ns" -> N+S,
        /// "curved_es" -> E+S. Splits on '_' and reads the two letters, matching vanilla's approach
        /// (works uniformly for flat_*, raised_*, and curved_* without needing separate cases).
        /// </summary>
        private static BlockFacing[] GetFacingsFromType(string type)
        {
            if (string.IsNullOrEmpty(type)) return null;

            string[] parts = type.Split('_');
            if (parts.Length < 2 || parts[1].Length < 2) return null;

            string codes = parts[1];
            return new[] { BlockFacing.FromFirstLetter(codes[0]), BlockFacing.FromFirstLetter(codes[1]) };
        }
    }
}