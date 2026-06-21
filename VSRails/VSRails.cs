using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
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
            api.RegisterBlockClass("BlockRailFiller", typeof(BlockRailFiller));
            api.RegisterBlockEntityClass("BlockEntityRail", typeof(BlockEntityRail));
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            Mod.Logger.Notification("Hello from template mod server side");
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            Mod.Logger.Notification("Hello from template mod client side");
        }
    }
}
