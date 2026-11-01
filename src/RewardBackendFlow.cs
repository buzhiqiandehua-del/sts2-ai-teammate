using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AiTeammate
{
    // BS4: keep DRA installed; replace only its merged combat reward transaction.
    // The synchronizer's native per-owner state and selection Task remain authoritative.
    public static class RewardBackendFlow
    {
        private static readonly Dictionary<Reward, RewardsSet> Owners = new Dictionary<Reward, RewardsSet>();
        private static readonly Dictionary<Reward, Task> Selections = new Dictionary<Reward, Task>();
        private static readonly HashSet<Reward> Decided = new HashSet<Reward>();
        private static readonly HashSet<NRewardsScreen> Screens = new HashSet<NRewardsScreen>();
        private static readonly Dictionary<NCardRewardSelectionScreen, CardReward> CardScreens = new Dictionary<NCardRewardSelectionScreen, CardReward>();
        private static readonly HashSet<NCardRewardSelectionScreen> SubmittedScreens = new HashSet<NCardRewardSelectionScreen>();
        private static readonly Dictionary<CardReward, string> CardDecisions = new Dictionary<CardReward, string>();
        private static readonly HashSet<CardReward> ManualRewards = new HashSet<CardReward>();
        private static readonly HashSet<Reward> ResolvedRewards = new HashSet<Reward>();
        private static readonly HashSet<string> MoveProducers = new HashSet<string>();
        private static readonly HashSet<string> MoveEnqueues = new HashSet<string>();
        private static readonly HashSet<string> PendingMoveEnqueues = new HashSet<string>();
        private static string MoveGeneration;
        private static readonly System.Threading.SemaphoreSlim SelectionSerial = new System.Threading.SemaphoreSlim(1, 1);
        [ThreadStatic] private static CardReward SelectingCard;
        private sealed class SetRegistration
        {
            public RewardsSet Set;
            public int Generation;
            public NRewardsScreen Screen;
            public Task Backend;
        }
        private static readonly Dictionary<RewardsSet, SetRegistration> Sets = new Dictionary<RewardsSet, SetRegistration>();
        private static readonly Dictionary<Reward, int> RewardGenerations = new Dictionary<Reward, int>();
        private static readonly HashSet<object> RetiredScreens = new HashSet<object>();
        private static readonly HashSet<NRewardsScreen> AutoStartedScreens = new HashSet<NRewardsScreen>();
        private static CombatRoom ActiveRoom;
        private static Task Producer;
        private static RewardsSet ActiveOwnerSet;
        private static int Generation;
        public static int RouteEpoch { get; private set; }
        private static TaskCompletionSource<bool> Cancelled = new TaskCompletionSource<bool>();

        private static RewardsSet ScreenSet(NRewardsScreen screen)
        {
            return screen == null ? null : AccessTools.Field(typeof(NRewardsScreen), "_rewardsSet").GetValue(screen) as RewardsSet;
        }
        public static void LogScreen(string stage, object screen, bool allow)
        {
            NRewardsScreen rewards = screen as NRewardsScreen;
            RewardsSet set = ScreenSet(rewards);
            CardReward card = null;
            if (screen is NCardRewardSelectionScreen && !CardScreens.TryGetValue((NCardRewardSelectionScreen)screen, out card)) card = SelectingCard;
            ulong owner = set != null ? set.Player.NetId : (card == null ? 0 : card.Player.NetId);
            bool managed = (set != null && Sets.ContainsKey(set)) || (card != null && Owns(card));
            int generation = Generation;
            if (set != null && Sets.ContainsKey(set)) generation = Sets[set].Generation;
            GD.Print("[AiTeammate][Flow] reward-overlay stage=" + stage + " screen=" + (screen == null ? "null" : screen.GetType().FullName) + " owner=" + owner + " managed=" + managed + " generation=" + generation + " allow=" + allow + " suppress=" + !allow);
        }

        public static bool IsManagedScreen(NRewardsScreen screen) { RewardsSet set = ScreenSet(screen); return set != null && Sets.ContainsKey(set) && Sets[set].Generation == Generation; }
        public static bool Owns(Reward reward) { return reward != null && Owners.ContainsKey(reward); }
        public static bool AllowNativeRewardOverlay(object screen)
        {
            if (RetiredScreens.Contains(screen)) return false;
            NRewardsScreen rewards = screen as NRewardsScreen;
            if (rewards != null && IsManagedScreen(rewards)) return ScreenSet(rewards).Player.NetId == TurnCoordinator.GetPlayer1NetId() || Object.ReferenceEquals(ScreenSet(rewards), ActiveOwnerSet);
            CardReward card;
            ulong p2Net = TurnCoordinator.GetPlayer2NetId();
            return screen is NCardRewardSelectionScreen && ((SelectingCard != null && (Owns(SelectingCard) || (p2Net != 0 && SelectingCard.Player != null && SelectingCard.Player.NetId == p2Net))) || (CardScreens.TryGetValue((NCardRewardSelectionScreen)screen, out card) && (Owns(card) || (p2Net != 0 && card.Player != null && card.Player.NetId == p2Net))));
        }

        public static void Install(Harmony harmony)
        {
            MethodInfo actual = AccessTools.Method(typeof(CombatRoom), "OfferRoomEndRewards", Type.EmptyTypes);
            if (actual == null) throw new MissingMethodException("CombatRoom.OfferRoomEndRewards()");
            harmony.Patch(actual, new HarmonyMethod(typeof(RewardBackendFlow), "RoomOfferPrefix") { priority = Priority.First });
            Type actualDra = AccessTools.TypeByName("LocalMultiControl.Scripts.Patch.CombatRoomOfferRewardsPatch");
            MethodInfo draPrefix = actualDra == null ? null : AccessTools.Method(actualDra, "Prefix");
            if (draPrefix == null) throw new MissingMethodException("CombatRoomOfferRewardsPatch.Prefix(CombatRoom, ref Task)");
            // Patch the DRA prefix itself too: ordering of foreign Harmony prefixes cannot
            // revive the old producer. Both entry points join the same room preparation task.
            harmony.Patch(draPrefix, new HarmonyMethod(typeof(RewardBackendFlow), "DraRoomOfferPrefix"));
            Type dra = AccessTools.TypeByName("LocalMultiControl.Scripts.Patch.RewardsSetPatch");
            MethodInfo merged = dra != null ? AccessTools.Method(dra, "OfferMergedCombatRewards", new Type[] { typeof(CombatRoom) }) : null;
            if (merged == null) throw new MissingMethodException("DRA OfferMergedCombatRewards(CombatRoom)");
            harmony.Patch(merged, new HarmonyMethod(typeof(RewardBackendFlow), "MergedOfferPrefix"));
            MethodInfo getReward = AccessTools.Method(typeof(NRewardButton), "GetReward");
            harmony.Patch(getReward, new HarmonyMethod(typeof(RewardBackendFlow), "ButtonPrefix") { priority = Priority.First });
            Type legacyButton = AccessTools.TypeByName("LocalMultiControl.Scripts.Patch.NRewardButtonMergedRewardSelectPatch");
            MethodInfo legacyPrefix = legacyButton == null ? null : AccessTools.Method(legacyButton, "Prefix");
            if (legacyPrefix == null) throw new MissingMethodException("DRA merged reward button prefix");
            harmony.Patch(legacyPrefix, new HarmonyMethod(typeof(RewardBackendFlow), "LegacyButtonPrefix"));
            harmony.Patch(AccessTools.Method(typeof(NRewardsScreen), "_Ready"), new HarmonyMethod(typeof(RewardBackendFlow), "ScreenReadyPrefix") { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(NRewardsScreen), "_Ready"), null, new HarmonyMethod(typeof(RewardBackendFlow), "ScreenReadyPostfix"));
            harmony.Patch(AccessTools.Method(typeof(NRewardsScreen), "ShowScreen"), null, new HarmonyMethod(typeof(RewardBackendFlow), "ShowRewardsPostfix"));
            harmony.Patch(AccessTools.Method(typeof(NOverlayStack), "Push"), new HarmonyMethod(typeof(RewardBackendFlow), "OverlayPushPrefix") { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(NOverlayStack), "Remove"), new HarmonyMethod(typeof(RewardBackendFlow), "OverlayRemovePrefix"));
            harmony.Patch(AccessTools.Method(typeof(RunManager), "ClearScreens"), new HarmonyMethod(typeof(RewardBackendFlow), "RetireForRoomLoad"));
            harmony.Patch(AccessTools.Method(typeof(RunManager), "CleanUp"), new HarmonyMethod(typeof(RewardBackendFlow), "RetireForRoomLoad"));
            harmony.Patch(AccessTools.Method(typeof(CombatRoom), "StartPreFinishedCombat"), new HarmonyMethod(typeof(RewardBackendFlow), "FinishedCombatPrefix"));
            harmony.Patch(AccessTools.Method(typeof(NRewardsScreen), "UpdateScreenState"), new HarmonyMethod(typeof(RewardBackendFlow), "UpdateStatePrefix"));
            harmony.Patch(AccessTools.Method(typeof(NRewardsScreen), "OnProceedButtonPressed"), new HarmonyMethod(typeof(RewardBackendFlow), "ProceedPrefix") { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(NCardRewardSelectionScreen), "ShowScreen"), null, new HarmonyMethod(typeof(RewardBackendFlow), "ShowCardScreenPostfix"));
            harmony.Patch(AccessTools.Method(typeof(NCardRewardSelectionScreen), "OptionSelected"), null, new HarmonyMethod(typeof(RewardBackendFlow), "OptionSelectedPostfix"));
            // DRA's vote Postfix writes other players' votes directly and can call MoveToMapCoord
            // even when the original vote Prefix was skipped. Gate this producer as well.
            Type mapPatch = AccessTools.TypeByName("LocalMultiControl.Scripts.Patch.MapSelectionSynchronizerPatch");
            MethodInfo mapPost = mapPatch != null ? AccessTools.Method(mapPatch, "Postfix") : null;
            if (mapPost == null) throw new MissingMethodException("DRA MapSelectionSynchronizerPatch.Postfix");
            harmony.Patch(mapPost, new HarmonyMethod(typeof(RewardBackendFlow), "DraMapProducerPrefix"));
            Type sync = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.MapSelectionSynchronizer");
            harmony.Patch(AccessTools.Method(sync, "MoveToMapCoord"), new HarmonyMethod(typeof(RewardBackendFlow), "MapMoveProducerPrefix"));
            Type queueSync = AccessTools.TypeByName("MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSynchronizer");
            Type gameAction = AccessTools.TypeByName("MegaCrit.Sts2.Core.GameActions.GameAction");
            MethodInfo enqueue = AccessTools.Method(queueSync, "RequestEnqueue", new Type[] { gameAction });
            if (enqueue == null) throw new MissingMethodException("ActionQueueSynchronizer.RequestEnqueue(GameAction)");
            harmony.Patch(enqueue, new HarmonyMethod(typeof(RewardBackendFlow), "MoveEnqueuePrefix") { priority = Priority.First });
            GD.Print("[AiTeammate] BS4 reward backend installed actual=" + actual.DeclaringType.FullName + "." + actual.Name + " dra-prefix=" + draPrefix + " assembly=" + actual.Module.Assembly.FullName);
        }

        private static bool Enabled()
        {
            Type context = AccessTools.TypeByName("LocalMultiControl.Scripts.Runtime.LocalSelfCoopContext");
            return context != null && (bool)AccessTools.Property(context, "IsEnabled").GetValue(null, null) && (bool)AccessTools.Property(context, "UseSingleAdventureMode").GetValue(null, null) && FlowCoordinator.GetPlayer2() != null;
        }
        public static bool RoomOfferPrefix(CombatRoom __instance, ref Task __result, MethodBase __originalMethod)
        {
            if (!Enabled()) return true;
            __result = EnterOffer(__instance, __originalMethod.DeclaringType.FullName + "." + __originalMethod.Name);
            return false;
        }
        public static bool DraRoomOfferPrefix(object[] __args, ref bool __result)
        {
            if (!Enabled()) return true;
            __args[1] = EnterOffer((CombatRoom)__args[0], "LocalMultiControl.Scripts.Patch.CombatRoomOfferRewardsPatch.Prefix");
            __result = false;
            return false;
        }
        private static Task EnterOffer(CombatRoom room, string actual)
        {
            GD.Print("[AiTeammate][Flow] reward-offer-entry actual=" + actual + " mode=per-owner generation=" + Generation);
            TurnCoordinator.DisarmIndependentP2Lifeline("combat-rewards-offered");
            if (Object.ReferenceEquals(room, ActiveRoom) && Producer != null) return Producer;
            RetireTransaction("new-room-producer");
            ActiveRoom = room;
            // Publish before executing user/relic hooks, which can synchronously reenter.
            var prepare = new TaskCompletionSource<bool>();
            Producer = prepare.Task;
            PrepareOffer(room, Generation, prepare);
            return Producer;
        }

        public static void FinishedCombatPrefix(CombatRoom __instance)
        {
            if (ActiveRoom != null) RetireTransaction("FinishedCombat-restart");
        }
        public static void RetireForRoomLoad() { if (ActiveRoom != null) RetireTransaction("room-load-or-cleanup"); }
        private static void RetireTransaction(string reason)
        {
            ++Generation; ++RouteEpoch;
            Cancelled.TrySetResult(true);
            Cancelled = new TaskCompletionSource<bool>();
            FlowCoordinator.CancelRewardBatch(reason);
            foreach (NRewardsScreen screen in new List<NRewardsScreen>(Screens)) RetireOverlay(screen);
            foreach (NCardRewardSelectionScreen screen in new List<NCardRewardSelectionScreen>(CardScreens.Keys)) RetireOverlay(screen);
            Owners.Clear(); RewardGenerations.Clear(); Sets.Clear(); Selections.Clear(); Decided.Clear(); Screens.Clear(); CardScreens.Clear(); SubmittedScreens.Clear(); CardDecisions.Clear(); ManualRewards.Clear(); ResolvedRewards.Clear(); AutoStartedScreens.Clear();
            SelectingCard = null;
            ChoiceSuppressionPatches.LastOverlaySuppressedTime = DateTime.MinValue;
            ActiveRoom = null; Producer = null; ActiveOwnerSet = null;
            GD.Print("[AiTeammate][Flow] reward-transaction-retired generation=" + Generation + " reason=" + reason);
        }
        private static void RetireOverlay(object screen)
        {
            RetiredScreens.Add(screen);
            Node node = screen as Node;
            if (node == null || !GodotObject.IsInstanceValid(node)) return;
            LogScreen("retire", screen, false);
            IList overlays = AccessTools.Field(typeof(NOverlayStack), "_overlays").GetValue(NOverlayStack.Instance) as IList;
            if (overlays != null && overlays.Contains(screen)) NOverlayStack.Instance.Remove((IOverlayScreen)screen);
        }
        private static async Task AwaitCurrent(Task task, int generation, Task cancellation)
        {
            if (await Task.WhenAny(task, cancellation) != task || generation != Generation) throw new OperationCanceledException("Retired reward transaction");
            await task;
        }

        public static bool MergedOfferPrefix(CombatRoom combatRoom, ref Task __result)
        {
            if (!Enabled()) return true;
            __result = EnterOffer(combatRoom, "LocalMultiControl.Scripts.Patch.RewardsSetPatch.OfferMergedCombatRewards");
            return false;
        }

        private static async void PrepareOffer(CombatRoom room, int generation, TaskCompletionSource<bool> prepared)
        {
            FlowCoordinator.BeginRewardBatch();
            Task cancellation = Cancelled.Task;
            try
            {
                var run = room.CombatState.RunState;
                var sync = RunManager.Instance.RewardsSetSynchronizer;
                List<Player> players = new List<Player>();
                // P2 first: all its choices finish before the P1 terminal screen is displayed.
                Player p2 = FlowCoordinator.GetPlayer2();
                if (p2 != null) players.Add(p2);
                foreach (Player player in run.Players) if (p2 == null || player.NetId != p2.NetId) players.Add(player);
                foreach (Player player in players)
                {
                    if (generation != Generation) throw new OperationCanceledException();
                    RewardsSet set = new RewardsSet(player, sync);
                    if (player.Creature.IsDead || (room.Encounter != null && !room.Encounter.ShouldGiveRewards)) set.EmptyForRoom(room);
                    else set.WithRewardsFromRoom(room);
                    await AwaitCurrent(set.GenerateWithoutOffering(), generation, cancellation);
                    await AwaitCurrent(MegaCrit.Sts2.Core.Hooks.Hook.BeforeCombatRewardOffered(set, run, room), generation, cancellation);
                    foreach (Reward reward in set.Rewards) { Owners[reward] = set; RewardGenerations[reward] = generation; }
                    // Begin registers the native rewardsStack and real completionSource. Do not await
                    // before showing choices; it completes only after selections (or explicit skip).
                    SetRegistration registration = new SetRegistration { Set = set, Generation = generation };
                    Sets.Add(set, registration);
                    registration.Backend = sync.BeginRewardsSet(set);
                }
                // Native OfferRoomEndRewards launches offers with TaskHelper.RunSafely, and
                // returns without waiting for player input. Preserve that lifecycle: FinishedCombat
                // must finish EnterRoom and its fade-in while rewards remain behind our exit gate.
                Callable.From(delegate { RunOwnerScreens(room, players, generation, cancellation); }).CallDeferred();
                prepared.TrySetResult(true);
            }
            catch (OperationCanceledException) { prepared.TrySetCanceled(); }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][Flow] reward-prepare-failed gate=held exception=" + ex); prepared.TrySetException(ex); }
        }

        private static async void RunOwnerScreens(CombatRoom room, List<Player> players, int generation, Task cancellation)
        {
            try
            {
                foreach (Player player in players)
                {
                    if (generation != Generation) return;
                    SetRegistration registration = null;
                    foreach (SetRegistration entry in Sets.Values) if (entry.Set.Player == player) { registration = entry; break; }
                    if (registration == null) throw new InvalidOperationException("Owner set registration missing");
                    RewardsSet set = registration.Set;
                    ActiveOwnerSet = set;
                    // Both native screens need Completed. Non-terminal screens remove themselves
                    // without emitting it. Close P2 normally after real terminal completion.
                    NRewardsScreen screen = NRewardsScreen.ShowScreen(set, true, room.CombatState.RunState);
                    if (screen == null) throw new InvalidOperationException("Native rewards screen unavailable");
                    RegisterScreen(screen);
                    LogScreen("await-visible-native", screen, screen.IsVisibleInTree());
                    await AwaitCurrent(FlowCoordinator.WaitForRewardScreen(screen), generation, cancellation);
                    await AwaitCurrent(registration.Backend, generation, cancellation);
                    if (!RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(set)) throw new InvalidOperationException("Reward Task completed without backend set confirmation");
                    if (player.NetId == TurnCoordinator.GetPlayer2NetId())
                    {
                        // Normal overlay close, only after native Completed + backend completion.
                        NOverlayStack.Instance.Remove(screen);
                    }
                }
                foreach (SetRegistration entry in Sets.Values) if (!entry.Backend.IsCompleted || !RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(entry.Set)) throw new InvalidOperationException("Owner backend set not confirmed");
                FlowCoordinator.CompleteRewardBatch();
            }
            catch (OperationCanceledException) { GD.Print("[AiTeammate][Flow] reward-callback-cancelled generation=" + generation); }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Flow] reward-batch-failed gate=held exception=" + ex);
                // Preserve the actual screen and outstanding native choice; no fake completion.
            }
        }

        public static void ScreenReadyPrefix(NRewardsScreen __instance)
        {
            RegisterScreen(__instance);
            LogScreen("_Ready-enter", __instance, !RetiredScreens.Contains(__instance));
        }
        private static void RegisterScreen(NRewardsScreen screen)
        {
            RewardsSet set = ScreenSet(screen);
            SetRegistration registration;
            if (set == null || !Sets.TryGetValue(set, out registration) || registration.Generation != Generation) return;
            registration.Screen = screen;
            if (!Screens.Add(screen)) return;
            FlowCoordinator.BeginRewardGate(screen);
            FlowCoordinator.TrackRewardScreen(screen);
        }
        public static void ScreenReadyPostfix(NRewardsScreen __instance)
        {
            RegisterScreen(__instance);
            LogScreen("_Ready-complete", __instance, !RetiredScreens.Contains(__instance));
            if (!IsManagedScreen(__instance) || !AutoStartedScreens.Add(__instance)) return;
            int generation = Generation;
            Callable.From(delegate { if (generation == Generation && GodotObject.IsInstanceValid(__instance)) AutoClaimManaged(__instance, generation); }).CallDeferred();
        }
        private static async void AutoClaimManaged(NRewardsScreen screen, int generation)
        {
            try
            {
                if (ScreenSet(screen).Player.NetId != TurnCoordinator.GetPlayer2NetId()) return;
                IList buttons = AccessTools.Field(typeof(NRewardsScreen), "_rewardButtons").GetValue(screen) as IList;
                var snapshot = new List<NRewardButton>();
                foreach (object value in buttons) if (value is NRewardButton) snapshot.Add((NRewardButton)value);
                foreach (NRewardButton button in snapshot)
                {
                    if (generation != Generation) return;
                    if (button.Reward is CardReward && !FlowCoordinator.AutoCardRewardEnabled) return;
                    Task selection = null;
                    ButtonPrefix(button, ref selection);
                    if (selection == null) throw new InvalidOperationException("Managed reward selection task missing");
                    await AwaitCurrent(selection, generation, Cancelled.Task);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][Flow] managed-reward-autoclaim-failed current-reward-manual=true exception=" + ex); }
        }
        public static void ShowRewardsPostfix(NRewardsScreen __result) { RegisterScreen(__result); LogScreen("ShowScreen", __result, true); }
        public static bool OverlayPushPrefix(object screen)
        {
            if (!(screen is NRewardsScreen) && !(screen is NCardRewardSelectionScreen)) return true;
            if (screen is NRewardsScreen) RegisterScreen((NRewardsScreen)screen);
            bool allow = !RetiredScreens.Contains(screen);
            if (allow && screen is NRewardsScreen && IsManagedScreen((NRewardsScreen)screen)) allow = AllowNativeRewardOverlay(screen);
            LogScreen("Push", screen, allow);
            return allow;
        }
        public static void OverlayRemovePrefix(object screen)
        {
            if (screen is NRewardsScreen || screen is NCardRewardSelectionScreen)
            {
                LogScreen("Remove", screen, true);
                FlowCoordinator.TryCompleteEventRewardGate("overlay-remove");
            }
        }

        public static bool UpdateStatePrefix(NRewardsScreen __instance)
        {
            if (!IsManagedScreen(__instance)) return true;
            RewardsSet set = AccessTools.Field(typeof(NRewardsScreen), "_rewardsSet").GetValue(__instance) as RewardsSet;
            IList buttons = AccessTools.Field(typeof(NRewardsScreen), "_rewardButtons").GetValue(__instance) as IList;
            // UpdateScreenState emits Completed directly via EmitSignal, not its generated helper.
            // Never let this terminal branch run ahead of the synchronizer.
            if (buttons != null && buttons.Count == 0 && !RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(set))
            {
                GD.Print("[AiTeammate][Flow] room-exit-gate-wait source=UpdateScreenState backend=false");
                return false;
            }
            return true;
        }

        public static bool ButtonPrefix(NRewardButton __instance, ref Task __result)
        {
            Reward reward = __instance.Reward;
            if (!Owns(reward)) return true;
            if (reward is CardReward && !FlowCoordinator.AutoCardRewardEnabled)
            {
                GD.Print("[AiTeammate][Flow] p2-card-reward-auto-disabled native-manual=true");
                return true;
            }
            Task existing;
            if (Selections.TryGetValue(reward, out existing))
            {
                GD.Print("[AiTeammate][Flow] reward-selection-deduped fingerprint=" + FlowCoordinator.RewardFingerprint(reward));
                __result = existing;
                return false;
            }
            // Publish the waiter before starting the async method: signals may reenter synchronously.
            var completion = new TaskCompletionSource<bool>();
            Selections[reward] = completion.Task;
            __result = completion.Task;
            SelectButton(__instance, reward, completion);
            return false;
        }

        public static bool LegacyButtonPrefix(object[] __args, ref bool __result)
        {
            NRewardButton button = __args[0] as NRewardButton;
            if (button == null || !Owns(button.Reward)) return true;
            Task task = null;
            bool runNative = ButtonPrefix(button, ref task);
            if (runNative) { __result = true; return false; }
            __args[1] = task;
            __result = false;
            return false;
        }

        private static object FindSetState(RewardsSet set)
        {
            object sync = RunManager.Instance.RewardsSetSynchronizer;
            MethodInfo get = AccessTools.Method(sync.GetType(), "GetRewardStateForPlayer");
            object state = get.Invoke(sync, new object[] { set.Player });
            IEnumerable stack = AccessTools.Field(state.GetType(), "rewardsStack").GetValue(state) as IEnumerable;
            foreach (object entry in stack)
                if (Object.ReferenceEquals(AccessTools.Field(entry.GetType(), "set").GetValue(entry), set)) return entry;
            throw new InvalidOperationException("No native reward set state for " + set.Player.NetId);
        }

        private static async void SelectButton(NRewardButton button, Reward reward, TaskCompletionSource<bool> completion)
        {
            int generation = Generation;
            Task cancellation = Cancelled.Task;
            await SelectionSerial.WaitAsync();
            try
            {
                if (generation != Generation) throw new OperationCanceledException();
                button.Disable();
                FlowCoordinator.ObserveRewardSelectionStart(reward);
                object sync = RunManager.Instance.RewardsSetSynchronizer;
                object setState = FindSetState(Owners[reward]);
                MethodInfo select = AccessTools.Method(sync.GetType(), "SelectRewardForPlayer", new Type[] { setState.GetType(), typeof(Reward) });
                if (select == null) throw new MissingMethodException("SelectRewardForPlayer(RewardsSetState, Reward)");
                Task<bool> task;
                ulong? previous = LocalContext.NetId;
                CardReward previousCard = SelectingCard;
                try
                {
                    // A synchronous identity scope only, no DRA foreground/UI switch. DRA's
                    // native SyncLocalChoice patch realigns sender when the awaited choice resumes.
                    LocalContext.NetId = reward.Player.NetId;
                    SelectingCard = reward as CardReward;
                    task = (Task<bool>)select.Invoke(sync, new object[] { setState, reward });
                }
                finally { SelectingCard = previousCard; LocalContext.NetId = previous; }
                await AwaitCurrent(task, generation, cancellation);
                bool claimed = await task;
                ulong p2Id = TurnCoordinator.GetPlayer2NetId();
                bool isP2 = p2Id != 0 && reward.Player != null && reward.Player.NetId == p2Id;
                if (!claimed && !isP2)
                {
                    Selections.Remove(reward);
                    if (GodotObject.IsInstanceValid(button))
                    {
                        button.Enable();
                        button.EmitSignal(NRewardButton.SignalName.RewardSkipped, button);
                    }
                    GD.Print("[AiTeammate][Flow] p1-reward-unclaimed-reenabled type=" + reward.GetType().Name + " fingerprint=" + FlowCoordinator.RewardFingerprint(reward));
                    completion.TrySetResult(false);
                    return;
                }
                Decided.Add(reward);
                RewardsSet set = Owners[reward];
                bool allDecided = true;
                foreach (Reward r in set.Rewards) if (!r.SuccessfullySelected && !Decided.Contains(r)) { allDecided = false; break; }
                if (allDecided && set.Player.NetId == TurnCoordinator.GetPlayer2NetId() && !RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(set))
                {
                    // A native declined card choice is a backend skip, never a fabricated claim.
                    AccessTools.Method(sync.GetType(), "SkipRewardsSet").Invoke(sync, new object[] { setState });
                }
                FlowCoordinator.MarkRewardBackendConfirmed(reward, reward.Player.NetId == TurnCoordinator.GetPlayer2NetId() ? "P2" : "P1");
                CardReward card = reward as CardReward;
                string decision;
                if (claimed && card != null && CardDecisions.TryGetValue(card, out decision))
                {
                    GD.Print("[AiTeammate][Flow] p2-choice-resolved kind=card-reward " + decision + " backend=true");
                    CardDecisions.Remove(card);
                }
                button.EmitSignal(claimed ? NRewardButton.SignalName.RewardClaimed : NRewardButton.SignalName.RewardSkipped, button);
                completion.TrySetResult(true);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception ex)
            {
                if (generation == Generation) Selections.Remove(reward);
                CardReward card = reward as CardReward;
                if (card != null) ManualRewards.Add(card);
                if (GodotObject.IsInstanceValid(button)) button.Enable();
                GD.PrintErr("[AiTeammate][Flow] reward-selection-failed current-reward-manual=true exception=" + ex);
                completion.TrySetException(ex);
            }
            finally { SelectionSerial.Release(); }
        }

        public static void NotifyCardRewardSelectBegin(CardReward reward)
        {
            ulong p2Id = TurnCoordinator.GetPlayer2NetId();
            if (reward != null && reward.Player != null && p2Id != 0 && reward.Player.NetId == p2Id)
            {
                SelectingCard = reward;
            }
        }

        public static void NotifyCardRewardSelectEnd(CardReward reward)
        {
            if (SelectingCard == reward)
            {
                SelectingCard = null;
            }
        }

        public static void ShowCardScreenPostfix(NCardRewardSelectionScreen __result)
        {
            ulong p2Id = TurnCoordinator.GetPlayer2NetId();
            if (__result != null && SelectingCard != null && (Owns(SelectingCard) || (p2Id != 0 && SelectingCard.Player != null && SelectingCard.Player.NetId == p2Id)))
            {
                CardScreens[__result] = SelectingCard;
            }
            LogScreen("ShowScreen-card", __result, true);
        }

        public static void OptionSelectedPostfix(NCardRewardSelectionScreen __instance, Task<int?> __result)
        {
            CardReward reward;
            if (!CardScreens.TryGetValue(__instance, out reward) || reward.Player.NetId != TurnCoordinator.GetPlayer2NetId() || !FlowCoordinator.AutoCardRewardEnabled || ManualRewards.Contains(reward)) return;
            if (!SubmittedScreens.Add(__instance)) return;
            // The ORIGINAL OptionSelected has created _completionSource. Never replace its Task.
            int generation = Generation;
            bool isManaged = Owns(reward);
            Callable.From(delegate
            {
                if (!isManaged || generation == Generation)
                {
                    SubmitNativeCardChoice(__instance, reward, __result);
                }
            }).CallDeferred();
        }

        private static void SubmitNativeCardChoice(NCardRewardSelectionScreen screen, CardReward reward, Task<int?> choice)
        {
            try
            {
                ulong p2Id = TurnCoordinator.GetPlayer2NetId();
                bool isManaged = reward != null && Owns(reward);
                bool isP2Reward = reward != null && reward.Player != null && p2Id != 0 && reward.Player.NetId == p2Id;
                if (reward == null || (!isManaged && !isP2Reward) || ResolvedRewards.Contains(reward)) return;

                if (choice.IsCompleted) return;
                if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree()) throw new InvalidOperationException("Native card reward screen is not live");
                var options = AccessTools.Field(typeof(NCardRewardSelectionScreen), "_options").GetValue(screen) as IReadOnlyList<CardCreationResult>;
                if (options == null || options.Count == 0) throw new InvalidOperationException("Native card reward options empty");
                string id, source, error, scores; CardModel card; int index;
                FlowCoordinator.EvaluateCardReward(reward.Player, options, out id, out card, out source, out index, out error, out scores);
                if (source == "circuit-open" || index < 0 || index >= options.Count || card == null) throw new InvalidOperationException("Card decision unavailable: " + source);
                var holder = screen.GetCardHolder(card);
                if (holder == null) throw new InvalidOperationException("Selected native card holder missing");
                CardDecisions[reward] = "pick=" + index + " reason=" + source + " scores=" + scores + " card=" + id;
                GD.Print("[AiTeammate][Flow] p2-card-choice-submit " + CardDecisions[reward] + " native=true");
                AccessTools.Method(typeof(NCardRewardSelectionScreen), "SelectCard").Invoke(screen, new object[] { holder });

                if (!isManaged)
                {
                    ResolvedRewards.Add(reward);
                    FlowCoordinator.MarkRewardBackendConfirmed(reward, "P2");
                    GD.Print("[AiTeammate][RewardFlow] Unmanaged P2 card reward auto-selected: " + (holder.CardModel != null ? holder.CardModel.Id.ToString() : (id ?? "unknown")));
                }
            }
            catch (Exception ex)
            {
                ManualRewards.Add(reward);
                CardDecisions.Remove(reward);
                GD.PrintErr("[AiTeammate][Flow] p2-card-choice-failed current-reward-manual=true exception=" + ex);
                // Leave the same native screen and completionSource available for this choice.
            }
        }

        public static bool ProceedPrefix(NRewardsScreen __instance)
        {
            if (ScreenSet(__instance) != null && ScreenSet(__instance).Player != null && ScreenSet(__instance).Player.NetId == TurnCoordinator.GetPlayer2NetId()) return false;

            if (!IsManagedScreen(__instance))
            {
                RewardsSet unmanagedSet = ScreenSet(__instance);
                if (unmanagedSet != null && unmanagedSet.Player != null && unmanagedSet.Player.NetId == TurnCoordinator.GetPlayer1NetId())
                {
                    try
                    {
                        if (RunManager.Instance != null && RunManager.Instance.RewardsSetSynchronizer != null && !RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(unmanagedSet))
                        {
                            object sync = RunManager.Instance.RewardsSetSynchronizer;
                            MethodInfo get = AccessTools.Method(sync.GetType(), "GetRewardStateForPlayer");
                            object state = get != null ? get.Invoke(sync, new object[] { unmanagedSet.Player }) : null;
                            if (state != null)
                            {
                                FieldInfo stackField = AccessTools.Field(state.GetType(), "rewardsStack");
                                IEnumerable stack = stackField != null ? stackField.GetValue(state) as IEnumerable : null;
                                if (stack != null)
                                {
                                    foreach (object entry in stack)
                                    {
                                        if (entry != null && Object.ReferenceEquals(AccessTools.Field(entry.GetType(), "set").GetValue(entry), unmanagedSet))
                                        {
                                            MethodInfo skip = AccessTools.Method(sync.GetType(), "SkipRewardsSet");
                                            if (skip != null) skip.Invoke(sync, new object[] { entry });
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr("[AiTeammate][Flow] p1-unmanaged-proceed-skip error: " + ex.Message);
                    }
                    FlowCoordinator.ForceCompleteRewardScreenFromProceed(__instance, "P1-unmanaged-proceed");
                    FlowCoordinator.TryCompleteEventRewardGate("p1-proceed");
                    return true;
                }
                return true;
            }

            RewardsSet set = AccessTools.Field(typeof(NRewardsScreen), "_rewardsSet").GetValue(__instance) as RewardsSet;
            if (set == null) return true;
            if (RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(set))
            {
                FlowCoordinator.ForceCompleteRewardScreenFromProceed(__instance, "P1-proceed-already-complete");
                return true;
            }
            foreach (Reward reward in set.Rewards)
            {
                Task pending;
                if (Selections.TryGetValue(reward, out pending) && !pending.IsCompleted)
                {
                    GD.Print("[AiTeammate][Flow] room-exit-gate-wait source=proceed reason=selection-pending");
                    return false;
                }
            }
            // Only an explicit P1 click may skip outstanding rewards. P2 auto flow never calls this.
            if (set.Player.NetId != TurnCoordinator.GetPlayer1NetId()) return false;
            object managedSync = RunManager.Instance.RewardsSetSynchronizer;
            AccessTools.Method(managedSync.GetType(), "SkipRewardsSet").Invoke(managedSync, new object[] { FindSetState(set) });
            IList buttons = AccessTools.Field(typeof(NRewardsScreen), "_rewardButtons").GetValue(__instance) as IList;
            List<NRewardButton> remaining = new List<NRewardButton>();
            foreach (object b in buttons) if (b is NRewardButton) remaining.Add((NRewardButton)b);
            foreach (NRewardButton button in remaining)
            {
                FlowCoordinator.MarkRewardBackendConfirmed(button.Reward, "P1-skip");
                button.EmitSignal(NRewardButton.SignalName.RewardSkipped, button);
            }
            FlowCoordinator.ForceCompleteRewardScreenFromProceed(__instance, "P1-proceed-skip");
            return true;
        }

        public static bool DraMapProducerPrefix()
        {
            // AiTeammate owns the P2 follow vote in this local mode. Avoid the DRA
            // Postfix filling the vote list behind the shared vote/gate/dedup path.
            if (FlowCoordinator.GetPlayer2() != null && FlowCoordinator.AutoRouteFollowEnabled) return false;
            return !FlowCoordinator.IsRewardGateActive();
        }
        public static bool MapMoveProducerPrefix(object __instance)
        {
            if (FlowCoordinator.IsRewardGateActive())
            {
                FlowCoordinator.DeferMapMoveProducer(__instance);
                return false;
            }
            string generation = FlowCoordinator.CurrentRunGeneration();
            ResetMoveGeneration(generation);
            IEnumerable votes = AccessTools.Field(__instance.GetType(), "_votes").GetValue(__instance) as IEnumerable;
            string destination = null;
            foreach (object vote in votes) if (vote != null) { destination = FlowCoordinator.DestinationKey(vote); break; }
            if (destination == null) { GD.Print("[AiTeammate][Flow] route-follow-rejected reason=no-destination-vote"); return false; }
            string key = generation + "|" + destination;
            if (!MoveProducers.Add(key)) { GD.Print("[AiTeammate][Flow] route-follow-deduped source=MoveToMapCoord key=" + key); return false; }
            GD.Print("[AiTeammate][Flow] route-follow-request source=MoveToMapCoord key=" + key);
            return true;
        }

        private static void ResetMoveGeneration(string generation)
        {
            if (MoveGeneration == generation) return;
            MoveProducers.Clear(); MoveEnqueues.Clear(); PendingMoveEnqueues.Clear(); MoveGeneration = generation;
        }

        public static bool MoveEnqueuePrefix(object __instance, object[] __args, MethodBase __originalMethod)
        {
            if (__args == null || __args.Length != 1 || __args[0] == null || __args[0].GetType().FullName != "MegaCrit.Sts2.Core.GameActions.MoveToMapCoordAction") return true;
            string generation = FlowCoordinator.CurrentRunGeneration();
            ResetMoveGeneration(generation);
            string key = generation + "|" + FlowCoordinator.DestinationKey(__args[0]);
            if (MoveEnqueues.Contains(key)) { GD.Print("[AiTeammate][Flow] route-follow-deduped source=RequestEnqueue key=" + key); return false; }
            if (FlowCoordinator.IsRewardGateActive())
            {
                if (PendingMoveEnqueues.Add(key)) ReplayEnqueue(__instance, (object[])__args.Clone(), __originalMethod, key, generation);
                else GD.Print("[AiTeammate][Flow] route-follow-deduped source=pending-enqueue key=" + key);
                return false;
            }
            MoveEnqueues.Add(key);
            GD.Print("[AiTeammate][Flow] route-follow-request source=RequestEnqueue key=" + key);
            return true;
        }

        private static async void ReplayEnqueue(object synchronizer, object[] args, MethodBase method, string key, string generation)
        {
            try
            {
                GD.Print("[AiTeammate][Flow] room-exit-gate-wait source=RequestEnqueue key=" + key);
                await FlowCoordinator.WaitForRewardGate();
                Callable.From(delegate
                {
                    try
                    {
                        if (generation == FlowCoordinator.CurrentRunGeneration()) method.Invoke(synchronizer, args);
                        else GD.Print("[AiTeammate][Flow] route-follow-rejected reason=stale-generation key=" + key);
                    }
                    catch (Exception ex) { GD.PrintErr("[AiTeammate][Flow] route-follow-rejected reason=enqueue-replay exception=" + ex); }
                    finally { PendingMoveEnqueues.Remove(key); }
                }).CallDeferred();
            }
            catch (Exception ex) { PendingMoveEnqueues.Remove(key); GD.PrintErr("[AiTeammate][Flow] route-follow-rejected exception=" + ex); }
        }
    }
}
