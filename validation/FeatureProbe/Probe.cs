using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace ServerSideTweaks.Validation
{
    // Install only for controlled test sessions. This assembly is not part of the mod.
    [BepInPlugin("warpalicious.sstFeatureProbe", "SST Feature Probe", "0.1.0")]
    public sealed class Probe : BaseUnityPlugin
    {
        private ZRoutedRpc registeredRpc;
        private static string[] pendingAction;
        private static Action<string> pendingOutput;
        private static ZDOID pendingId;
        private static float pendingDeadline;
        private static readonly Dictionary<ZDOID, int> testAttachments = new Dictionary<ZDOID, int>();

        private void Update()
        {
            if (pendingAction != null && Time.realtimeSinceStartup > pendingDeadline)
            {
                Action<string> output = pendingOutput;
                pendingAction = null;
                pendingOutput = null;
                output("FAIL Server ownership preparation timed out");
            }
            if (ZRoutedRpc.instance == null || registeredRpc == ZRoutedRpc.instance) return;
            registeredRpc = ZRoutedRpc.instance;
            registeredRpc.Register<ZDOID, string>("SSTProbePrepare", (sender, id, action) =>
            {
                if (!ZNet.instance.IsServer() || ZNet.instance.GetPeer(sender) == null) return;
                ZDO zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null) return;
                zdo.SetOwner(ZDOMan.instance.m_sessionID);
                Prime(zdo, action);
                ZDOMan.instance.ForceSendZDO(sender, id);
                Logger.LogInfo($"SSTPROBE prepared zdo={id} owner={zdo.GetOwner()} action={action}");
                registeredRpc.InvokeRoutedRPC(sender, "SSTProbeReady", id);
            });
            registeredRpc.Register<ZDOID>("SSTProbeReady", (sender, id) =>
            {
                if (ZNet.instance.IsServer() || pendingAction == null || id != pendingId ||
                    sender != ZNet.instance.GetServerPeer().m_uid) return;
                string[] action = pendingAction;
                Action<string> output = pendingOutput;
                pendingAction = null;
                pendingOutput = null;
                try
                {
                    ZDO zdo = ZDOMan.instance.GetZDO(id) ?? throw new InvalidOperationException("Prepared object is no longer loaded");
                    zdo.SetOwner(sender);
                    Prime(zdo, action[0]);
                    Execute(action, output);
                }
                catch (Exception error) { output("FAIL " + error.GetType().Name + ": " + error.Message); }
            });
        }

        private static void Prime(ZDO zdo, string action)
        {
            if (action == "harvest") zdo.Set(ZDOVars.s_level, 3);
            if (action == "tap")
            {
                zdo.Set(ZDOVars.s_content, "MeadBaseHealthMinor".GetStableHashCode());
                zdo.Set(ZDOVars.s_startTime, Math.Max(1L, ZNet.instance.GetTime().Ticks - TimeSpan.FromHours(2).Ticks));
            }
        }

        private void Awake()
        {
            new Terminal.ConsoleCommand("sst_probe", "Temporary ServerSideTweaks validation commands", args =>
            {
                Action<string> output = text =>
                {
                    Logger.LogInfo("SSTPROBE " + text);
                    args.Context.AddString(text);
                };
                try { Execute(args.Args.Skip(1).ToArray(), output); }
                catch (Exception error) { output("FAIL " + error.GetType().Name + ": " + error.Message); }
            });
        }

        private static void Execute(string[] args, Action<string> output)
        {
            string command = args[0];
            if (command == "status")
            {
                output($"server={ZNet.instance.IsServer()} session={ZDOMan.instance.m_sessionID} peers={ZNet.instance.GetPeers().Count}");
                if (Player.m_localPlayer != null)
                    output($"player={Player.m_localPlayer.GetPlayerName()} position={Player.m_localPlayer.transform.position} health={Player.m_localPlayer.GetHealth()}");
                return;
            }
            if (command == "own" || command == "seed" || command == "zdo")
            {
                if (!ZNet.instance.IsServer()) throw new InvalidOperationException("Server command");
                string[] parts = args[1].Split(':');
                ZDOID id = new ZDOID(long.Parse(parts[0]), uint.Parse(parts[1]));
                ZDO zdo = ZDOMan.instance.GetZDO(id) ?? throw new InvalidOperationException("ZDO not found");
                if (command == "own") zdo.SetOwner(ZDOMan.instance.m_sessionID);
                if (command == "seed")
                {
                    if (args[2] == "level") zdo.Set(ZDOVars.s_level, int.Parse(args[3]));
                    else if (args[2] == "fermented")
                    {
                        Prime(zdo, "tap");
                    }
                    else throw new ArgumentException("Unknown seed type");
                }
                if (command != "zdo") ZDOMan.instance.ForceSendZDO(id);
                Describe(zdo, output);
                return;
            }
            if (command == "locations")
            {
                if (!ZNet.instance.IsServer()) throw new InvalidOperationException("Server command");
                foreach (ZoneSystem.LocationInstance location in ZoneSystem.instance.GetLocationList()
                    .Where(value => value.m_location.m_prefabName.IndexOf(args[1], StringComparison.OrdinalIgnoreCase) >= 0).Take(8))
                    output($"location={location.m_location.m_prefabName} position={location.m_position}");
                return;
            }
            if (command == "keys")
            {
                output("keys=" + string.Join(",", ZoneSystem.instance.GetGlobalKeys()));
                return;
            }
            if (command == "icons")
            {
                FieldInfo field = typeof(ZoneSystem).GetField("m_locationIcons", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                IDictionary icons = (IDictionary)field.GetValue(ZoneSystem.instance);
                output("icons=" + icons.Count);
                foreach (DictionaryEntry entry in icons) output($"icon={entry.Value} position={entry.Key}");
                return;
            }
            Player player = Player.m_localPlayer ?? throw new InvalidOperationException("Client player required");
            if (command == "remote")
            {
                if (pendingAction != null) throw new InvalidOperationException("An interaction is already pending");
                ZNetView view = FindViews(args[2], player).FirstOrDefault() ?? throw new InvalidOperationException("Target not loaded");
                if (Vector3.Distance(view.transform.position, player.transform.position) > 30f)
                    throw new InvalidOperationException("Target exceeds the 30 metre test radius");
                pendingAction = args.Skip(1).ToArray();
                pendingOutput = output;
                pendingId = view.GetZDO().m_uid;
                pendingDeadline = Time.realtimeSinceStartup + 10f;
                ZRoutedRpc.instance.InvokeRoutedRPC(ZNet.instance.GetServerPeer().m_uid, "SSTProbePrepare", pendingId, pendingAction[0]);
                output("queuedServerOwnedInteraction=" + pendingId);
                return;
            }
            if (command == "public")
            {
                ZNet.instance.SetPublicReferencePosition(bool.Parse(args[1]));
                output("requestedPublic=" + args[1]);
                return;
            }
            if (command == "message")
            {
                string message = args[1] == "boss" ? "$enemy_boss_bonemass_spawnmessage" : "SST validation control";
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage", (int)MessageHud.MessageType.Center, message);
                output("sentMessage=" + message);
                return;
            }
            if (command == "bosskey")
            {
                ZoneSystem.instance.SetGlobalKey(args[1]);
                output("requestedKey=" + args[1]);
                return;
            }
            if (command == "inventory")
            {
                foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
                    output($"item={item.m_shared.m_name} count={item.m_stack}");
                return;
            }
            ZNetView[] views = FindViews(args[1], player);
            if (command == "views")
            {
                foreach (ZNetView entry in views.Take(12))
                {
                    output($"view={entry.name} distance={Vector3.Distance(entry.transform.position, player.transform.position):F1}");
                    Describe(entry.GetZDO(), output);
                }
                return;
            }
            ZNetView target = views.FirstOrDefault() ?? throw new InvalidOperationException("No matching loaded object");
            if (Vector3.Distance(target.transform.position, player.transform.position) > 30f)
                throw new InvalidOperationException("Target exceeds the 30 metre test radius");
            Describe(target.GetZDO(), output);
            if (command == "interact" || command == "harvest" || command == "tap")
            {
                Interactable interactable = target.GetComponent<Interactable>() ?? throw new InvalidOperationException("No Interactable");
                output("interactResult=" + interactable.Interact(player, false, false));
            }
            else if (command == "item")
            {
                ItemStand stand = target.GetComponentInChildren<ItemStand>();
                Interactable interactable = (Interactable)stand ?? target.GetComponent<Interactable>() ?? throw new InvalidOperationException("No Interactable");
                GameObject prefab = ObjectDB.instance.GetItemPrefab(args[2]) ?? throw new InvalidOperationException("Item prefab missing");
                ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
                item.m_dropPrefab = prefab;
                if (!player.GetInventory().AddItem(item)) throw new InvalidOperationException("Inventory full");
                // AddItem can merge into an existing stack; UseItem needs the stored instance.
                item = player.GetInventory().GetItem(item.m_shared.m_name) ?? throw new InvalidOperationException("Added item missing");
                bool wasEmpty = stand != null && !stand.HaveAttachment();
                bool used = interactable.UseItem(player, item);
                if (wasEmpty && used) testAttachments[target.GetZDO().m_uid] = args[2].GetStableHashCode();
                output("useItemResult=" + used);
            }
            else if (command == "clearitem")
            {
                ZDO zdo = target.GetZDO();
                if (!testAttachments.TryGetValue(zdo.m_uid, out int expected) || zdo.GetInt(ZDOVars.s_item) != expected)
                    throw new InvalidOperationException("This is not an attachment placed by this probe session");
                ItemStand stand = target.GetComponentInChildren<ItemStand>() ?? throw new InvalidOperationException("No ItemStand");
                stand.DestroyAttachment();
                testAttachments.Remove(zdo.m_uid);
                output("testAttachmentRemovalSent=" + zdo.m_uid);
            }
            else if (command == "damage")
            {
                IDestructible destructible = target.GetComponent<IDestructible>() ?? throw new InvalidOperationException("No IDestructible");
                HitData hit = new HitData();
                hit.SetAttacker(player);
                hit.m_hitCollider = target.GetComponentInChildren<Collider>();
                hit.m_point = hit.m_hitCollider != null ? hit.m_hitCollider.bounds.center : target.transform.position;
                hit.m_dir = Vector3.forward;
                hit.m_toolTier = 4;
                hit.m_damage.m_damage = 1f;
                if (args[2] == "pickaxe") hit.m_damage.m_pickaxe = 1f;
                else hit.m_damage.m_chop = 1f;
                destructible.Damage(hit);
                output("damageSent=" + args[2]);
            }
            else throw new ArgumentException("Unknown command");
        }

        private static ZNetView[] FindViews(string filter, Player player)
        {
            return UnityEngine.Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None)
                .Where(view => view.IsValid() && (filter.Contains(":") ? view.GetZDO().m_uid.ToString() == filter :
                    view.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(view => Vector3.Distance(view.transform.position, player.transform.position)).ToArray();
        }

        private static void Describe(ZDO zdo, Action<string> output)
        {
            output($"zdo={zdo.m_uid} owner={zdo.GetOwner()} position={zdo.GetPosition()} " +
                $"state={zdo.GetInt(ZDOVars.s_state)} picked={zdo.GetBool(ZDOVars.s_picked)} " +
                $"level={zdo.GetInt(ZDOVars.s_level)} content={zdo.GetInt(ZDOVars.s_content)} " +
                $"item={zdo.GetString(ZDOVars.s_item)} itemHash={zdo.GetInt(ZDOVars.s_item)} health={zdo.GetFloat(ZDOVars.s_health)}");
        }
    }
}
