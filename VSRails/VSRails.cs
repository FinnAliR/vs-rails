using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VSRails
{
    public class VSRails : ModSystem
    {
        public string ModId => Mod.Info.ModID;
        public ILogger Logger => Mod.Logger;
        public override void Start(ICoreAPI api)
        {
            api.RegisterBlockClass("BlockRail", typeof(BlockRail));
            api.RegisterBlockClass("BlockRail", typeof(BlockRail));w
            api.RegisterEntity("JonasCart", typeof(JonasCart));
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
        }
    }
}
