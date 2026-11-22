using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Persistent;

// Ideology: "found a new colony" after the third Archonexus core (QuestPart_NewColony).
//
// Vanilla runs the whole flow from UI callbacks: choose colonists/items (Dialog_ChooseThingsForNewColony),
// a cinematic, pick a tile, optionally reconfigure the ideoligion, then MoveColonyAndReset wipes and
// regenerates the colony. In MP the dialog opened on every client and each client then changed the game
// on its own. Here:
//  - multifaction games don't support it (the reset would hit every player faction): the quest step is
//    cancelled for everyone;
//  - otherwise the game pauses (session) and any player can make the choice; the first confirmed
//    choice is synced and applied on every client, and only that player continues to the tile picker;
//  - the chosen tile is synced and the colony move runs inside that command on every client;
//  - the optional ideoligion reconfiguration is skipped in MP (Dialog_ConfigureIdeo edits ideoligions
//    directly from the UI);
//  - cancelling anywhere cancels for everyone.
public static class ArchonexusNewColony
{
    internal static bool Enabled => Multiplayer.Client != null &&
        (!Multiplayer.IsReplay || Multiplayer.session.replayProtocol >= MpVersion.ArchonexusProtocol);

    // The things chosen for the new colony are kept in the (saved) session until a tile is chosen, so a
    // join point or autosave in between doesn't lose them.
    public static bool HasPendingThings(QuestPart_NewColony part) => GetSession(part)?.chosenThings != null;

    public static QuestPart_NewColony FindPart(int questId, int partIndex)
    {
        var quest = Find.QuestManager.QuestsListForReading.FirstOrDefault(q => q.id == questId);
        if (quest == null || partIndex < 0 || partIndex >= quest.parts.Count) return null;
        return quest.parts[partIndex] as QuestPart_NewColony;
    }

    public static QuestPart_NewColony FindPartByCancelSignal(string tag)
    {
        if (tag.NullOrEmpty()) return null;

        var session = Multiplayer.WorldComp?.sessionManager.GetFirstOfType<ArchonexusNewColonySession>();
        var part = session == null ? null : FindPart(session.questId, session.partIndex);
        return part?.outSignalCancelled == tag ? part : null;
    }

    internal static bool HasSession(QuestPart_NewColony part) => GetSession(part) != null;

    // Called in the simulation (quest signal) on every client.
    public static bool OnSignalReceived(QuestPart_NewColony part)
    {
        return Multiplayer.WorldComp.sessionManager.AddSession(new ArchonexusNewColonySession(null)
        {
            questId = part.quest.id,
            partIndex = part.Index
        });
    }

    public static void OpenChooseThingsDialog(QuestPart_NewColony part)
    {
        if (Find.WindowStack.IsOpen<Dialog_ChooseThingsForNewColony>()) return;

        Find.WindowStack.Add(new Dialog_ChooseThingsForNewColony(part.PostThingsSelected, 5, 5, part.maxRelics, 7,
            () => RequestCancel(part)));
    }

    public static void StartPickingTile(QuestPart_NewColony part)
    {
        MoveColonyUtility.PickNewColonyTile(tile => RequestTile(part, tile), () => RequestCancel(part));
    }

    private static ByteWriter WritePart(QuestPart_NewColony part)
    {
        var writer = new ByteWriter();
        writer.WriteInt32(part.quest.id);
        writer.WriteInt32(part.Index);
        return writer;
    }

    internal static void RequestThings(QuestPart_NewColony part, Dialog_ChooseThingsForNewColony dialog,
        List<Thing> selected, bool onlySlavesSelected)
    {
        if (GetSession(part) == null) return;

        var writer = WritePart(part);
        writer.WriteBool(onlySlavesSelected);
        writer.WriteInt32(selected.Count);
        foreach (var thing in selected)
        {
            var hasCount = dialog.itemArchonexusAllowedStackCount.TryGetValue(thing, out var count);
            writer.WriteInt32(thing.thingIDNumber);
            writer.WriteInt32(hasCount ? count : -1);
            writer.WriteBool(hasCount || dialog.items.Contains(thing));
        }

        Multiplayer.Client.SendCommand(CommandType.ArchonexusChooseThings, ScheduledCommand.Global, writer.ToArray());
    }

    internal static void RequestTile(QuestPart_NewColony part, PlanetTile tile)
    {
        var writer = WritePart(part);
        SyncSerialization.WriteSync(writer, tile);
        Multiplayer.Client.SendCommand(CommandType.ArchonexusChooseTile, ScheduledCommand.Global, writer.ToArray());
    }

    internal static void RequestCancel(QuestPart_NewColony part) =>
        Multiplayer.Client.SendCommand(CommandType.ArchonexusCancel, ScheduledCommand.Global, WritePart(part).ToArray());

    internal static void HandleCommand(CommandType type, ByteReader data)
    {
        var part = FindPart(data.ReadInt32(), data.ReadInt32());
        var session = GetSession(part);
        if (session == null) return;

        switch (type)
        {
            case CommandType.ArchonexusChooseThings when session.chosenThings == null:
                ApplyThings(part, session, data);
                break;
            case CommandType.ArchonexusChooseTile when session.chosenThings != null:
                ApplyTile(part, session, SyncSerialization.ReadSync<PlanetTile>(data));
                break;
            case CommandType.ArchonexusCancel:
                Cancel(part);
                break;
        }
    }

    private static void ApplyThings(QuestPart_NewColony part, ArchonexusNewColonySession session, ByteReader data)
    {
        bool onlySlavesSelected = data.ReadBool();
        int count = data.ReadInt32();
        if (count < 0 || count > data.Left / (sizeof(int) * 2 + sizeof(byte))) return;

        var chosen = new List<(Thing thing, int count, bool repair)>(count);
        var chosenIds = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int thingId = data.ReadInt32();
            int stackCount = data.ReadInt32();
            bool repair = data.ReadBool();
            // The picker can include things from multiple maps and world caravans.
            if (Multiplayer.ThingsById.GetValueSafe(thingId) is { Destroyed: false } thing && chosenIds.Add(thingId))
                chosen.Add((thing, stackCount, repair));
        }

        if (!chosen.Any(selection => selection.thing is Pawn))
        {
            MpLog.Error("Archonexus new colony: none of the chosen colonists could be found; cancelling");
            Cancel(part);
            return;
        }

        foreach (var (thing, stackCount, repair) in chosen)
        {
            if (onlySlavesSelected && thing is Pawn { IsSlaveOfColony: true } slave)
                slave.guest.SetGuestStatus(null);
            if (stackCount > 0)
                thing.stackCount = Math.Min(thing.stackCount, stackCount);
            if (repair)
                thing.HitPoints = thing.MaxHitPoints;
        }

        CloseLocalWindows();
        Find.WorldObjects.AllWorldObjects.FirstOrDefault(wo => wo.def == WorldObjectDefOf.EscapeShip)?.Destroy();

        session.chosenThings = chosen.Select(selection => selection.thing).ToList();

        if (TickPatch.currentExecutingCmdIssuedBySelf)
            part.PostThingsSelected(session.chosenThings); // Cinematic, then the tile picker (local UI)
        else
            // Messages from other players' commands are silenced; show it outside the command.
            OnMainThread.Enqueue(() => Messages.Message("MpArchonexusOtherChoosing".Translate(), MessageTypeDefOf.NeutralEvent, historical: false));
    }

    private static void ApplyTile(QuestPart_NewColony part, ArchonexusNewColonySession session, PlanetTile tile)
    {
        var things = session.chosenThings;
        things.RemoveAll(t => t == null || t.Destroyed);
        if (!things.Any(t => t is Pawn))
        {
            Cancel(part);
            return;
        }

        if (!tile.Valid || !TileFinder.IsValidTileForNewSettlement(tile))
        {
            MpLog.Error($"Archonexus new colony: tile {tile} is not valid for a new settlement");
            return;
        }

        session.Remove();
        CloseLocalWindows();
        Find.World.renderer.wantedMode = WorldRenderMode.None;

        // Same as QuestPart_NewColony.InitMoveColony, but run inside this command instead of a long event,
        // so it uses the command's synchronized random state on every client. MP skips the optional
        // ideoligion reconfiguration (QuestPart_NewColony.TileChosen).
        Find.MusicManagerPlay.ForceFadeoutAndSilenceFor(120f);
        var list = new List<Thing>();
        list.AddRange(MoveColonyUtility.GetStartingThingsForNewColony());
        list.AddRange(things);

        Settlement settlement;
        QuestPart_NewColony.IsGeneratingNewColony = true;
        try
        {
            settlement = MoveColonyUtility.MoveColonyAndReset(tile, list, part.otherFaction, part.worldObjectDef);
        }
        finally
        {
            QuestPart_NewColony.IsGeneratingNewColony = false;
        }

        if (settlement?.Map != null)
        {
            CameraJumper.TryJump(MapGenerator.PlayerStartSpot, settlement.Map);
            var async = settlement.Map.AsyncTime();
            if (async != null)
                async.DesiredTimeSpeed = TimeSpeed.Normal;
        }

        if (!part.outSignalCompleted.NullOrEmpty())
            Find.SignalManager.SendSignal(new Signal(part.outSignalCompleted));
    }

    private static void Cancel(QuestPart_NewColony part)
    {
        var session = GetSession(part);
        session?.Remove();
        CloseLocalWindows();

        if (session != null && !part.outSignalCancelled.NullOrEmpty())
            Find.SignalManager.SendSignal(new Signal(part.outSignalCancelled));
    }

    private static ArchonexusNewColonySession GetSession(QuestPart_NewColony part)
    {
        if (part == null) return null;
        return Multiplayer.WorldComp?.sessionManager.GetFirstOfType<ArchonexusNewColonySession>(
            s => s.questId == (part.quest?.id ?? -1) && s.partIndex == part.Index);
    }

    private static void CloseLocalWindows()
    {
        foreach (var window in Find.WindowStack.Windows.ToList())
        {
            if (window is Dialog_ChooseThingsForNewColony or Screen_ArchonexusSettlementCinematics)
                window.Close(doCloseSound: false);
            else if (window is Dialog_MessageBox box && box.title == "ConfirmDecisionsTitle".Translate())
                window.Close(doCloseSound: false);
        }

        if (Find.TilePicker.Active && Find.TilePicker.title == "ChooseNextColonySite".Translate())
            Find.TilePicker.StopTargetingInt();
    }
}

public class ArchonexusNewColonySession : ExposableSession, ISessionWithCreationRestrictions
{
    public int questId = -1;
    public int partIndex = -1;
    public List<Thing> chosenThings; // null until a player confirmed the choice

    public override bool IsSessionValid => ArchonexusNewColony.FindPart(questId, partIndex) != null;

    public override Map Map => null;

    public ArchonexusNewColonySession(Map _) : base(null)
    {
    }

    // Pauses everything while the new colony is being chosen.
    public override bool IsCurrentlyPausing(Map map) => true;

    public override FloatMenuOption GetBlockingWindowOptions(ColonistBar.Entry entry)
    {
        var part = ArchonexusNewColony.FindPart(questId, partIndex);
        if (part == null)
            return null;

        if (ArchonexusNewColony.HasPendingThings(part))
            return new FloatMenuOption("MpArchonexusChooseSite".Translate(), () => ArchonexusNewColony.StartPickingTile(part));

        return new FloatMenuOption("MpArchonexusChooseThings".Translate(), () => ArchonexusNewColony.OpenChooseThingsDialog(part));
    }

    public void Remove() => Multiplayer.WorldComp.sessionManager.RemoveSession(this);

    public bool CanExistWith(Session other) => other is not ArchonexusNewColonySession;

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref questId, "questId", -1);
        Scribe_Values.Look(ref partIndex, "partIndex", -1);
        Scribe_Collections.Look(ref chosenThings, "chosenThings", LookMode.Reference);
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
            chosenThings?.RemoveAll(t => t == null);
    }
}

[HarmonyPatch(typeof(QuestPart_NewColony), nameof(QuestPart_NewColony.Notify_QuestSignalReceived))]
static class ArchonexusNewColonySignalPatch
{
    static bool Prefix(QuestPart_NewColony __instance, Signal signal)
    {
        if (!ArchonexusNewColony.Enabled || signal.tag != __instance.inSignal)
            return true;

        if (ArchonexusNewColony.HasSession(__instance)) return false;

        if (Multiplayer.GameComp.multifaction)
        {
            Messages.Message("MpArchonexusMultifaction".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            if (!__instance.outSignalCancelled.NullOrEmpty())
                Find.SignalManager.SendSignal(new Signal(__instance.outSignalCancelled));
            return false;
        }

        if (!ArchonexusChooseThingsConfirmPatch.Ready || !ArchonexusNewColony.OnSignalReceived(__instance))
        {
            // Another new-colony flow is already running; don't open a dialog nobody can confirm.
            if (!__instance.outSignalCancelled.NullOrEmpty())
                Find.SignalManager.SendSignal(new Signal(__instance.outSignalCancelled));
            return false;
        }

        return true; // Vanilla opens the dialog on every client; the first confirmed choice wins.
    }
}

// The dialog's confirm action changes the game (slaves, stack counts, hit points, escape ship) before
// continuing the flow. In MP, send the choice instead and apply it on every client.
[HarmonyPatch]
static class ArchonexusChooseThingsConfirmPatch
{
    private static FieldInfo thisField, selectedField, slavesField;

    private static MethodBase target;

    internal static bool Ready => target != null;

    static bool Prepare()
    {
        if (target != null) return true;

        foreach (var nested in typeof(Dialog_ChooseThingsForNewColony).GetNestedTypes(AccessTools.all))
        {
            var method = nested.GetMethods(AccessTools.all)
                .FirstOrDefault(m => m.Name.Contains("ConfirmArchonexusSettlementConsequences") && m.Name.Contains("CloseAction"));
            if (method == null) continue;

            thisField = AccessTools.Field(nested, "<>4__this");
            selectedField = AccessTools.Field(nested, "selectedList");
            slavesField = AccessTools.Field(nested, "onlySlavesSelected");
            if (thisField == null || selectedField == null || slavesField == null) continue;
            target = method;
            return true;
        }

        Log.Error("MP: couldn't find Dialog_ChooseThingsForNewColony's confirm action; the Archonexus new colony choice isn't synced");
        return false;
    }

    static MethodBase TargetMethod() => target;

    static bool Prefix(object __instance)
    {
        if (!ArchonexusNewColony.Enabled)
            return true;

        var dialog = thisField?.GetValue(__instance) as Dialog_ChooseThingsForNewColony;
        var selected = selectedField?.GetValue(__instance) as List<Thing>;
        if (dialog == null || selected == null || slavesField == null)
        {
            MpLog.Error("Archonexus new colony: couldn't read the chosen things; ignoring the confirmation");
            return false;
        }

        if (dialog.postAccepted?.Target is not QuestPart_NewColony part)
            return true;

        ArchonexusNewColony.RequestThings(part, dialog, selected, (bool)slavesField.GetValue(__instance));
        dialog.Close();
        return false;
    }
}

// The tile picker's callback: sync the tile instead of starting the move locally.
[HarmonyPatch(typeof(QuestPart_NewColony), nameof(QuestPart_NewColony.TileChosen))]
static class ArchonexusTileChosenPatch
{
    static bool Prefix(QuestPart_NewColony __instance, PlanetTile chosenTile)
    {
        if (!ArchonexusNewColony.Enabled || Multiplayer.ExecutingCmds)
            return true;

        ArchonexusNewColony.RequestTile(__instance, chosenTile);
        return false;
    }
}

// Vanilla's cancel callbacks send the quest's cancel signal from the UI; sync them.
[HarmonyPatch(typeof(SignalManager), nameof(SignalManager.SendSignal))]
static class ArchonexusCancelSignalPatch
{
    static bool Prefix(Signal signal)
    {
        if (!ArchonexusNewColony.Enabled || !Multiplayer.InInterface)
            return true;

        var part = ArchonexusNewColony.FindPartByCancelSignal(signal.tag);
        if (part == null)
            return true;

        ArchonexusNewColony.RequestCancel(part);
        return false;
    }
}
