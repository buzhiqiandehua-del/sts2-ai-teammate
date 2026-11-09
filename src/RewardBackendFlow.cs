using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
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
        public static readonly HashSet<Reward> Decided = new HashSet<Reward>();
        private static readonly HashSet<NRewardsScreen> Screens = new HashSet<NRewardsScreen>();
        private static readonly Dictionary<NCardRewardSelectionScreen, CardReward> CardScreens = new Dictionary<NCardRewardSelectionScreen, CardReward>();
        private static readonly HashSet<NCardRewardSelectionScreen> SubmittedScreens = new HashSet<NCardRewardSelectionScreen>();
        private static readonly Dictionary<CardReward, string> CardDecisions = new Dictionary<CardReward, string>();
        private static readonly HashSet<CardReward> ManualRewards = new HashSet<CardReward>();
        public static readonly HashSet<Reward> ResolvedRewards = new HashSet<Reward>();
        private static readonly HashSet<string> MoveProducers = new HashSet<string>();
        private static readonly HashSet<string> MoveEnqueues = new HashSet<string>();
        private static readonly HashSet<string> PendingMoveEnqueues = new HashSet<string>();
        private static string MoveGeneration;
        private static readonly System.Threading.SemaphoreSlim SelectionSerial = new System.Threading.SemaphoreSlim(1, 1);
        [ThreadStatic] private static CardReward SelectingCard;
        private static readonly Queue<CardReward> PendingCardRewards = new Queue<CardReward>();
        private static readonly object CardRewardLock = new object();
        private static readonly Dictionary<CardReward, int> CardChoiceRetries = new Dictionary<CardReward, int>();

        public static void EnqueuePendingCardReward(CardReward reward)
        {
            if (reward == null) return;
            lock (CardRewardLock)
            {
                if (!PendingCardRewards.Contains(reward))
                {
                    PendingCardRewards.Enqueue(reward);
                }
            }
        }
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
        public static void RetireForRoomLoad()
        {
            SkipOutstandingP1RewardSets("room-load-or-cleanup");
            if (ActiveRoom != null) RetireTransaction("room-load-or-cleanup");
        }
        private static void RetireTransaction(string reason)
        {
            SkipOutstandingP1RewardSets("RetireTransaction-" + reason);
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

                    ulong p2Id = TurnCoordinator.GetPlayer2NetId();
                    bool isP2 = p2Id != 0 && player.NetId == p2Id;
                    if (isP2)
                    {
                        await AwaitCurrent(registration.Backend, generation, cancellation);
                        if (!RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(set)) throw new InvalidOperationException("Reward Task completed without backend set confirmation");
                        // Normal overlay close, only after native Completed + backend completion.
                        NOverlayStack.Instance.Remove(screen);
                    }
                    else
                    {
                        // Directive #5.44: P1 does not wait on registration.Backend; allow P1 to view map or come back to claim rewards
                    }
                }
                foreach (SetRegistration entry in Sets.Values)
                {
                    ulong p2Id = TurnCoordinator.GetPlayer2NetId();
                    if (entry.Set.Player != null && p2Id != 0 && entry.Set.Player.NetId == p2Id)
                    {
                        if (!entry.Backend.IsCompleted || !RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(entry.Set))
                            throw new InvalidOperationException("P2 owner backend set not confirmed");
                    }
                }
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
                var rawSnapshot = new List<NRewardButton>();
                if (buttons != null)
                {
                    foreach (object value in buttons)
                    {
                        NRewardButton btn = value as NRewardButton;
                        if (btn != null && GodotObject.IsInstanceValid(btn)) rawSnapshot.Add(btn);
                    }
                }
                var nonPotions = new List<NRewardButton>();
                var potions = new List<NRewardButton>();
                foreach (NRewardButton b in rawSnapshot)
                {
                    if (b.Reward is PotionReward) potions.Add(b);
                    else nonPotions.Add(b);
                }
                bool reordered = potions.Count > 0 && nonPotions.Count > 0 && rawSnapshot.IndexOf(potions[0]) < rawSnapshot.IndexOf(nonPotions[nonPotions.Count - 1]);
                var snapshot = new List<NRewardButton>(nonPotions.Count + potions.Count);
                snapshot.AddRange(nonPotions);
                snapshot.AddRange(potions);
                if (potions.Count > 0)
                {
                    GD.Print(string.Format("[AiTeammate][Flow] reward-claim-order potions-last reordered={0} total={1} potions={2} source=AutoClaimManaged",
                        reordered, snapshot.Count, potions.Count));
                }
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
            ulong p2Id = TurnCoordinator.GetPlayer2NetId();
            bool isP2 = reward != null && reward.Player != null && p2Id != 0 && reward.Player.NetId == p2Id;
            if (!Owns(reward) && !isP2) return true;
            if (reward is CardReward && !FlowCoordinator.AutoCardRewardEnabled)
            {
                GD.Print("[AiTeammate][Flow] p2-card-reward-auto-disabled native-manual=true");
                return true;
            }
            if (reward != null && Decided.Contains(reward))
            {
                GD.Print("[AiTeammate][Flow] reward-terminal-deduped owner=" + (isP2 ? "P2" : "P1") + " type=" + reward.GetType().Name + " fingerprint=" + FlowCoordinator.RewardFingerprint(reward));
                Task terminal; if (Selections.TryGetValue(reward, out terminal)) { __result = terminal; return false; }
                __result = Task.FromResult(true); return false;
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

        public static string DetermineRewardSource(Reward reward)
        {
            try
            {
                if (reward != null && reward.RewardsSetIndex >= 0 && Owns(reward)) return "combat";
                var manager = RunManager.Instance;
                if (manager != null)
                {
                    object state = manager.DebugOnlyGetState();
                    if (state != null)
                    {
                        PropertyInfo cp = AccessTools.Property(state.GetType(), "CurrentRoom");
                        object room = cp != null ? cp.GetValue(state, null) : null;
                        if (room != null)
                        {
                            string rType = room.GetType().Name;
                            if (rType.IndexOf("Neow", StringComparison.OrdinalIgnoreCase) >= 0) return "neow";
                            if (rType.IndexOf("Event", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                PropertyInfo evProp = AccessTools.Property(room.GetType(), "Event");
                                object ev = evProp != null ? evProp.GetValue(room, null) : null;
                                if (ev != null && ev.GetType().Name.IndexOf("Neow", StringComparison.OrdinalIgnoreCase) >= 0) return "neow";
                                return "event";
                            }
                            if (rType.IndexOf("Combat", StringComparison.OrdinalIgnoreCase) >= 0) return "combat";
                            if (rType.IndexOf("Treasure", StringComparison.OrdinalIgnoreCase) >= 0) return "treasure";
                            if (rType.IndexOf("Shop", StringComparison.OrdinalIgnoreCase) >= 0) return "shop";
                        }
                    }
                }
            }
            catch { }
            return "unknown";
        }

        public static object FindSetStateForReward(Reward reward)
        {
            if (reward == null || reward.Player == null) return null;
            try
            {
                object sync = RunManager.Instance.RewardsSetSynchronizer;
                if (sync == null) return null;
                MethodInfo get = AccessTools.Method(sync.GetType(), "GetRewardStateForPlayer");
                if (get == null) return null;
                object state = get.Invoke(sync, new object[] { reward.Player });
                if (state == null) return null;
                IEnumerable stack = AccessTools.Field(state.GetType(), "rewardsStack").GetValue(state) as IEnumerable;
                if (stack == null) return null;
                foreach (object entry in stack)
                {
                    if (entry == null) continue;
                    RewardsSet set = AccessTools.Field(entry.GetType(), "set").GetValue(entry) as RewardsSet;
                    if (set != null && set.Rewards != null)
                    {
                        foreach (Reward r in set.Rewards)
                        {
                            if (r == reward) return entry;
                        }
                    }
                }
                IEnumerator enumerator = stack.GetEnumerator();
                if (enumerator != null && enumerator.MoveNext())
                {
                    return enumerator.Current;
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Flow] FindSetStateForReward error: " + ex.Message);
            }
            return null;
        }

        // Native P2 potion path: the synchronizer owns selection state. Calling
        // Reward.OnSelect directly leaves RewardsSetState pending and strands OfferCustom.
        public static async Task<bool> SelectP2PotionRewardBackend(PotionReward reward, NRewardButton button, string source)
        {
            if (reward == null || reward.Player == null) return false;
            ulong p2Id = TurnCoordinator.GetPlayer2NetId();
            if (p2Id == 0 || reward.Player.NetId != p2Id) return false;
            object sync = RunManager.Instance != null ? RunManager.Instance.RewardsSetSynchronizer : null;
            if (sync == null) return false;
            object setState = FindSetStateForReward(reward);
            if (setState == null) throw new InvalidOperationException("No native reward set state for P2 potion");
            RewardsSet set = null;
            try { set = AccessTools.Field(setState.GetType(), "set").GetValue(setState) as RewardsSet; } catch { }
            MethodInfo select = AccessTools.Method(sync.GetType(), "SelectRewardForPlayer", new Type[] { setState.GetType(), typeof(Reward) });
            if (select == null) throw new MissingMethodException("SelectRewardForPlayer(RewardsSetState, Reward)");
            string potionId = reward.Potion != null && reward.Potion.Id != null ? reward.Potion.Id.ToString() : "unknown";
            GD.Print("[AiTeammate][Flow] p2-potion-native-select owner=P2 potion=" + potionId + " source=" + (source ?? "unknown"));
            ulong? previous = LocalContext.NetId;
            Task<bool> task;
            try { LocalContext.NetId = reward.Player.NetId; task = (Task<bool>)select.Invoke(sync, new object[] { setState, reward }); }
            finally { LocalContext.NetId = previous; }
            if (task == null) return false;
            Selections[reward] = task;
            Task completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2.0)));
            if (!Object.ReferenceEquals(completed, task) || !task.IsCompleted)
            {
                GD.PrintErr("[AiTeammate][Flow] p2-potion-reward-backend-timeout owner=P2 potion=" + potionId);
                return false;
            }
            bool selected = false;
            try { selected = await task; } catch { selected = false; }
            bool inInventory = reward.Player.PotionSlots != null && reward.Player.PotionSlots.Any(p => p != null && (p == reward.Potion || (p.Id != null && reward.Potion != null && p.Id.Equals(reward.Potion.Id))));
            if (!inInventory && reward.Player.Potions != null) inInventory = reward.Player.Potions.Any(p => p != null && (p == reward.Potion || (p.Id != null && reward.Potion != null && p.Id.Equals(reward.Potion.Id))));
            GD.Print("[AiTeammate][Flow] p2-potion-inventory-confirmed owner=P2 potion=" + potionId + " confirmed=" + inInventory);
            bool nativeComplete = false;
            if (set != null) { try { MethodInfo cm = AccessTools.Method(sync.GetType(), "IsRewardsSetCompleted", new Type[] { set.GetType() }); nativeComplete = cm != null && (bool)cm.Invoke(sync, new object[] { set }); } catch { } }
            if (selected && inInventory)
            {
                Decided.Add(reward); ResolvedRewards.Add(reward);
                GD.Print("[AiTeammate][Flow] p2-potion-claim-terminal owner=P2 potion=" + potionId + " state=claimed inventoryConfirmed=true");
                if (set != null && set.Rewards != null)
                {
                    string remaining = string.Join(",", set.Rewards.Where(r => r != null && r != reward && !Decided.Contains(r) && !ResolvedRewards.Contains(r)).Select(r => r.GetType().Name).ToArray());
                    if (remaining.Length > 0) GD.Print("[AiTeammate][Flow] p2-reward-set-partial-complete owner=P2 remaining=" + remaining);
                }
                return true;
            }
            GD.PrintErr("[AiTeammate][Flow] p2-potion-rewardset-unconfirmed owner=P2 potion=" + potionId + " selected=" + selected + " completed=" + nativeComplete);
            return false;
        }
        public static void SkipP2PotionRewardBackend(PotionReward reward, NRewardButton button, string source)
        {
            if (reward == null) return;
            object state = FindSetStateForReward(reward);
            RewardsSet set = null;
            try { if (state != null) set = AccessTools.Field(state.GetType(), "set").GetValue(state) as RewardsSet; } catch { }

            // Directive #5.93: never terminate the whole native RewardsSet while other
            // rewards in the same set are still unclaimed. SkipP2RewardsSetBackend force-
            // calls OnSkipped() on every un-resolved reward and completes the set with
            // state=Skipped on the native side, which permanently destroys a pending
            // CardReward (live evidence 11-07: reward=46187473 set=50197865 -> CardReward
            // wiped, later selects threw "No native reward set state"). Instead: mark THIS
            // potion as decided/skipped, and only close the set when nothing else remains.
            try { Decided.Add(reward); ResolvedRewards.Add(reward); } catch { }
            try { reward.OnSkipped(); } catch { }
            FlowCoordinator.MarkRewardBackendConfirmed(reward, "P2-skip");

            bool othersPending = false;
            string pendingNames = "";
            if (set != null && set.Rewards != null)
            {
                foreach (Reward r in set.Rewards)
                {
                    if (r != null && r != reward && !ResolvedRewards.Contains(r) && !Decided.Contains(r) && !r.SuccessfullySelected)
                    {
                        othersPending = true;
                        pendingNames += (pendingNames.Length > 0 ? "," : "") + r.GetType().Name;
                    }
                }
            }

            if (set != null && !othersPending)
            {
                SkipP2RewardsSetBackend(set, button != null ? button.GetParent() as NRewardsScreen : null, source ?? "p2-potion-skip");
            }
            else if (set != null)
            {
                // Leave the remaining rewards claimable (fail-open): the reward screen stays
                // interactive for the not-yet-claimed rewards; the set will be closed by the
                // normal claim path or by the player once the remaining buttons are handled.
                GD.Print(string.Format("[AiTeammate][Flow] potion-skip-deferred reason=set-has-unclaimed-rewards remaining={0} source={1}", pendingNames, source ?? "unknown"));
            }
        }
        public static async Task<bool> ClaimP2CardRewardBackend(CardReward reward, NRewardButton button, string source)
        {
            try
            {
                if (reward == null || reward.Player == null) return false;
                ulong p2NetId = TurnCoordinator.GetPlayer2NetId();
                if (reward.Player.NetId != p2NetId) return false;

                bool isManaged = Owns(reward);
                GD.Print(string.Format("[AiTeammate][Flow] p2-card-reward-backend-select owner=P2 managed={0} source={1}", isManaged, source ?? "unknown"));

                EnqueuePendingCardReward(reward);
                if (button != null && GodotObject.IsInstanceValid(button))
                {
                    button.Disable();
                }

                object sync = RunManager.Instance.RewardsSetSynchronizer;
                object setState = FindSetStateForReward(reward);
                if (setState == null && Owners.ContainsKey(reward))
                {
                    setState = FindSetState(Owners[reward]);
                }
                if (setState == null)
                {
                    GD.PrintErr("[AiTeammate][Flow] p2-card-reward-failed reason=set-state-not-found owner=P2");
                    return false;
                }

                MethodInfo select = AccessTools.Method(sync.GetType(), "SelectRewardForPlayer", new Type[] { setState.GetType(), typeof(Reward) });
                if (select == null) throw new MissingMethodException("SelectRewardForPlayer(RewardsSetState, Reward)");

                SelectingCard = reward;
                Task<bool> task;
                try
                {
                    task = (Task<bool>)select.Invoke(sync, new object[] { setState, reward });
                }
                finally
                {
                    SelectingCard = null;
                }

                Selections[reward] = task;
                bool claimed = await task;
                if (claimed)
                {
                    Decided.Add(reward);
                    if (button != null && GodotObject.IsInstanceValid(button))
                    {
                        button.EmitSignal(NRewardButton.SignalName.RewardClaimed, button);
                    }
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Flow] ClaimP2CardRewardBackend error: " + ex);
                return false;
            }
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
                object setState = null;
                if (Owners.ContainsKey(reward)) setState = FindSetState(Owners[reward]);
                if (setState == null) setState = FindSetStateForReward(reward);
                if (setState == null) throw new InvalidOperationException("No native reward set state for " + reward.Player.NetId);
                MethodInfo select = AccessTools.Method(sync.GetType(), "SelectRewardForPlayer", new Type[] { setState.GetType(), typeof(Reward) });
                if (select == null) throw new MissingMethodException("SelectRewardForPlayer(RewardsSetState, Reward)");
                Task<bool> task;
                ulong p2IdCheck = TurnCoordinator.GetPlayer2NetId();
                bool isP2RewardCheck = p2IdCheck != 0 && reward.Player != null && reward.Player.NetId == p2IdCheck;
                PotionReward p2PotEarly = reward as PotionReward;
                if (p2PotEarly != null && isP2RewardCheck)
                {
                    // Directive #5.65: Route directly to TryExecuteP2PotionReplacement without touching LocalContext.NetId!
                    bool rep = await FlowCoordinator.TryExecuteP2PotionReplacement(p2PotEarly, button);
                    Decided.Add(reward);
                    completion.TrySetResult(rep);
                    return;
                }

                ulong? previous = LocalContext.NetId;
                CardReward previousCard = SelectingCard;
                try
                {
                    // A synchronous identity scope only, no DRA foreground/UI switch. DRA's
                    // native SyncLocalChoice patch realigns sender when the awaited choice resumes.
                    LocalContext.NetId = reward.Player.NetId;
                    SelectingCard = reward as CardReward;
                    CardReward selCr = reward as CardReward;
                    if (selCr != null) EnqueuePendingCardReward(selCr);
                    task = (Task<bool>)select.Invoke(sync, new object[] { setState, reward });
                }
                finally { SelectingCard = previousCard; LocalContext.NetId = previous; }
                await AwaitCurrent(task, generation, cancellation);
                bool claimed = await task;
                ulong p2Id = TurnCoordinator.GetPlayer2NetId();
                bool isP2 = p2Id != 0 && reward.Player != null && reward.Player.NetId == p2Id;

                // Directive #5.45: Strict verification for PotionReward
                PotionReward potionReward = reward as PotionReward;
                if (potionReward != null)
                {
                    string potionId = (potionReward.Potion != null && potionReward.Potion.Id != null) ? potionReward.Potion.Id.ToString() : "unknown";
                    bool successfullySelected = reward.SuccessfullySelected;
                    bool hasClaimedModel = potionReward.ClaimedPotion != null;
                    bool inInventory = false;
                    if (reward.Player != null && reward.Player.Potions != null)
                    {
                        foreach (var p in reward.Player.Potions)
                        {
                            if (p != null && (p == potionReward.Potion || (p.Id != null && potionReward.Potion != null && p.Id.Equals(potionReward.Potion.Id))))
                            {
                                inInventory = true;
                                break;
                            }
                        }
                    }
                    RewardsSet potSet = Owners.ContainsKey(reward) ? Owners[reward] : null;
                    bool potSetCompleted = potSet != null && RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(potSet);

                    bool potionConfirmed = claimed && successfullySelected && hasClaimedModel && inInventory && !potSetCompleted;

                    if (potionConfirmed)
                    {
                        GD.Print(string.Format("[AiTeammate][Flow] potion-reward-backend-confirmed owner={0} potion={1}", isP2 ? "P2" : "P1", potionId));
                        GD.Print(string.Format("[AiTeammate][Flow] potion-reward-applied owner={0} potion={1}", isP2 ? "P2" : "P1", potionId));
                        FlowCoordinator.MarkRewardBackendConfirmed(reward, isP2 ? "P2" : "P1");
                        Decided.Add(reward);
                        button.EmitSignal(NRewardButton.SignalName.RewardClaimed, button);
                        completion.TrySetResult(true);
                        return;
                    }
                    else
                    {
                        Selections.Remove(reward);
                        int slotCount = reward.Player != null ? reward.Player.MaxPotionCount : 0;
                        int occupiedCount = 0;
                        if (reward.Player != null && reward.Player.PotionSlots != null)
                        {
                            slotCount = reward.Player.PotionSlots.Count;
                            for (int s = 0; s < reward.Player.PotionSlots.Count; s++)
                            {
                                if (reward.Player.PotionSlots[s] != null) occupiedCount++;
                            }
                        }
                        else if (reward.Player != null && reward.Player.Potions != null)
                        {
                            foreach (var p in reward.Player.Potions) if (p != null) occupiedCount++;
                        }
                        int openSlot = Math.Max(0, slotCount - occupiedCount);
                        bool isFull = openSlot <= 0 || (reward.Player != null && !reward.Player.HasOpenPotionSlots);

                        if (isP2)
                        {
                            bool replaced = await FlowCoordinator.TryExecuteP2PotionReplacement(potionReward, button);
                            Decided.Add(reward);
                            completion.TrySetResult(replaced);
                            return;
                        }
                        else
                        {
                            string realReason = !claimed ? "procure-returned-false" : (!successfullySelected ? "successfully-selected-false" : (!hasClaimedModel ? "claimed-potion-null" : (!inInventory ? "potion-not-in-inventory" : "reward-set-completed")));
                            GD.PrintErr(string.Format("[AiTeammate][Flow] potion-reward-failed reason={0} owner=P1 potion={1}", realReason, potionId));
                            if (GodotObject.IsInstanceValid(button))
                            {
                                button.Enable();
                            }
                            completion.TrySetResult(false);
                            return;
                        }
                    }
                }

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
                RewardsSet set;
                // Event rewards may be presented by a native screen without being
                // registered in the managed combat-owner map. Never index Owners
                // blindly after native selection has already succeeded.
                if (!Owners.TryGetValue(reward, out set) || set == null)
                {
                    // Directive #5.85 (Task 1): unmanaged rewards (event / Neow / chest) never reach the
                    // managed path below, so they never emitted RewardClaimed/RewardSkipped. The native
                    // NRewardsScreen then kept the button on screen (e.g. RELIC.PEAR), stayed
                    // IsComplete == false and never left NOverlayStack. Emit the signal here, first.
                    if (GodotObject.IsInstanceValid(button))
                    {
                        button.EmitSignal(claimed ? NRewardButton.SignalName.RewardClaimed : NRewardButton.SignalName.RewardSkipped, button);
                    }
                    RelicReward relic = reward as RelicReward;
                    if (isP2 && relic != null && NativeRelicInventoryConfirmed(relic))
                    {
                        FlowCoordinator.MarkRewardBackendConfirmed(reward, "P2-relic-native");
                        GD.Print("[AiTeammate][Flow] p2-relic-claim-terminal owner=P2 relic=" + RelicId(relic) + " source=native");
                        GD.Print("[AiTeammate][Flow] p2-reward-gate-released owner=P2 type=RelicReward reason=native-completed");
                        ResolvedRewards.Add(reward);
                        Selections.Remove(reward);
                        TryCloseP2UnmanagedRewardScreen(button, isP2);
                        completion.TrySetResult(true);
                        return;
                    }
                    // A native event reward with no managed owner has no sibling set
                    // to inspect; its successful native task is already terminal.
                    FlowCoordinator.MarkRewardBackendConfirmed(reward, isP2 ? "P2-native-unregistered" : "P1-native-unregistered");
                    ResolvedRewards.Add(reward);
                    Selections.Remove(reward);
                    TryCloseP2UnmanagedRewardScreen(button, isP2);
                    completion.TrySetResult(true);
                    return;
                }
                FlowCoordinator.MarkRewardBackendConfirmed(reward, reward.Player.NetId == TurnCoordinator.GetPlayer2NetId() ? "P2" : "P1");
                CardReward card = reward as CardReward;
                string decision;
                if (claimed && card != null && CardDecisions.TryGetValue(card, out decision))
                {
                    GD.Print("[AiTeammate][Flow] p2-choice-resolved kind=card-reward " + decision + " backend=true");
                    CardDecisions.Remove(card);
                }
                if (GodotObject.IsInstanceValid(button))
                {
                    button.EmitSignal(claimed ? NRewardButton.SignalName.RewardClaimed : NRewardButton.SignalName.RewardSkipped, button);
                }

                bool allDecided = true;
                if (set != null && set.Rewards != null)
                {
                    foreach (Reward r in set.Rewards)
                    {
                        if (r != null && !r.SuccessfullySelected && !Decided.Contains(r))
                        {
                            allDecided = false;
                            break;
                        }
                    }
                }

                if (set != null && allDecided)
                {
                    bool isP2Set = p2Id != 0 && set.Player != null && set.Player.NetId == p2Id;
                    bool isCompleted = RunManager.Instance != null && RunManager.Instance.RewardsSetSynchronizer != null &&
                                        RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(set);

                    if (isCompleted)
                    {
                        if (isP2Set)
                        {
                            var targetScreen = FindRewardsScreenAncestor(button) ?? FlowCoordinator.CurrentRewardGateScreen;
                            if (targetScreen != null)
                            {
                                FlowCoordinator.ForceCompleteRewardScreenFromProceed(targetScreen, "p2-native-set-completed");
                            }
                        }
                    }
                    else if (isP2Set)
                    {
                        GD.Print("[AiTeammate][Flow] p2-reward-set-finalize-deferred owner=P2 remaining=0 action=SkipP2RewardsSetBackend");
                        await SkipP2RewardsSetBackend(set, FindRewardsScreenAncestor(button) ?? FlowCoordinator.CurrentRewardGateScreen, "p2-all-decided-finalize");
                    }
                }

                completion.TrySetResult(true);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception ex)
            {
                Selections.Remove(reward);
                ulong p2Id = TurnCoordinator.GetPlayer2NetId();
                bool isP2 = p2Id != 0 && reward != null && reward.Player != null && reward.Player.NetId == p2Id;
                CardReward card = reward as CardReward;
                if (card != null && isP2) ManualRewards.Add(card);
                if (GodotObject.IsInstanceValid(button)) button.Enable();
                GD.PrintErr("[AiTeammate][Flow] reward-selection-failed isP2=" + isP2 + " current-reward-manual=" + (isP2 && card != null) + " exception=" + ex);
                completion.TrySetException(ex);
            }
            finally { SelectionSerial.Release(); }
        }

        private static string RelicId(RelicReward reward)
        {
            try { return reward != null && reward.Relic != null ? reward.Relic.Id.ToString() : (reward != null && reward.ClaimedRelic != null ? reward.ClaimedRelic.Id.ToString() : "unknown"); }
            catch { return "unknown"; }
        }

        private static bool NativeRelicInventoryConfirmed(RelicReward reward)
        {
            try
            {
                if (reward == null || reward.Player == null) return false;
                object target = reward.ClaimedRelic ?? reward.Relic;
                if (target == null || reward.Player.Relics == null) return false;
                PropertyInfo targetIdProperty = AccessTools.Property(target.GetType(), "Id");
                object targetId = targetIdProperty != null ? targetIdProperty.GetValue(target, null) : null;
                string id = targetId != null ? targetId.ToString() : target.ToString();
                foreach (object owned in reward.Player.Relics)
                {
                    if (owned == null) continue;
                    PropertyInfo ownedIdProperty = AccessTools.Property(owned.GetType(), "Id");
                    object ownedId = ownedIdProperty != null ? ownedIdProperty.GetValue(owned, null) : null;
                    if (ReferenceEquals(owned, target) || (ownedId != null && ownedId.ToString() == id)) return true;
                }
            }
            catch { }
            return false;
        }

        // Directive #5.85 (Task 1): an unmanaged P2 reward set (event / Neow / chest) that the native
        // backend already completed still leaves its NRewardsScreen inside NOverlayStack, because no
        // managed owner ever drives that screen. Close it so the window actually goes away.
        private static void TryCloseP2UnmanagedRewardScreen(NRewardButton button, bool isP2)
        {
            if (!isP2) return;
            try
            {
                NRewardsScreen screen = FindRewardsScreenAncestor(button);
                if (screen == null || !GodotObject.IsInstanceValid(screen)) return;
                if (IsManagedScreen(screen)) return;
                RewardsSet unmanagedSet = ScreenSet(screen);
                if (unmanagedSet == null || unmanagedSet.Player == null) return;
                ulong p2Id = TurnCoordinator.GetPlayer2NetId();
                if (p2Id == 0 || unmanagedSet.Player.NetId != p2Id) return;
                if (!RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(unmanagedSet)) return;
                bool inStack = IsScreenInOverlayStack(screen);
                bool complete = false;
                try { complete = screen.IsComplete; } catch { }
                if (!inStack && complete) return;
                FlowCoordinator.ForceCompleteRewardScreenFromProceed(screen, "P2-unmanaged-backend-complete");
                if (inStack)
                {
                    NOverlayStack overlay = NOverlayStack.Instance;
                    if (overlay != null && GodotObject.IsInstanceValid(overlay))
                    {
                        overlay.Remove(screen);
                    }
                }
                FlowCoordinator.TryCompleteEventRewardGate("p2-unmanaged-complete");
                GD.Print("[AiTeammate][Flow] p2-unmanaged-reward-screen-closed owner=P2 overlayPresent=" + inStack + " isComplete=" + complete);
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Flow] p2-unmanaged-reward-screen-close-error: " + ex.Message);
            }
        }

        private static NRewardsScreen FindRewardsScreenAncestor(Node node)
        {
            try
            {
                Node current = node;
                for (int depth = 0; depth < 8 && current != null; depth++)
                {
                    NRewardsScreen screen = current as NRewardsScreen;
                    if (screen != null) return screen;
                    current = current.GetParent();
                }
            }
            catch { }
            return FlowCoordinator.CurrentRewardGateScreen;
        }

        private static bool IsScreenInOverlayStack(NRewardsScreen screen)
        {
            try
            {
                if (screen == null || NOverlayStack.Instance == null) return false;
                IList overlays = AccessTools.Field(typeof(NOverlayStack), "_overlays").GetValue(NOverlayStack.Instance) as IList;
                if (overlays == null) return false;
                foreach (object item in overlays) if (Object.ReferenceEquals(item, screen)) return true;
            }
            catch { }
            return false;
        }

        // Directive #5.86 (Task 2): how many buttons of an unmanaged screen are still clickable.
        // An unmanaged NRewardsScreen (Neow / event / chest) has no managed owner driving it, so this
        // count is what decides whether it is exhausted and can be closed by us instead of by the
        // native OnProceedButtonPressed (which calls SkipLocalRewardsSet and throws for P1).
        public static int CountRemainingButtons(NRewardsScreen screen)
        {
            if (screen == null || !GodotObject.IsInstanceValid(screen)) return 0;
            int count = 0;
            try
            {
                FieldInfo buttonsField = AccessTools.Field(typeof(NRewardsScreen), "_rewardButtons");
                IList buttons = buttonsField == null ? null : buttonsField.GetValue(screen) as IList;
                if (buttons != null)
                {
                    foreach (object entry in buttons)
                    {
                        Node node = entry as Node;
                        if (node == null) continue;
                        if (!GodotObject.IsInstanceValid(node)) continue;
                        if (node.IsQueuedForDeletion()) continue;
                        CanvasItem canvasNode = node as CanvasItem;
                        if (canvasNode != null && !canvasNode.Visible) continue;
                        count++;
                    }
                    return count;
                }
            }
            catch { }
            try
            {
                foreach (Node child in screen.GetChildren())
                {
                    if (!(child is NRewardButton)) continue;
                    if (!GodotObject.IsInstanceValid(child)) continue;
                    CanvasItem canvasChild = child as CanvasItem;
                    if (canvasChild != null && !canvasChild.Visible) continue;
                    count++;
                }
            }
            catch { }
            return count;
        }

        // Directive #5.86 (Task 2): an unmanaged screen carries no owner, so inspect the buttons'
        // rewards directly before deciding that the proceeding screen belongs to P2.
        public static bool ScreenHasP2Buttons(NRewardsScreen screen, ulong p2Id)
        {
            if (screen == null || p2Id == 0 || !GodotObject.IsInstanceValid(screen)) return false;
            try
            {
                FieldInfo buttonsField = AccessTools.Field(typeof(NRewardsScreen), "_rewardButtons");
                IList buttons = buttonsField == null ? null : buttonsField.GetValue(screen) as IList;
                if (buttons == null) return false;
                foreach (object entry in buttons)
                {
                    NRewardButton button = entry as NRewardButton;
                    if (button == null || !GodotObject.IsInstanceValid(button)) continue;
                    Reward reward = button.Reward;
                    if (reward != null && reward.Player != null && reward.Player.NetId == p2Id) return true;
                }
            }
            catch { }
            return false;
        }

        // Directive #5.86 (Task 2): a card / relic selection overlay must never be closed underneath
        // the player. Reconcile only closes an unmanaged reward screen when nothing else is open.
        public static bool HasActiveCardSelectionOverlay()
        {
            try
            {
                if (SelectingCard != null) return true;
                foreach (NCardRewardSelectionScreen screen in CardScreens.Keys)
                {
                    if (screen != null && GodotObject.IsInstanceValid(screen)) return true;
                }
            }
            catch { }
            return false;
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
            if (__result != null)
            {
                CardReward reward = null;
                lock (CardRewardLock)
                {
                    while (PendingCardRewards.Count > 0)
                    {
                        var candidate = PendingCardRewards.Dequeue();
                        if (candidate != null && !ResolvedRewards.Contains(candidate))
                        {
                            reward = candidate;
                            break;
                        }
                    }
                }
                if (reward == null && SelectingCard != null)
                {
                    reward = SelectingCard;
                }

                if (reward != null)
                {
                    CardScreens[__result] = reward;
                    ulong p2Id = TurnCoordinator.GetPlayer2NetId();
                    if (reward.Player != null && p2Id != 0 && reward.Player.NetId == p2Id)
                    {
                        string src = reward.RewardsSetIndex >= 0 ? "rewards-set" : "lost-coffer";
                        GD.Print(string.Format("[AiTeammate][Flow] p2-card-reward-route owner=P2 source={0}", src));

                        // Directive #5.64: Trigger active background card selection immediately
                        TriggerActiveBackgroundCardSelection(__result, reward);
                    }
                }
            }
            LogScreen("ShowScreen-card", __result, true);
        }

        public static void OptionSelectedPostfix(NCardRewardSelectionScreen __instance, Task<int?> __result)
        {
            CardReward reward;
            if (!CardScreens.TryGetValue(__instance, out reward))
            {
                lock (CardRewardLock)
                {
                    while (PendingCardRewards.Count > 0)
                    {
                        var candidate = PendingCardRewards.Dequeue();
                        if (candidate != null && !ResolvedRewards.Contains(candidate))
                        {
                            reward = candidate;
                            CardScreens[__instance] = reward;
                            break;
                        }
                    }
                }
            }

            if (reward == null || reward.Player == null) return;
            ulong p2Id = TurnCoordinator.GetPlayer2NetId();
            if (p2Id == 0 || reward.Player.NetId != p2Id)
            {
                // P1's CardReward: MUST be chosen manually by human player!
                return;
            }

            if (!FlowCoordinator.AutoCardRewardEnabled)
            {
                GD.Print("[AiTeammate][Flow] p2-card-reward-auto-disabled native-manual=true");
                return;
            }
            if (ManualRewards.Contains(reward)) return;
            if (!SubmittedScreens.Add(__instance)) return;

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

        private static async void TriggerActiveBackgroundCardSelection(NCardRewardSelectionScreen screen, CardReward reward)
        {
            if (screen == null || reward == null) return;
            if (!SubmittedScreens.Add(screen)) return;

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                Exception caughtEx = null;
                try
                {
                    if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree())
                    {
                        await Task.Delay(50);
                        if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree())
                            throw new InvalidOperationException("Native card reward screen is not live");
                    }

                    var options = AccessTools.Field(typeof(NCardRewardSelectionScreen), "_options").GetValue(screen) as IReadOnlyList<CardCreationResult>;
                    if (options == null || options.Count == 0)
                    {
                        await Task.Delay(50);
                        options = AccessTools.Field(typeof(NCardRewardSelectionScreen), "_options").GetValue(screen) as IReadOnlyList<CardCreationResult>;
                        if (options == null || options.Count == 0)
                            throw new InvalidOperationException("Native card reward options empty");
                    }

                    string id, source, error, scores;
                    CardModel card;
                    int index;
                    FlowCoordinator.EvaluateCardReward(reward.Player, options, out id, out card, out source, out index, out error, out scores);
                    if (source == "circuit-open" || index < 0 || index >= options.Count || card == null)
                    {
                        throw new InvalidOperationException("Card decision unavailable: " + source);
                    }

                    var holder = screen.GetCardHolder(card);
                    if (holder == null)
                    {
                        await Task.Delay(50);
                        holder = screen.GetCardHolder(card);
                        if (holder == null)
                            throw new InvalidOperationException("Selected native card holder missing");
                    }

                    CardDecisions[reward] = "pick=" + index + " reason=" + source + " scores=" + scores + " card=" + id;
                    GD.Print(string.Format("[AiTeammate][Flow] p2-card-choice-submit owner=P2 pick={0} card={1}", index, id ?? "unknown"));

                    Player p2 = reward.Player;
                    Player p1 = FlowCoordinator.GetPlayer1();
                    int p2DeckBefore = (p2 != null && p2.Deck != null && p2.Deck.Cards != null) ? p2.Deck.Cards.Count : -1;
                    int p1DeckBefore = (p1 != null && p1.Deck != null && p1.Deck.Cards != null) ? p1.Deck.Cards.Count : -1;

                    bool isManaged = Owns(reward);
                    AccessTools.Method(typeof(NCardRewardSelectionScreen), "SelectCard").Invoke(screen, new object[] { holder });

                    ResolvedRewards.Add(reward);
                    FlowCoordinator.MarkRewardBackendConfirmed(reward, "P2");
                    if (!isManaged)
                    {
                        GD.Print("[AiTeammate][RewardFlow] Unmanaged P2 card reward auto-selected: " + (holder.CardModel != null ? holder.CardModel.Id.ToString() : (id ?? "unknown")));
                    }

                    Callable.From(delegate
                    {
                        try
                        {
                            int p2DeckAfter = (p2 != null && p2.Deck != null && p2.Deck.Cards != null) ? p2.Deck.Cards.Count : -1;
                            int p1DeckAfter = (p1 != null && p1.Deck != null && p1.Deck.Cards != null) ? p1.Deck.Cards.Count : -1;
                            GD.Print(string.Format("[AiTeammate][Flow] p2-card-choice-backend-confirmed owner=P2 deckOwner=P2 card={0} p2Deck={1} p1Deck={2}", id, p2DeckAfter, p1DeckAfter));
                        }
                        catch (Exception exV)
                        {
                            GD.PrintErr("[AiTeammate][Flow] p2-card-choice-verify error: " + exV.Message);
                        }
                    }).CallDeferred();

                    return;
                }
                catch (Exception ex)
                {
                    caughtEx = ex;
                }

                if (caughtEx != null)
                {
                    if (attempt < 3)
                    {
                        GD.Print(string.Format("[AiTeammate][Flow] p2-card-choice-retry-scheduled owner=P2 attempt={0} error={1}", attempt, caughtEx.Message));
                        await Task.Delay(100);
                    }
                    else
                    {
                        FailClosedAndUnlockP2CardChoice(screen, reward, caughtEx.Message);
                        return;
                    }
                }
            }
        }

        private static void FailClosedAndUnlockP2CardChoice(NCardRewardSelectionScreen screen, CardReward reward, string reason)
        {
            try
            {
                GD.PrintErr(string.Format("[AiTeammate][Flow] p2-card-choice-failed owner=P2 reason={0}", reason ?? "unknown"));
                CardDecisions.Remove(reward);

                object setState = FindSetStateForReward(reward);
                if (setState != null)
                {
                    object sync = RunManager.Instance.RewardsSetSynchronizer;
                    MethodInfo skip = AccessTools.Method(sync.GetType(), "SkipRewardsSet", new Type[] { setState.GetType() });
                    if (skip != null) skip.Invoke(sync, new object[] { setState });

                    RewardsSet set = AccessTools.Field(setState.GetType(), "set").GetValue(setState) as RewardsSet;
                    if (set != null && set.Rewards != null)
                    {
                        foreach (Reward r in set.Rewards)
                        {
                            if (r != null && !ResolvedRewards.Contains(r))
                            {
                                ResolvedRewards.Add(r);
                                try { r.OnSkipped(); } catch { }
                                FlowCoordinator.MarkRewardBackendConfirmed(r, "P2-skip");
                            }
                        }
                    }
                }
                else
                {
                    ResolvedRewards.Add(reward);
                    try { reward.OnSkipped(); } catch { }
                    FlowCoordinator.MarkRewardBackendConfirmed(reward, "P2-skip");
                }

                if (screen != null && GodotObject.IsInstanceValid(screen))
                {
                    var cs = AccessTools.Field(typeof(NCardRewardSelectionScreen), "_completionSource").GetValue(screen);
                    if (cs != null)
                    {
                        MethodInfo trySet = AccessTools.Method(cs.GetType(), "TrySetResult");
                        if (trySet != null) trySet.Invoke(cs, new object[] { null });
                    }

                    var overlay = NOverlayStack.Instance;
                    if (overlay != null && GodotObject.IsInstanceValid(overlay))
                    {
                        overlay.Remove(screen);
                    }
                }

                FlowCoordinator.TryCompleteEventRewardGate("p2-card-choice-failed");
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Flow] FailClosedAndUnlockP2CardChoice error: " + ex);
            }
        }

        private static void SubmitNativeCardChoice(NCardRewardSelectionScreen screen, CardReward reward, Task<int?> choice)
        {
            try
            {
                ulong p2Id = TurnCoordinator.GetPlayer2NetId();
                bool isManaged = reward != null && Owns(reward);
                bool isP2Reward = reward != null && reward.Player != null && p2Id != 0 && reward.Player.NetId == p2Id;
                if (reward == null || (!isManaged && !isP2Reward) || ResolvedRewards.Contains(reward)) return;

                if (choice != null && choice.IsCompleted) return;
                if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree()) throw new InvalidOperationException("Native card reward screen is not live");
                var options = AccessTools.Field(typeof(NCardRewardSelectionScreen), "_options").GetValue(screen) as IReadOnlyList<CardCreationResult>;
                if (options == null || options.Count == 0) throw new InvalidOperationException("Native card reward options empty");
                string id, source, error, scores; CardModel card; int index;
                FlowCoordinator.EvaluateCardReward(reward.Player, options, out id, out card, out source, out index, out error, out scores);
                if (source == "circuit-open" || index < 0 || index >= options.Count || card == null) throw new InvalidOperationException("Card decision unavailable: " + source);
                var holder = screen.GetCardHolder(card);
                if (holder == null) throw new InvalidOperationException("Selected native card holder missing");
                CardDecisions[reward] = "pick=" + index + " reason=" + source + " scores=" + scores + " card=" + id;
                GD.Print("[AiTeammate][Flow] p2-card-choice-submit owner=P2 " + CardDecisions[reward] + " native=true");

                Player p2 = reward.Player;
                Player p1 = FlowCoordinator.GetPlayer1();
                int p2DeckBefore = (p2 != null && p2.Deck != null && p2.Deck.Cards != null) ? p2.Deck.Cards.Count : -1;
                int p1DeckBefore = (p1 != null && p1.Deck != null && p1.Deck.Cards != null) ? p1.Deck.Cards.Count : -1;

                AccessTools.Method(typeof(NCardRewardSelectionScreen), "SelectCard").Invoke(screen, new object[] { holder });

                if (!isManaged)
                {
                    ResolvedRewards.Add(reward);
                    FlowCoordinator.MarkRewardBackendConfirmed(reward, "P2");
                    GD.Print("[AiTeammate][RewardFlow] Unmanaged P2 card reward auto-selected: " + (holder.CardModel != null ? holder.CardModel.Id.ToString() : (id ?? "unknown")));
                }

                Callable.From(delegate
                {
                    try
                    {
                        int p2DeckAfter = (p2 != null && p2.Deck != null && p2.Deck.Cards != null) ? p2.Deck.Cards.Count : -1;
                        int p1DeckAfter = (p1 != null && p1.Deck != null && p1.Deck.Cards != null) ? p1.Deck.Cards.Count : -1;
                        GD.Print(string.Format("[AiTeammate][Flow] p2-card-choice-backend-confirmed owner=P2 deckOwner=P2 card={0} p2Deck={1} p1Deck={2}", id, p2DeckAfter, p1DeckAfter));
                    }
                    catch (Exception exV)
                    {
                        GD.PrintErr("[AiTeammate][Flow] p2-card-choice-verify error: " + exV.Message);
                    }
                }).CallDeferred();
            }
            catch (Exception ex)
            {
                int retries = 0;
                CardChoiceRetries.TryGetValue(reward, out retries);
                retries++;
                CardChoiceRetries[reward] = retries;

                if (retries < 3)
                {
                    SubmittedScreens.Remove(screen);
                    GD.Print(string.Format("[AiTeammate][Flow] p2-card-choice-retry-scheduled owner=P2 attempt={0} error={1}", retries, ex.Message));
                }
                else
                {
                    ManualRewards.Add(reward);
                    CardDecisions.Remove(reward);
                    GD.PrintErr(string.Format("[AiTeammate][Flow] p2-card-choice-failed owner=P2 reason=retry-limit-exceeded attempts={0} error={1}", retries, ex.Message));
                }
            }
        }

        public static Task SkipP2RewardsSetBackend(RewardsSet set, NRewardsScreen screen, string source)
        {
            try
            {
                if (set == null) return Task.FromResult(false);
                object sync = RunManager.Instance.RewardsSetSynchronizer;
                if (sync != null)
                {
                    object setState = null;
                    if (set.Rewards != null && set.Rewards.Count > 0)
                    {
                        setState = FindSetStateForReward(set.Rewards[0]);
                    }
                    if (setState == null && set.Rewards != null && set.Rewards.Count > 0 && Owners.ContainsKey(set.Rewards[0]))
                    {
                        setState = FindSetState(Owners[set.Rewards[0]]);
                    }

                    if (setState != null)
                    {
                        MethodInfo skip = AccessTools.Method(sync.GetType(), "SkipRewardsSet", new Type[] { setState.GetType() });
                        if (skip != null) skip.Invoke(sync, new object[] { setState });
                    }
                    else
                    {
                        MethodInfo skipTop = AccessTools.Method(sync.GetType(), "SkipRewardsSetOnStackTopForPlayer");
                        if (skipTop != null && set.Player != null) skipTop.Invoke(sync, new object[] { set.Player });
                    }
                }

                if (set.Rewards != null)
                {
                    foreach (Reward r in set.Rewards)
                    {
                        if (r != null && !ResolvedRewards.Contains(r))
                        {
                            ResolvedRewards.Add(r);
                            try { r.OnSkipped(); } catch { }
                            FlowCoordinator.MarkRewardBackendConfirmed(r, "P2-skip");
                        }
                    }
                }

                if (screen != null && GodotObject.IsInstanceValid(screen))
                {
                    var overlay = NOverlayStack.Instance;
                    if (overlay != null && GodotObject.IsInstanceValid(overlay))
                    {
                        overlay.Remove(screen);
                    }
                }

                var targetScreen = screen ?? FlowCoordinator.CurrentRewardGateScreen;
                if (targetScreen != null)
                {
                    FlowCoordinator.ForceCompleteRewardScreenFromProceed(targetScreen, source ?? "p2-backend-skip");
                }

                FlowCoordinator.TryCompleteEventRewardGate(source ?? "p2-skip");
                GD.Print(string.Format("[AiTeammate][Flow] p2-reward-screen-skipped owner=P2 source={0}", source ?? "unknown"));
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Flow] SkipP2RewardsSetBackend error: " + ex);
            }
            return Task.FromResult(true);
        }

        public static bool ProceedPrefix(NRewardsScreen __instance)
        {
            RewardsSet set = ScreenSet(__instance);
            ulong p2Id = TurnCoordinator.GetPlayer2NetId();
            ulong p1Id = TurnCoordinator.GetPlayer1NetId();

            if (set != null && set.Player != null && p2Id != 0 && set.Player.NetId == p2Id)
            {
                // Directive #5.64: P2 reward screen proceed button pressed
                bool isComplete = RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(set);
                if (!isComplete)
                {
                    bool hasInFlightSelection = false;
                    if (set.Rewards != null)
                    {
                        foreach (Reward r in set.Rewards)
                        {
                            Task pending;
                            if (Selections.TryGetValue(r, out pending) && pending != null && !pending.IsCompleted)
                            {
                                hasInFlightSelection = true;
                                break;
                            }
                        }
                    }
                    if (hasInFlightSelection)
                    {
                        GD.Print("[AiTeammate][Flow] p2-proceed-wait source=proceed reason=selection-in-flight");
                        return false;
                    }

                    GD.Print("[AiTeammate][Flow] p2-proceed-backend-skip owner=P2 reason=user-proceed-pressed");
                    SkipP2RewardsSetBackend(set, __instance, "p2-proceed-skip");
                    return false;
                }
                else
                {
                    // Directive #5.85 (Task 2): the P2 set is already complete, so the native
                    // OnProceedButtonPressed handler must NOT run: it calls SkipLocalRewardsSet()
                    // for the local player while that player is not viewing any set, throwing
                    // "Tried to skip reward set for player <P1>, but they are not currently viewing
                    // any reward set!" and leaving the window stuck. Close the screen ourselves.
                    FlowCoordinator.ForceCompleteRewardScreenFromProceed(__instance, "P2-proceed-complete");
                    if (NOverlayStack.Instance != null && GodotObject.IsInstanceValid(__instance))
                    {
                        NOverlayStack.Instance.Remove(__instance);
                        GD.Print("[AiTeammate][Flow] p2-proceed-overlay-removed owner=P2 reason=set-complete");
                    }
                    FlowCoordinator.TryCompleteEventRewardGate("p2-proceed");
                    return false;
                }
            }

            if (!IsManagedScreen(__instance))
            {
                RewardsSet unmanagedSet = ScreenSet(__instance);
                if (unmanagedSet != null && unmanagedSet.Player != null && unmanagedSet.Player.NetId == TurnCoordinator.GetPlayer1NetId())
                {
                    // Directive #5.44: Preserve unmanaged rewards set in stack for P1 until room exit
                    FlowCoordinator.ForceCompleteRewardScreenFromProceed(__instance, "P1-unmanaged-proceed");
                    FlowCoordinator.TryCompleteEventRewardGate("p1-proceed");
                    return true;
                }

                // Directive #5.86 (Task 2): an unmanaged P2 (or ownerless) screen used to fall through
                // to `return true`, so the native OnProceedButtonPressed ran SkipLocalRewardsSet() for
                // the local player P1 and threw
                //   "Tried to skip reward set for player <P1>, but they are not currently viewing any
                //    reward set!"
                // leaving the window impossible to close. Handle it here and never let native skip.
                ulong p2ProceedId = TurnCoordinator.GetPlayer2NetId();
                bool p2Owned = (unmanagedSet != null && unmanagedSet.Player != null && p2ProceedId != 0 && unmanagedSet.Player.NetId == p2ProceedId)
                    || ScreenHasP2Buttons(__instance, p2ProceedId);
                int remaining = CountRemainingButtons(__instance);
                if (p2Owned || remaining == 0)
                {
                    string closeReason = p2Owned ? "proceed-unmanaged-p2-complete" : "proceed-unmanaged-empty";
                    FlowCoordinator.ForceCompleteRewardScreenFromProceed(__instance, closeReason);
                    if (GodotObject.IsInstanceValid(__instance) && IsScreenInOverlayStack(__instance))
                    {
                        NOverlayStack proceedOverlay = NOverlayStack.Instance;
                        if (proceedOverlay != null) proceedOverlay.Remove(__instance);
                    }
                    FlowCoordinator.TryCompleteEventRewardGate("proceed-unmanaged-complete");
                    GD.Print("[AiTeammate][Flow] proceed-unmanaged-p2-complete owner=" + (p2Owned ? "P2" : "unknown")
                        + " remaining=" + remaining + " nativeSkipped=true");
                    return false;
                }
                return true;
            }

            RewardsSet mSet = AccessTools.Field(typeof(NRewardsScreen), "_rewardsSet").GetValue(__instance) as RewardsSet;
            if (mSet == null) return true;
            if (RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(mSet))
            {
                FlowCoordinator.ForceCompleteRewardScreenFromProceed(__instance, "P1-proceed-already-complete");
                return true;
            }
            if (mSet.Rewards != null)
            {
                foreach (Reward reward in mSet.Rewards)
                {
                    Task pending;
                    if (Selections.TryGetValue(reward, out pending) && pending != null && !pending.IsCompleted)
                    {
                        GD.Print("[AiTeammate][Flow] room-exit-gate-wait source=proceed reason=selection-pending");
                        return false;
                    }
                }
            }
            if (mSet.Player.NetId != TurnCoordinator.GetPlayer1NetId()) return false;

            FlowCoordinator.ForceCompleteRewardScreenFromProceed(__instance, "P1-proceed-map");
            FlowCoordinator.TryCompleteEventRewardGate("p1-proceed");
            return true;
        }

        public static void SkipOutstandingP1RewardSets(string reason)
        {
            try
            {
                if (RunManager.Instance == null || RunManager.Instance.RewardsSetSynchronizer == null) return;
                object sync = RunManager.Instance.RewardsSetSynchronizer;
                ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                if (p1Id == 0) return;

                MethodInfo skipMethod = AccessTools.Method(sync.GetType(), "SkipRewardsSet");
                if (skipMethod == null) return;

                // 1. Direct inspection of RewardsSetSynchronizer.rewardsStack for P1
                Player p1 = FlowCoordinator.GetPlayer1();
                if (p1 != null)
                {
                    MethodInfo get = AccessTools.Method(sync.GetType(), "GetRewardStateForPlayer");
                    object state = get != null ? get.Invoke(sync, new object[] { p1 }) : null;
                    if (state != null)
                    {
                        FieldInfo stackField = AccessTools.Field(state.GetType(), "rewardsStack");
                        IEnumerable stack = stackField != null ? stackField.GetValue(state) as IEnumerable : null;
                        if (stack != null)
                        {
                            List<object> entries = new List<object>();
                            foreach (object entry in stack) if (entry != null) entries.Add(entry);
                            foreach (object entry in entries)
                            {
                                RewardsSet set = AccessTools.Field(entry.GetType(), "set").GetValue(entry) as RewardsSet;
                                if (set != null && !RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(set))
                                {
                                    skipMethod.Invoke(sync, new object[] { entry });
                                    if (set.Rewards != null)
                                    {
                                        foreach (Reward r in set.Rewards)
                                        {
                                            if (r != null) FlowCoordinator.MarkRewardBackendConfirmed(r, "P1-exit-skip");
                                        }
                                    }
                                    GD.Print("[AiTeammate][Flow] p1-outstanding-stack-skipped source=rewardsStack reason=" + (reason ?? "unknown"));
                                }
                            }
                        }
                    }
                }

                // 2. Check Sets.Values in RewardBackendFlow
                List<SetRegistration> p1Registrations = new List<SetRegistration>();
                lock (Sets)
                {
                    foreach (SetRegistration reg in Sets.Values)
                    {
                        if (reg != null && reg.Set != null && reg.Set.Player != null && reg.Set.Player.NetId == p1Id)
                        {
                            p1Registrations.Add(reg);
                        }
                    }
                }
                foreach (SetRegistration reg in p1Registrations)
                {
                    RewardsSet set = reg.Set;
                    if (!RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted(set))
                    {
                        try
                        {
                            object setState = FindSetState(set);
                            if (setState != null)
                            {
                                skipMethod.Invoke(sync, new object[] { setState });
                                GD.Print("[AiTeammate][Flow] p1-outstanding-set-skipped source=Sets reason=" + (reason ?? "unknown"));
                            }
                        }
                        catch { }

                        if (set.Rewards != null)
                        {
                            foreach (Reward r in set.Rewards)
                            {
                                if (r != null) FlowCoordinator.MarkRewardBackendConfirmed(r, "P1-exit-skip");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Flow] SkipOutstandingP1RewardSets exception: " + ex);
            }
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
            SkipOutstandingP1RewardSets("MapMoveProducer");
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
            SkipOutstandingP1RewardSets("MoveEnqueue");
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
