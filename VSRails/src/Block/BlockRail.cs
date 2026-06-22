using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VSRails
{
    /// <summary>
    /// Handles smart rail placement: straight by default, auto-curves when placed adjacent to existing rails.
    /// Mirrors vanilla BlockRails (Vintagestory.GameContent) placement logic, adapted to this mod's
    /// "type" variantgroup states: flat_ns, flat_we (straight) | curved_es, curved_sw, curved_wn, curved_ne
    /// (curves) | raised_ns, raised_we (raised straight). Uses CodeWithParts, matching vanilla, instead
    /// of CodeWithVariant, since CodeWithParts does direct code-segment substitution and does not depend
    /// on the variantgroup dictionary resolving "type" at runtime.
    /// </summary>
    public class BlockRail : Block
    {
        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
        {
            if (!CanPlaceBlock(world, byPlayer, blockSel, ref failureCode))
                return false;

            // Place by looking direction
            BlockFacing targetFacing = SuggestedHVOrientation(byPlayer, blockSel)[0];
            Block blockToPlace = null;

            // 1. Same-plane neighbor attachment (existing curve logic) takes priority.
            for (int i = 0; i < BlockFacing.HORIZONTALS.Length; i++)
            {
                BlockFacing facing = BlockFacing.HORIZONTALS[i];
                if (TryAttachPlaceToHorizontal(world, byPlayer, blockSel.Position, facing, targetFacing))
                {
                    return true;
                }
            }

            // 2. No same-plane neighbor found — check one block up/down in each horizontal
            // direction for a rail to slope toward. Only ever reachable through placement,
            // never held directly, same as curves.
            if (TryAttachSlope(world, byPlayer, itemstack, blockSel.Position))
            {
                return true;
            }

            // 3. Fall back to a flat straight rail aligned to look direction.
            if (blockToPlace == null)
            {
                if (targetFacing.Axis == EnumAxis.Z)
                {
                    blockToPlace = world.GetBlock(CodeWithParts("flat_ns"));
                }
                else
                {
                    blockToPlace = world.GetBlock(CodeWithParts("flat_we"));
                }
            }

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
            bool hasHorizontal = false;

            // STEP 1: HARD PRIORITY — detect any horizontal rail first
            for (int i = 0; i < BlockFacing.HORIZONTALS.Length; i++)
            {
                BlockFacing dir = BlockFacing.HORIZONTALS[i];
                BlockPos pos = position.AddCopy(dir);

                if (world.BlockAccessor.GetBlock(pos) is BlockRail)
                {
                    hasHorizontal = true;
                    break;
                }
            }

            // STEP 2: check for vertical relationships (only if no horizontal rails)
            bool hasUpperRail = false;
            BlockFacing upperDir = null;

            for (int i = 0; i < BlockFacing.HORIZONTALS.Length; i++)
            {
                BlockFacing dir = BlockFacing.HORIZONTALS[i];

                BlockPos upPos = position.AddCopy(dir).Up();
                if (world.BlockAccessor.GetBlock(upPos) is BlockRail)
                {
                    hasUpperRail = true;
                    upperDir = dir;
                    break;
                }
            }

            // STEP 3: ONLY slope if upper rail exists
            if (hasUpperRail && upperDir != null)
            {
                Block slope = world.GetBlock(CodeWithParts("raised_" + upperDir.Code[0]));
                if (slope != null)
                {
                    slope.DoPlaceBlock(world, byPlayer,
                        new BlockSelection { Position = position, Face = BlockFacing.UP },
                        itemstack);

                    return true;
                }
            }

            // STEP 4: IMPORTANT RULE — lower rail does NOTHING for slope decision
            // It is intentionally ignored here.
            return false;
        }

        /// <summary>
        /// Attempt to place a rail at position that curves to connect with a neighboring rail at toFacing.
        /// May also bend the neighbor rail to form a smooth connection.
        /// </summary>
        private bool TryAttachPlaceToHorizontal(IWorldAccessor world, IPlayer byPlayer, BlockPos position, BlockFacing toFacing, BlockFacing targetFacing)
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

            Block blockToPlace = GetRailBlock(world, "curved_", toFacing, targetFacing);

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