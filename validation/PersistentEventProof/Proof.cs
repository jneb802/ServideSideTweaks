using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

[BepInPlugin("odin.validation.persistentevents", "PersistentEventProof", "1.0.0")]
public sealed class PersistentEventProof : BaseUnityPlugin
{
    private string Request => Path.Combine(Paths.ConfigPath, "persistent-proof.request");
    private string Result => Path.Combine(Paths.ConfigPath, "persistent-proof.result");
    private readonly List<ZDO> testObjects = new List<ZDO>();
    private Type feature;
    private Type config;
    private float next;
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private void Update()
    {
        if (Time.time < next) return;
        next = Time.time + 1;
        if (!File.Exists(Request) || ZNet.instance == null || PersistentEventSystem.instance == null) return;
        string command = File.ReadAllText(Request).Trim(); File.Delete(Request);
        try
        {
            if (command == "client-start" || command == "client-info")
            {
                if (ZNet.instance.IsServer()) throw new Exception("Client proof requires a connected client.");
                if (command == "client-start") PersistentEventSystem.instance.TriggerEvent("jotun_invasion");
                File.WriteAllText(Result, "game=" + Version.GetVersionString() + " server=false\n" +
                    string.Join("\n", PersistentEventSystem.instance.m_activePersistentEvents.list.Select(e =>
                        "active=" + e.eventId + " center=" + e.position + " radius=" + e.radius)));
                return;
            }
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "serverSideTweaks");
            feature = assembly.GetType("ServerSideTweaks.Features.Events.PersistentEventPlacement");
            config = assembly.GetType("ServerSideTweaks.ModConfig");
            File.WriteAllText(Result, Run(command));
        }
        catch (Exception e) { File.WriteAllText(Result, "FAIL " + e); Logger.LogError(e); }
    }
    private void Set<T>(string name, T value) => ((ConfigEntry<T>)config.GetField(name, Flags).GetValue(null)).Value = value;
    private object Rules(PersistentEventSystem.PersistentEvent definition) => feature.GetMethod("ReadRules", Flags).Invoke(null, new object[] { definition });
    private bool Allow(Vector3 position, object rules, bool vanilla = true) => (bool)feature.GetMethod("AllowCandidate", Flags).Invoke(null, new object[] { vanilla, position, rules });
    private void Check(bool condition, string message, List<string> log) { if (!condition) throw new Exception(message); log.Add("PASS " + message); }
    private ZDO AddStored(string prefab, Vector3 position)
    {
        ZDO zdo = ZDOMan.instance.CreateNewZDO(position, prefab.GetStableHashCode());
        zdo.SetPrefab(prefab.GetStableHashCode()); zdo.Persistent = true;
        testObjects.Add(zdo); return zdo;
    }
    private string Run(string command)
    {
        List<string> log = new List<string>();
        PersistentEventSystem system = PersistentEventSystem.instance;
        if (command == "info")
        {
            log.Add("game=" + Version.GetVersionString() + " server=" + ZNet.instance.IsServer() + " zdos=" + ZDOMan.instance.NrOfObjects());
            foreach (PersistentEventSystem.PersistentEvent e in system.m_possibleEvents)
                log.Add("definition=" + e.internalName + " biomes=" + e.biomes + " radius=" + e.minRadius + "," + e.maxRadius + " count=" + e.maxConcurrent);
            foreach (PersistentEventSystem.ActivePersistentEvent e in system.m_activePersistentEvents.list)
                log.Add("active=" + e.eventId + " center=" + e.position + " radius=" + e.radius + " biome=" + WorldGenerator.instance.GetBiome(e.position));
            return string.Join("\n", log);
        }
        PersistentEventSystem.PersistentEvent definition = system.m_possibleEvents.First(e => e.internalName.IndexOf("jotun", StringComparison.OrdinalIgnoreCase) >= 0);
        if (command == "verify")
        {
            Check(system.m_activePersistentEvents.list.Count > 0, "server has active event", log);
            object rules = Rules(definition);
            foreach (PersistentEventSystem.ActivePersistentEvent e in system.m_activePersistentEvents.list)
            {
                Check(Allow(e.position, rules), "live event respects biome and stored-prefab clearance " + e.eventId, log);
                log.Add("active=" + e.eventId + " center=" + e.position + " radius=" + e.radius + " biome=" + WorldGenerator.instance.GetBiome(e.position));
            }
            return string.Join("\n", log);
        }
        if (command == "clear")
        {
            foreach (PersistentEventSystem.ActivePersistentEvent e in system.m_activePersistentEvents.list.ToArray()) system.StopEvent(e.internalName, e.position);
            foreach (ZDO zdo in testObjects) ZDOMan.instance.DestroyZDO(zdo); testObjects.Clear();
            return "test event state and stored fixtures removed";
        }
        if (command != "test") return "unknown command";
        Set("EnablePersistentEventPlacement", true); Set("PersistentEventProtectedPrefabs", "guard_stone"); Set("PersistentEventPrefabClearance", 100f);
        Check(Rules(definition) != null, "real event definition matches patch target", log);
        List<PersistentEventSystem.ActivePersistentEvent> existing = system.m_activePersistentEvents.list;
        Vector3 candidate = Vector3.zero; int selectedSeed = 0; int mountains = 0, plains = 0;
        for (int seed = 1; seed <= 100; seed++)
        {
            UnityEngine.Random.InitState(seed);
            Check(definition.GenerateEventLocation(existing, out Vector3 point), "candidate search " + seed, log);
            Check(WorldGenerator.instance.GetBiome(point) == Heightmap.Biome.Mountain || WorldGenerator.instance.GetBiome(point) == Heightmap.Biome.Plains, "candidate biome " + seed, log);
            if (WorldGenerator.instance.GetBiome(point) == Heightmap.Biome.Mountain) mountains++; else plains++;
            if (seed == 1) { candidate = point; selectedSeed = seed; }
        }
        Check(mountains > 0 && plains > 0, "both allowed biomes found: Mountains=" + mountains + ", Plains=" + plains, log);
        float distance = Mathf.Max(definition.minRadius, definition.maxRadius) + 100f;
        ZDO ward = AddStored("guard_stone", candidate + new Vector3(distance, 2000, 0));
        Check(ZNetScene.instance.FindInstance(ward) == null, "stored ward is unloaded", log);
        Check(!ward.GetBool("enabled"), "stored ward is disabled", log);
        Check(!Allow(candidate, Rules(definition)), "boundary rejected with large vertical separation", log);
        ward.SetPosition(candidate + new Vector3(distance + 1, 2000, 0));
        Check(Allow(candidate, Rules(definition)), "point beyond clearance accepted", log);
        ward.SetPosition(candidate);
        UnityEngine.Random.InitState(selectedSeed);
        Check(definition.GenerateEventLocation(existing, out Vector3 replacement), "search retries after ward rejection", log);
        float dx = replacement.x - candidate.x, dz = replacement.z - candidate.z;
        Check(dx * dx + dz * dz > distance * distance, "replacement clears stored ward horizontally", log);
        Set("PersistentEventProtectedPrefabs", "piece_workbench");
        Check(Allow(candidate, Rules(definition)), "changing prefab list removes ward exclusion", log);
        ZDO bench = AddStored("piece_workbench", candidate);
        Check(!Allow(candidate, Rules(definition)), "configured second prefab excluded", log);
        Set("PersistentEventProtectedPrefabs", "");
        Check(Allow(candidate, Rules(definition)), "empty list disables only prefab exclusion", log);
        Vector3 meadows = Vector3.zero;
        for (int i = 0; i < 10000; i++) { Vector3 p = new Vector3(i % 100 * 20, 0, i / 100 * 20); if (WorldGenerator.instance.GetBiome(p) == Heightmap.Biome.Meadows) { meadows = p; break; } }
        Check(!Allow(meadows, Rules(definition)), "Meadows rejected with empty prefab list", log);
        Set("EnablePersistentEventPlacement", false);
        Check(Rules(definition) == null && Allow(meadows, null), "disabled feature leaves vanilla check unchanged", log);
        Check(!Allow(candidate, null, false), "vanilla rejections remain rejected", log);
        Set("EnablePersistentEventPlacement", true); Set("PersistentEventProtectedPrefabs", "guard_stone");
        PersistentEventSystem.PersistentEvent other = new PersistentEventSystem.PersistentEvent { internalName = "unrelated" };
        Check(Rules(other) == null, "unrelated persistent events unchanged", log);
        Set("PersistentEventPrefabClearance", 10000f);
        ZDO originWard = AddStored("guard_stone", Vector3.zero);
        float oldMin = definition.minDistanceFromCenter, oldMax = definition.maxDistanceFromCenter;
        try
        {
            definition.minDistanceFromCenter = 1000; definition.maxDistanceFromCenter = 2000;
            Check(!definition.GenerateEventLocation(existing, out Vector3 blocked), "fully protected search fails without unsafe fallback", log);
        }
        finally { definition.minDistanceFromCenter = oldMin; definition.maxDistanceFromCenter = oldMax; Set("PersistentEventPrefabClearance", 100f); }
        log.Add("ward=" + candidate + " replacement=" + replacement);
        log.Add("TOTAL PASSES=" + log.Count(x => x.StartsWith("PASS")));
        return string.Join("\n", log);
    }
}
