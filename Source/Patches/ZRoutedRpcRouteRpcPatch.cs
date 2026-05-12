using HarmonyLib;
using ServideSideTweaks.Features.Chat;
using ServideSideTweaks.Features.Doors;
using ServideSideTweaks.Features.Pickables;
using ServideSideTweaks.Features.Trees;

namespace ServideSideTweaks.Patches
{
    [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
    internal static class ZRoutedRpcRouteRpcPatch
    {
        private static bool Prefix(ZRoutedRpc.RoutedRPCData rpcData)
        {
            if (PickableOwnershipHandoff.TryConsume(rpcData))
            {
                return false;
            }

            DoorOwnershipHandoff.TryApply(rpcData);
            TreeOwnershipHandoff.TrySchedule(rpcData);
            NormalChatToShout.TryConvert(rpcData);
            return true;
        }
    }
}
