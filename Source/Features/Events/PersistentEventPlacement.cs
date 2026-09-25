using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ServerSideTweaks.Features.Events
{
    // Keep vanilla's bounded search, player separation, slope, altitude and event spacing.
    // Filter candidates inside that search, before anything is spawned or broadcast.
    [HarmonyPatch(typeof(PersistentEventSystem.PersistentEvent), nameof(PersistentEventSystem.PersistentEvent.GenerateEventLocation))]
    internal static class PersistentEventPlacement
    {
        private const string InvasionEventName = "jotun_invasion";

        internal sealed class PlacementRules
        {
            internal readonly List<Vector3> ProtectedPositions = new List<Vector3>();
            internal Heightmap.Biome AllowedBiomes;
            internal float MinimumDistanceSquared;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            List<CodeInstruction> code = instructions.ToList();
            MethodInfo biomeCheck = AccessTools.Method(typeof(Enum), nameof(Enum.HasFlag));
            if (code.Count(instruction => instruction.Calls(biomeCheck)) != 1)
            {
                throw new InvalidOperationException("Persistent event placement: expected one vanilla biome check. Review this patch for the installed Valheim version.");
            }

            LocalBuilder rules = generator.DeclareLocal(typeof(PlacementRules));
            // A method-local snapshot avoids scanning every world object per random attempt,
            // and avoids retaining world objects or settings across calls/world changes.
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PersistentEventPlacement), nameof(ReadRules)));
            yield return new CodeInstruction(OpCodes.Stloc, rules);
            foreach (CodeInstruction instruction in code)
            {
                yield return instruction;
                if (!instruction.Calls(biomeCheck))
                {
                    continue;
                }

                yield return new CodeInstruction(OpCodes.Ldarg_2); // out Vector3 position
                yield return new CodeInstruction(OpCodes.Ldobj, typeof(Vector3));
                yield return new CodeInstruction(OpCodes.Ldloc, rules);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PersistentEventPlacement), nameof(AllowCandidate)));
            }
        }

        internal static PlacementRules? ReadRules(PersistentEventSystem.PersistentEvent definition)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || !ModConfig.EnablePersistentEventPlacement.Value ||
                !string.Equals(definition.internalName, InvasionEventName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            PlacementRules rules = new PlacementRules
            {
                AllowedBiomes = ModConfig.PersistentEventAllowedBiomes.Value
            };
            float clearance = ModConfig.PersistentEventPrefabClearance.Value;
            float radius = Mathf.Max(definition.minRadius, definition.maxRadius);
            if (float.IsNaN(clearance) || float.IsInfinity(clearance) || float.IsNaN(radius) || float.IsInfinity(radius) || ZDOMan.instance == null)
            {
                throw new InvalidOperationException("Persistent event placement: invalid radius/clearance or world object data unavailable. Event placement stopped.");
            }
            float distance = Mathf.Max(0f, radius) + Mathf.Max(0f, clearance);
            rules.MinimumDistanceSquared = distance * distance;
            HashSet<int> hashes = new HashSet<int>();
            foreach (string entry in ModConfig.PersistentEventProtectedPrefabs.Value.Split(','))
            {
                string name = entry.Trim();
                if (name.Length > 0)
                {
                    hashes.Add(name.GetStableHashCode());
                }
            }

            if (hashes.Count > 0)
            {
                foreach (ZDO zdo in ZDOMan.instance.m_objectsByID.Values)
                {
                    if (hashes.Contains(zdo.GetPrefab()))
                    {
                        rules.ProtectedPositions.Add(zdo.GetPosition());
                    }
                }
            }
            if (ModConfig.DebugPersistentEventPlacement.Value)
            {
                ServerSideTweaksPlugin.ModLogger.LogInfo($"Persistent event placement: protectedObjects={rules.ProtectedPositions.Count}, centerClearance={distance:F1}m, allowedBiomes={rules.AllowedBiomes}.");
            }
            return rules;
        }

        internal static bool AllowCandidate(bool vanillaAllowed, Vector3 position, PlacementRules? rules)
        {
            if (rules == null)
            {
                return vanillaAllowed;
            }
            if ((WorldGenerator.instance.GetBiome(position) & rules.AllowedBiomes) == 0)
            {
                return false;
            }
            foreach (Vector3 protectedPosition in rules.ProtectedPositions)
            {
                float dx = position.x - protectedPosition.x;
                float dz = position.z - protectedPosition.z;
                if (dx * dx + dz * dz <= rules.MinimumDistanceSquared)
                {
                    return false;
                }
            }
            return true;
        }

        private static void Postfix(PersistentEventSystem.PersistentEvent __instance, bool __result, Vector3 position)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || !ModConfig.EnablePersistentEventPlacement.Value ||
                !ModConfig.DebugPersistentEventPlacement.Value || !string.Equals(__instance.internalName, InvasionEventName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            ServerSideTweaksPlugin.ModLogger.LogInfo(__result
                ? $"Persistent event placement accepted: center={position}, biome={WorldGenerator.instance.GetBiome(position)}."
                : "Persistent event placement: no valid location within vanilla's attempt limit; no event created.");
        }
    }
}
