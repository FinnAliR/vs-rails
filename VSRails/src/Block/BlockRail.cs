using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VSRails;

/// <summary>
/// The rail anchor block - this is the cell the player actually places
/// and clicks. Footprint is 2x2 blocks, 4px (0.25m) tall. Only THIS cell
/// holds a BlockEntityRail; the other 3 cells get a BlockRailFiller
/// placed automatically so the whole 2x2 area is solid and clickable.
///
/// Facing auto-snaps to the player's look direction on placement.
/// Shift + right-click rotates the facing afterward.
/// </summary>
public class BlockRail : Block
{
    /// <summary>
    /// Code of the filler block to place in the other 3 cells.
    /// Set in Block.json via "attributes.fillerBlockCode".
    /// </summary>
    public AssetLocation FillerBlockCode;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        string fillerCode = Attributes?["fillerBlockCode"]?.AsString();
        if (!string.IsNullOrEmpty(fillerCode))
        {
            // Use the two-arg constructor directly rather than a static
            // helper, since helper availability can vary across API
            // versions - this constructor form has been stable.
            FillerBlockCode = new AssetLocation(Code.Domain, fillerCode);
        }
    }

    public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
    {
        BlockPos anchorPos = blockSel.Position;

        // Determine facing from the player's look direction, snapped to
        // one of the 4 cardinal directions - rails only care about
        // horizontal travel direction, not pitch.
        var look = byPlayer.Entity.SidedPos.GetViewVector();
        BlockFacing facing = BlockFacing.FromVector(look.X, 0, look.Z);
        if (facing == null || facing.Axis == EnumAxis.Y) facing = BlockFacing.NORTH;

        BlockPos[] footprintOffsets = GetFootprintOffsets(facing);

        // Verify all 3 filler cells are clear/replaceable before
        // committing to placement.
        foreach (var offset in footprintOffsets)
        {
            BlockPos checkPos = anchorPos.AddCopy(offset.X, offset.Y, offset.Z);
            Vintagestory.API.Common.Block existing = world.BlockAccessor.GetBlock(checkPos);
            if (!existing.IsReplacableBy(this))
            {
                failureCode = "notenoughspace";
                return false;
            }
        }

        // Place the anchor itself.
        world.BlockAccessor.SetBlock(this.BlockId, anchorPos);
        var be = world.BlockAccessor.GetBlockEntity(anchorPos) as BlockEntityRail;
        if (be != null)
        {
            be.Facing = facing;
            be.RailShape = "straight";
            be.FillerOffsets.Clear();
        }

        // Place fillers and record their relative offsets on the anchor.
        Vintagestory.API.Common.Block fillerBlock = FillerBlockCode != null ? world.GetBlock(FillerBlockCode) : null;
        if (fillerBlock != null)
        {
            foreach (var offset in footprintOffsets)
            {
                BlockPos fillerPos = anchorPos.AddCopy(offset.X, offset.Y, offset.Z);
                world.BlockAccessor.SetBlock(fillerBlock.BlockId, fillerPos);
                be?.FillerOffsets.Add(offset.Copy());
            }
            be?.MarkDirty(true);
        }

        return true;
    }

    /// <summary>
    /// Shift + right-click rotates the rail 90 degrees clockwise.
    /// Plain right-click is reserved for future cart interactions
    /// (coupling/uncoupling) and currently falls through to base.
    /// </summary>
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (!byPlayer.Entity.Controls.ShiftKey)
        {
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

        BlockPos anchorPos = blockSel.Position;
        var be = world.BlockAccessor.GetBlockEntity(anchorPos) as BlockEntityRail;
        if (be == null) return false;

        BlockFacing newFacing = be.Facing.GetCW(); // rotate 90 deg clockwise

        BlockPos[] oldOffsets = be.FillerOffsets.ToArray();
        BlockPos[] newOffsets = GetFootprintOffsets(newFacing);

        // Validate clearance for the new footprint, ignoring cells that
        // are already this rail's own current fillers (those are fine
        // to keep occupying, or to vacate and re-place).
        foreach (var offset in newOffsets)
        {
            bool isOwnExistingFiller = false;
            foreach (var old in oldOffsets)
            {
                if (old.Equals(offset)) { isOwnExistingFiller = true; break; }
            }
            if (isOwnExistingFiller) continue;

            BlockPos checkPos = anchorPos.AddCopy(offset.X, offset.Y, offset.Z);
            Vintagestory.API.Common.Block existing = world.BlockAccessor.GetBlock(checkPos);
            if (!existing.IsReplacableBy(this))
            {
                // Can't rotate into occupied space - fail silently,
                // leaving the rail as it was.
                return true;
            }
        }

        // Clear all old filler cells.
        foreach (var old in oldOffsets)
        {
            world.BlockAccessor.SetBlock(0, anchorPos.AddCopy(old.X, old.Y, old.Z));
        }

        be.FillerOffsets.Clear();
        be.Facing = newFacing;

        // Place new fillers.
        Vintagestory.API.Common.Block fillerBlock = FillerBlockCode != null ? world.GetBlock(FillerBlockCode) : null;
        if (fillerBlock != null)
        {
            foreach (var offset in newOffsets)
            {
                world.BlockAccessor.SetBlock(fillerBlock.BlockId, anchorPos.AddCopy(offset.X, offset.Y, offset.Z));
                be.FillerOffsets.Add(offset.Copy());
            }
        }

        be.MarkDirty(true);
        return true;
    }

    /// <summary>
    /// When the anchor itself is removed (broken directly, or via a
    /// filler's break redirect), clean up all 3 filler cells so nothing
    /// is left behind as orphaned solid blocks.
    /// </summary>
    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        var be = world.BlockAccessor.GetBlockEntity(pos) as BlockEntityRail;
        if (be != null)
        {
            foreach (var fillerPos in be.GetFillerWorldPositions())
            {
                Vintagestory.API.Common.Block fillerAt = world.BlockAccessor.GetBlock(fillerPos);
                if (fillerAt is BlockRailFiller)
                {
                    world.BlockAccessor.SetBlock(0, fillerPos);
                }
            }
        }
        base.OnBlockRemoved(world, pos);
    }

    /// <summary>
    /// Relative cell offsets (excluding the anchor itself, which is
    /// implicitly 0,0,0) covering a 2x2 footprint. "facing" is the
    /// direction of travel; the footprint extends one cell along facing
    /// and one cell along facing's clockwise perpendicular (the gauge/
    /// width side), plus the diagonal corner cell.
    /// </summary>
    private BlockPos[] GetFootprintOffsets(BlockFacing facing)
    {
        BlockFacing widthDir = facing.GetCW();

        return new BlockPos[]
        {
            new BlockPos(widthDir.Normali.X, 0, widthDir.Normali.Z),
            new BlockPos(facing.Normali.X, 0, facing.Normali.Z),
            new BlockPos(facing.Normali.X + widthDir.Normali.X, 0, facing.Normali.Z + widthDir.Normali.Z),
        };
    }
}
