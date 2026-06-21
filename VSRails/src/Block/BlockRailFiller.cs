using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VSRails;

/// <summary>
/// Placed automatically in the 3 non-anchor cells of a 2x2 rail footprint.
/// Has its own real collision box (matching the anchor's shape, evaluated
/// at its own position) so the whole 2x2 footprint is seamlessly solid -
/// no cell is left as a hole. Interaction (break / right-click) is
/// redirected back to the anchor block, which owns all rail data.
/// </summary>
public class BlockRailFiller : Block
{
    /// <summary>
    /// Finds the anchor block for this filler cell by checking the 3
    /// candidate anchor positions that could place a 2x2 footprint
    /// containing this cell (anchor is always up-left/up-right/down-left/
    /// down-right of a filler depending on facing).
    /// </summary>
    public BlockPos FindAnchorPos(IWorldAccessor world, BlockPos fillerPos)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                BlockPos candidate = fillerPos.AddCopy(dx, 0, dz);
                var be = world.BlockAccessor.GetBlockEntity(candidate) as BlockEntityRail;
                if (be == null) continue;

                foreach (var fp in be.GetFillerWorldPositions())
                {
                    if (fp.Equals(fillerPos)) return candidate;
                }
            }
        }
        return null;
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        BlockPos anchorPos = FindAnchorPos(world, blockSel.Position);
        if (anchorPos != null)
        {
            Vintagestory.API.Common.Block anchorBlock = world.BlockAccessor.GetBlock(anchorPos);
            BlockSelection redirected = blockSel.Clone();
            redirected.Position = anchorPos;
            return anchorBlock.OnBlockInteractStart(world, byPlayer, redirected);
        }
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f)
    {
        BlockPos anchorPos = FindAnchorPos(world, pos);
        if (anchorPos != null && !anchorPos.Equals(pos))
        {
            // Removing any filler removes the whole rail. Explicitly
            // invoke the anchor's own removal cleanup (clears its other
            // fillers) before clearing the anchor cell itself - SetBlock
            // alone isn't guaranteed to chain into OnBlockRemoved on every
            // call path, so we drive it ourselves to be safe.
            Vintagestory.API.Common.Block anchorBlock = world.BlockAccessor.GetBlock(anchorPos);
            anchorBlock?.OnBlockRemoved(world, anchorPos);
            world.BlockAccessor.SetBlock(0, anchorPos);
            return;
        }
        base.OnBlockBroken(world, pos, byPlayer, dropQuantityMultiplier);
    }
}
