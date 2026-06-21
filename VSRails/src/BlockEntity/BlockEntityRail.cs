using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Datastructures;

namespace VSRails;

/// <summary>
/// Lives on the ANCHOR cell of a 2x2 rail multiblock.
/// Stores facing + which neighbor cells are occupied by filler blocks,
/// so we can clean them up on break and so cart-follow code (later) can
/// read "what direction does this rail point" from a single lookup.
/// </summary>
public class BlockEntityRail : BlockEntity
{
    /// <summary>
    /// Facing the rail points along its long axis (the direction a cart
    /// travels when entering/leaving this rail segment). North/East/South/West.
    /// </summary>
    public BlockFacing Facing = BlockFacing.NORTH;

    /// <summary>
    /// Shape variant - "straight" for now, "curve"/"slope" later.
    /// Mirrors the block's own Variant but kept here too so cart-follow
    /// code only ever needs to touch the BlockEntity, not re-derive from Block.
    /// </summary>
    public string RailShape = "straight";

    /// <summary>
    /// World-relative offsets of the 3 filler cells that make up the
    /// other 3 corners of this rail's 2x2 footprint. Anchor itself is (0,0,0).
    /// </summary>
    public List<BlockPos> FillerOffsets = new List<BlockPos>();

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetInt("railFacing", Facing.Index);
        tree.SetString("railShape", RailShape);

        // Serialize filler offsets as flat int array: x,y,z,x,y,z...
        // ITreeAttribute has no dedicated SetIntArray/GetIntArray helper -
        // the real pattern is to assign a concrete IntArrayAttribute
        // through the indexer, which is how the base game stores arrays.
        int[] flat = new int[FillerOffsets.Count * 3];
        for (int i = 0; i < FillerOffsets.Count; i++)
        {
            flat[i * 3] = FillerOffsets[i].X;
            flat[i * 3 + 1] = FillerOffsets[i].Y;
            flat[i * 3 + 2] = FillerOffsets[i].Z;
        }
        tree["railFillerOffsets"] = new IntArrayAttribute(flat);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        int facingIndex = tree.GetInt("railFacing", BlockFacing.NORTH.Index);
        Facing = BlockFacing.ALLFACES[facingIndex];
        RailShape = tree.GetString("railShape", "straight");

        int[] flat = (tree["railFillerOffsets"] as IntArrayAttribute)?.value ?? Array.Empty<int>();
        FillerOffsets = new List<BlockPos>();
        for (int i = 0; i + 2 < flat.Length; i += 3)
        {
            FillerOffsets.Add(new BlockPos(flat[i], flat[i + 1], flat[i + 2]));
        }
    }

    /// <summary>
    /// Absolute world positions of the filler cells, computed from this
    /// block's own position + stored relative offsets.
    /// </summary>
    public List<BlockPos> GetFillerWorldPositions()
    {
        var result = new List<BlockPos>();
        foreach (var off in FillerOffsets)
        {
            result.Add(Pos.AddCopy(off.X, off.Y, off.Z));
        }
        return result;
    }
}
