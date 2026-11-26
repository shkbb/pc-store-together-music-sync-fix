using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using Fusion;
using HarmonyLib;
using PcStore.Audio;
using PcStore.Shop;
using PcStore.World;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

namespace PcStoreMusicSync;

public sealed partial class MusicSyncFix
{
    private static readonly MethodInfo ShareMethod = AccessTools.DeclaredMethod(typeof(ShopTerminal), "Share");
    private static readonly Func<ShopTerminal, float> ReadStartTime =
        (Func<ShopTerminal, float>)AccessTools.DeclaredPropertyGetter(typeof(ShopTerminal), "MusicStartedAt")
            .CreateDelegate(typeof(Func<ShopTerminal, float>));
    private static readonly Action<ShopTerminal, float> WriteStartTime =
        (Action<ShopTerminal, float>)AccessTools.DeclaredPropertySetter(typeof(ShopTerminal), "MusicStartedAt")
            .CreateDelegate(typeof(Action<ShopTerminal, float>));
    private static readonly Action<ShopTerminal, int> WriteStep =
        (Action<ShopTerminal, int>)AccessTools.DeclaredPropertySetter(typeof(ShopTerminal), nameof(ShopTerminal.SharedStep))
            .CreateDelegate(typeof(Action<ShopTerminal, int>));
    private static readonly Action<ShopTerminal, int> WriteStepOf =
        (Action<ShopTerminal, int>)AccessTools.DeclaredPropertySetter(typeof(ShopTerminal), nameof(ShopTerminal.SharedStepOf))
            .CreateDelegate(typeof(Action<ShopTerminal, int>));
    private static readonly Action<PersonalMusic, PlayerRef, long, int, int> ExpectTrack =
        (Action<PersonalMusic, PlayerRef, long, int, int>)AccessTools.DeclaredMethod(typeof(PersonalMusic), "Expect")
            .CreateDelegate(typeof(Action<PersonalMusic, PlayerRef, long, int, int>));

    private void PatchPlayback()
    {
        Patch(typeof(ShopTerminal), "Share", prefix: nameof(StageShare));
        harmony.Patch(ShareMethod, transpiler: new HarmonyMethod(typeof(MusicSyncFix), nameof(UseQueuedAuthorization)));
        Patch(typeof(ShopTerminal), "AdvanceMusic", prefix: nameof(WaitForPreparation), postfix: nameof(PrepareAutoNext));
        Patch(typeof(ShopTerminal), "ChangeMusic", prefix: nameof(BeforeMusicControl), postfix: nameof(AfterMusicControl));
        Patch(typeof(ShopTerminal), nameof(ShopTerminal.RequestShare), prefix: nameof(RememberLocalSelection));
        Patch(typeof(ShopTerminal), nameof(ShopTerminal.RPC_OfferSong), prefix: nameof(RecordLocalOffer));
        Patch(typeof(PersonalMusic), nameof(PersonalMusic.Offered), postfix: nameof(RecordRemoteOffer));
        Patch(typeof(PersonalMusic), "Want", prefix: nameof(LoadPersonalClip));
        Patch(typeof(PersonalMusic), "ClipOf", prefix: nameof(LoadPersonalClip));
        Patch(typeof(PersonalMusic), nameof(PersonalMusic.ClipFor), prefix: nameof(LoadSharedClip));
        Patch(typeof(MusicPlayer), "Update", prefix: nameof(WaitForStartTime));
        Patch(typeof(MusicPlayerPanel), "Refresh", postfix: nameof(ShowPreparationStatus));
    }

    private static CoopPlayback Playback => instance?.transfer?.Playback;

    private static bool StageShare(ShopTerminal __instance, PlayerRef source, long hash, int bytes, int format, float length, string title)
    {
        var playback = Playback;
        if (playback == null || playback.Committing(__instance, source) || !__instance.MusicReady ||
            !__instance.HasStateAuthority || __instance.Runner.GameMode != GameMode.Shared || !CoopPlayback.HasPeers(__instance.Runner))
            return true;

        var track = new SharedTrack(source, hash, bytes, format);
        if (track.Valid && !float.IsNaN(length) && length >= 1f && length <= 1800f &&
            (__instance.UsesOs(source) || (__instance.SharedStepPending && __instance.SharedOwner == source)))
        {
            instance.transfer.Attach(PersonalMusic.For(__instance), __instance);
            playback.Queue(track, length, title);
        }

        return false;
    }

    private static IEnumerable<CodeInstruction> UseQueuedAuthorization(IEnumerable<CodeInstruction> instructions)
    {
        var usesOs = AccessTools.Method(typeof(ShopTerminal), nameof(ShopTerminal.UsesOs));
        var replacement = AccessTools.Method(typeof(MusicSyncFix), nameof(CanPlayQueuedTrack));
        int replacements = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(usesOs))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                replacements++;
            }

            yield return instruction;
        }

        if (replacements != 1)
            throw new InvalidOperationException("The game's shared music authorization has changed.");
    }

    private static bool CanPlayQueuedTrack(ShopTerminal terminal, PlayerRef source) =>
        terminal.UsesOs(source) || Playback?.Committing(terminal, source) == true;

    private static void PrepareAutoNext(ShopTerminal __instance) => Playback?.PrepareAutoNext(__instance);

    private static bool WaitForPreparation(ShopTerminal __instance) => Playback?.ShouldAdvance(__instance) != false;

    private static void BeforeMusicControl(ShopTerminal __instance, ShopTerminal.MusicAction action, out float? __state)
    {
        __state = null;
        if (action == ShopTerminal.MusicAction.Select || action == ShopTerminal.MusicAction.Toggle || action == ShopTerminal.MusicAction.Seek)
            Playback?.CancelForControl(__instance, action);

        if ((action == ShopTerminal.MusicAction.Volume || action == ShopTerminal.MusicAction.Output) &&
            __instance.MusicReady && __instance.MusicShared && __instance.MusicRunning)
        {
            float start = ReadStartTime(__instance);
            if (start > __instance.Runner.SimulationTime)
                __state = start;
        }
    }

    private static void AfterMusicControl(ShopTerminal __instance, float? __state)
    {
        if (__state.HasValue)
            WriteStartTime(__instance, __state.Value);
    }

    private static void RecordLocalOffer(ShopTerminal __instance, long hash, int bytes, int format)
    {
        if (bytes > 0 && __instance.MusicReady && __instance.MusicShared && __instance.SharedOwner == __instance.Runner.LocalPlayer)
            Playback?.Offer(__instance, new SharedTrack(__instance.SharedOwner, hash, bytes, format));
    }

    private static void RecordRemoteOffer(PersonalMusic __instance, PlayerRef owner, long hash, int bytes, int format)
    {
        ShopTerminal shop = Terminal(__instance);
        if (bytes < 0)
        {
            Playback?.HandleControl(shop, owner, hash, bytes, format);
            return;
        }

        Playback?.Offer(shop, new SharedTrack(owner, hash, bytes, format));
    }

    private static void RememberLocalSelection(ShopTerminal __instance, long hash) => Playback?.Remember(__instance, hash);

    private static bool LoadPersonalClip(PersonalMusic __instance, string path, ref AudioClip __result)
    {
        if (Playback?.Manages(__instance) != true)
            return true;

        __result = Playback.LoadPath(path);
        return false;
    }

    private static bool LoadSharedClip(PersonalMusic __instance, long hash, ref AudioClip __result)
    {
        if (Playback?.Manages(__instance) != true)
            return true;

        __result = Playback.LoadHash(hash, audible: true);
        return false;
    }

    private static bool WaitForStartTime(ShopTerminal ___terminal) =>
        ___terminal == null || !___terminal.MusicReady || !___terminal.MusicShared ||
        !___terminal.MusicRunning || ___terminal.Runner.SimulationTime >= ReadStartTime(___terminal);

    private static void ShowPreparationStatus(ShopTerminal ___terminal, TMP_Text ___time, ref int ___lastSeconds)
    {
        string status = Playback?.StatusFor(___terminal);
        if (status != null && ___time != null)
        {
            ___time.text = status;
            ___lastSeconds = int.MinValue;
        }
    }

    private readonly struct SharedTrack
    {
        public SharedTrack(PlayerRef owner, long hash, int bytes, int format)
        {
            Owner = owner;
            Hash = hash;
            Bytes = bytes;
            Format = format;
        }

        public PlayerRef Owner { get; }
        public long Hash { get; }
        public int Bytes { get; }
        public int Format { get; }
        public bool Valid => Owner != PlayerRef.None && Hash != 0 && Bytes > 0 && Bytes <= 8 * 1024 * 1024 && Format >= 1 && Format <= 3;
    }

    private sealed class CoopPlayback : IDisposable
    {
        private enum Control { Prepare = 1, Ready = 2, Cancel = 3 }
        private const float PrepareTimeout = 30f;
        private const float StartLead = 0.8f;
        private const float AutoLead = 4f;
        private const float RetryControl = 1.5f;
        private const float NoticeDuration = 6f;

        private static readonly AccessTools.FieldRef<PersonalMusic, Dictionary<long, string>> OwnPaths =
            AccessTools.FieldRefAccess<PersonalMusic, Dictionary<long, string>>("ownPath");
        private static readonly AccessTools.FieldRef<PersonalMusic, Dictionary<long, string>> Copies =
            AccessTools.FieldRefAccess<PersonalMusic, Dictionary<long, string>>("copies");

        private readonly SteamMusicTransfer transport;
        private readonly ManualLogSource log;
        private PersonalMusic music;
        private ShopTerminal terminal;
        private NetworkRunner runner;
        private TrackClips clips;
        private PendingTrack pending;
        private PendingTrack committing;
        private SharedTrack offered;
        private byte[] offeredBytes;
        private long selectedHash;
        private byte[] selectedBytes;
        private int offerForSong;
        private long lastActiveHash;
        private int earlySong = -1;
        private float earlyStart;
        private int nextToken = Environment.TickCount;
        private string notice;
        private float noticeUntil;

        private sealed class PendingTrack
        {
            public SharedTrack Track;
            public PlayerRef Authority;
            public int Token;
            public int PreviousSong;
            public float Deadline;
            public float NextMessage;
            public float Length;
            public string Title;
            public float EarliestStart;
            public readonly HashSet<PlayerRef> ReadyPeers = new HashSet<PlayerRef>();
        }

        private readonly struct ControlFields
        {
            public ControlFields(int flags, int header)
            {
                Type = (Control)((flags >> 26) & 3);
                Token = (int)((uint)header >> 16);
                Owner = header & 0xffff;
                PreviousSong = (flags >> 28) & 7;
                Value = flags & 0xffffff;
                Format = (flags >> 24) & 3;
            }

            public readonly Control Type;
            public readonly int Token;
            public readonly int Owner;
            public readonly int PreviousSong;
            public readonly int Value;
            public readonly int Format;

            public static int Flags(Control type, int previousSong, int format, int value) =>
                unchecked((int)(0x80000000u | ((uint)(previousSong & 7) << 28) | ((uint)type << 26) |
                    ((uint)format << 24) | (uint)value));
        }

        public CoopPlayback(SteamMusicTransfer transport, ManualLogSource log)
        {
            this.transport = transport;
            this.log = log;
        }

        private bool Running => terminal != null && runner != null && runner.IsRunning && terminal.MusicReady;
        private bool HasOffer => Running && offered.Hash != 0 && terminal.MusicShared && terminal.SharedSong == offerForSong;
        public long PendingHash => pending?.Track.Hash ?? 0;

        public void Attach(PersonalMusic source, ShopTerminal shop)
        {
            Dispose();
            music = source;
            terminal = shop;
            runner = shop.Runner;
            clips = new TrackClips(source, log);
            offered = default;
            offeredBytes = null;
            selectedHash = 0;
            selectedBytes = null;
            lastActiveHash = 0;
            earlySong = -1;
            notice = null;
        }

        public static bool HasPeers(NetworkRunner network)
        {
            foreach (PlayerRef player in network.ActivePlayers)
                if (player != network.LocalPlayer)
                    return true;

            return false;
        }

        public bool Manages(PersonalMusic source) => source == music && Running;

        public bool Committing(ShopTerminal shop, PlayerRef owner) => shop == terminal && committing?.Track.Owner == owner;

        public AudioClip LoadPath(string path) => clips.Get(path, audible: false);

        public AudioClip LoadHash(long hash, bool audible = false)
        {
            if (!OwnPaths(music).TryGetValue(hash, out string path))
                Copies(music).TryGetValue(hash, out path);

            return clips.Get(path, audible);
        }

        public void Remember(ShopTerminal shop, long hash)
        {
            if (shop == terminal && Running && OwnBytes(music).TryGetValue(hash, out byte[] bytes))
            {
                selectedHash = hash;
                selectedBytes = bytes;
            }
        }

        public bool TryGetBytes(long hash, out byte[] bytes)
        {
            if (OwnBytes(music).TryGetValue(hash, out bytes))
                return true;

            bytes = hash == selectedHash ? selectedBytes : hash == offered.Hash ? offeredBytes : null;
            return bytes != null;
        }

        public void Offer(ShopTerminal shop, SharedTrack track)
        {
            if (shop != terminal || !Running || !track.Valid || !terminal.MusicShared ||
                terminal.SharedOwner != track.Owner || terminal.SharedHash == track.Hash)
                return;

            if (offered.Hash != 0 && offered.Hash != track.Hash && offered.Hash != PendingHash)
                Forget(offered.Hash);

            offered = track;
            offerForSong = terminal.SharedSong;
            OwnBytes(music).TryGetValue(track.Hash, out offeredBytes);
        }

        public bool TryTrack(long hash, out SharedTrack track)
        {
            if (Running && terminal.MusicShared && terminal.SharedHash == hash)
                track = new SharedTrack(terminal.SharedOwner, hash, terminal.SharedBytes, terminal.SharedFormat);
            else if (pending != null && pending.Track.Hash == hash)
                track = pending.Track;
            else if (HasOffer && offered.Hash == hash)
                track = offered;
            else
                track = default;

            return track.Valid;
        }

        public void Queue(SharedTrack track, float length, string title)
        {
            bool auto = earlySong == terminal.SharedSong && terminal.MusicShared && terminal.SharedStepPending &&
                terminal.SharedOwner == track.Owner && HasOffer && offered.Hash == track.Hash;
            ClearPending();
            notice = null;
            pending = new PendingTrack
            {
                Track = track,
                Authority = runner.LocalPlayer,
                Token = unchecked(++nextToken) & 0xffff,
                PreviousSong = terminal.SharedSong,
                Deadline = Time.unscaledTime + PrepareTimeout,
                Length = length,
                Title = title,
                EarliestStart = auto ? earlyStart : 0f
            };
            ExpectTrack(music, track.Owner, track.Hash, track.Bytes, track.Format);
            log.LogInfo($"Preparing shared track {track.Hash:x16}.");
        }

        public void PrepareAutoNext(ShopTerminal shop)
        {
            if (shop != terminal || !Running || !shop.HasStateAuthority || !shop.MusicRunning || !HasOffer ||
                !HasPeers(runner) || pending != null || shop.SharedStepPending)
                return;

            float remaining = shop.SharedLength - shop.MusicPosition;
            if (remaining <= 0f || remaining > AutoLead)
                return;

            earlySong = shop.SharedSong;
            earlyStart = runner.SimulationTime + remaining;
            WriteStep(shop, 1);
            WriteStepOf(shop, shop.SharedSong);
        }

        public bool ShouldAdvance(ShopTerminal shop) =>
            shop != terminal || !Running || pending == null || !shop.MusicShared;

        public void CancelForControl(ShopTerminal shop, ShopTerminal.MusicAction action)
        {
            if (shop != terminal || !shop.HasStateAuthority || pending == null)
                return;

            // Seeking the current track only cancels a transition tied to its end time.
            if (action != ShopTerminal.MusicAction.Seek || pending.EarliestStart != 0f)
                CancelPending(timedOut: false);
        }

        public string StatusFor(ShopTerminal shop)
        {
            if (shop == null || shop != terminal || !Running)
                return null;

            return pending != null ? "Preparing track…" : Time.unscaledTime < noticeUntil ? notice : null;
        }

        // Negative byte counts are ignored by the unmodified game. Small control messages use
        // the offer RPC so coordinating the start does not depend on the file transport.
        private void SendControl(Control type, PendingTrack request, int value = 0)
        {
            int owner = request.Track.Owner.PlayerId;
            if (owner < 0 || owner > ushort.MaxValue)
                return;

            int flags = ControlFields.Flags(type, request.PreviousSong, request.Track.Format, value);
            int header = (request.Token << 16) | owner;
            terminal.RPC_OfferSong(request.Track.Hash, flags, header);
        }

        public void HandleControl(ShopTerminal shop, PlayerRef sender, long hash, int flags, int header)
        {
            if (shop != terminal || !Running || sender == PlayerRef.None)
                return;

            var fields = new ControlFields(flags, header);
            if (fields.Type == Control.Ready && fields.Value == 0 && terminal.HasStateAuthority &&
                SameRequest(hash, fields.Token) && (pending.PreviousSong & 7) == fields.PreviousSong &&
                pending.Track.Owner.PlayerId == fields.Owner && TryFindOwner(sender.PlayerId, out PlayerRef active) && active == sender)
            {
                pending.ReadyPeers.Add(sender);
                return;
            }

            PlayerRef authority = terminal.Object.StateAuthority;
            if (terminal.HasStateAuthority || sender != authority)
                return;

            if (fields.Type == Control.Cancel && SameRequest(hash, fields.Token))
            {
                ClearPending();
                if (fields.Value == 1)
                    ShowTimeout();
                return;
            }

            if (fields.Type != Control.Prepare || SameRequest(hash, fields.Token) || (terminal.SharedSong & 7) != fields.PreviousSong ||
                !TryFindOwner(fields.Owner, out PlayerRef owner))
                return;

            var track = new SharedTrack(owner, hash, fields.Value, fields.Format);
            if (!track.Valid)
                return;

            ClearPending();
            notice = null;
            pending = new PendingTrack
            {
                Track = track,
                Authority = authority,
                Token = fields.Token,
                PreviousSong = terminal.SharedSong,
                Deadline = Time.unscaledTime + PrepareTimeout + RetryControl
            };
            ExpectTrack(music, owner, hash, track.Bytes, track.Format);
        }

        private bool SameRequest(long hash, int token) => pending != null && pending.Track.Hash == hash && pending.Token == token;

        private bool TryFindOwner(int playerId, out PlayerRef owner)
        {
            foreach (PlayerRef player in runner.ActivePlayers)
            {
                if (player.PlayerId == playerId)
                {
                    owner = player;
                    return true;
                }
            }

            owner = PlayerRef.None;
            return false;
        }

        public void Tick()
        {
            if (!Running)
            {
                if (terminal == null || runner == null || !runner.IsRunning)
                    Dispose();
                return;
            }

            long active = terminal.MusicShared ? terminal.SharedHash : 0;
            if (active != lastActiveHash)
            {
                if (lastActiveHash != PendingHash)
                    transport.ForgetDownload(lastActiveHash);
                lastActiveHash = active;
            }

            if (HasOffer)
                LoadHash(offered.Hash);
            else if (offered.Hash != 0)
            {
                if (offered.Hash != PendingHash && offered.Hash != active)
                    Forget(offered.Hash);
                offered = default;
                offeredBytes = null;
            }

            if (pending == null)
                return;

            if (terminal.Object.StateAuthority != pending.Authority || terminal.SharedSong != pending.PreviousSong ||
                !TryFindOwner(pending.Track.Owner.PlayerId, out _))
            {
                ClearPending();
                return;
            }

            float now = Time.unscaledTime;
            if (now >= pending.Deadline)
            {
                CancelPending(timedOut: true);
                return;
            }

            AudioClip clip = LoadHash(pending.Track.Hash);
            bool localReady = clip != null && clip.loadState == AudioDataLoadState.Loaded;
            bool send = now >= pending.NextMessage;

            if (terminal.HasStateAuthority)
                TickAuthority(localReady, send);
            else if (localReady && send)
            {
                pending.NextMessage = now + RetryControl;
                SendControl(Control.Ready, pending);
            }
        }

        private void TickAuthority(bool localReady, bool send)
        {
            var request = pending;
            bool peersReady = true;
            foreach (PlayerRef player in runner.ActivePlayers)
            {
                if (player != runner.LocalPlayer && !request.ReadyPeers.Contains(player))
                {
                    peersReady = false;
                    break;
                }
            }
            if (!peersReady && send)
            {
                request.NextMessage = Time.unscaledTime + RetryControl;
                SendControl(Control.Prepare, request, request.Track.Bytes);
            }

            float? scheduled = ScheduleStart(localReady, peersReady, runner.SimulationTime, request.EarliestStart);
            if (!scheduled.HasValue)
                return;

            committing = request;
            try
            {
                int previous = terminal.SharedSong;
                ShareMethod.Invoke(terminal, new object[] { request.Track.Owner, request.Track.Hash, request.Track.Bytes, request.Track.Format, request.Length, request.Title });
                if (terminal.SharedSong != previous)
                {
                    float start = scheduled.Value;
                    WriteStartTime(terminal, start);
                    log.LogInfo($"All players prepared track {request.Track.Hash:x16}. Shared start at {start:F2}.");
                }
            }
            finally
            {
                committing = null;
                ClearPending();
            }
        }

        private static float? ScheduleStart(bool localReady, bool peersReady, float now, float earliest)
        {
            if (!localReady || !peersReady || now < earliest - StartLead)
                return null;

            return Math.Max(now + StartLead, earliest);
        }

        private void CancelPending(bool timedOut)
        {
            var request = pending;
            if (request == null)
                return;

            if (terminal.HasStateAuthority)
                SendControl(Control.Cancel, request, timedOut ? 1 : 0);

            ClearPending();
            if (timedOut)
            {
                log.LogInfo($"Preparation timed out for track {request.Track.Hash:x16}.");
                ShowTimeout();
            }
        }

        private void ShowTimeout()
        {
            notice = "Track couldn't finish preparing. Try again.";
            noticeUntil = Time.unscaledTime + NoticeDuration;
        }

        private void ClearPending()
        {
            long hash = PendingHash;
            pending = null;
            if (hash != 0 && (!terminal.MusicShared || terminal.SharedHash != hash) && (!HasOffer || offered.Hash != hash))
                Forget(hash);
        }

        private void Forget(long hash)
        {
            transport.ForgetDownload(hash);
            var incoming = (IDictionary)IncomingTransfers.GetValue(music);
            object item = incoming[hash];
            if (item != null && !(bool)Done.GetValue(item))
                incoming.Remove(hash);
        }

        public void Dispose()
        {
            clips?.Dispose();
            clips = null;
            pending = null;
            committing = null;
            music = null;
            terminal = null;
            runner = null;
        }

        private sealed class TrackClips : IDisposable
        {
            private const int Capacity = 5;
            private static readonly AccessTools.FieldRef<PersonalMusic, HashSet<string>> Failed =
                AccessTools.FieldRefAccess<PersonalMusic, HashSet<string>>("failed");
            private static readonly AccessTools.FieldRef<PersonalMusic, HashSet<long>> BadCopies =
                AccessTools.FieldRefAccess<PersonalMusic, HashSet<long>>("badCopies");
            private static readonly AccessTools.FieldRef<PersonalMusic, Dictionary<string, float>> Lengths =
                AccessTools.FieldRefAccess<PersonalMusic, Dictionary<string, float>>("lengths");
            private static readonly Action<PersonalMusic, int> SetRevision =
                (Action<PersonalMusic, int>)AccessTools.DeclaredPropertySetter(typeof(PersonalMusic), nameof(PersonalMusic.Revision))
                    .CreateDelegate(typeof(Action<PersonalMusic, int>));

            private readonly PersonalMusic music;
            private readonly ManualLogSource log;
            private readonly Dictionary<string, ClipEntry> entries = new Dictionary<string, ClipEntry>(StringComparer.OrdinalIgnoreCase);
            private string playing;
            private int access;

            private sealed class ClipEntry
            {
                public UnityWebRequest Request;
                public AudioClip Clip;
                public int Access;
            }

            public TrackClips(PersonalMusic music, ManualLogSource log)
            {
                this.music = music;
                this.log = log;
            }

            public AudioClip Get(string path, bool audible)
            {
                if (path == null || Failed(music).Contains(path))
                    return null;

                if (!entries.TryGetValue(path, out ClipEntry entry))
                {
                    if (entries.Count >= Capacity)
                        Evict();

                    var type = Path.GetExtension(path).ToLowerInvariant() switch
                    {
                        ".wav" => AudioType.WAV,
                        ".ogg" => AudioType.OGGVORBIS,
                        _ => AudioType.MPEG
                    };
                    var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, type);
                    if (request.downloadHandler is DownloadHandlerAudioClip handler)
                    {
                        handler.streamAudio = false;
                        handler.compressed = type != AudioType.WAV;
                    }
                    entry = new ClipEntry { Request = request };
                    entries.Add(path, entry);
                    request.SendWebRequest();
                }

                entry.Access = ++access;
                if (entry.Request?.isDone == true)
                    Complete(path, entry);

                if (audible && entry.Clip != null)
                    playing = path;

                return entry.Clip;
            }

            private void Complete(string path, ClipEntry entry)
            {
                try
                {
                    if (entry.Request.result == UnityWebRequest.Result.Success)
                        entry.Clip = DownloadHandlerAudioClip.GetContent(entry.Request);
                }
                catch (Exception error)
                {
                    log.LogWarning("Could not decode shared audio: " + error.Message);
                }

                entry.Request.Dispose();
                entry.Request = null;
                if (entry.Clip != null && (entry.Clip.length <= 0f || entry.Clip.loadState == AudioDataLoadState.Failed))
                {
                    UnityEngine.Object.Destroy(entry.Clip);
                    entry.Clip = null;
                }

                if (entry.Clip != null)
                {
                    entry.Clip.name = Path.GetFileNameWithoutExtension(path);
                    Lengths(music)[path] = entry.Clip.length;
                }
                else
                {
                    entries.Remove(path);
                    Failed(music).Add(path);
                    foreach (var copy in Copies(music))
                        if (StringComparer.OrdinalIgnoreCase.Equals(copy.Value, path))
                            BadCopies(music).Add(copy.Key);
                }

                SetRevision(music, music.Revision + 1);
            }

            private void Evict()
            {
                string oldest = null;
                int earliest = int.MaxValue;
                foreach (var entry in entries)
                {
                    if (StringComparer.OrdinalIgnoreCase.Equals(entry.Key, playing) || entry.Value.Access >= earliest)
                        continue;

                    oldest = entry.Key;
                    earliest = entry.Value.Access;
                }

                if (oldest != null)
                {
                    Release(entries[oldest]);
                    entries.Remove(oldest);
                }
            }

            private static void Release(ClipEntry entry)
            {
                if (entry.Request != null)
                {
                    entry.Request.Abort();
                    entry.Request.Dispose();
                }
                if (entry.Clip != null)
                    UnityEngine.Object.Destroy(entry.Clip);
            }

            public void Dispose()
            {
                foreach (ClipEntry entry in entries.Values)
                    Release(entry);
                entries.Clear();
            }
        }
    }
}
