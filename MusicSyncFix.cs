using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepInEx;
using Fusion;
using HarmonyLib;
using PcStore.Audio;
using PcStore.Frontend;
using PcStore.Shop;
using PcStore.World;
using Steamworks;
using TMPro;
using UnityEngine;

namespace PcStoreMusicSync;

[BepInPlugin(Id, "PC Store Together Music Sync Fix", "1.2.1")]
public sealed partial class MusicSyncFix : BaseUnityPlugin
{
    private const string Id = "local.pcstore.musicsyncfix";
    private const float RetrySeconds = 5f;
    private Harmony harmony;
    private SteamMusicTransfer transfer;
    private static MusicSyncFix instance;

    private static readonly AccessTools.FieldRef<PersonalMusic, ShopTerminal> Terminal =
        AccessTools.FieldRefAccess<PersonalMusic, ShopTerminal>("terminal");
    private static readonly AccessTools.FieldRef<PersonalMusic, Dictionary<long, byte[]>> OwnBytes =
        AccessTools.FieldRefAccess<PersonalMusic, Dictionary<long, byte[]>>("ownBytes");
    private static readonly AccessTools.FieldRef<PersonalMusic, Dictionary<(PlayerRef, long, int), float>> ServedAt =
        AccessTools.FieldRefAccess<PersonalMusic, Dictionary<(PlayerRef, long, int), float>>("servedAt");
    private static readonly AccessTools.FieldRef<PersonalMusic, List<long>> IncomingKeys =
        AccessTools.FieldRefAccess<PersonalMusic, List<long>>("incomingKeys");
    private static readonly FieldInfo IncomingTransfers = AccessTools.Field(typeof(PersonalMusic), "incoming");
    private static readonly ConditionalWeakTable<PersonalMusic, TrackTransferState> TrackTransfers =
        new ConditionalWeakTable<PersonalMusic, TrackTransferState>();

    private static readonly Type Incoming = AccessTools.Inner(typeof(PersonalMusic), "Incoming");
    private static readonly FieldInfo Hash = AccessTools.Field(Incoming, "Hash");
    private static readonly FieldInfo Owner = AccessTools.Field(Incoming, "Owner");
    private static readonly FieldInfo Parts = AccessTools.Field(Incoming, "Parts");
    private static readonly FieldInfo AskedFrom = AccessTools.Field(Incoming, "AskedFrom");
    private static readonly FieldInfo AskedAt = AccessTools.Field(Incoming, "AskedAt");
    private static readonly FieldInfo PartAt = AccessTools.Field(Incoming, "PartAt");
    private static readonly FieldInfo Stalls = AccessTools.Field(Incoming, "Stalls");
    private static readonly FieldInfo ViaRpc = AccessTools.Field(Incoming, "ViaRpc");
    private static readonly FieldInfo Done = AccessTools.Field(Incoming, "Done");

    private void Awake()
    {
        harmony = new Harmony(Id);
        try
        {
            transfer = new SteamMusicTransfer(Logger);
            instance = this;
            Patch(typeof(PersonalMusic), "Register", postfix: nameof(Registered));
            Patch(typeof(PersonalMusic), "TickIncoming", prefix: nameof(RequestMissingParts));
            Patch(typeof(PersonalMusic), nameof(PersonalMusic.ServeParts), prefix: nameof(AllowRetry));
            Patch(typeof(PersonalMusic), nameof(PersonalMusic.WillPlay), postfix: nameof(SilenceWhileReceiving));
            Patch(typeof(PersonalMusic), "Shareable", prefix: nameof(AllowLocalTrack));
            Patch(typeof(ShopTerminal), nameof(ShopTerminal.CanShare), prefix: nameof(AllowSharedTrack));
            Patch(typeof(MusicPlayerPanel), "ShowSource", postfix: nameof(ShowLibraryCount));
            Patch(typeof(PersonalMusic), "TickReceiving", prefix: nameof(PruneOldTransfers));
            PatchPlayback();
            Logger.LogInfo("Music sync fix loaded. Custom tracks can prepare in the background before a shared start.");
        }
        catch (Exception error)
        {
            harmony.UnpatchSelf();
            instance = null;
            Logger.LogError("Could not patch this game build: " + error);
        }
    }

    private void Patch(Type target, string method, string prefix = null, string postfix = null)
    {
        MethodInfo original = AccessTools.DeclaredMethod(target, method)
            ?? throw new MissingMethodException(target.FullName, method);
        harmony.Patch(original,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(MusicSyncFix), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(MusicSyncFix), postfix));
    }

    private void LateUpdate() => transfer?.Poll();

    private void OnDestroy()
    {
        harmony?.UnpatchSelf();
        transfer?.Dispose();
        instance = null;
    }

    private static void Registered(PersonalMusic __instance)
    {
        ShopTerminal terminal = Terminal(__instance);
        if (terminal != null && terminal.MusicReady && terminal.Runner != null && terminal.Runner.GameMode == GameMode.Shared)
            instance.transfer.Attach(__instance, terminal);
    }

    private static bool RequestMissingParts(PersonalMusic __instance, object __0, float __1)
    {
        var parts = (byte[][])Parts.GetValue(__0);
        int firstMissing = Array.IndexOf(parts, null);
        if (firstMissing < 0)
            return false;

        var owner = (PlayerRef)Owner.GetValue(__0);
        long hash = (long)Hash.GetValue(__0);
        ShopTerminal terminal = Terminal(__instance);
        if (instance.transfer.TryDownload(__instance, owner, hash, firstMissing, parts.Length, __1))
            return false;

        int previousStart = (int)AskedFrom.GetValue(__0);
        float previousTime = (float)AskedAt.GetValue(__0);
        float lastPart = (float)PartAt.GetValue(__0);
        bool windowComplete = previousStart >= 0 && firstMissing >= Math.Min(parts.Length, previousStart + 4);
        bool stalled = previousStart >= 0 && !windowComplete && __1 - Math.Max(previousTime, lastPart) >= RetrySeconds;
        if (previousStart >= 0 && !windowComplete && !stalled)
            return false;

        int stalls = windowComplete || lastPart > previousTime ? 0 : (int)Stalls.GetValue(__0);
        bool viaRpc = (bool)ViaRpc.GetValue(__0);
        if (stalled && ++stalls >= 2)
        {
            viaRpc = !viaRpc;
            stalls = 0;
            ViaRpc.SetValue(__0, viaRpc);
            instance.Logger.LogInfo($"Retrying song {hash:x16} starting at part {firstMissing + 1}/{parts.Length} via {(viaRpc ? "RPC" : "data stream")}.");
        }

        Stalls.SetValue(__0, stalls);
        AskedFrom.SetValue(__0, firstMissing);
        AskedAt.SetValue(__0, __1);
        terminal.RPC_WantParts(owner, hash, firstMissing, 4, viaRpc);
        return false;
    }

    private static void AllowRetry(PersonalMusic __instance, PlayerRef asker, long hash, int from, int count)
    {
        if (from < 0 || from >= 256)
            return;

        var sent = ServedAt(__instance);
        float now = Time.unscaledTime;
        int end = Math.Min(256, from + Mathf.Clamp(count, 1, 8));
        for (int index = from; index < end; index++)
        {
            var part = (asker, hash, index);
            if (sent.TryGetValue(part, out float time) && now - time >= RetrySeconds - 0.5f)
                sent.Remove(part);
        }
    }

    private static void SilenceWhileReceiving(PersonalMusic __instance, long hash, ref bool __result)
    {
        if (!__result && __instance.Receiving(out _) && Terminal(__instance).SharedHash == hash)
            __result = true;
    }

    private static bool AllowLocalTrack(ref bool __result)
    {
        __result = true;
        return false;
    }

    private static bool AllowSharedTrack(long hash, ref bool __result)
    {
        // The game still validates the request and replicates the current song separately.
        __result = hash != 0;
        return false;
    }

    private static void ShowLibraryCount(PersonalMusic ___personal, TMP_Text ___libraryCount)
    {
        if (___libraryCount != null && ___personal != null && ___personal.ShowMine)
        {
            int count = ___personal.Count;
            ___libraryCount.text = ShopOsText.F(count == 1 ? "os.music.songs1" : "os.music.songs", count);
        }
    }

    private static void PruneOldTransfers(PersonalMusic __instance)
    {
        ShopTerminal terminal = Terminal(__instance);
        if (terminal == null || !terminal.MusicReady)
            return;

        long hash = terminal.MusicShared ? terminal.SharedHash : 0;
        TrackTransferState state = TrackTransfers.GetValue(__instance, _ => new TrackTransferState());
        if (hash == state.Hash)
            return;

        state.Hash = hash;
        var incoming = (IDictionary)IncomingTransfers.GetValue(__instance);
        var abandoned = IncomingKeys(__instance);
        abandoned.Clear();
        foreach (DictionaryEntry entry in incoming)
        {
            long pending = (long)entry.Key;
            if (pending != hash && pending != instance.transfer.Playback.PendingHash && !(bool)Done.GetValue(entry.Value))
                abandoned.Add(pending);
        }

        foreach (long pending in abandoned)
            incoming.Remove(pending);

        var sent = ServedAt(__instance);
        float now = Time.unscaledTime;
        state.ExpiredParts.Clear();
        foreach (var part in sent)
            if (now - part.Value >= RetrySeconds)
                state.ExpiredParts.Add(part.Key);

        foreach (var part in state.ExpiredParts)
            sent.Remove(part);
    }

    private sealed class TrackTransferState
    {
        public long Hash;
        public readonly List<(PlayerRef, long, int)> ExpiredParts = new List<(PlayerRef, long, int)>();
    }

    private sealed class SteamMusicTransfer : IDisposable
    {
        private const int Channel = 23817;
        private const int Request = 1;
        private const int Data = 2;
        private const int HeaderSize = 20;
        private const int Window = 8;
        private const int PartSize = PersonalMusic.PartBytes;
        private const int MaxParts = 256;
        private const int SendFlags = Constants.k_nSteamNetworkingSend_ReliableNoNagle |
            Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;

        private readonly BepInEx.Logging.ManualLogSource log;
        private readonly IntPtr[] messages = new IntPtr[16];
        private readonly byte[] packet = new byte[HeaderSize + PartSize];
        private readonly Dictionary<long, PartDownload> downloads = new Dictionary<long, PartDownload>();
        private Callback<SteamNetworkingMessagesSessionRequest_t> sessionRequests;
        private PersonalMusic music;
        private ShopTerminal terminal;
        private NetworkRunner runner;
        private CSteamID localId;
        private bool disabled;

        private sealed class PartDownload
        {
            public CSteamID Peer;
            public int NextWindow;
            public int LastMissing;
            public float LastProgress;
            public float NextRequest;
            public bool Failed;
        }

        public SteamMusicTransfer(BepInEx.Logging.ManualLogSource log)
        {
            this.log = log;
            Playback = new CoopPlayback(this, log);
        }

        public CoopPlayback Playback { get; }

        public void Attach(PersonalMusic source, ShopTerminal shop)
        {
            if (music == source && runner == shop.Runner && Playback.Manages(source))
                return;

            music = source;
            terminal = shop;
            runner = shop.Runner;
            downloads.Clear();
            Playback.Attach(source, shop);
        }

        private bool Ready => !disabled && StoreSteam.Ready && terminal != null && runner != null &&
            runner.IsRunning && terminal.MusicReady;

        public bool CanSend => Ready && sessionRequests != null;

        public bool TryDownload(PersonalMusic source, PlayerRef owner, long hash, int missing, int total, float now)
        {
            if (!CanSend || source != music || !Playback.TryTrack(hash, out var track) || track.Owner != owner)
                return false;

            if (!TrySteamId(owner, out CSteamID peer))
                return false;

            if (!downloads.TryGetValue(hash, out PartDownload download) || download.Peer != peer)
            {
                download = new PartDownload { Peer = peer, LastProgress = now, NextRequest = now, NextWindow = missing, LastMissing = missing };
                downloads[hash] = download;
            }

            if (download.Failed)
                return false;

            if (missing != download.LastMissing)
            {
                download.LastMissing = missing;
                download.LastProgress = now;
            }

            if (now - download.LastProgress >= 8f)
            {
                download.Failed = true;
                log.LogInfo($"Steam transfer stalled for song {hash:x16}. Resuming through the game transport.");
                return false;
            }

            if (missing >= download.NextWindow || now >= download.NextRequest)
            {
                int count = Math.Min(Window, total - missing);
                EResult result;
                try
                {
                    result = Send(peer, Request, hash, missing, count, null, 0, 0);
                }
                catch (Exception error)
                {
                    Disable(error);
                    return false;
                }

                download.NextRequest = now + 2.5f;
                download.NextWindow = missing + count;
                if (result != EResult.k_EResultOK)
                    log.LogDebug($"Steam music request returned {result}.");
            }

            return true;
        }

        public void Poll()
        {
            if (!Ready)
            {
                Playback.Tick();
                return;
            }

            try
            {
                if (sessionRequests == null)
                {
                    localId = SteamUser.GetSteamID();
                    SteamNetworkingUtils.InitRelayNetworkAccess();
                    sessionRequests = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(AcceptCurrentPlayer);
                }

                int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, messages, messages.Length);
                for (int index = 0; index < count; index++)
                {
                    try
                    {
                        ReadMessage(SteamNetworkingMessage_t.FromIntPtr(messages[index]));
                    }
                    catch (Exception error)
                    {
                        log.LogWarning("Steam music message failed: " + error.Message);
                    }
                    finally
                    {
                        SteamNetworkingMessage_t.Release(messages[index]);
                    }
                }

                Playback.Tick();
            }
            catch (Exception error)
            {
                Disable(error);
            }
        }

        private void Disable(Exception error)
        {
            disabled = true;
            log.LogWarning("Steam transfer unavailable. Using game transport: " + error.Message);
        }

        public bool TrySteamId(PlayerRef player, out CSteamID steamId)
        {
            steamId = default;
            if (player == PlayerRef.None || player == runner.LocalPlayer ||
                !ulong.TryParse(runner.GetPlayerUserId(player), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
                return false;

            steamId = new CSteamID(id);
            return steamId != localId && steamId.IsValid() && steamId.GetEAccountType() == EAccountType.k_EAccountTypeIndividual;
        }

        private bool InCurrentShop(CSteamID sender)
        {
            foreach (PlayerRef player in runner.ActivePlayers)
                if (TrySteamId(player, out CSteamID steamId) && steamId == sender)
                    return true;

            return false;
        }

        private void AcceptCurrentPlayer(SteamNetworkingMessagesSessionRequest_t request)
        {
            if (Ready && InCurrentShop(request.m_identityRemote.GetSteamID()))
                SteamNetworkingMessages.AcceptSessionWithUser(ref request.m_identityRemote);
        }

        private void ReadMessage(SteamNetworkingMessage_t message)
        {
            int size = message.m_cbSize;
            if (size < HeaderSize || size > packet.Length)
                return;

            Marshal.Copy(message.m_pData, packet, 0, size);
            int type = BitConverter.ToInt32(packet, 0);
            long hash = BitConverter.ToInt64(packet, 4);
            int index = BitConverter.ToInt32(packet, 12);
            int count = BitConverter.ToInt32(packet, 16);
            CSteamID sender = message.m_identityPeer.GetSteamID();
            if (index < 0 || index >= MaxParts || !Playback.TryTrack(hash, out var track))
                return;

            if (type == Request && size == HeaderSize && count >= 1 && count <= Window &&
                track.Owner == runner.LocalPlayer && InCurrentShop(sender))
            {
                if (!Playback.TryGetBytes(hash, out byte[] bytes) || bytes.Length != track.Bytes)
                    return;

                int total = (bytes.Length + PartSize - 1) / PartSize;
                for (int part = index; part < Math.Min(total, index + count); part++)
                {
                    int offset = part * PartSize;
                    if (Send(sender, Data, hash, part, total, bytes, offset, Math.Min(PartSize, bytes.Length - offset)) != EResult.k_EResultOK)
                        break;
                }
            }
            else if (type == Data && downloads.TryGetValue(hash, out PartDownload download) && sender == download.Peer &&
                TrySteamId(track.Owner, out CSteamID owner) && sender == owner &&
                count == (track.Bytes + PartSize - 1) / PartSize && index < count)
            {
                int length = size - HeaderSize;
                if (length != Math.Min(PartSize, track.Bytes - index * PartSize))
                    return;

                var data = new byte[length];
                Buffer.BlockCopy(packet, HeaderSize, data, 0, length);
                music.PartByRpc(runner.LocalPlayer, hash, index, count, track.Format, data);
            }
        }

        public void ForgetDownload(long hash) => downloads.Remove(hash);

        private unsafe EResult Send(CSteamID user, int type, long hash, int index, int count, byte[] data, int offset, int length)
        {
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(user);
            fixed (byte* start = packet)
            {
                *(int*)start = type;
                *(long*)(start + 4) = hash;
                *(int*)(start + 12) = index;
                *(int*)(start + 16) = count;
                if (length != 0)
                    Buffer.BlockCopy(data, offset, packet, HeaderSize, length);

                return SteamNetworkingMessages.SendMessageToUser(ref identity, (IntPtr)start, (uint)(HeaderSize + length), SendFlags, Channel);
            }
        }

        public void Dispose()
        {
            Playback.Dispose();
            sessionRequests?.Dispose();
        }
    }
}
