using System;
using ServerSideTweaks;
using ServerSideTweaks.Infrastructure;
using UnityEngine;

internal static class Program
{
    private static void Main()
    {
        Run("release only after timeout and send changed object", () =>
        {
            ZDO zdo = Add(1);
            TemporaryOwnershipHandoffs.Assign(zdo, 10);
            Tick(4);
            Require(zdo.GetOwner() == 10 && ZDOMan.instance.Sent.Count == 0);
            Tick(5);
            Require(zdo.GetOwner() == 0 && ZDOMan.instance.Sent.Contains(zdo.m_uid));
        });
        Run("repeated tracked interaction extends timeout", () =>
        {
            ZDO zdo = Add(1);
            TemporaryOwnershipHandoffs.Assign(zdo, 10);
            Time.time = 4;
            TemporaryOwnershipHandoffs.RefreshIfTracked(zdo.m_uid, 10);
            Tick(5);
            Require(zdo.GetOwner() == 10);
            Tick(9);
            Require(zdo.GetOwner() == 0);
        });
        Run("existing player ownership is not adopted", () =>
        {
            ZDO zdo = Add(1);
            zdo.SetOwner(10);
            TemporaryOwnershipHandoffs.RefreshIfTracked(zdo.m_uid, 10);
            Tick(10);
            Require(zdo.GetOwner() == 10);
        });
        Run("changed owner survives old expiry", () =>
        {
            ZDO zdo = Add(1);
            TemporaryOwnershipHandoffs.Assign(zdo, 10);
            zdo.SetOwner(20);
            Tick(5);
            Require(zdo.GetOwner() == 20 && ZDOMan.instance.Sent.Count == 0);
        });
        Run("observed owner change ends tracking", () =>
        {
            ZDO zdo = Add(1);
            TemporaryOwnershipHandoffs.Assign(zdo, 10);
            zdo.SetOwner(20);
            Tick(1);
            zdo.SetOwner(10);
            Tick(10);
            Require(zdo.GetOwner() == 10);
        });
        Run("second handoff replaces the first deadline", () =>
        {
            ZDO zdo = Add(1);
            TemporaryOwnershipHandoffs.Assign(zdo, 10);
            Time.time = 4;
            TemporaryOwnershipHandoffs.Assign(zdo, 20);
            Tick(5);
            Require(zdo.GetOwner() == 20);
            Tick(9);
            Require(zdo.GetOwner() == 0);
        });
        Run("disconnect releases unchanged owner early", () =>
        {
            ZDO zdo = Add(1);
            TemporaryOwnershipHandoffs.Assign(zdo, 10);
            ZNet.instance.Peers.Remove(10);
            Tick(1);
            Require(zdo.GetOwner() == 0);
        });
        Run("deleted tracked object releases its record", () =>
        {
            ZDO zdo = Add(1);
            TemporaryOwnershipHandoffs.Assign(zdo, 10);
            ZDOMan.instance.Objects.Remove(zdo.m_uid);
            Tick(1);
            Require(!ZDOExtraData.HasOwner(zdo.m_uid));
        });
        Run("world switch cannot release matching IDs in new world", () =>
        {
            TemporaryOwnershipHandoffs.Assign(Add(1), 10);
            ZDOMan.instance = new ZDOMan();
            ZDO replacement = Add(1);
            replacement.SetOwner(10);
            Tick(10);
            Require(replacement.GetOwner() == 10);
        });
        Run("stale scan preserves live objects and obeys interval", () =>
        {
            ZDO live = Add(1);
            live.SetOwner(10);
            ZDOID missing = new(2);
            ZDOExtraData.AddOwner(missing);
            StaleZdoOwnerCleanup.Update();
            Require(!ZDOExtraData.HasOwner(missing) && ZDOExtraData.HasOwner(live.m_uid));
            ZDOExtraData.AddOwner(missing);
            Time.time = 59;
            StaleZdoOwnerCleanup.Update();
            Require(ZDOExtraData.HasOwner(missing));
            Time.time = 60;
            StaleZdoOwnerCleanup.Update();
            Require(!ZDOExtraData.HasOwner(missing) && live.GetOwner() == 10);
        });
        Run("disabled cleanup preserves stale records", () =>
        {
            ZDOID missing = new(1);
            ZDOExtraData.AddOwner(missing);
            ModConfig.EnableStaleZdoOwnerCleanup.Value = false;
            StaleZdoOwnerCleanup.Update();
            Require(ZDOExtraData.HasOwner(missing));
        });
        Run("client cannot clean ownership", () =>
        {
            ZDO zdo = Add(1);
            TemporaryOwnershipHandoffs.Assign(zdo, 10);
            ZDOID missing = new(2);
            ZDOExtraData.AddOwner(missing);
            ZNet.instance.Server = false;
            Tick(10);
            StaleZdoOwnerCleanup.Update();
            Require(zdo.GetOwner() == 10 && ZDOExtraData.HasOwner(missing));
        });
        Run("world switch resets stale scan schedule", () =>
        {
            Time.time = 100;
            StaleZdoOwnerCleanup.Update();
            ZDOMan.instance = new ZDOMan();
            Time.time = 0;
            ZDOID missing = new(1);
            ZDOExtraData.AddOwner(missing);
            StaleZdoOwnerCleanup.Update();
            Require(!ZDOExtraData.HasOwner(missing));
        });
    }

    private static void Run(string name, Action test)
    {
        TemporaryOwnershipHandoffs.ClearRuntimeCache();
        StaleZdoOwnerCleanup.ClearRuntimeCache();
        ZDOExtraData.Clear();
        ZDOMan.instance = new ZDOMan();
        ZNet.instance = new ZNet();
        Time.time = 0;
        ModConfig.EnableStaleZdoOwnerCleanup.Value = true;
        test();
        Console.WriteLine("PASS: " + name);
    }

    private static ZDO Add(int id)
    {
        ZDO zdo = new(id);
        ZDOMan.instance.Objects.Add(zdo.m_uid, zdo);
        return zdo;
    }

    private static void Tick(float time)
    {
        Time.time = time;
        TemporaryOwnershipHandoffs.Update();
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new Exception("Ownership cleanup assertion failed.");
    }
}
