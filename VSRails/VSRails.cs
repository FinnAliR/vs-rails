using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VSRails
{
    public class VSRails : ModSystem
    {
        public string ModId => Mod.Info.ModID;
        public ILogger Logger => Mod.Logger;

        public override void Start(ICoreAPI api)
        {
            api.RegisterBlockClass("BlockRail", typeof(BlockRail));
            api.RegisterEntity("Minecart", typeof(EntityMinecart));
            api.RegisterEntity("SmartCart", typeof(EntitySmartCart));
            api.RegisterMountable("railcart", EntityRailCart.GetMountable);
        }

        public override void StartServerSide(ICoreServerAPI api) { }
        public override void StartClientSide(ICoreClientAPI api) { }
    }
}
