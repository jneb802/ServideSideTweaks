using UnityEngine;

namespace ServideSideTweaks.Infrastructure
{
    internal static class ZdoComponentLookup
    {
        internal static bool HasComponent<T>(ZDO zdo) where T : Component
        {
            ZNetView? instance = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
            if (instance != null && instance.GetComponent<T>() != null)
            {
                return true;
            }

            GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            return prefab != null && prefab.GetComponent<T>() != null;
        }
    }
}
