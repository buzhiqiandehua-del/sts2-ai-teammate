using System;

using System.Collections;

using System.Collections.Generic;

using System.Reflection;

using System.Threading;

using System.Threading.Tasks;

using HarmonyLib;

using Godot;

using MegaCrit.Sts2.Core.Combat;

using MegaCrit.Sts2.Core.Commands;

using MegaCrit.Sts2.Core.Context;

using MegaCrit.Sts2.Core.Entities.Cards;

using MegaCrit.Sts2.Core.Entities.Creatures;

using MegaCrit.Sts2.Core.Entities.Players;

using MegaCrit.Sts2.Core.Entities.UI;

using MegaCrit.Sts2.Core.Models;

using MegaCrit.Sts2.Core.Modding;

using MegaCrit.Sts2.Core.GameActions;

using MegaCrit.Sts2.Core.GameActions.Multiplayer;

using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.Potions;

using MegaCrit.Sts2.Core.Nodes.Cards.Holders;

using MegaCrit.Sts2.Core.Nodes.Combat;

using MegaCrit.Sts2.Core.Nodes.Rooms;

using MegaCrit.Sts2.Core.CardSelection;

using MegaCrit.Sts2.Core.Nodes.Screens;

using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

using MegaCrit.Sts2.Core.Runs;

using MegaCrit.Sts2.Core.Rooms;

using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;

using MegaCrit.Sts2.Core.Entities.RestSite;

using MegaCrit.Sts2.Core.Nodes.RestSite;

using System.Text.RegularExpressions;

using MegaCrit.Sts2.Core.Nodes.Events;

using MegaCrit.Sts2.Core.Events;

using MegaCrit.Sts2.Core.Multiplayer.Game;

using MegaCrit.Sts2.Core.Map;



namespace AiTeammate

{

    public static class AiTeammateMod

    {

        public static bool SuppressControlSwitchUi

        {

            get { return ModEntry.SuppressControlSwitchUi; }

            set { ModEntry.SuppressControlSwitchUi = value; }

        }

    }



    public enum CoordinatorDecision

    {

        Immediate,

        ImmediateSetup,

        Wait

    }



    public static class TurnCoordinator

    {

        public static bool IsWaiting = false;

        public static bool IsDeploying = false;

        public static bool HasReleasedThisTurn = false;

        public static string LastReleaseTrigger = null;

        public static DateTime WaitStartTime = DateTime.MinValue;

        public static int CheapestP1DebuffCost = int.MaxValue;

        public static int LastRoundNumber = -1;

        public static object CachedSolverController = null;

        public static MethodInfo DeployCurrentTurnMethod = null;

        public static MethodInfo RequestSearchMethod = null;

        public static MethodInfo CancelDeploymentMethod = null;

        public static FieldInfo DeploymentField = null;

        public static FieldInfo CombatField = null;

        public static object[] CachedDeployArgs = null;

        public static object CachedDeployResult = null;

        public static DateTime LastProcessCheckTime = DateTime.MinValue;

        public static bool IsSuppressingCancelHook = false;

        public static DateTime ReleaseRequestedTime = DateTime.MinValue;

        public static bool RecalcSucceeded = false;

        public static int CurrentRecalcGeneration = 0;

        public static DateTime DeployStartTime = DateTime.MinValue;

        public static bool Watchdog3sFired = false;

        public static double WaitTimeoutSeconds = 20.0;

        // Directive #5.23: independent P2 lifeline. This timer is deliberately
        // outside OnProcessTick and does not consult/reset the normal watchdog flags.
        private static readonly object LifelineGate = new object();
        private static System.Threading.Timer IndependentLifelineTimer = null;
        private static bool IndependentLifelineArmed = false;
        private static bool IndependentLifelineFired = false;
        private static DateTime IndependentLifelineStartTime = DateTime.MinValue;
        private static int IndependentLifelineTurn = -1;
        private static Player IndependentLifelinePlayer = null;
        private static CombatManager IndependentLifelineCombatManager = null;
        private static CombatState IndependentLifelineState = null;
        private static bool IndependentSearchWatchArmed = false;
        private static DateTime IndependentLastSearchHeartbeat = DateTime.MinValue;
        private static string IndependentSearchArmSource = null;
        private const double IndependentLifelineTimeoutSeconds = 20.0;
        private const double IndependentSearchHeartbeatTimeoutSeconds = 5.0;



        // Directive #5.16 T1: Mid-deployment Watchdog & Search Heartbeat

        public static DateTime MidDeployStartTime = DateTime.MinValue;

        public static bool MidDeployHangTimeoutTriggered = false;

        public static bool MidDeployHangCriticalTriggered = false;

        public static DateTime SearchAliveStartTime = DateTime.MinValue;

        public static DateTime LastSearchHeartbeatTime = DateTime.MinValue;

        private static MethodInfo IsDeployingGetter = null;

        private static MethodInfo IsSearchingGetter = null;



        // Directive #5.14 T1: Room enter window control switch ban & deferral

        public static volatile bool IsEnteringRoom = false;

        public static DateTime RoomEnteringStartedTime = DateTime.MinValue;

        public static DateTime RoomSettleTime = DateTime.MinValue;

        public struct DeferredSwitchRequest

        {

            public ulong TargetNetId;

            public string Source;

            public bool SuppressUi;

        }

        public static readonly List<DeferredSwitchRequest> DeferredModControlSwitches = new List<DeferredSwitchRequest>();



        // Directive #5.0.10 T1: Watchdog room transition silence & transition timing telemetry

        public static bool IsPreCombatOrLoading = true;

        public static DateTime RoomEnterTime = DateTime.MinValue;

        public static double PotionRebuildMs = 0.0;



        // Directive #5.13 T2: Action execution window nail & attribution guard

        public static bool IsP1ActionExecuting = false;
        public static int P1InputLockDepth = 0;
        public static bool P1InputLockedByOverlap { get { return P1InputLockDepth > 0; } }

        public static object P1ActionExecutingInstance = null;

        // Directive #5.30: P2 Turn Bootstrap Internal Channel
        public static bool IsP2TurnBootstrapping = false;

        public static bool IsTurnBootstrapSource(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            return source == "auto-end-turn-next-playable"
                || source == "auto-end-turn-all-vakuu-no-cards"
                || source == "auto-end-turn-fallback"
                || source == "no-playable-cards"
                || source == "wakuu-no-playable-cards-tick"
                || source == "wakuu-autoplay-loop"
                || source.StartsWith("p2-turn-bootstrap");
        }

        public static double RelicRebuildMs = 0.0;

        public static double TopBarRebuildMs = 0.0;

        public static string LastKickTrigger = null;

        public static DateTime LastKickTime = DateTime.MinValue;



        // Directive #3.6 & #3.6.1: Solver search stall watchdog & once-per-turn recalc guard

        public static DateTime TurnStartTime = DateTime.MinValue;

        public static bool HasEnteredDeployPrefixThisTurn = false;

        public static bool StallRecalcTriggered = false;

        public static DateTime StallRecalcTime = DateTime.MinValue;

        public static bool StallForceEndTurnTriggered = false;

        public static bool HasCompletedDeploymentThisTurn = false;

        public static bool RecalcRequestedThisTurn = false;



        // Directive #3.6.3: Hand ownership self-healing guard

        public static bool HasSelfHealedHandThisTurn = false;

        public static int TotalHandRebindHeals = 0;



        // Directive #3.6.5: Empty turn telemetry & leak stack dump

        public static int TurnNumber = 1;

        public static int P2TurnStartEnergy = -1;

        public static int P2PlayedCardsThisTurn = 0;
        public static int P2PlanLengthThisTurn = -1;
        public static string P2SearchStatusThisTurn = "unknown";
        public static long P2SearchNodesThisTurn = -1;
        public static bool P2PlanHasAttackThisTurn = false;
        public static bool P2PlanHasCardActionThisTurn = false;
        public static bool HasRetriedSolverEarlyEndThisTurn = false;
        public static bool SolverEarlyEndReportedThisTurn = false;
        public static bool P2FallbackGreedyUsedThisTurn = false;
        public static bool IsEnqueuingFallbackBatch = false;
        public static string P2TurnEndReason = "normal";

        public static int P2SummaryLoggedTurn = -1;

        public static int P2TurnStartHandCount = 0;

        // Directive #5.26: deployment-chain evidence is kept by turn and is
        // not discarded before the final summary is emitted.
        public static int P2TelemetryTurn = -1;
        public static int P2SolverActionCountThisTurn = -1;
        public static int P2FullAutoActionCountThisTurn = -1;
        public static int P2DeployStartActionCountThisTurn = -1;
        public static bool P2DeployHookObservedThisTurn = false;
        public static bool P2EndTurnEnqueuedThisTurn = false;
        public static bool P2ZeroActionRetryUsedThisTurn = false;
        public static bool P2SummaryFinalizedThisTurn = false;
        public static bool P2FallbackActionPendingThisTurn = false;
        public static bool P2FallbackActionEnqueuedThisTurn = false;

        // Directive #5.34: P2 hand & critical section tracking

        public static int P2CardPileOpInFlightCount = 0;

        public static int P2PotionOpInFlightCount = 0;

        public static bool IsP2HandOrUiTransitioning = false;

        public static bool PendingDeferredHandCheckAfterP2 = false;

        public static DateTime HandMismatchStartTime = DateTime.MinValue;

        public static int ConsecutiveHandMismatchFrames = 0;

        // Directive #5.35 T2.3: Separate timers for P2 card residue and truly blank UI
        public static DateTime P2ResidueStartTime = DateTime.MinValue;

        public static int ConsecutiveP2ResidueFrames = 0;

        public static DateTime BlankHandStartTime = DateTime.MinValue;

        public static int ConsecutiveBlankHandFrames = 0;

        public static string LastHandOwnershipDeferredReason = null;

        public static DateTime LastHandOwnershipDeferredLogTime = DateTime.MinValue;

        public static int LastFullAutoEnsureTurn = -1;



        public static bool IsInP2HandDeferredPeriod(out string reason)

        {

            reason = null;

            // Directive #5.35 T2.2: If P1 is actively playing/selecting cards, defer all hand ownership checks
            var currentHand = MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand.Instance;
            if (currentHand != null && IsPlayer1HandBusy(currentHand))
            {
                reason = "p1-card-play-in-progress";
                return true;
            }

            if (P2ActionSenderScopeDepth > 0)

            {

                reason = "scope-depth";

                return true;

            }

            if (CurrentExecutingActionOwnerNetId != 0 && IsP2Target(CurrentExecutingActionOwnerNetId))

            {

                reason = "executing-action-p2";

                return true;

            }

            if (P2PendingChoiceCount > 0 || FlowCoordinator.IsSubPageManualInteracting)

            {

                reason = "p2-player-choice";

                return true;

            }

            if (P2CardPileOpInFlightCount > 0)

            {

                reason = "cardpile-op-in-flight";

                return true;

            }

            if (P2PotionOpInFlightCount > 0)

            {

                reason = "potion-op-in-flight";

                return true;

            }

            if (IsP2HandOrUiTransitioning || P1RestorePendingAfterP2Scope || P1RestoreDeferredQueued)

            {

                reason = "p2-ui-transitioning";

                return true;

            }

            return false;

        }



        public static string ComputeP1ModelHandFingerprint(Player p1)

        {

            if (p1 == null) return "none";

            try

            {

                var pile = PileType.Hand.GetPile(p1);

                if (pile == null || pile.Cards == null || pile.Cards.Count == 0) return "empty";

                List<string> ids = new List<string>();

                for (int i = 0; i < pile.Cards.Count; i++)

                {

                    var c = pile.Cards[i];

                    if (c != null) ids.Add(c.Id.ToString());

                }

                ids.Sort();

                return string.Join(",", ids.ToArray());

            }

            catch { return "error"; }

        }



        public static string ComputeUiHolderFingerprint(MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand hand)

        {

            if (hand == null) return "none";

            try

            {

                var holders = hand.ActiveHolders;

                if (holders == null || holders.Count == 0) return "empty";

                List<string> ids = new List<string>();

                for (int i = 0; i < holders.Count; i++)

                {

                    var h = holders[i];

                    if (h != null && h.CardModel != null)

                    {

                        if (h.CardModel.Owner != null && !TurnCoordinator.IsP2Target(h.CardModel.Owner.NetId))

                        {

                            ids.Add(h.CardModel.Id.ToString());

                        }

                    }

                }

                ids.Sort();

                return string.Join(",", ids.ToArray());

            }

            catch { return "error"; }

        }



        // Directive #5.35 T2.2: Guard against destructive hand rebinding while P1 is busy playing/selecting cards
        private static FieldInfo _fieldCurrentCardPlay;
        private static FieldInfo _fieldDraggedHolderIndex;
        private static FieldInfo _fieldHoldersAwaitingQueue;
        private static FieldInfo _fieldSelectedCards;

        public static bool IsPlayer1HandBusy(MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand hand)
        {
            if (hand == null) return false;
            try
            {
                if (hand.InCardPlay) return true;
                if (hand.IsInCardSelection) return true;
                // HasDraggedHolder property may be internal; check via reflection + dragged holder index
                var hasDraggedProp = hand.GetType().GetProperty("HasDraggedHolder", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (hasDraggedProp != null)
                {
                    object hd = hasDraggedProp.GetValue(hand, null);
                    if (hd is bool && (bool)hd) return true;
                }

                if (_fieldCurrentCardPlay == null)
                {
                    _fieldCurrentCardPlay = AccessTools.Field(hand.GetType(), "_currentCardPlay");
                }
                if (_fieldCurrentCardPlay != null && _fieldCurrentCardPlay.GetValue(hand) != null)
                {
                    return true;
                }

                if (_fieldDraggedHolderIndex == null)
                {
                    _fieldDraggedHolderIndex = AccessTools.Field(hand.GetType(), "_draggedHolderIndex");
                }
                if (_fieldDraggedHolderIndex != null)
                {
                    object val = _fieldDraggedHolderIndex.GetValue(hand);
                    if (val is int && ((int)val) >= 0) return true;
                }

                if (_fieldHoldersAwaitingQueue == null)
                {
                    _fieldHoldersAwaitingQueue = AccessTools.Field(hand.GetType(), "_holdersAwaitingQueue");
                }
                if (_fieldHoldersAwaitingQueue != null)
                {
                    var awaiting = _fieldHoldersAwaitingQueue.GetValue(hand) as System.Collections.ICollection;
                    if (awaiting != null && awaiting.Count > 0) return true;
                }

                if (_fieldSelectedCards == null)
                {
                    _fieldSelectedCards = AccessTools.Field(hand.GetType(), "_selectedCards");
                }
                if (_fieldSelectedCards != null)
                {
                    var sel = _fieldSelectedCards.GetValue(hand) as System.Collections.ICollection;
                    if (sel != null && sel.Count > 0) return true;
                }

                var modeProp = hand.GetType().GetProperty("CurrentMode");
                if (modeProp != null)
                {
                    object modeVal = modeProp.GetValue(hand, null);
                    if (modeVal != null)
                    {
                        string modeStr = modeVal.ToString();
                        if (modeStr == "SimpleSelect" || modeStr == "UpgradeSelect")
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Coord] IsPlayer1HandBusy exception: " + ex.Message);
            }
            return false;
        }



        public static int GetActionQueueCount(ulong ownerId)

        {

            try

            {

                if (RunManager.Instance != null && RunManager.Instance.ActionQueueSet != null)

                {

                    var aqs = RunManager.Instance.ActionQueueSet;

                    FieldInfo qField = AccessTools.Field(aqs.GetType(), "_actionQueues");

                    if (qField != null)

                    {

                        var qList = qField.GetValue(aqs) as System.Collections.IList;

                        if (qList != null)

                        {

                            foreach (var q in qList)

                            {

                                if (q == null) continue;

                                FieldInfo ownerField = AccessTools.Field(q.GetType(), "ownerId");

                                FieldInfo actionsField = AccessTools.Field(q.GetType(), "actions");

                                if (ownerField != null && actionsField != null)

                                {

                                    ulong qOwner = (ulong)ownerField.GetValue(q);

                                    if (qOwner == ownerId)

                                    {

                                        var acts = actionsField.GetValue(q) as System.Collections.IList;

                                        return acts != null ? acts.Count : 0;

                                    }

                                }

                            }

                        }

                    }

                }

            }

            catch { }

            return 0;

        }



        public static string GetSolverUiToggleStatus()

        {

            try

            {

                var controller = CachedSolverController;

                if (controller != null)

                {

                    bool disabled = false;

                    PropertyInfo disProp = AccessTools.Property(controller.GetType(), "SolverDisabled");

                    if (disProp != null) disabled = (bool)disProp.GetValue(controller, null);



                    bool autoCalc = true;

                    PropertyInfo acProp = AccessTools.Property(controller.GetType(), "AutomaticCalculationEnabled");

                    if (acProp != null) autoCalc = (bool)acProp.GetValue(controller, null);



                    return string.Format("solverEnabled={0},autoCalc={1}", !disabled, autoCalc);

                }

            }

            catch { }

            return "unknown";

        }



        public static string GetSolverSessionStatus()

        {

            try

            {

                var controller = CachedSolverController;

                if (controller != null)

                {

                    FieldInfo combatField = AccessTools.Field(controller.GetType(), "_combat");

                    object combatObj = combatField != null ? combatField.GetValue(controller) : null;

                    return combatObj != null ? "active" : "none";

                }

            }

            catch { }

            return "unknown";

        }



        public static string GetSolverSearchStatus()

        {

            try

            {

                var controller = CachedSolverController;

                if (controller != null)

                {

                    PropertyInfo isSearchingProp = AccessTools.Property(controller.GetType(), "IsSearching");

                    bool isSearching = isSearchingProp != null && (bool)isSearchingProp.GetValue(controller, null);

                    if (isSearching) return "searching";



                    FieldInfo searchField = AccessTools.Field(controller.GetType(), "_search");

                    object searchObj = searchField != null ? searchField.GetValue(controller) : null;

                    if (searchObj != null)

                    {

                        PropertyInfo workerProp = AccessTools.Property(searchObj.GetType(), "WorkerCompletion");

                        Task workerTask = workerProp != null ? workerProp.GetValue(searchObj, null) as Task : null;

                        if (workerTask != null) return workerTask.Status.ToString();

                        return "active";

                    }

                    return "idle";

                }

            }

            catch { }

            return "unknown";

        }



        public static string GetSolverDeploymentStatus()

        {

            try

            {

                var controller = CachedSolverController;

                if (controller != null)

                {

                    PropertyInfo isDeployingProp = AccessTools.Property(controller.GetType(), "IsDeploying");

                    bool isDeploying = isDeployingProp != null && (bool)isDeployingProp.GetValue(controller, null);

                    if (isDeploying) return "deploying";



                    FieldInfo deployField = AccessTools.Field(controller.GetType(), "_deployment");

                    object deployObj = deployField != null ? deployField.GetValue(controller) : null;

                    if (deployObj != null)

                    {

                        PropertyInfo opProp = AccessTools.Property(deployObj.GetType(), "Operation");

                        Task opTask = opProp != null ? opProp.GetValue(deployObj, null) as Task : null;

                        if (opTask != null) return opTask.Status.ToString();

                        return "active";

                    }

                    return "idle";

                }

            }

            catch { }

            return "unknown";

        }

        public static bool P2FallbackActionExecutedThisTurn = false;
        public static bool P2FallbackEndTurnQueuedThisTurn = false;
        public static bool P2FallbackEndTurnBlockUsedThisTurn = false;
        public static bool P2ZeroActionProtectionFinalizedThisTurn = false;
        public static bool P2DeployHookMissLoggedThisTurn = false;
        public static readonly Dictionary<int, int> P2PlayedCardsByTurn = new Dictionary<int, int>();

        public static int LeakStackDumpCount = 0;



        // Directive #3.8: FullAuto guard & autonomous evaluation

        public static int FullAutoDisabledDumpCount = 0;

        public static bool HasAutonomousEvaluatedThisTurn = false;



        // Directive #3.9: Control divergence self-healing guard

        public static DateTime LastDivergenceHealTime = DateTime.MinValue;



        // Directive #5.11 H1: Divergence exemption during P2 pending choices

        public static int P2PendingChoiceCount = 0;
        public static bool ForcedResolveOnLeaveInProgress = false;
        public static DateTime LastForcedResolveOnLeave = DateTime.MinValue;
        public static bool GuardPendingChoicesBeforeLeave(string reason)
        {
            if (ForcedResolveOnLeaveInProgress) return true;
            try { Player p2 = FlowCoordinator.GetPlayer2(); if (p2 == null) return true; bool pending = P2PendingChoiceCount > 0 || FlowCoordinator.HasPendingP2Choice(p2); if (!pending) return true; ForcedResolveOnLeaveInProgress = true; LastForcedResolveOnLeave = DateTime.UtcNow; GD.Print(string.Format("[AiTeammate][Flow] forced-resolve-on-leave begin reason={0} pendingChoice={1}", reason ?? "unknown", P2PendingChoiceCount)); bool resolved = FlowCoordinator.ResolveAllPendingP2Choices("forced-resolve-on-leave:" + (reason ?? "unknown")); if (resolved) P2PendingChoiceCount = 0; GD.Print(string.Format("[AiTeammate][Flow] forced-resolve-on-leave {0} reason={1}", resolved ? "resolved" : "fail-open", reason ?? "unknown")); return true; } catch (Exception ex) { GD.PrintErr("[AiTeammate][Flow] forced-resolve-on-leave exception: " + ex); return true; } finally { ForcedResolveOnLeaveInProgress = false; }
        }

        // Pure decision function used by the independent timer and the offline
        // self-test. Keeping this free of game state makes the fail-open rule auditable.
        public static bool ShouldFireIndependentLifeline(DateTime now, DateTime armedAt, DateTime lastHeartbeat, bool searchWatchArmed, out string reason)
        {
            reason = null;
            if (armedAt != DateTime.MinValue && (now - armedAt).TotalSeconds >= IndependentLifelineTimeoutSeconds)
            {
                reason = "timeout-20s";
                return true;
            }
            if (searchWatchArmed && lastHeartbeat != DateTime.MinValue && (now - lastHeartbeat).TotalSeconds > IndependentSearchHeartbeatTimeoutSeconds)
            {
                reason = "search-heartbeat-missed";
                return true;
            }
            return false;
        }

        public static void ArmIndependentP2Lifeline(string source)
        {
            try
            {
                var cm = CombatManager.Instance;
                var state = cm != null ? cm.DebugOnlyGetState() : null;
                Player p2 = state != null && state.Players != null && state.Players.Count > 1 ? state.Players[state.Players.Count - 1] : null;
                if (p2 == null || p2.Creature == null || !p2.Creature.IsAlive) return;

                lock (LifelineGate)
                {
                    if (IndependentLifelineArmed && IndependentLifelineTurn == TurnNumber &&
                        IndependentLifelinePlayer != null && IndependentLifelinePlayer.NetId == p2.NetId)
                    {
                        return;
                    }

                    IndependentLifelineArmed = true;
                    IndependentLifelineFired = false;
                    IndependentLifelineStartTime = DateTime.UtcNow;
                    IndependentLifelineTurn = TurnNumber;
                    IndependentLifelinePlayer = p2;
                    IndependentLifelineCombatManager = cm;
                    IndependentLifelineState = state;
                    IndependentSearchWatchArmed = false;
                    IndependentLastSearchHeartbeat = DateTime.MinValue;
                    IndependentSearchArmSource = null;

                    if (IndependentLifelineTimer == null)
                    {
                        IndependentLifelineTimer = new System.Threading.Timer(IndependentLifelineTimerTick, null, 250, 250);
                    }
                    else
                    {
                        IndependentLifelineTimer.Change(250, 250);
                    }
                }

                GD.Print(string.Format("[AiTeammate][Coord] lifeline-armed turn={0} owner=P2 timeout=20s source={1}", TurnNumber, source ?? "unknown"));
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Coord] lifeline-arm failed-open: " + ex.Message);
            }
        }

        public static void DisarmIndependentP2Lifeline(string reason)
        {
            System.Threading.Timer timer = null;
            lock (LifelineGate)
            {
                IndependentLifelineArmed = false;
                IndependentLifelineFired = false;
                IndependentSearchWatchArmed = false;
                IndependentSearchArmSource = null;
                IndependentLifelineStartTime = DateTime.MinValue;
                IndependentLastSearchHeartbeat = DateTime.MinValue;
                IndependentLifelinePlayer = null;
                IndependentLifelineCombatManager = null;
                IndependentLifelineState = null;
                timer = IndependentLifelineTimer;
                IndependentLifelineTimer = null;
            }
            if (timer != null)
            {
                try { timer.Dispose(); } catch { }
            }
            if (!string.IsNullOrEmpty(reason)) GD.Print("[AiTeammate][Coord] lifeline-disarmed reason=" + reason);
        }

        public static void ArmIndependentSearchHeartbeat(string source)
        {
            bool newlyArmed = false;
            lock (LifelineGate)
            {
                if (!IndependentLifelineArmed) return;
                if (!IndependentSearchWatchArmed)
                {
                    IndependentSearchWatchArmed = true;
                    IndependentLastSearchHeartbeat = DateTime.UtcNow;
                    IndependentSearchArmSource = source;
                    newlyArmed = true;
                }
            }
            if (newlyArmed) GD.Print("[AiTeammate][Coord] search-watch-armed source=" + (source ?? "unknown"));
        }

        public static void RecordIndependentSearchHeartbeat(DateTime now, string source)
        {
            lock (LifelineGate)
            {
                if (!IndependentLifelineArmed || !IndependentSearchWatchArmed) return;
                IndependentLastSearchHeartbeat = now;
            }
        }

        public static void DisarmIndependentSearchHeartbeat(string reason)
        {
            lock (LifelineGate)
            {
                IndependentSearchWatchArmed = false;
                IndependentLastSearchHeartbeat = DateTime.MinValue;
                IndependentSearchArmSource = null;
            }
        }

        private static void IndependentLifelineTimerTick(object _)
        {
            if (!IsInCombat()) { DisarmIndependentP2Lifeline("timer-tick-not-in-combat"); return; }
            CombatManager cm = null;
            CombatState state = null;
            Player p2 = null;
            DateTime armedAt = DateTime.MinValue;
            DateTime lastHeartbeat = DateTime.MinValue;
            bool searchArmed = false;
            int turn = -1;
            string reason = null;

            try
            {
                DateTime now = DateTime.UtcNow;
                lock (LifelineGate)
                {
                    if (!IndependentLifelineArmed || IndependentLifelineFired) return;
                    armedAt = IndependentLifelineStartTime;
                    lastHeartbeat = IndependentLastSearchHeartbeat;
                    searchArmed = IndependentSearchWatchArmed;
                    if (!ShouldFireIndependentLifeline(now, armedAt, lastHeartbeat, searchArmed, out reason)) return;

                    IndependentLifelineFired = true;
                    cm = IndependentLifelineCombatManager;
                    state = IndependentLifelineState;
                    p2 = IndependentLifelinePlayer;
                    turn = IndependentLifelineTurn;
                }

                double elapsed = armedAt == DateTime.MinValue ? 0.0 : (now - armedAt).TotalSeconds;
                if (reason == "search-heartbeat-missed")
                {
                    GD.PrintErr(string.Format("[AiTeammate][Coord] thread-dead-detected turn={0} elapsed={1:F1}s last-heartbeat={2:O}", turn, elapsed, lastHeartbeat));
                }
                GD.PrintErr(string.Format("[AiTeammate][Coord] CRITICAL-lifeline turn={0} elapsed={1:F1}s reason={2}", turn, elapsed, reason ?? "unknown"));

                // This is intentionally a direct fail-open call from the independent
                // timer. Routing it through OnProcessTick would reintroduce the deadlock.
                // Directive #5.35 T3: System.Threading.Timer runs on ThreadPool; dispatch to Godot main thread via CallDeferred
                var defCm = cm;
                var defState = state;
                var defP2 = p2;
                Callable.From(delegate {
                    if (!IsInCombat()) { DisarmIndependentP2Lifeline("deferred-not-in-combat"); return; }
                    try
                    {
                        ForceEndTurnForPlayer2(defCm, defState, defP2);
                    }
                    catch (Exception exDef)
                    {
                        GD.PrintErr("[AiTeammate][Coord] CRITICAL-lifeline deferred ForceEndTurn exception: " + exDef);
                    }
                }).CallDeferred();
                DisarmIndependentP2Lifeline("fired:" + (reason ?? "unknown"));
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Coord] CRITICAL-lifeline callback exception: " + ex);
                try { DisarmIndependentP2Lifeline("callback-exception"); } catch { }
            }
        }

        public static DateTime LastDivergenceDeferLogTime = DateTime.MinValue;



        public static bool IsP2ActionGatheringChoice(ulong p2NetId)

        {

            try

            {

                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;

                if (rm != null)

                {

                    var aqs = rm.ActionQueueSet;

                    if (aqs != null && aqs.ActionQueueIsPaused(p2NetId))

                    {

                        return true;

                    }

                }

            }

            catch { }

            return false;

        }



        public static bool IsPlayer2(Player p)

        {

            if (p == null) return false;

            ulong p2NetId = GetPlayer2NetId();

            if (p2NetId != 0 && p.NetId == p2NetId) return true;

            return FlowCoordinator.IsPlayer2Owner(p.NetId);

        }



        // Directive #3.9.1: Divergence persistence & empty plan recovery

        public static int ConsecutiveDivergenceTicks = 0;

        public static int TransientIgnoredCount = 0;

        public static DateTime LastActionExecutedTime = DateTime.MinValue;

        public static DateTime LastTransientLogTime = DateTime.MinValue;

        public static bool HasRetriedEmptyPlanThisTurn = false;

        public static bool HasExecutedActionInDeployment = false;



        // Directive #3.9.3: Dual evaluation path tracking (prefix vs gatekeeper)

        public static bool HasEvaluatedThisTurn = false;

        public static string EvaluatedPath = null;



        // Directive #3.9.4: Prefix verification, Gatekeeper deduplication, Hold, Mid-turn replan, Intent telemetry

        public static bool DeploySeenThisTurn = false;

        public static bool HoldActive = false;

        public static DateTime HoldStartTime = DateTime.MinValue;

        public static bool PendingP2EndTurn = false;

        public static bool HasReleasedHoldThisTurn = false;

        public static int MidturnReplanCount = 0;

        public static DateTime LastMidturnReplanTime = DateTime.MinValue;

        public static DateTime DeploymentEndTime = DateTime.MinValue;

        public static Dictionary<uint, int> EnemyTurnStartHp = new Dictionary<uint, int>();

        public static Dictionary<uint, string> EnemyTurnStartIntents = new Dictionary<uint, string>();

        public static string TurnStartIntentsSummary = "intents=[attack×0, non-attack×0]";



        // Directive #4.0: Coordination frozen toggle and sub-switches (sub-switches AND-gated by master switch)

        public static bool RawCoordinationEnabled = false;

        public static bool RawWaitEnabled = false;

        public static bool RawHoldEnabled = false;

        public static bool RawMidturnEnabled = false;

        // Directive #5.25-R2: keep P1 as the only combat foreground owner.
        public static bool FixedP1CombatControl = true;
        public static int P2ActionSenderScopeDepth = 0;
        // Directive #5.38 T2: Native choice failure recovery tracking
        public static bool PendingNativeChoiceContinuation = false;
        public static DateTime NativeChoiceFailureTime = DateTime.MinValue;
        public static bool HasRetriedNativeChoiceRecoveryThisTurn = false;

        public static bool IsPlayPhase()
        {
            try
            {
                var cm = CombatManager.Instance;
                if (cm == null) return false;
                var state = cm.DebugOnlyGetState();
                if (state == null || state.Players == null || state.Players.Count == 0) return false;
                Player p1 = state.Players[0];
                if (p1 == null || p1.PlayerCombatState == null) return false;
                return p1.PlayerCombatState.Phase == MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play;
            }
            catch { return false; }
        }

        public static void CheckNativeChoiceContinuation(string source)
        {
            try
            {
                if (!PendingNativeChoiceContinuation) return;
                if (P2ActionSenderScopeDepth > 0) return;
                if (!IsInCombat()) { PendingNativeChoiceContinuation = false; return; }

                var cm = CombatManager.Instance;
                var state = cm != null ? cm.DebugOnlyGetState() : null;
                Player p2 = FlowCoordinator.GetPlayer2();
                if (p2 == null || state == null) return;

                PendingNativeChoiceContinuation = false;

                bool hasAttack;
                bool hasUsableCard = P2HasUsableCard(p2, out hasAttack);
                int energy = p2.PlayerCombatState != null ? p2.PlayerCombatState.Energy : 0;

                if (!hasUsableCard || energy <= 0)
                {
                    GD.Print("[AiTeammate][Coord] solver-native-choice-recovery: no playable cards remaining, ending P2 turn");
                    EnqueueP2EndTurn(p2);
                    return;
                }

                if (!HasRetriedNativeChoiceRecoveryThisTurn)
                {
                    HasRetriedNativeChoiceRecoveryThisTurn = true;
                    GD.Print("[AiTeammate][Coord] solver-native-choice-recovery: kicking recalculation turn=" + TurnNumber);
                    KickSolverRecalculation(state, "native-choice-recovery");
                }
                else
                {
                    GD.Print("[AiTeammate][Coord] solver-native-choice-recovery: retry already used, running greedy fallback turn=" + TurnNumber);
                    TryFallbackGreedy(p2);
                    EnqueueP2EndTurn(p2);
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Coord] CheckNativeChoiceContinuation error: " + ex);
            }
        }


        public struct P2ActionSenderScope : IDisposable
        {
            private bool _active;
            private bool _hadPrevious;
            private ulong _previous;
            private bool _previousSuppressUi;
            private string _tag;

            public P2ActionSenderScope(string tag)
            {
                _tag = tag ?? "p2-scope";
                ulong p2Id = GetPlayer2NetId();
                if (FixedP1CombatControl && p2Id != 0)
                {
                    _active = true;
                    _hadPrevious = LocalContext.NetId.HasValue;
                    _previous = LocalContext.NetId.HasValue ? LocalContext.NetId.Value : 0;
                    _previousSuppressUi = ModEntry.SuppressControlSwitchUi;
                    P2ActionSenderScopeDepth++;
                    ModEntry.SuppressControlSwitchUi = true;
                    LocalContext.NetId = p2Id;
                    GD.Print("[AiTeammate][Coord] p2-action-sender-scope begin owner=P2 tag=" + _tag);
                }
                else
                {
                    _active = false;
                    _hadPrevious = false;
                    _previous = 0;
                    _previousSuppressUi = false;
                }
            }

            public void Dispose()
            {
                if (_active)
                {
                    _active = false;
                    if (_hadPrevious) LocalContext.NetId = _previous;
                    else LocalContext.NetId = null;
                    ModEntry.SuppressControlSwitchUi = _previousSuppressUi;
                    if (P2ActionSenderScopeDepth > 0) P2ActionSenderScopeDepth--;
                    MarkP1RestoreAfterP2Scope("scope=" + _tag);
                    if (P2ActionSenderScopeDepth == 0) QueueP1RestoreAfterP2Scope();
                    GD.Print("[AiTeammate][Coord] p2-action-sender-scope end owner=P2 tag=" + _tag);
                }
            }
        }

        public static P2ActionSenderScope EnterP2ActionSenderScope(string tag)
        {
            return new P2ActionSenderScope(tag);
        }
        // #5.25-R3: P2 actions may finish while DRA still has a sender scope
        // active.  Coalesce the P1 restore until the outermost scope is gone.
        private static readonly object P1RestoreGate = new object();
        private static bool P1RestorePendingAfterP2Scope = false;
        private static bool P1RestoreDeferredQueued = false;
        private static string P1RestorePendingSource = null;
        public static ulong CurrentExecutingActionOwnerNetId = 0;

        public static void MarkP1RestoreAfterP2Scope(string source)
        {
            lock (P1RestoreGate)
            {
                P1RestorePendingAfterP2Scope = true;
                P1RestorePendingSource = source ?? "p2-scope";
            }
            if (P2ActionSenderScopeDepth == 0) QueueP1RestoreAfterP2Scope();
        }

        public static void QueueP1RestoreAfterP2Scope()
        {
            lock (P1RestoreGate)
            {
                if (!P1RestorePendingAfterP2Scope || P1RestoreDeferredQueued || P2ActionSenderScopeDepth > 0) return;
                P1RestoreDeferredQueued = true;
            }

            try
            {
                Callable.From(delegate { ExecuteP1RestoreAfterP2Scope(); }).CallDeferred();
            }
            catch (Exception ex)
            {
                lock (P1RestoreGate) { P1RestoreDeferredQueued = false; }
                GD.PrintErr("[AiTeammate][Coord] p1-restore-defer failed: " + ex.Message);
            }
        }

        private static void ExecuteP1RestoreAfterP2Scope()
        {
            string source;
            lock (P1RestoreGate)
            {
                P1RestoreDeferredQueued = false;
                if (P2ActionSenderScopeDepth > 0)
                {
                    return;
                }
                if (!P1RestorePendingAfterP2Scope) return;
                P1RestorePendingAfterP2Scope = false;
                source = P1RestorePendingSource ?? "p2-scope";
                P1RestorePendingSource = null;
            }

            GD.Print("[AiTeammate][Coord] p1-restore-after-p2-scope begin source=" + source + " scopeDepth=" + P2ActionSenderScopeDepth);
            bool previousSuppressUi = ModEntry.SuppressControlSwitchUi;
            try
            {
                ModEntry.SuppressControlSwitchUi = false;
                TurnFlowPatches.RestorePlayer1ControlIfActive("wakuu-p1-restore-after-p2-scope:" + source, true);
            }
            finally
            {
                ModEntry.SuppressControlSwitchUi = previousSuppressUi;
            }
        }

        public static bool IsP1RecoverySource(string source)
        {
            return !string.IsNullOrEmpty(source) &&
                (source.StartsWith("wakuu-p1-restore", StringComparison.OrdinalIgnoreCase) ||
                 source.StartsWith("wakuu-p2-scope-restore", StringComparison.OrdinalIgnoreCase));
        }

        public static void ResetP1RestoreState()
        {
            lock (P1RestoreGate)
            {
                P1RestorePendingAfterP2Scope = false;
                P1RestoreDeferredQueued = false;
                P1RestorePendingSource = null;
            }
        }

        public static ulong ResolveEndedTurnOwner()
        {
            try
            {
                if (CurrentExecutingActionOwnerNetId != 0) return CurrentExecutingActionOwnerNetId;
                ulong p2 = GetPlayer2NetId();
                if (P2ActionSenderScopeDepth > 0) return p2;
                if (LocalContext.NetId.HasValue && LocalContext.NetId.Value != 0) return LocalContext.NetId.Value;
                ulong dra = TurnFlowPatches.GetDraCurrentControlledPlayerId();
                return dra;
            }
            catch { return 0; }
        }

        public static bool IsFixedP1CombatControlActive()
        {
            try
            {
                return FixedP1CombatControl && IsInCombat() && !IsPlayer1Dead();
            }
            catch { return false; }
        }

        public static bool IsFullRunP1ControlLockActive()
        {
            try
            {
                if (!FixedP1CombatControl) return false;
                ulong p1 = GetPlayer1NetId();
                ulong p2 = GetPlayer2NetId();
                if (p1 == 0 || p2 == 0) return false;
                return !IsInCombat() || !IsPlayer1Dead();
            }
            catch { return false; }
        }

        public static bool IsP2Target(ulong playerId)
        {
            ulong p2 = GetPlayer2NetId();
            return p2 != 0 && playerId == p2;
        }

        public static void LogFixedP1StateCheck(string source, ulong targetId = 0)
        {
            try
            {
                ulong p1 = GetPlayer1NetId();
                ulong current = TurnFlowPatches.GetDraCurrentControlledPlayerId();
                GD.Print(string.Format("[AiTeammate][Coord] fixed-p1-state-check source={0} active={1} p1={2} current={3} target={4} local={5}",
                    source ?? "unknown", IsFixedP1CombatControlActive(), p1, current, targetId,
                    LocalContext.NetId.HasValue ? LocalContext.NetId.Value.ToString() : "none"));
            }
            catch { }
        }



        public static bool CoordinationEnabled

        {

            get { return RawCoordinationEnabled; }

            set { RawCoordinationEnabled = value; }

        }



        public static bool WaitEnabled

        {

            get { return RawCoordinationEnabled && RawWaitEnabled; }

            set { RawWaitEnabled = value; }

        }



        public static bool HoldEnabled

        {

            get { return RawCoordinationEnabled && RawHoldEnabled; }

            set { RawHoldEnabled = value; }

        }



        public static bool MidturnEnabled

        {

            get { return RawCoordinationEnabled && RawMidturnEnabled; }

            set { RawMidturnEnabled = value; }

        }



        public static ulong GetPlayer1NetId()

        {

            try

            {
                var cm = CombatManager.Instance;

                if (cm != null)

                {

                    var state = cm.DebugOnlyGetState();

                    if (state != null && state.Players != null && state.Players.Count > 0)

                    {

                        var p1 = state.Players[0];

                        if (p1 != null) return p1.NetId;

                    }

                }

            }

            catch { }

            return 0;

        }



        public static ulong GetPlayer2NetId()

        {

            try

            {

                var cm = CombatManager.Instance;

                if (cm != null)

                {

                    var state = cm.DebugOnlyGetState();

                    if (state != null && state.Players != null && state.Players.Count > 1)

                    {

                        var p2 = state.Players[state.Players.Count - 1];

                        if (p2 != null) return p2.NetId;

                    }

                }

            }

            catch { }

            return 0;

        }



        public static bool IsInCombat()

        {

            try

            {

                var cm = CombatManager.Instance;

                if (cm == null) return false;

                var state = cm.DebugOnlyGetState();

                if (state == null) return false;

                if (state.Players == null || state.Players.Count < 1) return false;

                return true;

            }

            catch

            {

                return false;

            }

        }

        public static bool IsInEventRoom()
        {
            try
            {
                if (MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom.Instance != null && Godot.GodotObject.IsInstanceValid(MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom.Instance))
                {
                    return true;
                }
                object state = RunManager.Instance != null ? RunManager.Instance.DebugOnlyGetState() : null;
                if (state != null)
                {
                    PropertyInfo cp = AccessTools.Property(state.GetType(), "CurrentRoom");
                    object room = cp != null ? cp.GetValue(state, null) : null;
                    if (room != null)
                    {
                        string rName = room.GetType().Name;
                        if (rName.IndexOf("Event", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static bool IsAutoEventSource(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            if (IsInEventRoom())
            {
                if (string.Equals(source, "rewards-offer", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(source, "merged-rewards-offer", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(source, "merged-rewards-offer-from-rewardsset", StringComparison.OrdinalIgnoreCase) ||
                    source.StartsWith("rewards-offer", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return string.Equals(source, "event-auto-next", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(source, "event-proceed-next-player", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(source, "event-finished-next-player", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(source, "event-shared-serial-execution", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(source, "wakuu-subpage-manual", StringComparison.OrdinalIgnoreCase) ||
                   source.StartsWith("event-auto", StringComparison.OrdinalIgnoreCase) ||
                   source.StartsWith("event-proceed", StringComparison.OrdinalIgnoreCase) ||
                   source.StartsWith("event-finished", StringComparison.OrdinalIgnoreCase);
        }




        private static readonly string[] DebuffCardWhitelist = new string[]

        {

            "Bash", "Uppercut", "Shockwave", "Clothesline",

            "Neutralize", "SuckerPunch", "CripplingCloud", "CripplingPoison", "LegSweep", "Terror",

            "Trip", "Blind", "BeamCell", "GoForTheEyes", "CrushJoints", "CrushUnder",

            "BASH", "UPPERCUT", "SHOCKWAVE", "CLOTHESLINE",

            "NEUTRALIZE", "SUCKER_PUNCH", "CRIPPLING_CLOUD", "LEG_SWEEP", "TERROR",

            "TRIP", "BLIND", "BEAM_CELL", "GO_FOR_THE_EYES", "CRUSH_JOINTS"

        };



        public static bool IsInWhitelist(string name)

        {

            if (string.IsNullOrEmpty(name)) return false;

            string clean = name;

            if (clean.StartsWith("CARD.", StringComparison.OrdinalIgnoreCase))

            {

                clean = clean.Substring(5);

            }

            clean = clean.Replace("_", "");



            for (int i = 0; i < DebuffCardWhitelist.Length; i++)

            {

                string item = DebuffCardWhitelist[i];

                string cleanItem = item.Replace("_", "");

                if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase) ||

                    string.Equals(cleanItem, clean, StringComparison.OrdinalIgnoreCase) ||

                    clean.IndexOf(cleanItem, StringComparison.OrdinalIgnoreCase) >= 0)

                    return true;

            }

            return false;

        }



        public static bool IsPlayer1Dead()

        {

            try

            {

                var cm = CombatManager.Instance;

                if (cm == null) return false;

                var state = cm.DebugOnlyGetState();

                if (state == null || state.Players == null || state.Players.Count == 0) return false;

                var p1 = state.Players[0];

                return p1 == null || p1.Creature == null || !p1.Creature.IsAlive;

            }

            catch

            {

                return false;

            }

        }



        public static void OnRoomEnterBegin(string reason)

        {

            try

            {

                IsEnteringRoom = true;
                FlowCoordinator.ResetInFlightEventState();

                RoomEnteringStartedTime = DateTime.UtcNow;

                GD.Print("[AiTeammate][Coord] room-enter-window-begin: reason=" + reason);

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] OnRoomEnterBegin error: " + ex);

            }

        }



        public static void OnRoomSettled(string reason)

        {

            try

            {

                if (IsEnteringRoom)

                {

                    IsEnteringRoom = false;

                    RoomSettleTime = DateTime.UtcNow;

                    GD.Print("[AiTeammate][Coord] room-enter-window-settled: reason=" + reason);

                    DrainDeferredModSwitches();



                    // Directive #5.31: Event Foreground Assertion on Room Settle
                    if (IsInEventRoom() || NEventRoom.Instance != null || (reason != null && reason.IndexOf("Event", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                        ulong actual = TurnFlowPatches.GetDraCurrentControlledPlayerId();
                        string actualStr = (p1Id != 0 && actual == p1Id) ? "P1" : actual.ToString();
                        GD.Print(string.Format("[AiTeammate][Event] event-room-enter expected=P1 actual={0}", actualStr));
                        GD.Print(string.Format("[AiTeammate][Event] control-context-assert room=event expected=P1 actual={0}", actual));
                        if (p1Id != 0 && actual != p1Id)
                        {
                            TurnFlowPatches.SwitchControlToPlayer(p1Id, "wakuu-p1-restore:event-room-settled", false);
                            GD.Print(string.Format("[AiTeammate][Event] event-auto-next-p1-restore target={0}", p1Id));
                        }
                        else
                        {
                            GD.Print(string.Format("[AiTeammate][Event] event-entry-p1 confirmed-room-settled target={0}", p1Id));
                        }
                    }

                    // Directive #5.31: Merchant Room Settled Hook
                    if (MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom.Instance != null || (reason != null && reason.IndexOf("Merchant", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        FlowCoordinator.OnMerchantRoomEnteredOrSettled("room-settle:" + (reason ?? "unknown"));
                    }

                    // Directive #5.14 T2: Trigger event resolution after event room settles

                    if (RunManager.Instance != null && RunManager.Instance.EventSynchronizer != null)

                    {

                        Player p2 = FlowCoordinator.GetPlayer2();

                        if (p2 != null)

                        {

                            FlowCoordinator.ResolveP2PendingEvent(RunManager.Instance.EventSynchronizer, p2);

                        }

                    }
                    FlowCoordinator.ResolvePendingP2RestSiteOnSettle("room-settle:" + (reason ?? "unknown"));

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] OnRoomSettled error: " + ex);

            }

        }



        public static void QueueDeferredModSwitch(ulong targetNetId, string source, bool suppressUi)

        {

            lock (DeferredModControlSwitches)

            {

                DeferredModControlSwitches.Add(new DeferredSwitchRequest

                {

                    TargetNetId = targetNetId,

                    Source = source,

                    SuppressUi = suppressUi

                });

            }

        }



        public static void DrainDeferredModSwitches()

        {

            List<DeferredSwitchRequest> toExecute = null;

            lock (DeferredModControlSwitches)

            {

                if (DeferredModControlSwitches.Count > 0)

                {

                    toExecute = new List<DeferredSwitchRequest>(DeferredModControlSwitches);

                    DeferredModControlSwitches.Clear();

                }

            }



            if (toExecute != null && toExecute.Count > 0)

            {

                foreach (var req in toExecute)

                {

                    GD.Print(string.Format("[AiTeammate][Coord] releasing deferred mod control switch: target={0} source={1}", req.TargetNetId, req.Source));

                    TurnFlowPatches.SwitchControlToPlayer(req.TargetNetId, req.Source, req.SuppressUi);

                }

            }

        }



        public static void ResetTurnState(string source)

        {

            IsP1ActionExecuting = false;
            PendingNativeChoiceContinuation = false;
            NativeChoiceFailureTime = DateTime.MinValue;
            HasRetriedNativeChoiceRecoveryThisTurn = false;

            P1ActionExecutingInstance = null;

            IsWaiting = false;

            IsDeploying = false;

            HasReleasedThisTurn = false;

            LastReleaseTrigger = null;

            CheapestP1DebuffCost = int.MaxValue;

            WaitStartTime = DateTime.MinValue;

            MidDeployStartTime = DateTime.MinValue;

            MidDeployHangTimeoutTriggered = false;

            MidDeployHangCriticalTriggered = false;

            SearchAliveStartTime = DateTime.MinValue;

            LastSearchHeartbeatTime = DateTime.MinValue;

            ReleaseRequestedTime = DateTime.MinValue;

            DeployStartTime = DateTime.MinValue;

            Watchdog3sFired = false;

            RecalcSucceeded = false;

            CachedDeployArgs = null;

            CachedDeployResult = null;

            CurrentRecalcGeneration = 0;

            HasSelfHealedHandThisTurn = false;

            ConsecutiveDivergenceTicks = 0;

            TransientIgnoredCount = 0;

            HasRetriedEmptyPlanThisTurn = false;

            HasExecutedActionInDeployment = false;
            // #5.26: P2 deployment telemetry is reset only when a new P2
            // turn baseline is captured, after the prior turn was finalized.

            HasEvaluatedThisTurn = false;

            EvaluatedPath = null;

            DeploySeenThisTurn = false;

            HoldActive = false;

            PendingP2EndTurn = false;

            HasReleasedHoldThisTurn = false;

            MidturnReplanCount = 0;

            LastMidturnReplanTime = DateTime.MinValue;



            if (source == "SetupPlayerTurn" || source == "CombatStart" || source == "RoundStart" || (source.IndexOf("Turn", StringComparison.OrdinalIgnoreCase) >= 0 && source != "OnEndedTurnLocally"))

            {
                P2ZeroActionProtectionFinalizedThisTurn = false;
                P2SummaryFinalizedThisTurn = false;
                P2FallbackGreedyUsedThisTurn = false;
                P2FallbackActionPendingThisTurn = false;
                P2FallbackActionEnqueuedThisTurn = false;
                P2FallbackActionExecutedThisTurn = false;
                P2FallbackEndTurnQueuedThisTurn = false;
                P2TelemetryTurn = -1;

                // R2-4: any P2 choice overlay left over from the previous turn must be resolved or

                // dropped before the new turn starts, so P1 clicks always stay responsive.

                FlowCoordinator.ResolveOrClearP2ChoiceOverlays("turn-switch:" + source);



                EnemyTurnStartHp.Clear();

                EnemyTurnStartIntents.Clear();

                TurnStartIntentsSummary = "intents=[attack×0, non-attack×0]";

                TurnStartTime = DateTime.UtcNow;

                HasEnteredDeployPrefixThisTurn = false;

                StallRecalcTriggered = false;

                StallRecalcTime = DateTime.MinValue;

                StallForceEndTurnTriggered = false;

                HasCompletedDeploymentThisTurn = false;

                RecalcRequestedThisTurn = false;

                HasAutonomousEvaluatedThisTurn = false;

                LastDivergenceHealTime = DateTime.MinValue;

                LastActionExecutedTime = DateTime.MinValue;

                LastTransientLogTime = DateTime.MinValue;



                // Directive #3.6.5: Record turn number and P2 turn start energy baseline

                try

                {

                    var cm = CombatManager.Instance;

                    var state = cm != null ? cm.DebugOnlyGetState() : null;

                    if (state != null)

                    {

                        TurnNumber = state.RoundNumber;

                        if (TurnNumber == 1)

                        {

                            LeakStackDumpCount = 0;

                            SwitchSourceAuditor.ResetForCombat();

                        }

                        if (state.Players != null && state.Players.Count >= 2)

                        {

                            var p2 = state.Players[state.Players.Count - 1];

                            if (p2 != null && p2.PlayerCombatState != null)

                            {

                                P2TurnStartEnergy = p2.PlayerCombatState.Energy;

                                P2TurnStartHandCount = p2.PlayerCombatState.Hand != null && p2.PlayerCombatState.Hand.Cards != null

                                    ? p2.PlayerCombatState.Hand.Cards.Count : 0;

                            }

                        }

                    }

                }

                catch { }

            }



            IsEnqueuingFallbackBatch = false;

            if (source == "CompleteDeployment" || source == "OnEndedTurnLocally" || source == "StallWatchdog")

            {

                HasCompletedDeploymentThisTurn = true;

            }

            if (source == "SetupPlayerTurn" || source == "CombatStart" || source == "RoundStart" || source == "SetUpCombat" || source == "OnEndedTurnLocally")
            {
                if (P2ActionSenderScopeDepth > 0)
                {
                    GD.Print(string.Format("[AiTeammate][Coord] p2-scope-self-heal source={0} leaked_depth={1}", source, P2ActionSenderScopeDepth));
                    AttributionPatches.ClearAllP2SenderScopes();
                    P2ActionSenderScopeDepth = 0;
                    ulong p1 = GetPlayer1NetId();
                    if (p1 != 0) LocalContext.NetId = p1;
                    TurnFlowPatches.RestorePlayer1ControlIfActive("p2-scope-self-heal", false);
                }
            }

            GD.Print("[AiTeammate][Coord] Coordinator state reset via " + source);

        }

        public static void ResetState(string source)
        {
            ResetTurnState(source);
            bool isNewTurnOrCombat = (source == "SetupPlayerTurn" || source == "SetUpCombat" || source == "CombatStart" || source == "RoundStart" || source == "OnEndedTurnLocally");
            bool noP2ActionExecuting = !IsPlayer2ActionInFlight(false);
            if (P2ActionSenderScopeDepth > 0 && (isNewTurnOrCombat || noP2ActionExecuting))
            {
                GD.Print(string.Format("[AiTeammate][Coord] p2-scope-self-heal source={0} leaked_depth={1}", source, P2ActionSenderScopeDepth));
                AttributionPatches.ClearAllP2SenderScopes();
                P2ActionSenderScopeDepth = 0;
                ulong p1 = GetPlayer1NetId();
                if (p1 != 0) LocalContext.NetId = p1;
                TurnFlowPatches.RestorePlayer1ControlIfActive("p2-scope-self-heal", false);
            }
        }



        public static bool IsAttack(CardModel card)

        {

            if (card == null) return false;

            try

            {

                return card.Type == CardType.Attack;

            }

            catch

            {

                return false;

            }

        }



        // Directive #3.9.5: Expanded Team Setup Predicates & Harmonized Buff/Debuff System

        public static bool CardIsTeamSetup(CardModel card, out string setupType)

        {

            setupType = "";

            if (card == null) return false;



            try

            {

                TargetType target = TargetType.None;

                try

                {

                    target = card.TargetType;

                }

                catch (Exception exTarget)

                {

                    GD.PrintErr("[AiTeammate][Coord] CardIsTeamSetup target lookup error: " + exTarget.Message);

                }



                // Rule A: Helpful buffs to teammate/allies

                // (Pure self-only buffs like Inflame, Limit Break, Demon Form, Metallicize do NOT count!)

                if (target == TargetType.AnyAlly || target == TargetType.AllAllies || target == TargetType.AnyPlayer)

                {

                    setupType = "buff-ally";

                    return true;

                }

                if (IsAllyBuffWhitelist(card))

                {

                    setupType = "buff-ally";

                    return true;

                }



                // Rule B: Harmful status to enemies (Vulnerable, Weak, Frail, Poison, Strength Down, etc.)

                // Must not target Self or Allies!

                if (target != TargetType.Self && target != TargetType.AnyAlly && target != TargetType.AllAllies)

                {

                    if (CardDebuffsEnemy(card))

                    {

                        setupType = "debuff-enemy";

                        return true;

                    }

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] CardIsTeamSetup error: " + ex.Message);

            }



            return false;

        }



        public static bool CardDebuffsEnemy(CardModel card)

        {

            if (card == null) return false;



            // 1. Check DynamicVars (canonical & runtime)

            try

            {

                var dvars = card.DynamicVars;

                if (dvars != null)

                {

                    if (dvars.Vulnerable != null && dvars.Vulnerable.BaseValue > 0) return true;

                    if (dvars.Weak != null && dvars.Weak.BaseValue > 0) return true;

                    if (dvars.ContainsKey("Vulnerable") || dvars.ContainsKey("Weak")) return true;



                    foreach (KeyValuePair<string, MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar> kvp in dvars)

                    {

                        string vname = kvp.Key;

                        if (!string.IsNullOrEmpty(vname) &&

                            (vname.IndexOf("Vulnerable", StringComparison.OrdinalIgnoreCase) >= 0 ||

                             vname.IndexOf("Weak", StringComparison.OrdinalIgnoreCase) >= 0 ||

                             vname.IndexOf("Frail", StringComparison.OrdinalIgnoreCase) >= 0 ||

                             vname.IndexOf("Poison", StringComparison.OrdinalIgnoreCase) >= 0 ||

                             vname.IndexOf("StrengthDown", StringComparison.OrdinalIgnoreCase) >= 0 ||

                             vname.IndexOf("Shackled", StringComparison.OrdinalIgnoreCase) >= 0 ||

                             vname.IndexOf("Disarm", StringComparison.OrdinalIgnoreCase) >= 0 ||

                             vname.IndexOf("Constricted", StringComparison.OrdinalIgnoreCase) >= 0 ||

                             vname.IndexOf("CorpseExplosion", StringComparison.OrdinalIgnoreCase) >= 0 ||

                             vname.IndexOf("Doom", StringComparison.OrdinalIgnoreCase) >= 0))

                        {

                            return true;

                        }

                        var v = kvp.Value;

                        if (v != null)

                        {

                            var vType = v.GetType();

                            string tname = vType.FullName ?? vType.Name;

                            if (tname.IndexOf("Vulnerable", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("Weak", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("Frail", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("Poison", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("StrengthDown", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("Shackled", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("Disarm", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("Constricted", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("CorpseExplosion", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("Doom", StringComparison.OrdinalIgnoreCase) >= 0)

                            {

                                return true;

                            }

                        }

                    }

                }

            }

            catch { }



            // 2. CanonicalVars reflection fallback

            try

            {

                var canProp = card.GetType().GetProperty("CanonicalVars", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (canProp != null)

                {

                    var vars = canProp.GetValue(card) as IEnumerable;

                    if (vars != null)

                    {

                        foreach (var v in vars)

                        {

                            if (v == null) continue;

                            var vType = v.GetType();

                            string tname = vType.FullName ?? vType.Name;

                            if (tname.IndexOf("Vulnerable", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("Weak", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("Frail", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("Poison", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("StrengthDown", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("Shackled", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("Disarm", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("Constricted", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("CorpseExplosion", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                tname.IndexOf("Doom", StringComparison.OrdinalIgnoreCase) >= 0)

                            {

                                return true;

                            }

                            var nameProp = vType.GetProperty("Name");

                            if (nameProp != null)

                            {

                                var nameVal = nameProp.GetValue(v) as string;

                                if (!string.IsNullOrEmpty(nameVal) &&

                                    (nameVal.IndexOf("Vulnerable", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                     nameVal.IndexOf("Weak", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                     nameVal.IndexOf("Frail", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                     nameVal.IndexOf("Poison", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                     nameVal.IndexOf("StrengthDown", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                     nameVal.IndexOf("Shackled", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                     nameVal.IndexOf("Disarm", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                     nameVal.IndexOf("Constricted", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                     nameVal.IndexOf("CorpseExplosion", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                     nameVal.IndexOf("Doom", StringComparison.OrdinalIgnoreCase) >= 0))

                                {

                                    return true;

                                }

                            }

                        }

                    }

                }

            }

            catch { }



            // 3. Known-card ID whitelist fallback with explicit log marking

            string id = card.Id.ToString();

            string typeName = card.GetType().Name;

            if (IsInWhitelist(id) || IsInWhitelist(typeName) || IsEnemyDebuffWhitelist(id) || IsEnemyDebuffWhitelist(typeName))

            {

                GD.Print("[AiTeammate][Coord] Whitelist matched for debuff card: " + id);

                return true;

            }



            return false;

        }



        public static bool AppliesEnemyDebuff(CardModel card)

        {

            return CardDebuffsEnemy(card);

        }



        private static readonly string[] EnemyDebuffWhitelist = new string[]

        {

            "Bash", "Uppercut", "Shockwave", "Clothesline", "Disarm", "Intimidate", "Lariat",

            "Neutralize", "SuckerPunch", "LegSweep", "CripplingCloud", "BouncingFlask", "NoxiousFumes",

            "DeadlyPoison", "PoisonedStab", "Catalyst", "CorpseExplosion", "Malaise", "Terror",

            "BeamCell", "GoForTheEyes", "Trip", "Blind", "WaveOfTheHand", "TalkToTheHand",

            "CrushJoints", "SashWhip", "BowlingForBoulders", "Judgement", "Tantrum", "FearNoEvil"

        };



        public static bool IsEnemyDebuffWhitelist(string name)

        {

            if (string.IsNullOrEmpty(name)) return false;

            string clean = name;

            if (clean.StartsWith("CARD.", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(5);

            clean = clean.Replace("_", "");

            for (int i = 0; i < EnemyDebuffWhitelist.Length; i++)

            {

                string item = EnemyDebuffWhitelist[i];

                string cleanItem = item.Replace("_", "");

                if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase) ||

                    string.Equals(cleanItem, clean, StringComparison.OrdinalIgnoreCase) ||

                    clean.IndexOf(cleanItem, StringComparison.OrdinalIgnoreCase) >= 0)

                    return true;

            }

            return false;

        }



        private static readonly string[] AllyBuffWhitelist = new string[]

        {

            "BelieveInYou", "TagTeam", "Rally", "Cover", "Cheer", "Teamwork",

            "ProtectTeammate", "ShareKnowledge", "BattleHymn", "Warcry",

            "BELIEVE_IN_YOU", "TAG_TEAM", "RALLY", "COVER", "CHEER", "TEAMWORK",

            "PROTECT_TEAMMATE", "SHARE_KNOWLEDGE"

        };



        public static bool IsAllyBuffWhitelist(CardModel card)

        {

            if (card == null) return false;

            string id = card.Id.ToString();

            string typeName = card.GetType().Name;

            return IsAllyBuffWhitelistName(id) || IsAllyBuffWhitelistName(typeName);

        }



        public static bool IsAllyBuffWhitelistName(string name)

        {

            if (string.IsNullOrEmpty(name)) return false;

            string clean = name;

            if (clean.StartsWith("CARD.", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(5);

            clean = clean.Replace("_", "");

            for (int i = 0; i < AllyBuffWhitelist.Length; i++)

            {

                string item = AllyBuffWhitelist[i];

                string cleanItem = item.Replace("_", "");

                if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase) ||

                    string.Equals(cleanItem, clean, StringComparison.OrdinalIgnoreCase) ||

                    clean.IndexOf(cleanItem, StringComparison.OrdinalIgnoreCase) >= 0)

                    return true;

            }

            return false;

        }



        private static readonly string[] AllyEnergyCardWhitelist = new string[]

        {

            "BelieveInYou", "TagTeam", "Rally", "Cover", "Cheer", "Teamwork",

            "BELIEVE_IN_YOU", "TAG_TEAM", "RALLY", "COVER", "CHEER", "TEAMWORK"

        };



        public static bool IsAllyEnergyWhitelist(string name)

        {

            if (string.IsNullOrEmpty(name)) return false;

            string clean = name;

            if (clean.StartsWith("CARD.", StringComparison.OrdinalIgnoreCase))

            {

                clean = clean.Substring(5);

            }

            clean = clean.Replace("_", "");



            for (int i = 0; i < AllyEnergyCardWhitelist.Length; i++)

            {

                string item = AllyEnergyCardWhitelist[i];

                string cleanItem = item.Replace("_", "");

                if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase) ||

                    string.Equals(cleanItem, clean, StringComparison.OrdinalIgnoreCase) ||

                    clean.IndexOf(cleanItem, StringComparison.OrdinalIgnoreCase) >= 0)

                    return true;

            }

            return false;

        }



        public static bool CardGrantsAllyEnergy(CardModel card)

        {

            if (card == null) return false;



            try

            {

                TargetType target;

                try

                {

                    target = card.TargetType;

                }

                catch (Exception exTarget)

                {

                    GD.PrintErr("[AiTeammate][Coord] CardGrantsAllyEnergy target lookup error (unknown-target): " + exTarget.Message);

                    return false;

                }



                // Rule 1: Exclude enemy targets and self-only targets!

                if (target == TargetType.AnyEnemy || 

                    target == TargetType.AllEnemies || 

                    target == TargetType.RandomEnemy || 

                    target == TargetType.Self)

                {

                    return false;

                }



                string id = card.Id.ToString();

                string typeName = card.GetType().Name;



                // Rule 2: Whitelist check for known ally energy/support cards

                if (IsAllyEnergyWhitelist(id) || IsAllyEnergyWhitelist(typeName))

                {

                    return true;

                }



                // Rule 3: Must target ally or player

                bool isAllyTarget = (target == TargetType.AnyAlly || target == TargetType.AllAllies || target == TargetType.AnyPlayer);



                if (isAllyTarget)

                {

                    var dvars = card.DynamicVars;

                    if (dvars != null)

                    {

                        foreach (KeyValuePair<string, MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar> kvp in dvars)

                        {

                            string vname = kvp.Key;

                            if (!string.IsNullOrEmpty(vname) &&

                                (vname.IndexOf("Energy", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                 vname.IndexOf("Draw", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                 vname.IndexOf("Card", StringComparison.OrdinalIgnoreCase) >= 0))

                            {

                                return true;

                            }

                        }

                    }

                    return true;

                }



                // If target is None or TargetedNoCreature, not an ally energy card

                if (target == TargetType.None || target == TargetType.TargetedNoCreature || target == TargetType.Osty)

                {

                    return false;

                }



                GD.Print("[AiTeammate][Coord] CardGrantsAllyEnergy: unknown-target for " + id + " (target=" + target + ")");

                return false;

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] CardGrantsAllyEnergy error (unknown-target): " + ex.Message);

                return false;

            }

        }



        public static int GetCardEnergyCost(CardModel card)

        {

            if (card == null) return 0;

            try

            {

                var costObj = card.EnergyCost;

                if (costObj != null)

                {

                    if (costObj.CostsX) return 0;

                    return costObj.GetAmountToSpend();

                }

            }

            catch { }

            try

            {

                var prop = typeof(CardModel).GetProperty("CanonicalEnergyCost", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (prop != null)

                {

                    var costObj = prop.GetValue(card) as CardEnergyCost;

                    if (costObj != null)

                    {

                        if (costObj.CostsX) return 0;

                        return costObj.GetAmountToSpend();

                    }

                }

            }

            catch { }

            return 0;

        }



        public static bool CheckIsPlayable(CardModel card)
        {
            if (card == null) return false;

            // First: immediately reject Curse, Status, Quest, and Ascender's Bane
            if (card.Type == CardType.Curse || card.Type == CardType.Status || card.Type == CardType.Quest)
            {
                return false;
            }
            string cid = card.Id != null ? card.Id.ToString() : "";
            if (cid.IndexOf("ASCENDERS_BANE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            // Second: check if Keywords contains "Unplayable"
            try
            {
                var kwProp = typeof(CardModel).GetProperty("Keywords", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var keywords = kwProp != null ? kwProp.GetValue(card, null) as System.Collections.IEnumerable : null;
                if (keywords != null)
                {
                    foreach (var kw in keywords)
                    {
                        if (kw != null && kw.ToString().IndexOf("Unplayable", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return false;
                        }
                    }
                }
            }
            catch { }

            // Third: check CanPlay on card
            try
            {
                var canPlayMethod = typeof(CardModel).GetMethod("CanPlay", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (canPlayMethod != null)
                {
                    object res = canPlayMethod.Invoke(card, null);
                    if (res is bool && !(bool)res)
                    {
                        return false;
                    }
                }
            }
            catch { }

            try
            {
                foreach (var m in typeof(CardModel).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name == "CanPlay" && m.GetParameters().Length == 1)
                    {
                        object[] args = new object[] { null };
                        object res = m.Invoke(card, args);
                        if (res is bool && !(bool)res)
                        {
                            return false;
                        }
                    }
                }
            }
            catch { }

            // Fourth: check IsPlayable property as final filter
            try
            {
                var prop = typeof(CardModel).GetProperty("IsPlayable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null)
                {
                    object val = prop.GetValue(card, null);
                    if (val is bool && !(bool)val)
                    {
                        return false;
                    }
                }
            }
            catch { }

            return true;
        }

        public static bool IsAffordable(CardModel card, Player player)

        {

            if (card == null || player == null) return false;

            try

            {

                if (!CheckIsPlayable(card)) return false;

                var pState = player.PlayerCombatState;

                if (pState == null) return false;

                int currentEnergy = pState.Energy;

                int cost = GetCardEnergyCost(card);

                return cost <= currentEnergy;

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] IsAffordable error: " + ex.Message);

                return false;

            }

        }



        public static bool HasEnemyHarmfulStatus(CombatState state)

        {

            if (state == null || state.Enemies == null) return false;

            try

            {

                foreach (var enemy in state.Enemies)

                {

                    if (enemy == null || !enemy.IsAlive) continue;

                    var powers = enemy.Powers;

                    if (powers != null)

                    {

                        foreach (var power in powers)

                        {

                            if (power == null || power.Amount <= 0) continue;

                            string pname = power.GetType().Name;

                            if (pname.IndexOf("Vulnerable", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                pname.IndexOf("Weak", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                pname.IndexOf("Frail", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                pname.IndexOf("Poison", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                pname.IndexOf("StrengthDown", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                pname.IndexOf("Shackled", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                pname.IndexOf("Disarm", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                pname.IndexOf("Constricted", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                pname.IndexOf("CorpseExplosion", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                pname.IndexOf("Doom", StringComparison.OrdinalIgnoreCase) >= 0)

                            {

                                return true;

                            }

                        }

                    }

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] HasEnemyHarmfulStatus error: " + ex.Message);

            }

            return false;

        }



        public static bool HasEnemyDebuff(CombatState state)

        {

            return HasEnemyHarmfulStatus(state);

        }



        public static bool HasP2ReceivedBuffOrResources(CombatState state, Player player2)

        {

            if (player2 == null) return false;

            try

            {

                var creature = player2.Creature;

                if (creature != null)

                {

                    if (creature.Block > 0) return true;

                    if (creature.Powers != null)

                    {

                        foreach (var power in creature.Powers)

                        {

                            if (power == null || power.Amount <= 0) continue;

                            string pname = power.GetType().Name;

                            if (pname.IndexOf("Strength", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                pname.IndexOf("Buffer", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                pname.IndexOf("PlatedArmor", StringComparison.OrdinalIgnoreCase) >= 0)

                            {

                                return true;

                            }

                        }

                    }

                }

                var pCombat = player2.PlayerCombatState;

                if (pCombat != null)

                {

                    if (P2TurnStartEnergy >= 0 && pCombat.Energy > P2TurnStartEnergy) return true;

                    if (P2TurnStartHandCount > 0 && pCombat.Hand != null && pCombat.Hand.Cards != null && pCombat.Hand.Cards.Count > P2TurnStartHandCount) return true;

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] HasP2ReceivedBuffOrResources error: " + ex.Message);

            }

            return false;

        }



        public static int GetCardBaseDamage(CardModel card)

        {

            if (card == null) return 0;

            try

            {

                var dvars = card.DynamicVars;

                if (dvars != null)

                {

                    if (dvars.Damage != null)

                    {

                        return Convert.ToInt32(dvars.Damage.BaseValue);

                    }

                    if (dvars.CalculatedDamage != null)

                    {

                        return Convert.ToInt32(dvars.CalculatedDamage.BaseValue);

                    }

                    if (dvars.ContainsKey("Damage"))

                    {

                        var v = dvars["Damage"];

                        if (v != null) return Convert.ToInt32(v.BaseValue);

                    }

                }

            }

            catch { }

            try

            {

                var canProp = card.GetType().GetProperty("CanonicalVars", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (canProp != null)

                {

                    var vars = canProp.GetValue(card) as IEnumerable;

                    if (vars != null)

                    {

                        foreach (var v in vars)

                        {

                            if (v == null) continue;

                            var nameProp = v.GetType().GetProperty("Name");

                            if (nameProp != null)

                            {

                                string nameVal = nameProp.GetValue(v) as string;

                                if (string.Equals(nameVal, "Damage", StringComparison.OrdinalIgnoreCase))

                                {

                                    var baseProp = v.GetType().GetProperty("BaseValue");

                                    if (baseProp != null)

                                    {

                                        return Convert.ToInt32(baseProp.GetValue(v));

                                    }

                                }

                            }

                        }

                    }

                }

            }

            catch { }

            return 0;

        }



        public static int CalculateMaxAffordableDamageWithEnergy(Player player, int availableEnergy)

        {

            if (player == null || availableEnergy <= 0) return 0;

            try

            {

                var handPile = PileType.Hand.GetPile(player);

                var cards = (handPile != null && handPile.Cards != null) ? handPile.Cards : null;

                if (cards == null || cards.Count == 0) return 0;



                var attacks = new List<KeyValuePair<int, int>>();

                foreach (var c in cards)

                {

                    if (c == null) continue;

                    if (!IsAttack(c) || !CheckIsPlayable(c)) continue;

                    int cost = Math.Max(0, GetCardEnergyCost(c));

                    int dmg = GetCardBaseDamage(c);

                    if (dmg > 0)

                    {

                        attacks.Add(new KeyValuePair<int, int>(cost, dmg));

                    }

                }



                if (attacks.Count == 0) return 0;



                int[] dp = new int[availableEnergy + 1];

                for (int i = 0; i < attacks.Count; i++)

                {

                    int cost = attacks[i].Key;

                    int dmg = attacks[i].Value;

                    for (int w = availableEnergy; w >= cost; w--)

                    {

                        if (dp[w - cost] + dmg > dp[w])

                        {

                            dp[w] = dp[w - cost] + dmg;

                        }

                    }

                }

                return dp[availableEnergy];

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] CalculateMaxAffordableDamageWithEnergy error: " + ex);

                return 0;

            }

        }



        public static int CalculateMaxAffordableDamage(Player player)

        {

            if (player == null || player.PlayerCombatState == null) return 0;

            return CalculateMaxAffordableDamageWithEnergy(player, player.PlayerCombatState.Energy);

        }



        public static bool IsEnemyVulnerable(Creature enemy)

        {

            if (enemy == null || enemy.Powers == null) return false;

            try

            {

                foreach (var p in enemy.Powers)

                {

                    if (p != null && p.Amount > 0)

                    {

                        string pname = p.GetType().Name;

                        if (pname.IndexOf("Vulnerable", StringComparison.OrdinalIgnoreCase) >= 0)

                        {

                            return true;

                        }

                    }

                }

            }

            catch { }

            return false;

        }



        public static bool CheckJointLethalPush(CombatState state, Player player1, Player player2, out string lethalInfo)

        {

            lethalInfo = "";

            if (state == null || state.Enemies == null || player1 == null || player2 == null) return false;

            if (player1.Creature == null || !player1.Creature.IsAlive) return false;

            if (player2.Creature == null || !player2.Creature.IsAlive) return false;



            try

            {

                int est_p2 = CalculateMaxAffordableDamage(player2);

                if (est_p2 <= 0)

                {

                    return false; // Requirement: est_p2 > 0 (pure P1 lethal does not trigger push)

                }



                int est_p1 = CalculateMaxAffordableDamage(player1);



                foreach (var enemy in state.Enemies)

                {

                    if (enemy == null || !enemy.IsAlive || enemy.CurrentHp <= 0) continue;



                    bool isVuln = IsEnemyVulnerable(enemy);

                    double mult = isVuln ? 1.5 : 1.0;

                    int totalEst = (int)Math.Floor(est_p1 * mult) + (int)Math.Floor(est_p2 * mult);



                    if (totalEst >= enemy.CurrentHp)

                    {

                        lethalInfo = string.Format("enemy={0} est_p1={1} est_p2={2} mult={3} hp={4}",

                            enemy.CombatId,

                            est_p1,

                            est_p2,

                            mult.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),

                            enemy.CurrentHp);

                        GD.Print(string.Format("[AiTeammate][Coord] joint-lethal-push {0}", lethalInfo));

                        return true;

                    }

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] CheckJointLethalPush error: " + ex);

            }

            return false;

        }



        public static CoordinatorDecision EvaluateDecision(CombatManager cm, CombatState state, Player player1, Player player2, out string reason)

        {

            reason = "";

            if (player1 == null || player2 == null)

            {

                reason = "missing player";

                return CoordinatorDecision.Immediate;

            }

            if (player1.Creature == null || !player1.Creature.IsAlive)

            {

                reason = "player 1 dead";

                return CoordinatorDecision.Immediate;

            }

            if (player2.Creature == null || !player2.Creature.IsAlive)

            {

                reason = "player 2 dead";

                return CoordinatorDecision.Immediate;

            }



            bool hasLivingEnemies = false;

            if (state != null && state.Enemies != null)

            {

                foreach (var enemy in state.Enemies)

                {

                    if (enemy != null && enemy.IsAlive)

                    {

                        hasLivingEnemies = true;

                        break;

                    }

                }

            }

            if (!hasLivingEnemies)

            {

                reason = "no living enemies";

                return CoordinatorDecision.Immediate;

            }



            // Directive #3.9.2: Joint Lethal Timing Heuristic

            string lethalInfo;

            if (CheckJointLethalPush(state, player1, player2, out lethalInfo))

            {

                reason = "joint-lethal-push: " + lethalInfo;

                return CoordinatorDecision.Immediate;

            }



            try

            {

                // Reset round tracking if round changed

                if (state != null && state.RoundNumber != LastRoundNumber)

                {

                    LastRoundNumber = state.RoundNumber;

                    HasReleasedThisTurn = false;

                    IsWaiting = false;

                }



                var p1Combat = player1.PlayerCombatState;

                var p2Combat = player2.PlayerCombatState;

                if (p1Combat == null || p2Combat == null)

                {

                    reason = "missing player combat state";

                    return CoordinatorDecision.Immediate;

                }



                var p1Hand = p1Combat.Hand != null ? p1Combat.Hand.Cards : null;

                var p2Hand = p2Combat.Hand != null ? p2Combat.Hand.Cards : null;



                // Hand audit logging (Directive #3.9.5: setup:Y(debuff-enemy|buff-ally))

                try

                {

                    var p1Audits = new List<string>();

                    if (p1Hand != null)

                    {

                        int count = 0;

                        foreach (var c in p1Hand)

                        {

                            if (c == null) continue;

                            string id = c.Id.ToString();

                            string sType;

                            bool isSetup = CardIsTeamSetup(c, out sType);

                            string setupStr = isSetup ? ("setup:Y(" + sType + ")") : "setup:N";

                            bool afford = IsAffordable(c, player1);

                            p1Audits.Add(string.Format("{0}({1}/可付:{2})", id, setupStr, afford ? "Y" : "N"));

                            count++;

                            if (count >= 8) break;

                        }

                    }



                    var p2Audits = new List<string>();

                    if (p2Hand != null)

                    {

                        int count = 0;

                        foreach (var c in p2Hand)

                        {

                            if (c == null) continue;

                            string id = c.Id.ToString();

                            bool atk = IsAttack(c);

                            string sType;

                            bool isSetup = CardIsTeamSetup(c, out sType);

                            string setupStr = isSetup ? ("setup:Y(" + sType + ")") : "setup:N";

                            p2Audits.Add(string.Format("{0}(attack:{1}/{2})", id, atk ? "Y" : "N", setupStr));

                            count++;

                            if (count >= 8) break;

                        }

                    }



                    GD.Print("[AiTeammate][Coord] Hand Audit: P1=[" + string.Join(", ", p1Audits) + "], P2=[" + string.Join(", ", p2Audits) + "]");

                }

                catch (Exception exAudit)

                {

                    GD.PrintErr("[AiTeammate][Coord] Hand audit log error: " + exAudit.Message);

                }



                // Condition 1: Player 2's hand has at least one affordable Attack card (cost <= P2 current energy)

                bool p2HasAffordableAttack = false;

                if (p2Hand != null)

                {

                    foreach (var card in p2Hand)

                    {

                        if (card != null && IsAttack(card) && IsAffordable(card, player2))

                        {

                            p2HasAffordableAttack = true;

                            break;

                        }

                    }

                }



                // Condition 2: Player 2's hand has affordable team setup card (debuff-enemy or buff-ally)

                bool p2HasAffordableSetup = false;

                if (p2Hand != null)

                {

                    foreach (var card in p2Hand)

                    {

                        string sType;

                        if (card != null && CardIsTeamSetup(card, out sType) && IsAffordable(card, player2))

                        {

                            p2HasAffordableSetup = true;

                            break;

                        }

                    }

                }



                // Condition 3: Player 1's hand has at least one affordable team setup card

                bool p1HasAffordableSetup = false;

                string p1SetupReason = "";

                int cheapestSetup = int.MaxValue;

                if (p1Hand != null)

                {

                    foreach (var card in p1Hand)

                    {

                        string sType;

                        if (card != null && CheckIsPlayable(card) && CardIsTeamSetup(card, out sType))

                        {

                            int cost = GetCardEnergyCost(card);

                            if (cost < cheapestSetup)

                            {

                                cheapestSetup = cost;

                            }

                            if (IsAffordable(card, player1))

                            {

                                p1HasAffordableSetup = true;

                                p1SetupReason = sType;

                            }

                        }

                    }

                }

                CheapestP1DebuffCost = cheapestSetup;



                // Directive #3.9.5 Decision Rules:

                // Rule 1: If P1 has affordable setup AND P2 has affordable attack -> ALWAYS WAIT!

                if (p1HasAffordableSetup && p2HasAffordableAttack)

                {

                    reason = "P1 has affordable setup (" + p1SetupReason + ") and P2 has affordable attack";

                    return CoordinatorDecision.Wait;

                }



                // Rule 2: If P1 has NO affordable setup, BUT P2 has affordable setup -> ImmediateSetup (ai-setup)

                if (!p1HasAffordableSetup && p2HasAffordableSetup)

                {

                    reason = "ai-setup";

                    return CoordinatorDecision.ImmediateSetup;

                }



                // Rule 3: Otherwise -> Immediate

                reason = "conditions not met (p1Setup=" + p1HasAffordableSetup + ", p2Attack=" + p2HasAffordableAttack + ", p2Setup=" + p2HasAffordableSetup + ", p1Debuff=" + p1HasAffordableSetup + ", p2Debuff=" + p2HasAffordableSetup + ")";

                return CoordinatorDecision.Immediate;

            }

            catch (Exception ex)

            {

                // Fail-open guarantee: default to immediate deployment

                GD.PrintErr("[AiTeammate][Coord] EvaluateDecision exception, fail-open to Immediate: " + ex);

                reason = "exception: " + ex.Message;

                return CoordinatorDecision.Immediate;

            }

        }



        public static void CleanHangingDeployment()

        {

            try

            {

                IsSuppressingCancelHook = true;

                var solverType = AccessTools.TypeByName("CombatSolver.SolverController");

                if (CancelDeploymentMethod == null && solverType != null)

                {

                    CancelDeploymentMethod = AccessTools.Method(solverType, "CancelDeployment");

                }

                if (CancelDeploymentMethod != null)

                {

                    CancelDeploymentMethod.Invoke(null, null);

                }

                else if (solverType != null)

                {

                    if (DeploymentField == null)

                    {

                        DeploymentField = AccessTools.Field(solverType, "_deployment");

                    }

                    if (DeploymentField != null)

                    {

                        DeploymentField.SetValue(null, null);

                    }

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] CleanHangingDeployment error: " + ex.Message);

            }

            finally

            {

                IsSuppressingCancelHook = false;

                IsDeploying = false; // Directive #5.35 T1.3: ensure IsDeploying is reset

            }

        }



        public static bool IsFullAutoEnabled()

        {

            try

            {

                var solverType = AccessTools.TypeByName("CombatSolver.SolverController");

                if (solverType == null) return true;

                if (CombatField == null)

                {

                    CombatField = AccessTools.Field(solverType, "_combat");

                }

                if (CombatField != null)

                {

                    object combatObj = CombatField.GetValue(null);

                    if (combatObj != null)

                    {

                        var getter = AccessTools.PropertyGetter(combatObj.GetType(), "FullAutoEnabled")

                            ?? AccessTools.Method(combatObj.GetType(), "get_FullAutoEnabled", Type.EmptyTypes);

                        if (getter != null)

                        {

                            return (bool)getter.Invoke(combatObj, null);

                        }

                    }

                }

            }

            catch { }

            return true;

        }



        public static void EnsureFullAutoEnabled()

        {

            try

            {

                var solverType = AccessTools.TypeByName("CombatSolver.SolverController");

                if (solverType == null) return;



                if (CombatField == null)

                {

                    CombatField = AccessTools.Field(solverType, "_combat");

                }

                if (CombatField != null)

                {

                    object combatObj = CombatField.GetValue(null);

                    if (combatObj != null)

                    {

                        var setter = AccessTools.PropertySetter(combatObj.GetType(), "FullAutoEnabled")

                            ?? AccessTools.Method(combatObj.GetType(), "set_FullAutoEnabled", new Type[] { typeof(bool) });

                        if (setter != null)

                        {

                            setter.Invoke(combatObj, new object[] { true });

                            return;

                        }

                    }

                }



                var controller = CachedSolverController;

                if (controller != null)

                {

                    var setFullAutoMethod = AccessTools.Method(controller.GetType(), "SetFullAuto");

                    if (setFullAutoMethod != null)

                    {

                        var p = setFullAutoMethod.GetParameters();

                        if (p.Length == 1 && p[0].ParameterType == typeof(bool))

                        {

                            setFullAutoMethod.Invoke(controller, new object[] { true });

                        }

                        else if (p.Length == 2)

                        {

                            var cm = CombatManager.Instance;

                            var state = cm != null ? cm.DebugOnlyGetState() : null;

                            setFullAutoMethod.Invoke(controller, new object[] { state, true });

                        }

                        else if (p.Length == 3)

                        {

                            var cm = CombatManager.Instance;

                            var state = cm != null ? cm.DebugOnlyGetState() : null;

                            setFullAutoMethod.Invoke(controller, new object[] { null, state, true });

                        }

                    }

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] EnsureFullAutoEnabled error: " + ex.Message);

            }

        }



        public static void CheckFullAutoGuard()

        {

            try

            {

                var cm = CombatManager.Instance;

                if (cm == null) return;

                var state = cm.DebugOnlyGetState();

                if (state == null || state.Players == null || state.Players.Count < 2) return;



                var player2 = state.Players[state.Players.Count - 1];

                if (player2 == null || player2.Creature == null || !player2.Creature.IsAlive) return;



                // Directive #5.34 T0.2: Rate-limit EnsureFullAutoEnabled (not repeated every frame)
                if (!IsFullAutoEnabled())
                {
                    if (LastFullAutoEnsureTurn != TurnNumber && FlowCoordinator.AutoCardRewardEnabled)
                    {
                        LastFullAutoEnsureTurn = TurnNumber;
                        EnsureFullAutoEnabled();
                        GD.Print("[AiTeammate][Coord] full-auto-reenabled turn=" + TurnNumber);
                    }
                }

                // Directive #5.36 T2.3: Keep checking hand ownership until residue or blank hand is resolved
                string _critReason;
                if ((PendingDeferredHandCheckAfterP2 || P2ResidueStartTime != DateTime.MinValue || BlankHandStartTime != DateTime.MinValue) &&
                    !IsInP2HandDeferredPeriod(out _critReason))
                {
                    CheckHandOwnershipGuard(DateTime.UtcNow);
                    if (P2ResidueStartTime == DateTime.MinValue && BlankHandStartTime == DateTime.MinValue)
                    {
                        PendingDeferredHandCheckAfterP2 = false;
                    }
                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] CheckFullAutoGuard error: " + ex.Message);

            }

        }



        public static void CheckAutonomousTurnEvaluation(DateTime now)

        {

            try

            {

                // Directive #4.0: Wait/Gatekeeper switch gate

                if (!WaitEnabled) return;

                if (HasAutonomousEvaluatedThisTurn) return;

                if (HasEvaluatedThisTurn) return;

                if (IsPreCombatOrLoading || TurnStartTime == DateTime.MinValue) return;

                if ((now - TurnStartTime).TotalSeconds < 2.5) return;

                if (DeploySeenThisTurn || HasEnteredDeployPrefixThisTurn) return;

                if (IsDeploying) return;

                if (HasCompletedDeploymentThisTurn) return;



                var cm = CombatManager.Instance;

                if (cm == null) return;

                var state = cm.DebugOnlyGetState();

                if (state == null || state.Players == null || state.Players.Count < 2) return;



                var p1 = state.Players[0];

                var p2 = state.Players[state.Players.Count - 1];

                if (p1 == null || p2 == null) return;

                if (p2.Creature == null || !p2.Creature.IsAlive) return;



                HasAutonomousEvaluatedThisTurn = true;

                HasEvaluatedThisTurn = true;

                EvaluatedPath = "gatekeeper";



                string reason;

                var decision = EvaluateDecision(cm, state, p1, p2, out reason);

                GD.Print("[AiTeammate][Coord] eval-path=gatekeeper decision=" + decision + " (" + reason + ")");



                if (decision == CoordinatorDecision.Wait)

                {

                    if (HasEnemyDebuff(state))

                    {

                        GD.Print("[AiTeammate][Coord] eval-path=gatekeeper: enemy already debuffed, skipping wait and kicking recalculation");

                        KickSolverRecalculation(state, "autonomous-enemy-already-debuffed");

                        return;

                    }



                    IsWaiting = true;

                    WaitStartTime = now;

                    HasReleasedThisTurn = false;

                    CleanHangingDeployment();

                    GD.Print("[AiTeammate][Coord] Autonomous wait armed (trigger=autonomous-wait, timeout=" + WaitTimeoutSeconds.ToString("F1") + "s)");

                }

                else

                {

                    string trig = (reason != null && reason.IndexOf("joint-lethal-push", StringComparison.OrdinalIgnoreCase) >= 0) ? "autonomous-joint-lethal-push" : "autonomous-immediate";

                    GD.Print("[AiTeammate][Coord] eval-path=gatekeeper: kicking search with deployWhenReady=true (" + trig + ")");

                    KickSolverRecalculation(state, trig);

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] CheckAutonomousTurnEvaluation error: " + ex.Message);

            }

        }



        public static MethodInfo FindRequestSearchMethod(Type solverControllerType)

        {

            if (solverControllerType == null) return null;

            var methods = solverControllerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

            foreach (var m in methods)

            {

                if (m.Name == "RequestSearch")

                {

                    return m;

                }

            }

            return null;

        }



        public static void ResetSolverStateBeforeSearch()

        {

            try

            {

                var solverType = AccessTools.TypeByName("CombatSolver.SolverController");

                if (solverType == null) return;



                // 1. Unfreeze route & resume automatic search on _combat

                if (CombatField == null)

                {

                    CombatField = AccessTools.Field(solverType, "_combat");

                }

                if (CombatField != null)

                {

                    object combatObj = CombatField.GetValue(null);

                    if (combatObj != null)

                    {

                        var routeFrozenSetter = AccessTools.PropertySetter(combatObj.GetType(), "RouteFrozen")

                            ?? AccessTools.Method(combatObj.GetType(), "set_RouteFrozen", new Type[] { typeof(bool) });

                        if (routeFrozenSetter != null)

                        {

                            routeFrozenSetter.Invoke(combatObj, new object[] { false });

                        }



                        var autoPausedSetter = AccessTools.PropertySetter(combatObj.GetType(), "AutomaticSearchPaused")

                            ?? AccessTools.Method(combatObj.GetType(), "set_AutomaticSearchPaused", new Type[] { typeof(bool) });

                        if (autoPausedSetter != null)

                        {

                            autoPausedSetter.Invoke(combatObj, new object[] { false });

                        }

                    }

                }



                // 2. Cancel hanging search (CTS cancel & null _search)

                var cancelSearchMethod = AccessTools.Method(solverType, "CancelSearch");

                if (cancelSearchMethod != null)

                {

                    cancelSearchMethod.Invoke(null, null);

                }



                // 3. Cancel deferred search

                var cancelDeferredMethod = AccessTools.Method(solverType, "CancelDeferredSearch");

                if (cancelDeferredMethod != null)

                {

                    cancelDeferredMethod.Invoke(null, null);

                }



                // 4. Cancel deployment

                CleanHangingDeployment();

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] ResetSolverStateBeforeSearch error: " + ex.Message);

            }

        }



        public static void TryInvokeResetCore()

        {

            try

            {

                var solverType = AccessTools.TypeByName("CombatSolver.SolverController");

                if (solverType != null)

                {

                    var resetCoreMethod = AccessTools.Method(solverType, "ResetCore");

                    if (resetCoreMethod != null)

                    {

                        var pars = resetCoreMethod.GetParameters();

                        if (pars.Length == 0)

                        {

                            resetCoreMethod.Invoke(null, null);

                            GD.Print("[AiTeammate][Coord] ResetCore invoked successfully.");

                        }

                        else if (pars.Length == 1 && pars[0].ParameterType.IsEnum)

                        {

                            object enumZero = Enum.ToObject(pars[0].ParameterType, 0);

                            resetCoreMethod.Invoke(null, new object[] { enumZero });

                            GD.Print("[AiTeammate][Coord] ResetCore(0) invoked successfully.");

                        }

                    }

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] TryInvokeResetCore error: " + ex);

            }

        }



        public static MethodInfo ResolveLiveRequestSearchMethod()

        {

            try

            {

                var liveHost = MegaCrit.Sts2.Core.Nodes.NGame.Instance;

                Type liveHostType = (liveHost != null) ? liveHost.GetType() : typeof(MegaCrit.Sts2.Core.Nodes.NGame);



                // Priority 1: Match param[0].ParameterType == liveHostType exactly across all loaded assemblies

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())

                {

                    if (asm.GetName().Name == "CombatSolver")

                    {

                        Type scType = asm.GetType("CombatSolver.SolverController");

                        if (scType != null)

                        {

                            var methods = scType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

                            foreach (var m in methods)

                            {

                                if (m.Name == "RequestSearch")

                                {

                                    var pars = m.GetParameters();

                                    if (pars.Length >= 4 && pars[0].ParameterType == liveHostType)

                                    {

                                        return m;

                                    }

                                }

                            }

                        }

                    }

                }



                // Priority 2: Match assembly of liveHostType

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())

                {

                    if (asm.GetName().Name == "CombatSolver")

                    {

                        Type scType = asm.GetType("CombatSolver.SolverController");

                        if (scType != null)

                        {

                            var methods = scType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

                            foreach (var m in methods)

                            {

                                if (m.Name == "RequestSearch")

                                {

                                    var pars = m.GetParameters();

                                    if (pars.Length >= 4 && pars[0].ParameterType.Assembly == liveHostType.Assembly)

                                    {

                                        return m;

                                    }

                                }

                            }

                        }

                    }

                }



                // Priority 3: Fallback across any CombatSolver assembly

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())

                {

                    if (asm.GetName().Name == "CombatSolver")

                    {

                        Type scType = asm.GetType("CombatSolver.SolverController");

                        if (scType != null)

                        {

                            var methods = scType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

                            foreach (var m in methods)

                            {

                                if (m.Name == "RequestSearch")

                                {

                                    return m;

                                }

                            }

                        }

                    }

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] ResolveLiveRequestSearchMethod error: " + ex.Message);

            }

            return RequestSearchMethod;

        }



        public static bool InvokeLiveRequestSearch(CombatState state, string trigger)

        {

            try

            {

                if (state == null)

                {

                    var cm = CombatManager.Instance;

                    if (cm != null) state = cm.DebugOnlyGetState();

                }



                // Directive #5.0.7 T0: Eradicate cross-context NGame ArgumentException by resolving

                // MethodInfo where parameter 0 matches the live NGame.Instance type.

                var targetMethod = ResolveLiveRequestSearchMethod();

                if (targetMethod != null && state != null)

                {

                    var parameters = targetMethod.GetParameters();

                    object[] args = new object[parameters.Length];

                    for (int i = 0; i < parameters.Length; i++)

                    {

                        var p = parameters[i];

                        var pType = p.ParameterType;



                        if (pType.Name == "CombatState" || pType.IsAssignableFrom(typeof(CombatState)) || pType.Name.Contains("CombatState"))

                        {

                            args[i] = state;

                        }

                        else if (pType.Name == "NGame" || typeof(Godot.Node).IsAssignableFrom(pType) || p.Name == "host")

                        {

                            object hostInst = null;

                            if (pType == typeof(MegaCrit.Sts2.Core.Nodes.NGame))

                            {

                                hostInst = MegaCrit.Sts2.Core.Nodes.NGame.Instance;

                            }

                            else

                            {

                                try

                                {

                                    var instProp = pType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);

                                    if (instProp != null) hostInst = instProp.GetValue(null, null);

                                    if (hostInst == null)

                                    {

                                        var instField = pType.GetField("<Instance>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)

                                            ?? pType.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);

                                        if (instField != null) hostInst = instField.GetValue(null);

                                    }

                                }

                                catch { }

                                if (hostInst == null)

                                {

                                    hostInst = MegaCrit.Sts2.Core.Nodes.NGame.Instance;

                                }

                            }

                            args[i] = hostInst ?? (object)CombatManager.Instance;

                        }

                        else if (pType.IsEnum || p.Name.Contains("reason") || pType.Name.Contains("Reason"))

                        {

                            object enumVal = null;

                            try

                            {

                                foreach (var val in Enum.GetValues(pType))

                                {

                                    if (val.ToString().IndexOf("Manual", StringComparison.OrdinalIgnoreCase) >= 0)

                                    {

                                        enumVal = val;

                                        break;

                                    }

                                }

                            }

                            catch { }

                            args[i] = enumVal ?? Enum.ToObject(pType, 0);

                        }

                        else if (pType == typeof(bool))

                        {

                            args[i] = true; // Directive #3.7: deployWhenReady = true so completed searches always deploy

                        }

                        else

                        {

                            args[i] = null;

                        }

                    }



                    targetMethod.Invoke(null, args);

                    RecalcSucceeded = true;

                    return true;

                }

                else

                {

                    GD.PrintErr("[AiTeammate][Coord] KickSolverRecalculation failed: missing method or controller, attempting ResetCore");

                    TryInvokeResetCore();

                    return false;

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] KickSolverRecalculation exception: " + ex);

                TryInvokeResetCore();

                return false;

            }

        }



        public static void KickSolverRecalculation(CombatState state, string trigger)

        {

            DateTime now = DateTime.UtcNow;

            if (trigger == LastKickTrigger && (now - LastKickTime).TotalMilliseconds < 500.0)

            {

                return;

            }

            LastKickTrigger = trigger;

            LastKickTime = now;



            CurrentRecalcGeneration++;

            ReleaseRequestedTime = now;

            RecalcSucceeded = false;

            Watchdog3sFired = false;



            // Reset all solver state: unfreeze route, resume auto, cancel search CTS, cancel deferred, cancel deployment

            ResetSolverStateBeforeSearch();



            // Ensure full auto is enabled

            EnsureFullAutoEnabled();



            GD.Print("[AiTeammate][Coord] recalc-requested trigger=" + trigger + " time=" + ReleaseRequestedTime.ToString("HH:mm:ss.fff"));



            InvokeLiveRequestSearch(state, trigger);

        }



        public static void RefreshPlayerHandVisuals(CombatState state)

        {

            try

            {

                if (IsDeploying) return;

                if (LocalContext.NetId != GetPlayer1NetId()) return;



                bool prevSuppress = ModEntry.SuppressControlSwitchUi;

                ModEntry.SuppressControlSwitchUi = true;

                try

                {

                    if (state == null)

                    {

                        var cm = CombatManager.Instance;

                        if (cm != null)

                        {

                            state = cm.DebugOnlyGetState();

                        }

                    }



                    var hand = NPlayerHand.Instance;

                    if (hand == null) return;



                    ulong p1NetId = GetPlayer1NetId();



                    var holders = hand.ActiveHolders;

                    if (holders != null)

                    {

                        foreach (var h in holders)

                        {

                            if (h != null)

                            {

                                var handHolder = h as MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder;

                                if (handHolder != null && handHolder.CardModel != null && handHolder.CardModel.Owner != null)

                                {

                                    if (handHolder.CardModel.Owner.NetId == p1NetId)

                                    {

                                        handHolder.UpdateCard();

                                    }

                                }

                            }

                        }

                    }

                }

                finally

                {

                    ModEntry.SuppressControlSwitchUi = prevSuppress;

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate] RefreshPlayerHandVisuals error: " + ex.Message);

            }

        }



        public static DateTime LastHandHealTime = DateTime.MinValue;

        public static int ConsecutiveCountMismatchTicks = 0;



        public static void CheckHandOwnershipGuard(DateTime now)
        {
            try
            {
                if (IsDeploying) return;

                var cm = CombatManager.Instance;
                if (cm == null) return;
                var state = cm.DebugOnlyGetState();
                if (state == null || state.Players == null || state.Players.Count < 2) return;

                var player1 = state.Players[0];
                if (player1 == null || player1.Creature == null) return;

                ulong p1NetId = player1.NetId;
                bool isP1Dead = !player1.Creature.IsAlive;
                var hand = MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand.Instance;
                if (hand == null) return;

                // Directive #5.35 T2.2: If P1 is actively playing/selecting cards, skip guard and clear mismatch tracking
                if (!isP1Dead && IsPlayer1HandBusy(hand))
                {
                    HandMismatchStartTime = DateTime.MinValue;
                    ConsecutiveHandMismatchFrames = 0;
                    P2ResidueStartTime = DateTime.MinValue;
                    ConsecutiveP2ResidueFrames = 0;
                    BlankHandStartTime = DateTime.MinValue;
                    ConsecutiveBlankHandFrames = 0;
                    LastHandOwnershipDeferredReason = "p1-card-play-in-progress";
                    return;
                }

                // Directive #5.34 T1.1: defer during P2 scope or actions
                string deferReason;
                if (IsInP2HandDeferredPeriod(out deferReason))
                {
                    if (deferReason != LastHandOwnershipDeferredReason || (now - LastHandOwnershipDeferredLogTime).TotalMilliseconds >= 500)
                    {
                        LastHandOwnershipDeferredReason = deferReason;
                        LastHandOwnershipDeferredLogTime = now;
                        GD.Print(string.Format("[AiTeammate][Coord] hand-ownership-check-deferred owner=P2 reason={0}", deferReason));
                    }
                    HandMismatchStartTime = DateTime.MinValue;
                    ConsecutiveHandMismatchFrames = 0;
                    P2ResidueStartTime = DateTime.MinValue;
                    ConsecutiveP2ResidueFrames = 0;
                    BlankHandStartTime = DateTime.MinValue;
                    ConsecutiveBlankHandFrames = 0;
                    return; // skip RebindHandContainerToPlayer1
                }

                var holders = hand.ActiveHolders;
                bool hasP2Holder = false;
                if (holders != null)
                {
                    for (int i = 0; i < holders.Count; i++)
                    {
                        var h = holders[i];
                        if (h != null && h.CardModel != null && h.CardModel.Owner != null)
                        {
                            if (TurnCoordinator.IsP2Target(h.CardModel.Owner.NetId))
                            {
                                hasP2Holder = true;
                                break;
                            }
                        }
                    }
                }

                // Directive #5.35 T2.3: Tighten CheckHandOwnershipGuard trigger conditions
                // Branch 1: Actual P2 card residue outside P2 deferred period
                if (hasP2Holder)
                {
                    BlankHandStartTime = DateTime.MinValue;
                    ConsecutiveBlankHandFrames = 0;
                    if (P2ResidueStartTime == DateTime.MinValue) P2ResidueStartTime = now;
                    ConsecutiveP2ResidueFrames++;
                    double residueDuration = (now - P2ResidueStartTime).TotalMilliseconds;
                    if (residueDuration >= 250.0 && ConsecutiveP2ResidueFrames >= 3)
                    {
                        P2ResidueStartTime = DateTime.MinValue;
                        ConsecutiveP2ResidueFrames = 0;
                        HasSelfHealedHandThisTurn = true;
                        LastHandHealTime = now;
                        TotalHandRebindHeals++;

                        // Directive #5.36 T2.3: Precisely remove only P2 card holders if P1 cards exist
                        List<MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder> p2HoldersToRemove = new List<MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder>();
                        int p1HolderCount = 0;
                        if (holders != null)
                        {
                            for (int i = 0; i < holders.Count; i++)
                            {
                                var h = holders[i] as MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder;
                                if (h != null && h.CardModel != null && h.CardModel.Owner != null)
                                {
                                    if (TurnCoordinator.IsP2Target(h.CardModel.Owner.NetId))
                                    {
                                        p2HoldersToRemove.Add(h);
                                    }
                                    else
                                    {
                                        p1HolderCount++;
                                    }
                                }
                            }
                        }

                        if (p2HoldersToRemove.Count > 0 && p1HolderCount > 0)
                        {
                            GD.Print(string.Format("[AiTeammate][Coord] p2-card-residue-pruned count={0} p1HoldersRemaining={1}", p2HoldersToRemove.Count, p1HolderCount));
                            foreach (var ph in p2HoldersToRemove)
                            {
                                try
                                {
                                    hand.RemoveCardHolder(ph);
                                    ph.QueueFree();
                                }
                                catch { }
                            }
                            try { RefreshPlayerHandVisuals(state); } catch { }
                        }
                        else
                        {
                            GD.Print(string.Format("[AiTeammate][Coord] p2-card-residue-leak detected in P1 hand, rebinding to P1 (duration={0:F0}ms frames={1})", residueDuration, ConsecutiveP2ResidueFrames));
                            RebindHandContainerToPlayer1(cm, state, player1);
                        }
                    }
                    return;
                }
                else
                {
                    P2ResidueStartTime = DateTime.MinValue;
                    ConsecutiveP2ResidueFrames = 0;
                }

                // Branch 2: !hasP2Holder. All cards in UI belong to P1.
                int uiHolderCount = holders != null ? holders.Count : 0;
                int p1ModelCardCount = 0;
                try
                {
                    var p1HandPile = MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand.GetPile(player1);
                    if (p1HandPile != null && p1HandPile.Cards != null) p1ModelCardCount = p1HandPile.Cards.Count;
                }
                catch { }

                // If UI has holders, P1 is playing normally - NEVER rebind for mere count differences!
                if (uiHolderCount > 0)
                {
                    BlankHandStartTime = DateTime.MinValue;
                    ConsecutiveBlankHandFrames = 0;
                    HandMismatchStartTime = DateTime.MinValue;
                    ConsecutiveHandMismatchFrames = 0;
                    LastHandOwnershipDeferredReason = null;
                    return;
                }

                // UI is truly blank (uiHolderCount == 0) while P1 model has cards (p1ModelCardCount > 0)
                if (p1ModelCardCount > 0 && !isP1Dead && !IsPlayer1HandBusy(hand))
                {
                    if (BlankHandStartTime == DateTime.MinValue) BlankHandStartTime = now;
                    ConsecutiveBlankHandFrames++;
                    double blankDuration = (now - BlankHandStartTime).TotalMilliseconds;
                    if (blankDuration >= 500.0 && ConsecutiveBlankHandFrames >= 5)
                    {
                        BlankHandStartTime = DateTime.MinValue;
                        ConsecutiveBlankHandFrames = 0;
                        HasSelfHealedHandThisTurn = true;
                        LastHandHealTime = now;
                        TotalHandRebindHeals++;
                        GD.Print(string.Format("[AiTeammate][Coord] blank-hand-leak detected (modelCards={0}, uiHolders=0), rebinding to P1 (duration={1:F0}ms frames={2})", p1ModelCardCount, blankDuration, ConsecutiveBlankHandFrames));
                        RebindHandContainerToPlayer1(cm, state, player1);
                    }
                }
                else
                {
                    BlankHandStartTime = DateTime.MinValue;
                    ConsecutiveBlankHandFrames = 0;
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Coord] CheckHandOwnershipGuard error: " + ex);
            }
        }

        public static void RebindHandContainerToPlayer1(CombatManager cm, CombatState state, Player player1)

        {

            // Directive #5.35 T2.4: Never rebuild hand while P1 is busy playing cards
            if (player1 != null && player1.Creature != null && player1.Creature.IsAlive)
            {
                var handCheck = MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand.Instance;
                if (handCheck != null && IsPlayer1HandBusy(handCheck))
                {
                    GD.Print("[AiTeammate][Coord] RebindHandContainerToPlayer1 deferred: P1 card play in progress");
                    return;
                }
            }

            TurnCoordinator.P1InputLockDepth++; GD.Print("[AiTeammate][Coord] input-lock-window begin=hand-rebuild");
            bool prevSuppress = ModEntry.SuppressControlSwitchUi;

            // Directive #5.0.5 Task 0: Unsuppress UI to allow full combat UI refresh for P1 after full 5-container holder purge

            ModEntry.SuppressControlSwitchUi = false;

            try

            {

                if (player1 == null) return;

                bool isP1Dead = player1.Creature == null || !player1.Creature.IsAlive;



                var hand = NPlayerHand.Instance;

                if (hand == null) return;



                try

                {

                    if (!hand.InCardPlay)

                    {

                        hand.CancelAllCardPlay();

                    }

                }

                catch { }



                // Step 1 (Directive #5.0.5 Task 0): ALWAYS purge all residual holders across all 5 containers FIRST

                var nodesToPurge = new List<Node>();



                // Container A: ActiveHolders

                try

                {

                    var active = hand.ActiveHolders;

                    if (active != null)

                    {

                        foreach (var h in active)

                        {

                            if (h != null && GodotObject.IsInstanceValid(h) && !nodesToPurge.Contains(h)) nodesToPurge.Add(h);

                        }

                    }

                }

                catch { }



                // Container B: Holders (private property IReadOnlyList<NHandCardHolder>)

                try

                {

                    var holdersProp = AccessTools.Property(hand.GetType(), "Holders");

                    var holders = holdersProp != null ? holdersProp.GetValue(hand, null) as System.Collections.IEnumerable : null;

                    if (holders != null)

                    {

                        foreach (var obj in holders)

                        {

                            var h = obj as Node;

                            if (h != null && GodotObject.IsInstanceValid(h) && !nodesToPurge.Contains(h)) nodesToPurge.Add(h);

                        }

                    }

                }

                catch { }



                // Container C: CardHolderContainer.GetChildren()

                var container = hand.CardHolderContainer;

                if (container != null && GodotObject.IsInstanceValid(container))

                {

                    try

                    {

                        var children = container.GetChildren();

                        if (children != null)

                        {

                            foreach (var child in children)

                            {

                                if (child != null && GodotObject.IsInstanceValid(child) && !nodesToPurge.Contains(child)) nodesToPurge.Add(child);

                            }

                        }

                    }

                    catch { }

                }



                // Container D: NPlayerHand direct children (catches reparented holders during StartCardPlay)

                try

                {

                    var handChildren = hand.GetChildren();

                    if (handChildren != null)

                    {

                        foreach (var child in handChildren)

                        {

                            if (child != null && GodotObject.IsInstanceValid(child))

                            {

                                string tname = child.GetType().Name;

                                if ((child is MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder ||

                                    tname == "NCardPlay" ||

                                    tname.Contains("CardHolder")) && !nodesToPurge.Contains(child))

                                {

                                    nodesToPurge.Add(child);

                                }

                            }

                        }

                    }

                }

                catch { }



                // Container E: Internal _holdersAwaitingQueue & _selectedHandCardContainer via reflection

                try

                {

                    var queueField = AccessTools.Field(hand.GetType(), "_holdersAwaitingQueue");

                    if (queueField != null)

                    {

                        var queue = queueField.GetValue(hand) as System.Collections.IEnumerable;

                        if (queue != null)

                        {

                            foreach (var item in queue)

                            {

                                var n = item as Node;

                                if (n != null && GodotObject.IsInstanceValid(n) && !nodesToPurge.Contains(n)) nodesToPurge.Add(n);

                            }

                        }

                    }



                    var selectedField = AccessTools.Field(hand.GetType(), "_selectedHandCardContainer");

                    if (selectedField != null)

                    {

                        var selCont = selectedField.GetValue(hand) as Node;

                        if (selCont != null && GodotObject.IsInstanceValid(selCont))

                        {

                            var selChildren = selCont.GetChildren();

                            if (selChildren != null)

                            {

                                foreach (var sc in selChildren)

                                {

                                    if (sc != null && GodotObject.IsInstanceValid(sc) && !nodesToPurge.Contains(sc)) nodesToPurge.Add(sc);

                                }

                            }

                        }

                    }

                }

                catch { }



                int purgedCount = nodesToPurge.Count;



                // Purge all collected nodes

                foreach (var node in nodesToPurge)

                {

                    try

                    {

                        if (node == null || !GodotObject.IsInstanceValid(node)) continue;



                        var holder = node as MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder;

                        if (holder != null)

                        {

                            try

                            {

                                hand.RemoveCardHolder(holder);

                            }

                            catch { }

                        }



                        if (GodotObject.IsInstanceValid(node))

                        {

                            var parent = node.GetParent();

                            if (parent != null)

                            {

                                parent.RemoveChild(node);

                            }

                            node.QueueFree();

                        }

                    }

                    catch { }

                }



                // Clear internal references via reflection

                try

                {

                    var queueField = AccessTools.Field(hand.GetType(), "_holdersAwaitingQueue");

                    if (queueField != null)

                    {

                        var queueList = queueField.GetValue(hand) as System.Collections.IList;

                        if (queueList != null) queueList.Clear();

                    }



                    // Directive #5.35 T2.4: Only reset _currentCardPlay if hand is not in card play
                    if (!hand.InCardPlay)
                    {
                        var cardPlayField = AccessTools.Field(hand.GetType(), "_currentCardPlay");
                        if (cardPlayField != null)
                        {
                            cardPlayField.SetValue(hand, null);
                        }
                    }

                }

                catch { }



                // Step 2: Restore LocalContext to P1 (when alive) and invoke DRA RefreshCombatUiForControlledPlayer unsuppressed

                if (!isP1Dead)

                {

                    try

                    {

                        LocalContext.NetId = player1.NetId;

                    }

                    catch { }

                }



                bool draRefreshed = false;

                if (!isP1Dead)

                {

                    try

                    {

                        var multiRuntimeType = AccessTools.TypeByName("LocalMultiControl.Scripts.Runtime.LocalMultiControlRuntime");

                        if (multiRuntimeType != null)

                        {

                            var refreshMethod = AccessTools.Method(multiRuntimeType, "RefreshCombatUiForControlledPlayer", new Type[] { typeof(ulong) });

                            if (refreshMethod != null)

                            {

                                GD.Print(string.Format("[AiTeammate][Coord] hand-rebind: invoking DRA RefreshCombatUiForControlledPlayer for P1 at line 1788 (p1={0} dead={1})", player1.NetId, isP1Dead));

                                object ret = refreshMethod.Invoke(null, new object[] { player1.NetId });

                                if (ret is bool && (bool)ret)

                                {

                                    draRefreshed = true;

                                }

                            }

                        }

                    }

                    catch (Exception exDra)

                    {

                        GD.PrintErr("[AiTeammate][Coord] DRA RefreshCombatUiForControlledPlayer failed, using explicit rebuild: " + exDra);

                    }

                }



                // Step 3: Verify P1 hand cards and pile subscriptions; if DRA deferred or count mismatches, rebuild explicitly

                var handPile = PileType.Hand.GetPile(player1);

                int expectedCount = (!isP1Dead && handPile != null && handPile.Cards != null) ? handPile.Cards.Count : 0;

                int currentHolderCount = (hand.ActiveHolders != null) ? hand.ActiveHolders.Count : 0;



                bool draSuccess = draRefreshed;

                if (draSuccess)

                {

                    if (currentHolderCount == expectedCount && !isP1Dead)

                    {

                        GD.Print("[AiTeammate][Coord] Hand container rebound to Player 1 via DRA RefreshCombatUiForControlledPlayer.");

                        return;

                    }

                }



                if (!isP1Dead && (!draSuccess || currentHolderCount != expectedCount))

                {

                    // Ensure no half-added holders remain before explicit rebuild

                    try

                    {

                        if (hand.ActiveHolders != null && hand.ActiveHolders.Count > 0)

                        {

                            var copy = new List<MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder>(hand.ActiveHolders);

                            foreach (var h in copy)

                            {

                                if (h != null && GodotObject.IsInstanceValid(h))

                                {

                                    try { hand.RemoveCardHolder(h); } catch { }

                                    try { h.QueueFree(); } catch { }

                                }

                            }

                        }

                    }

                    catch { }



                    if (handPile != null && handPile.Cards != null)

                    {

                        foreach (var card in handPile.Cards)

                        {

                            if (card != null)

                            {

                                var nCard = NCard.Create(card, (ModelVisibility)1);

                                if (nCard != null)

                                {

                                    hand.Add(nCard, -1);

                                }

                            }

                        }

                    }

                }



                hand.ForceRefreshCardIndices();



                var room = NCombatRoom.Instance;

                var combatUi = room != null ? room.Ui : null;

                if (combatUi != null)

                {

                    if (!isP1Dead && !draRefreshed)

                    {

                        // Re-initialize Draw/Discard/Exhaust piles for P1 so pile counts & signals stay bound

                        try

                        {

                            string[] pileProps = new string[] { "DrawPile", "DiscardPile", "ExhaustPile" };

                            for (int i = 0; i < pileProps.Length; i++)

                            {

                                var pProp = AccessTools.Property(combatUi.GetType(), pileProps[i]);

                                object pileNode = pProp != null ? pProp.GetValue(combatUi, null) : null;

                                if (pileNode != null)

                                {

                                    var initMethod = AccessTools.Method(pileNode.GetType(), "Initialize", new Type[] { typeof(Player) });

                                    if (initMethod != null) initMethod.Invoke(pileNode, new object[] { player1 });

                                }

                            }

                        }

                        catch { }

                    }



                    if (combatUi.EndTurnButton != null)

                    {

                        var playerHandField = AccessTools.Field(combatUi.EndTurnButton.GetType(), "_playerHand");

                        if (playerHandField != null)

                        {

                            playerHandField.SetValue(combatUi.EndTurnButton, isP1Dead ? null : handPile);

                        }

                    }

                }



                if (!isP1Dead)

                {

                    RefreshPlayerHandVisuals(state);

                }



                // Directive #5.12 H7: Silent hand rebuild - suppress dealing tween/animations and instantly snap positions

                try

                {

                    hand.ForceRefreshCardIndices();

                    if (hand.ActiveHolders != null)

                    {

                        foreach (var h in hand.ActiveHolders)

                        {

                            if (h != null && GodotObject.IsInstanceValid(h))

                            {

                                try

                                {

                                    MethodInfo stopAnim = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder), "StopAnimations");

                                    if (stopAnim != null) stopAnim.Invoke(h, null);

                                    h.SetDefaultTargets();

                                    h.Position = h.TargetPosition;

                                    h.SetAngleInstantly(h.TargetAngle);

                                    h.SetScaleInstantly(Godot.Vector2.One);

                                }

                                catch { }

                            }

                        }

                    }

                }

                catch { }



                int rebuiltCount = (hand.ActiveHolders != null) ? hand.ActiveHolders.Count : 0;

                GD.Print(string.Format("[AiTeammate][Coord] hand-holder-purge-rebuild purged={0} rebuilt={1} p1={2} dead={3} draRefreshed={4}", purgedCount, rebuiltCount, player1.NetId, isP1Dead, draRefreshed));

                GD.Print("[AiTeammate][Coord] Hand container successfully rebound to Player 1 (" + player1.NetId + "). alive=" + (!isP1Dead) + " cardCount=" + rebuiltCount + " purgedNodes=" + purgedCount);

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] RebindHandContainerToPlayer1 error: " + ex);

            }

            finally

            {

                ModEntry.SuppressControlSwitchUi = prevSuppress;

                TurnCoordinator.P1InputLockDepth = Math.Max(0, TurnCoordinator.P1InputLockDepth - 1); GD.Print("[AiTeammate][Coord] input-lock-window end=hand-rebuild");
            }

        }



        public static void CheckControlDivergenceGuard(DateTime now)

        {

            try

            {

                var cm = CombatManager.Instance;

                if (cm == null) return;

                var state = cm.DebugOnlyGetState();

                if (state == null || state.Players == null || state.Players.Count < 2) return;



                var p1 = state.Players[0];

                if (p1 == null || p1.Creature == null || !p1.Creature.IsAlive)

                {

                    ConsecutiveDivergenceTicks = 0;

                    TransientIgnoredCount = 0;

                    return;

                }



                ulong p1NetId = p1.NetId;

                var p2 = state.Players[state.Players.Count - 1];

                ulong p2NetId = (p2 != null) ? p2.NetId : 0;

                ulong localNetId = LocalContext.NetId.HasValue ? LocalContext.NetId.Value : 0;

                ulong draSessionId = TurnFlowPatches.GetDraCurrentControlledPlayerId();



                // Directive #3.9.1: Divergence condition: localContext == P2 and draSession == P1 (or localContext != P1)

                bool isDiverged = (localNetId == p2NetId && (draSessionId == p1NetId || draSessionId == 0));



                if (isDiverged)

                {

                    // Directive #5.11 H1: Divergence exemption during pending choice or gathering player choice

                    if (P2PendingChoiceCount > 0 || IsP2ActionGatheringChoice(p2NetId))

                    {

                        ConsecutiveDivergenceTicks = 0;

                        TransientIgnoredCount = 0;

                        if ((now - LastDivergenceDeferLogTime).TotalSeconds >= 1.5)

                        {

                            LastDivergenceDeferLogTime = now;

                            GD.Print(string.Format("[AiTeammate][Coord] divergence-deferred-during-pending-choice: pendingChoice={0}, gatheringChoice={1}", P2PendingChoiceCount, IsP2ActionGatheringChoice(p2NetId)));

                        }

                        return;

                    }



                    ConsecutiveDivergenceTicks++;



                    // Persistence threshold requires:

                    // 1. Consecutive >= 3 tick samples (>= 1.5 seconds)

                    // 2. IsDeploying == false

                    // 3. Time since last PlayCardAction > 2.0 seconds

                    bool persistent = (ConsecutiveDivergenceTicks >= 3);

                    bool actionQuiet = (LastActionExecutedTime == DateTime.MinValue || (now - LastActionExecutedTime).TotalSeconds > 2.0);



                    if (!persistent || !actionQuiet || IsDeploying)

                    {

                        // Transient divergence (e.g. during card execution sequence). Suppress healing.

                        TransientIgnoredCount++;

                        if ((now - LastTransientLogTime).TotalSeconds >= 1.5)

                        {

                            LastTransientLogTime = now;

                            GD.Print(string.Format("[AiTeammate][Coord] divergence-transient-ignored x{0}", TransientIgnoredCount));

                        }

                        return;

                    }



                    // Persistent threshold satisfied!

                    if ((now - LastDivergenceHealTime).TotalSeconds >= 1.0)

                    {

                        LastDivergenceHealTime = now;

                        GD.PrintErr(string.Format("[AiTeammate][Coord] control-divergence detected (localContext={0}, draSession={1}, p1={2}), persistent >=1.5s, recovering control to P1 (logic unsuppressed, UI suppressed)", localNetId, draSessionId, p1NetId));

                        TurnFlowPatches.SwitchControlToPlayer(p1NetId, "wakuu-divergence-heal", true);

                        TurnFlowPatches.RefreshPlayer1CombatUiMinimal();

                        ConsecutiveDivergenceTicks = 0;

                        TransientIgnoredCount = 0;

                    }

                }

                else

                {

                    ConsecutiveDivergenceTicks = 0;

                    TransientIgnoredCount = 0;

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] CheckControlDivergenceGuard error: " + ex);

            }

        }



        public static int GetPlayerHandCardCount(Player player)

        {

            if (player == null) return 0;

            try

            {

                var pile = PileType.Hand.GetPile(player);

                if (pile != null && pile.Cards != null)

                {

                    return pile.Cards.Count;

                }

            }

            catch { }

            try

            {

                var handProp = player.GetType().GetProperty("Hand", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (handProp != null)

                {

                    var handObj = handProp.GetValue(player) as ICollection;

                    if (handObj != null) return handObj.Count;

                }

            }

            catch { }

            return 0;

        }



        public static int GetPlanActionCount(object[] args)

        {

            if (args == null) return -1;

            for (int i = 0; i < args.Length; i++)

            {

                var arg = args[i];

                if (arg == null) continue;

                try

                {

                    var propBestNode = arg.GetType().GetProperty("BestNode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    if (propBestNode != null)

                    {

                        var bestNode = propBestNode.GetValue(arg);

                        if (bestNode != null)

                        {

                            int c = ExtractCountFromPlan(bestNode);

                            if (c >= 0) return c;

                        }

                    }



                    int direct = ExtractCountFromPlan(arg);

                    if (direct >= 0) return direct;

                }

                catch { }

            }

            return -1;

        }

        private static object FindPlanObject(object[] args)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                object arg = args[i];
                if (arg == null) continue;
                try
                {
                    object best = GetMemberValue(arg, "BestNode");
                    if (best != null && (GetMemberValue(best, "Actions") != null || GetMemberValue(best, "ActionCount") != null)) return best;
                    if (GetMemberValue(arg, "Actions") != null || GetMemberValue(arg, "Plan") != null || GetMemberValue(arg, "ActionCount") != null) return arg;
                }
                catch { }
            }
            return null;
        }

        private static IEnumerable GetPlanActions(object planObj)
        {
            if (planObj == null) return null;
            object actions = GetMemberValue(planObj, "Actions") ?? GetMemberValue(planObj, "Plan");
            return actions as IEnumerable;
        }

        public static void RecordPlanTelemetry(object[] args, string source)
        {
            try
            {
                int count = GetPlanActionCount(args);
                if (count >= 0) P2PlanLengthThisTurn = count;
                P2PlanHasAttackThisTurn = false;
                P2PlanHasCardActionThisTurn = false;
                object plan = FindPlanObject(args);
                IEnumerable actions = GetPlanActions(plan);
                if (actions != null)
                {
                    foreach (object action in actions)
                    {
                        if (action == null) continue;
                        string actionName = action.GetType().Name;
                        if (actionName.IndexOf("PlayCardAction", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        P2PlanHasCardActionThisTurn = true;
                        CardModel card = GetMemberValue(action, "Card") as CardModel ?? GetMemberValue(action, "CardModel") as CardModel;
                        if (card != null && IsAttack(card)) P2PlanHasAttackThisTurn = true;
                    }
                }
                UpdateSearchTelemetry(source);
                GD.Print(string.Format("[AiTeammate][Coord] p2-plan-observed turn={0} plan-len={1} plan-card={2} plan-attack={3} search-status={4} search-nodes={5} source={6}", TurnNumber, P2PlanLengthThisTurn, P2PlanHasCardActionThisTurn, P2PlanHasAttackThisTurn, P2SearchStatusThisTurn, P2SearchNodesThisTurn, source ?? "unknown"));
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][Coord] p2-plan-observed failed: " + ex.Message); }
        }

        private static bool ReadBoolMember(object obj, string name)
        {
            object value = GetMemberValue(obj, name);
            return value is bool && (bool)value;
        }

        private static long ReadLongMember(object obj, string[] names)
        {
            if (obj == null) return -1;
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    object value = GetMemberValue(obj, names[i]);
                    if (value == null) continue;
                    return Convert.ToInt64(value);
                }
                catch { }
            }
            return -1;
        }

        private static string ReadTextMember(object obj, string[] names)
        {
            if (obj == null) return null;
            for (int i = 0; i < names.Length; i++)
            {
                object value = GetMemberValue(obj, names[i]);
                if (value != null) return value.ToString();
            }
            return null;
        }

        public static void UpdateSearchTelemetry(string source)
        {
            try
            {
                Type solverType = AccessTools.TypeByName("CombatSolver.SolverController");
                object search = null;
                if (solverType != null)
                {
                    FieldInfo searchField = AccessTools.Field(solverType, "_search");
                    if (searchField != null) search = searchField.GetValue(null);
                }
                object interaction = GetMemberValue(search, "Interaction");
                object progress = GetMemberValue(interaction, "Progress");
                object stopped = GetMemberValue(interaction, "StoppedResult");
                object result = GetMemberValue(search, "LatestResult") ?? stopped ?? GetMemberValue(progress, "CurrentBestResult");
                bool timedOut = ReadBoolMember(search, "TimedOut") || ReadBoolMember(search, "IsTimedOut") || ReadBoolMember(interaction, "TimedOut") || ReadBoolMember(interaction, "IsTimedOut") || ReadBoolMember(progress, "TimedOut") || ReadBoolMember(progress, "IsTimedOut") || ReadBoolMember(result, "TimedOut") || ReadBoolMember(result, "IsTimedOut");
                string statusText = ReadTextMember(search, new string[] { "SearchStatus", "Status", "CompletionReason", "TerminationReason" }) ?? ReadTextMember(interaction, new string[] { "SearchStatus", "Status", "CompletionReason", "TerminationReason" }) ?? ReadTextMember(result, new string[] { "SearchStatus", "Status", "CompletionReason", "TerminationReason" });
                bool searching = ReadBoolMember(search, "IsSearching") || ReadBoolMember(interaction, "IsSearching");
                if (timedOut || (!string.IsNullOrEmpty(statusText) && statusText.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)) P2SearchStatusThisTurn = "timeout";
                else if (searching) P2SearchStatusThisTurn = "searching";
                else if (result != null || stopped != null) P2SearchStatusThisTurn = "complete";
                else if (!string.IsNullOrEmpty(statusText) && statusText.IndexOf("complete", StringComparison.OrdinalIgnoreCase) >= 0) P2SearchStatusThisTurn = "complete";
                else P2SearchStatusThisTurn = "unknown";
                long nodes = ReadLongMember(progress, new string[] { "Nodes", "NodeCount", "ExpandedNodes", "ExpandedNodeCount", "NodesExpanded", "TotalNodes" });
                if (nodes < 0) nodes = ReadLongMember(interaction, new string[] { "Nodes", "NodeCount", "ExpandedNodes", "ExpandedNodeCount", "NodesExpanded", "TotalNodes" });
                if (nodes < 0) nodes = ReadLongMember(search, new string[] { "Nodes", "NodeCount", "ExpandedNodes", "ExpandedNodeCount", "NodesExpanded", "TotalNodes" });
                if (nodes >= 0) P2SearchNodesThisTurn = nodes;
            }
            catch { P2SearchStatusThisTurn = "unknown"; }
        }



        private static int ExtractCountFromPlan(object planObj)

        {

            if (planObj == null) return -1;

            try

            {

                var propCount = planObj.GetType().GetProperty("ActionCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (propCount != null)

                {

                    return Convert.ToInt32(propCount.GetValue(planObj));

                }

            }

            catch { }

            try

            {

                var propActions = planObj.GetType().GetProperty("Actions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (propActions != null)

                {

                    var actionsObj = propActions.GetValue(planObj);

                    if (actionsObj is ICollection)

                    {

                        return ((ICollection)actionsObj).Count;

                    }

                    if (actionsObj is IEnumerable)

                    {

                        int c = 0;

                        foreach (var x in (IEnumerable)actionsObj) c++;

                        return c;

                    }

                }

            }

            catch { }

            return -1;

        }



        public static bool CheckEmptyPlanRecovery(CombatState state, Player player2, object[] args, ref Task resultTask)

        {

            int p2HandCount = GetPlayerHandCardCount(player2);

            int actionCount = GetPlanActionCount(args);
            if (actionCount < 0)
            {
                if (P2DeployStartActionCountThisTurn >= 0) actionCount = P2DeployStartActionCountThisTurn;
                else if (P2FullAutoActionCountThisTurn >= 0) actionCount = P2FullAutoActionCountThisTurn;
            }

            bool hasAttack;
            bool hasUsableCard = P2HasUsableCard(player2, out hasAttack);
            int energy = player2 != null && player2.PlayerCombatState != null ? player2.PlayerCombatState.Energy : 0;

            if (actionCount == 0 && (!hasUsableCard || energy <= 0))
            {
                GD.Print(string.Format("[AiTeammate][Coord] p2-zero-action-legal turn={0} reason={1} hand={2} energy={3}", TurnNumber, energy <= 0 ? "zero-energy" : "no-playable-card", p2HandCount, energy));
                P2TurnEndReason = energy <= 0 ? "zero-energy" : "no-playable-card";
                P2ZeroActionProtectionFinalizedThisTurn = true;
                return true;
            }



            if (p2HandCount > 0 && actionCount == 0 && hasUsableCard)

            {

                if (!P2ZeroActionRetryUsedThisTurn)

                {

                    P2ZeroActionRetryUsedThisTurn = true;
                    HasRetriedEmptyPlanThisTurn = true;

                    IsWaiting = false;

                    HasReleasedThisTurn = true;

                    LastReleaseTrigger = "empty-plan-retry";

                    RecalcSucceeded = false;

                    CachedDeployResult = null;



                    GD.Print(string.Format("[AiTeammate][Coord] p2-zero-action-retry turn={0} physicalHand={1} energy={2} actionCount=0", TurnNumber, p2HandCount, energy));

                    CleanHangingDeployment();

                    RequestRecalculation(state, "empty-plan-retry");

                    resultTask = Task.CompletedTask;

                    return false;

                }

                else

                {

                    GD.Print(string.Format("[AiTeammate][Coord] p2-zero-action-retry-exhausted turn={0} physicalHand={1} energy={2}, entering fallback", TurnNumber, p2HandCount, energy));
                    if (!P2FallbackGreedyUsedThisTurn && TryFallbackGreedy(player2))
                    {
                        P2TurnEndReason = "fallback-greedy";
                        resultTask = Task.CompletedTask;
                        GD.Print("[AiTeammate][Coord] p2-zero-action-fallback-greedy-enqueued turn=" + TurnNumber);
                        return false;
                    }
                    P2TurnEndReason = "fallback-failed";
                    P2ZeroActionProtectionFinalizedThisTurn = true;
                    GD.Print("[AiTeammate][Coord] p2-zero-action-fallback-failed turn=" + TurnNumber + " allowing finite failure state");

                }

            }

            return true;

        }



        public static void RequestRecalculation(CombatState state, string trigger)

        {

            try

            {

                if (trigger != null && trigger.StartsWith("midturn-replan"))

                {

                    // Directive #3.9.4: Midturn replans are allowed up to 2 times per turn

                }

                else if (RecalcRequestedThisTurn && 
                    trigger != "empty-plan-retry" && 
                    trigger != "plan-empty-retry" && 
                    trigger != "deploy-hook-missed-recovery" && 
                    trigger != "enemy-already-debuffed" && 
                    !(trigger != null && trigger.StartsWith("solver-early-end", StringComparison.OrdinalIgnoreCase) && !HasRetriedSolverEarlyEndThisTurn))

                {

                    GD.Print("[AiTeammate][Coord] RequestRecalculation blocked: already requested once this turn (trigger=" + trigger + ")");

                    TriggerCachedDeployment();

                    return;

                }

                RecalcRequestedThisTurn = true;

                if (trigger != null && trigger.StartsWith("solver-early-end", StringComparison.OrdinalIgnoreCase)) HasRetriedSolverEarlyEndThisTurn = true;

                if (trigger == "empty-plan-retry")

                {

                    CachedDeployResult = null;

                }



                CurrentRecalcGeneration++;

                ReleaseRequestedTime = DateTime.UtcNow;

                RecalcSucceeded = false;

                Watchdog3sFired = false;



                // 1. Reset all solver state: unfreeze route, resume auto, cancel search CTS, cancel deferred, cancel deployment

                ResetSolverStateBeforeSearch();



                // 2. Ensure FullAuto is enabled so new results auto-deploy

                EnsureFullAutoEnabled();



                // 3. Obtain SolverController instance

                var controller = CachedSolverController;

                if (controller == null)

                {

                    var solverType = AccessTools.TypeByName("CombatSolver.SolverController");

                    if (solverType != null)

                    {

                        var instProp = AccessTools.Property(solverType, "Instance");

                        if (instProp != null)

                        {

                            controller = instProp.GetValue(null);

                            CachedSolverController = controller;

                        }

                    }

                }



                if (state == null)

                {

                    var cm = CombatManager.Instance;

                    if (cm != null)

                    {

                        state = cm.DebugOnlyGetState();

                    }

                }



                GD.Print("[AiTeammate][Coord] recalc-requested trigger=" + trigger + " time=" + ReleaseRequestedTime.ToString("HH:mm:ss.fff"));

                if (!InvokeLiveRequestSearch(state, trigger))

                {

                    GD.PrintErr(string.Format("[AiTeammate][Coord] Cannot request recalculation: state={0}; falling back to cached deploy.", state == null));

                    TriggerCachedDeployment();

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] RequestRecalculation exception, fail-open to cached deploy: " + ex);

                TriggerCachedDeployment();

            }

        }



        public static void ReleaseWait(string trigger)

        {

            if (!IsWaiting) return;

            IsWaiting = false;

            HasReleasedThisTurn = true;

            LastReleaseTrigger = trigger;

            GD.Print("[AiTeammate][Coord] Release trigger fired: trigger=" + trigger);



            var cm = CombatManager.Instance;

            var state = cm != null ? cm.DebugOnlyGetState() : null;



            if (!RecalcRequestedThisTurn)

            {

                RequestRecalculation(state, trigger);

            }

            else

            {

                GD.Print("[AiTeammate][Coord] Recalculation already requested this turn, deploying plan for trigger=" + trigger);

                TriggerCachedDeployment();

            }

        }



        public static void TriggerCachedDeployment()

        {

            try

            {

                if (DeployCurrentTurnMethod == null)

                {

                    var solverType = AccessTools.TypeByName("CombatSolver.SolverController");

                    if (solverType != null)

                    {

                        DeployCurrentTurnMethod = AccessTools.Method(solverType, "DeployCurrentTurn");

                    }

                }



                if (DeployCurrentTurnMethod != null)

                {

                    int paramCount = DeployCurrentTurnMethod.GetParameters().Length;

                    object target = DeployCurrentTurnMethod.IsStatic ? null : CachedSolverController;

                    if (paramCount == 0)

                    {

                        IsDeploying = true;

                        DeployStartTime = DateTime.UtcNow;

                        GD.Print("[AiTeammate][Coord] Invoking DeployCurrentTurn (0 params) on cached SolverController.");

                        DeployCurrentTurnMethod.Invoke(target, null);

                    }

                    else if (CachedDeployArgs != null && CachedDeployArgs.Length == paramCount)

                    {

                        IsDeploying = true;

                        DeployStartTime = DateTime.UtcNow;

                        GD.Print("[AiTeammate][Coord] Invoking DeployCurrentTurn (" + paramCount + " params) on cached SolverController with captured args.");

                        var task = DeployCurrentTurnMethod.Invoke(target, CachedDeployArgs) as Task;

                        if (task != null)

                        {

                            MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(task);

                        }

                    }

                    else

                    {

                        GD.PrintErr("[AiTeammate][Coord] Cannot trigger cached deployment: parameter count mismatch (method expects " + paramCount + ", cached args " + (CachedDeployArgs != null ? CachedDeployArgs.Length.ToString() : "null") + ")!");

                        TurnFlowPatches.RestorePlayer1ControlIfActive("wakuu-coord-failopen");

                    }

                }

                else

                {

                    GD.PrintErr("[AiTeammate][Coord] Cannot trigger cached deployment: controller or method is null!");

                    TurnFlowPatches.RestorePlayer1ControlIfActive("wakuu-coord-failopen");

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] TriggerCachedDeployment error: " + ex);

                try

                {

                    TurnFlowPatches.RestorePlayer1ControlIfActive("wakuu-coord-failopen");

                }

                catch { }

            }

        }



        public static void CheckTriggers(string sourceContext)

        {

            if (!IsWaiting) return;



            try

            {

                var cm = CombatManager.Instance;

                if (cm == null) return;

                var state = cm.DebugOnlyGetState();

                if (state == null || state.Players == null || state.Players.Count < 2) return;



                var player1 = state.Players[0];

                var player2 = state.Players[state.Players.Count - 1];



                // Directive #3.9.2: Joint Lethal Push trigger

                string lethalInfo;

                if (CheckJointLethalPush(state, player1, player2, out lethalInfo))

                {

                    ReleaseWait("joint-lethal-push");

                    return;

                }



                // a. Enemy Harmful Status: Any living enemy acquires harmful status (Vulnerable, Weak, Frail, Poison, Strength Down, etc.)

                if (HasEnemyHarmfulStatus(state))

                {

                    ReleaseWait("enemy-debuffed");

                    return;

                }



                // b. Ally Buff / Resource Received: Player 2 receives Strength, Block, Energy, or Draw from teammate

                if (HasP2ReceivedBuffOrResources(state, player2))

                {

                    ReleaseWait("p2-buff-received");

                    return;

                }



                // Dynamic inspection of cheapest team setup card currently in Player 1's hand

                int currentCheapestDebuff = int.MaxValue;

                bool p1HasDebuffInHand = false;

                if (player1 != null && player1.PlayerCombatState != null && player1.PlayerCombatState.Hand != null)

                {

                    var handCards = player1.PlayerCombatState.Hand.Cards;

                    if (handCards != null)

                    {

                        foreach (var c in handCards)

                        {

                            string sType;

                            if (c != null && CheckIsPlayable(c) && CardIsTeamSetup(c, out sType))

                            {

                                p1HasDebuffInHand = true;

                                int cCost = GetCardEnergyCost(c);

                                if (cCost < currentCheapestDebuff)

                                {

                                    currentCheapestDebuff = cCost;

                                }

                            }

                        }

                    }

                }

                if (currentCheapestDebuff != int.MaxValue)

                {

                    CheapestP1DebuffCost = currentCheapestDebuff;

                }



                // c. Player 1 Energy Insufficient: remaining energy < cheapest setup card in P1 hand

                if (player1 != null && player1.PlayerCombatState != null)

                {

                    int currentEnergy = player1.PlayerCombatState.Energy;

                    if (p1HasDebuffInHand && currentCheapestDebuff != int.MaxValue && currentEnergy < currentCheapestDebuff)

                    {

                        ReleaseWait("p1-energy-insufficient");

                        return;

                    }

                }



                // e. Re-evaluation Unmet: re-evaluating 3 wait conditions shows they are no longer simultaneously satisfied

                string reason;

                var decision = EvaluateDecision(cm, state, player1, player2, out reason);

                if (decision != CoordinatorDecision.Wait)

                {

                    ReleaseWait("re-evaluation-unmet");

                    return;

                }

            }

            catch (Exception ex)

            {

                // Fail-open guarantee: on error in trigger inspection, release wait immediately!

                GD.PrintErr("[AiTeammate][Coord] CheckTriggers exception, releasing wait fail-open: " + ex);

                ReleaseWait("re-evaluation-unmet");

            }

        }



        // Directive #5.16 T1: Solver state getters and Mid-deployment / Search watchdog

        public static bool IsSolverDeploying()

        {

            try

            {

                if (IsDeployingGetter == null)

                {

                    var sc = AccessTools.TypeByName("CombatSolver.SolverController");

                    if (sc != null)

                    {

                        IsDeployingGetter = AccessTools.PropertyGetter(sc, "IsDeploying");

                    }

                }

                if (IsDeployingGetter != null)

                {

                    return (bool)IsDeployingGetter.Invoke(null, null);

                }

                if (DeploymentField != null)

                {

                    return DeploymentField.GetValue(null) != null;

                }

            }

            catch { }

            return IsDeploying;

        }



        public static bool IsSolverSearching()

        {

            try

            {

                if (IsSearchingGetter == null)

                {

                    var sc = AccessTools.TypeByName("CombatSolver.SolverController");

                    if (sc != null)

                    {

                        IsSearchingGetter = AccessTools.PropertyGetter(sc, "IsSearching");

                    }

                }

                if (IsSearchingGetter != null)

                {

                    return (bool)IsSearchingGetter.Invoke(null, null);

                }

            }

            catch { }

            return false;

        }



        public static void CheckMidDeploymentAndSearchWatchdog(DateTime now)

        {

            if (IsPreCombatOrLoading || TurnStartTime == DateTime.MinValue) return;



            var cm = CombatManager.Instance;

            var state = cm != null ? cm.DebugOnlyGetState() : null;

            if (state == null) return;



            Player player2 = null;

            if (state.Players != null && state.Players.Count > 1)

            {

                player2 = state.Players[state.Players.Count - 1];

            }

            if (player2 == null || player2.Creature == null || !player2.Creature.IsAlive)

            {

                return;

            }



            // 1. Mid-deployment watchdog

            bool isDeploying = IsSolverDeploying() || IsDeploying;

            if (isDeploying)

            {

                if (MidDeployStartTime == DateTime.MinValue)

                {

                    MidDeployStartTime = now;

                    MidDeployHangTimeoutTriggered = false;

                    MidDeployHangCriticalTriggered = false;

                }



                double elapsed = (now - MidDeployStartTime).TotalSeconds;



                // > 20s check: cancel deployment and trigger recalc

                if (elapsed > 20.0 && !MidDeployHangTimeoutTriggered)

                {

                    MidDeployHangTimeoutTriggered = true;

                    GD.PrintErr(string.Format("[AiTeammate][Coord] deploy-hang-timeout: deployment hung for {0:F1}s, cancelling deployment and triggering recalc", elapsed));

                    try

                    {

                        if (CancelDeploymentMethod != null)

                        {

                            CancelDeploymentMethod.Invoke(null, null);

                        }

                        IsDeploying = false;

                        CleanHangingDeployment();

                    }

                    catch (Exception ex)

                    {

                        GD.PrintErr("[AiTeammate][Coord] deploy-hang-timeout cancel exception: " + ex);

                    }



                    RequestRecalculation(state, "deploy-hang-timeout");

                }



                // > 35s check: escalate to CRITICAL force-end-turn path

                if (elapsed > 35.0 && !MidDeployHangCriticalTriggered)

                {

                    MidDeployHangCriticalTriggered = true;

                    GD.PrintErr("[AiTeammate][Coord] CRITICAL deploy-hang: >35s elapsed without deployment completion, forcing EndTurn for Player 2");

                    try

                    {

                        ForceEndTurnForPlayer2(cm, state, player2);

                    }

                    catch (Exception ex)

                    {

                        GD.PrintErr("[AiTeammate][Coord] CRITICAL deploy-hang force-end-turn exception: " + ex);

                    }

                }

            }

            else

            {

                MidDeployStartTime = DateTime.MinValue;

                MidDeployHangTimeoutTriggered = false;

                MidDeployHangCriticalTriggered = false;

            }



            // 2. Search alive heartbeat

            bool isSearching = IsSolverSearching();

            if (isSearching)

            {

                ArmIndependentSearchHeartbeat("process-tick-search-detected");

                if (SearchAliveStartTime == DateTime.MinValue)

                {

                    SearchAliveStartTime = now;

                    LastSearchHeartbeatTime = now;

                }



                if ((now - LastSearchHeartbeatTime).TotalSeconds >= 5.0)

                {

                    LastSearchHeartbeatTime = now;

                    double searchElapsed = (now - SearchAliveStartTime).TotalSeconds;

                    GD.Print(string.Format("[AiTeammate][Coord] search-alive: elapsed={0:F1}s search thread alive", searchElapsed));
                    RecordIndependentSearchHeartbeat(now, "process-tick");

                }

            }

            else

            {

                SearchAliveStartTime = DateTime.MinValue;

                LastSearchHeartbeatTime = DateTime.MinValue;
                DisarmIndependentSearchHeartbeat("search-inactive");

            }

        }



        public static void CheckSolverStallWatchdog(DateTime now)
        {
            if (IsPreCombatOrLoading || TurnStartTime == DateTime.MinValue) return;
            if (HasCompletedDeploymentThisTurn) return;
            if (IsDeploying) return;

            // Directive #5.34 T0.4: 放行手动流程
            if (!IsFullAutoEnabled() || !FlowCoordinator.AutoCardRewardEnabled)
            {
                return;
            }

            var cm = CombatManager.Instance;
            var state = cm != null ? cm.DebugOnlyGetState() : null;
            if (state == null) return;

            Player player2 = null;
            if (state.Players != null && state.Players.Count > 1)
            {
                player2 = state.Players[state.Players.Count - 1];
            }
            if (player2 == null || player2.Creature == null || !player2.Creature.IsAlive)
            {
                return;
            }

            try
            {
                bool anyHostileAlive = false;
                var creatures = state.Creatures;
                if (creatures != null)
                {
                    foreach (var c in creatures)
                    {
                        if (c != null && c.IsMonster && c.IsAlive)
                        {
                            anyHostileAlive = true;
                            break;
                        }
                    }
                }
                if (!anyHostileAlive) return;
            }
            catch { }

            // Directive #5.39 Task 3: Level 1 - > 12s elapsed OR >= 2.5s with solver already dead/stopped
            if (!StallRecalcTriggered)
            {
                bool timeStall = (now - TurnStartTime).TotalSeconds > 12.0;
                bool fastStall = (now - TurnStartTime).TotalSeconds >= 2.5 && !IsSolverSearching() && !IsSolverDeploying() && !P2DeployHookObservedThisTurn;
                if (timeStall || fastStall)
                {
                    StallRecalcTriggered = true;
                    StallRecalcTime = now;
                    string reason = fastStall ? "solver-search-aborted" : "solver-search-stall";
                    GD.Print(string.Format("[AiTeammate][Coord] {0}: elapsed={1:F1}s without deployment, kicking recalculation (once-per-turn)", reason, (now - TurnStartTime).TotalSeconds));
                    KickSolverRecalculation(state, reason);
                }
                return;
            }

            // Directive #5.39 Task 3: Level 2 - >= 8s after stall recalc OR >= 1.5s with solver already stopped
            if (!StallForceEndTurnTriggered)
            {
                bool timeStall2 = (now - StallRecalcTime).TotalSeconds >= 8.0;
                bool fastStall2 = (now - StallRecalcTime).TotalSeconds >= 1.5 && !IsSolverSearching() && !IsSolverDeploying();
                if (timeStall2 || fastStall2)
                {
                    StallForceEndTurnTriggered = true;
                    string reason2 = fastStall2 ? "solver-recalc-aborted" : "solver-recalc-stall";
                    GD.Print(string.Format("[AiTeammate][Coord] {0}: elapsed={1:F1}s after recalc without deployment, attempting fallback", reason2, (now - StallRecalcTime).TotalSeconds));
                    if (TryDeployCheckpointOrInterimPlan(cm, state, player2))
                    {
                        GD.Print("[AiTeammate][Coord] solver-search-stall: cached-plan deployed successfully");
                        return;
                    }

                    bool hasAttack;
                    if (P2HasUsableCard(player2, out hasAttack))
                    {
                        TryFallbackGreedy(player2);
                    }

                    if (cm != null && !cm.IsPlayerReadyToEndTurn(player2))
                    {
                        if (!EnqueueP2EndTurn(player2) || !cm.IsPlayerReadyToEndTurn(player2))
                        {
                            GD.PrintErr("[AiTeammate][Coord] CRITICAL solver-dead: fallback did not end turn, force-ending P2 turn");
                            TryInvokeResetCore();
                            ForceEndTurnForPlayer2(cm, state, player2);
                        }
                    }
                }
            }
        }

        public static object GetMemberValue(object obj, string name)

        {

            if (obj == null || string.IsNullOrEmpty(name)) return null;

            try

            {

                var t = obj.GetType();

                var prop = AccessTools.Property(t, name);

                if (prop != null) return prop.GetValue(obj, null);

                var field = AccessTools.Field(t, name) ?? AccessTools.Field(t, "<" + name + ">k__BackingField");

                if (field != null) return field.GetValue(obj);

            }

            catch { }

            return null;

        }



        public static int GetSolverResultActionCount(object solverResult)

        {

            if (solverResult == null) return 0;

            try

            {

                object actionsObj = null;

                var bestNode = GetMemberValue(solverResult, "BestNode");

                if (bestNode != null)

                {

                    actionsObj = GetMemberValue(bestNode, "Actions");

                }

                if (actionsObj == null)

                {

                    actionsObj = GetMemberValue(solverResult, "Actions") ?? GetMemberValue(solverResult, "Plan");

                }

                var col = actionsObj as System.Collections.ICollection;

                if (col != null) return col.Count;

                var enumerable = actionsObj as System.Collections.IEnumerable;

                if (enumerable != null)

                {

                    int c = 0;

                    foreach (var item in enumerable) c++;

                    return c;

                }

            }

            catch { }

            return 0;

        }



        public static bool TryDeployCheckpointOrInterimPlan(CombatManager cm, CombatState state, Player player2)

        {

            try

            {

                var solverType = AccessTools.TypeByName("CombatSolver.SolverController");

                if (solverType == null) return false;



                object candidateResult = null;

                string candidateSource = null;

                int actionCount = 0;



                // 1. Inspect _combat.LatestResult and _combat.ContinuationSource

                if (CombatField == null)

                {

                    CombatField = AccessTools.Field(solverType, "_combat");

                }

                object combatObj = CombatField != null ? CombatField.GetValue(null) : null;

                if (combatObj != null)

                {

                    var latest = GetMemberValue(combatObj, "LatestResult");

                    int cnt = GetSolverResultActionCount(latest);

                    if (cnt > 0)

                    {

                        candidateResult = latest;

                        candidateSource = "combat.LatestResult";

                        actionCount = cnt;

                    }

                    else

                    {

                        var cont = GetMemberValue(combatObj, "ContinuationSource");

                        cnt = GetSolverResultActionCount(cont);

                        if (cnt > 0)

                        {

                            candidateResult = cont;

                            candidateSource = "combat.ContinuationSource";

                            actionCount = cnt;

                        }

                    }

                }



                // 2. Inspect _search.Interaction.Progress.CurrentBestResult and _search.Interaction.StoppedResult

                if (candidateResult == null)

                {

                    var searchField = AccessTools.Field(solverType, "_search");

                    object searchObj = searchField != null ? searchField.GetValue(null) : null;

                    if (searchObj != null)

                    {

                        var interactionObj = GetMemberValue(searchObj, "Interaction");

                        if (interactionObj != null)

                        {

                            var progressObj = GetMemberValue(interactionObj, "Progress");

                            if (progressObj != null)

                            {

                                var curBest = GetMemberValue(progressObj, "CurrentBestResult");

                                int cnt = GetSolverResultActionCount(curBest);

                                if (cnt > 0)

                                {

                                    candidateResult = curBest;

                                    candidateSource = "search.CurrentBestResult";

                                    actionCount = cnt;

                                }

                            }

                            if (candidateResult == null)

                            {

                                var stopped = GetMemberValue(interactionObj, "StoppedResult");

                                int cnt = GetSolverResultActionCount(stopped);

                                if (cnt > 0)

                                {

                                    candidateResult = stopped;

                                    candidateSource = "search.StoppedResult";

                                    actionCount = cnt;

                                }

                            }

                        }

                    }

                }



                // 3. Inspect LastCompletedResultForTesting on SolverController

                if (candidateResult == null)

                {

                    var lastTestingField = AccessTools.Field(solverType, "<LastCompletedResultForTesting>k__BackingField")

                        ?? AccessTools.Field(solverType, "_lastCompletedResultForTesting");

                    if (lastTestingField != null)

                    {

                        var lastTesting = lastTestingField.GetValue(null);

                        int cnt = GetSolverResultActionCount(lastTesting);

                        if (cnt > 0)

                        {

                            candidateResult = lastTesting;

                            candidateSource = "LastCompletedResultForTesting";

                            actionCount = cnt;

                        }

                    }

                }



                if (candidateResult == null || actionCount <= 0)

                {

                    return false;

                }



                // Find StartDeployment or StartFullAutoDeployment

                MethodInfo deployMethod = AccessTools.Method(solverType, "StartDeployment")

                    ?? AccessTools.Method(solverType, "StartFullAutoDeployment");

                if (deployMethod == null)

                {

                    return false;

                }



                var controller = CachedSolverController;

                var pars = deployMethod.GetParameters();

                object[] invokeArgs = new object[pars.Length];

                for (int i = 0; i < pars.Length; i++)

                {

                    var pType = pars[i].ParameterType;

                    if (pType.IsAssignableFrom(typeof(CombatState)) || pType.Name.Contains("CombatState"))

                    {

                        invokeArgs[i] = state;

                    }

                    else if (pType.IsAssignableFrom(candidateResult.GetType()) || pType.Name.Contains("SolverResult"))

                    {

                        invokeArgs[i] = candidateResult;

                    }

                    else if (typeof(Godot.Node).IsAssignableFrom(pType))

                    {

                        // Same type-identity fix as the kick path: take the instance through the

                        // parameter's own type (CombatSolver may carry a second sts2 type copy).

                        object hostInst = null;

                        try

                        {

                            var instProp = pType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);

                            if (instProp != null) hostInst = instProp.GetValue(null, null);

                        }

                        catch { }

                        if (hostInst == null)

                        {

                            try { hostInst = MegaCrit.Sts2.Core.Nodes.NGame.Instance; } catch { }

                        }

                        invokeArgs[i] = hostInst ?? (object)cm;

                    }

                    else

                    {

                        invokeArgs[i] = null;

                    }

                }



                GD.Print("[AiTeammate][Coord] CRITICAL recovery-tier=checkpoint-deploy source=" + candidateSource + " actions=" + actionCount);

                HasReleasedThisTurn = true;

                LastReleaseTrigger = "checkpoint-deploy";

                IsDeploying = true;

                DeployStartTime = DateTime.UtcNow;

                Watchdog3sFired = false;



                if (player2 != null)

                {

                    TurnFlowPatches.SwitchControlToPlayer(player2.NetId, "wakuu-checkpoint-deploy", true);

                    TurnFlowPatches.SetPlayerActionsDisabled(cm, true);

                }



                object resTaskObj = deployMethod.Invoke(deployMethod.IsStatic ? null : controller, invokeArgs);

                var task = resTaskObj as Task;

                if (task != null)

                {

                    MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(task);

                }

                return true;

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] TryDeployCheckpointOrInterimPlan error: " + ex);

                return false;

            }

        }



        public static void ForceEndTurnForPlayer2(CombatManager cm, CombatState state, Player player2)
        {
            try
            {
                if (!IsInCombat()) { DisarmIndependentP2Lifeline("force-end-not-in-combat"); return; }
                DisarmIndependentP2Lifeline("force-end-path");
                P2TurnEndReason = "force-end";
                LogTurnSummary("ForceEndTurnForPlayer2");

                HasCompletedDeploymentThisTurn = true;

                // R2-4: a leftover P2 choice overlay (loot/"战利品" reward, card grid) must never
                // survive a forced end turn — it soft-locked P1's UI on 10-26.
                FlowCoordinator.ResolveOrClearP2ChoiceOverlays("force-end-turn");

                bool needControlSwitch = !FixedP1CombatControl;
                if (needControlSwitch)
                {
                    GD.Print("[AiTeammate][Coord] ForceEndTurnForPlayer2: switching control to P2 (" + player2.NetId + ") with UI suppressed");
                    TurnFlowPatches.SwitchControlToPlayer(player2.NetId, "wakuu-stall-endturn", true);
                }

                try
                {
                    using (EnterP2ActionSenderScope("ForceEndTurnForPlayer2"))
                    {
                        GD.Print("[AiTeammate][Coord] ForceEndTurnForPlayer2: invoking PlayerCmd.EndTurn for P2");
                        PlayerCmd.EndTurn(player2, false, null);
                    }
                }
                catch (Exception exCmd)
                {
                    GD.PrintErr("[AiTeammate][Coord] ForceEndTurnForPlayer2 PlayerCmd.EndTurn failed: " + exCmd);
                    if (cm != null)
                    {
                        try
                        {
                            using (EnterP2ActionSenderScope("ForceEndTurnForPlayer2-fallback"))
                            {
                                GD.Print("[AiTeammate][Coord] ForceEndTurnForPlayer2 fallback: SetReadyToEndTurn");
                                cm.SetReadyToEndTurn(player2, true, null);
                            }
                        }
                        catch (Exception exCm)
                        {
                            GD.PrintErr("[AiTeammate][Coord] SetReadyToEndTurn fallback failed: " + exCm);
                        }
                    }
                }
                finally
                {
                    if (needControlSwitch)
                    {
                        GD.Print("[AiTeammate][Coord] ForceEndTurnForPlayer2: restoring Player 1 control");
                        TurnFlowPatches.RestorePlayer1ControlIfActive("wakuu-stall-endturn");
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Coord] ForceEndTurnForPlayer2 error: " + ex);
            }
        }

        public static bool HasPlayableCards(Player player)

        {

            if (player == null) return false;

            try

            {

                var handPile = PileType.Hand.GetPile(player);

                if (handPile == null || handPile.Cards == null) return false;

                var cards = handPile.Cards;

                for (int i = 0; i < cards.Count; i++)

                {

                    var c = cards[i];

                    if (c != null && CheckIsPlayable(c) && IsAffordable(c, player))

                    {

                        return true;

                    }

                }

            }

            catch { }

            return false;

        }



        public static bool IsMonsterAttacking(Creature c)

        {

            if (c == null || !c.IsMonster || !c.IsAlive) return false;

            try

            {

                var monster = c.Monster;

                if (monster == null) return false;

                var nextMove = monster.NextMove;

                if (nextMove == null || nextMove.Intents == null) return false;



                foreach (var intent in nextMove.Intents)

                {

                    if (intent == null) continue;

                    string typeName = intent.GetType().Name;

                    if (typeName.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0 ||

                        typeName.IndexOf("DeathBlow", StringComparison.OrdinalIgnoreCase) >= 0)

                    {

                        return true;

                    }

                    var iType = intent.IntentType.ToString();

                    if (iType.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0 ||

                        iType.IndexOf("DeathBlow", StringComparison.OrdinalIgnoreCase) >= 0)

                    {

                        return true;

                    }

                }

            }

            catch { }

            return false;

        }



        public static string GetMonsterIntentSignature(Creature c)

        {

            if (c == null || !c.IsMonster || !c.IsAlive) return "dead";

            try

            {

                var monster = c.Monster;

                if (monster == null) return "none";

                var nextMove = monster.NextMove;

                if (nextMove == null) return "no-move";

                var stateId = nextMove.StateId ?? "";

                var intentsList = new List<string>();

                if (nextMove.Intents != null)

                {

                    foreach (var i in nextMove.Intents)

                    {

                        if (i != null) intentsList.Add(i.IntentType.ToString());

                    }

                }

                return stateId + ":" + string.Join("+", intentsList.ToArray());

            }

            catch

            {

                return "err";

            }

        }



        public static void SnapshotEnemyIntents()

        {

            try

            {

                EnemyTurnStartHp.Clear();

                EnemyTurnStartIntents.Clear();

                int attackCount = 0;

                int nonAttackCount = 0;



                var cm = CombatManager.Instance;

                if (cm == null) return;

                var state = cm.DebugOnlyGetState();

                if (state == null) return;



                var creatures = state.Creatures;

                if (creatures == null) return;



                for (int i = 0; i < creatures.Count; i++)

                {

                    var c = creatures[i];

                    if (c != null && c.IsMonster && c.IsAlive && c.CombatId.HasValue)

                    {

                        uint cid = c.CombatId.Value;

                        EnemyTurnStartHp[cid] = c.CurrentHp;

                        bool isAttack = IsMonsterAttacking(c);

                        string sig = GetMonsterIntentSignature(c);

                        EnemyTurnStartIntents[cid] = sig;



                        if (isAttack) attackCount++;

                        else nonAttackCount++;

                    }

                }



                TurnStartIntentsSummary = string.Format("intents=[attack×{0}, non-attack×{1}]", attackCount, nonAttackCount);

                GD.Print("[AiTeammate][Coord] " + TurnStartIntentsSummary);

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] SnapshotEnemyIntents error: " + ex);

                TurnStartIntentsSummary = "intents=[attack×0, non-attack×0]";

            }

        }



        public static void CheckMidturnReplan(string explicitTrigger)

        {

            try

            {

                // Directive #4.0: Midturn replan switch gate

                if (!MidturnEnabled) return;

                if (!IsInCombat()) return;

                if (!HoldActive || HasReleasedHoldThisTurn) return;



                var now = DateTime.UtcNow;

                if ((now - LastMidturnReplanTime).TotalSeconds < 3.0) return;

                if (MidturnReplanCount >= 2) return;



                var cm = CombatManager.Instance;

                if (cm == null) return;

                var state = cm.DebugOnlyGetState();

                if (state == null || state.Players == null || state.Players.Count < 2) return;



                var player2 = state.Players[state.Players.Count - 1];

                if (player2 == null || player2.Creature == null || !player2.Creature.IsAlive) return;



                if (!HasPlayableCards(player2))

                {

                    return;

                }



                string trigger = explicitTrigger;

                if (string.IsNullOrEmpty(trigger))

                {

                    var creatures = state.Creatures;

                    if (creatures != null)

                    {

                        foreach (var kvp in EnemyTurnStartHp)

                        {

                            var id = kvp.Key;

                            int startHp = kvp.Value;

                            bool stillAlive = false;

                            for (int i = 0; i < creatures.Count; i++)

                            {

                                var c = creatures[i];

                                if (c != null && c.CombatId.HasValue && c.CombatId.Value == id && c.IsAlive)

                                {

                                    stillAlive = true;

                                    if (startHp - c.CurrentHp >= 10)

                                    {

                                        trigger = "enemy-hp-dropped: id=" + id + " delta=" + (startHp - c.CurrentHp);

                                        break;

                                    }

                                    string curSig = GetMonsterIntentSignature(c);

                                    string snapSig;

                                    if (EnemyTurnStartIntents.TryGetValue(id, out snapSig) && curSig != snapSig)

                                    {

                                        trigger = "enemy-intent-changed: id=" + id + " from=" + snapSig + " to=" + curSig;

                                        break;

                                    }

                                }

                            }

                            if (trigger != null) break;

                            if (!stillAlive)

                            {

                                trigger = "enemy-died: id=" + id;

                                break;

                            }

                        }

                    }

                }



                if (string.IsNullOrEmpty(trigger)) return;



                MidturnReplanCount++;

                LastMidturnReplanTime = now;

                GD.Print("[AiTeammate][Coord] midturn-replan trigger=" + trigger);



                RequestRecalculation(state, "midturn-replan: " + trigger);

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] CheckMidturnReplan error: " + ex);

            }

        }



        public static void CheckHoldRelease(DateTime now)

        {

            try

            {

                if (!HoldActive || HasReleasedHoldThisTurn) return;



                var cm = CombatManager.Instance;

                if (cm == null) return;

                var state = cm.DebugOnlyGetState();

                if (state == null || state.Players == null || state.Players.Count < 2) return;



                var player1 = state.Players[0];

                var player2 = state.Players[state.Players.Count - 1];

                if (player1 == null || player2 == null) return;



                // Condition 1: P1 ended turn

                if (cm.IsPlayerReadyToEndTurn(player1))

                {

                    ReleaseHold("p1-end-turn");

                    return;

                }



                // Condition 2: 8.0s timeout since hold started

                if (HoldStartTime != DateTime.MinValue && (now - HoldStartTime).TotalSeconds >= 8.0)

                {

                    ReleaseHold("timeout");

                    return;

                }



                // Condition 3: P2 has no payable playable cards AND no recalculation in flight

                bool isRecalcInFlight = (RecalcRequestedThisTurn && !RecalcSucceeded);

                if (!isRecalcInFlight && !HasPlayableCards(player2))

                {

                    ReleaseHold("no-playable-cards");

                    return;

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] CheckHoldRelease error: " + ex);

                ReleaseHold("fail-open-error");

            }

        }



        public static void ReleaseHold(string reason)

        {

            try

            {

                if (HasReleasedHoldThisTurn) return;

                HasReleasedHoldThisTurn = true;

                HoldActive = false;

                GD.Print("[AiTeammate][Coord] hold-release reason=" + reason);



                var cm = CombatManager.Instance;

                if (cm == null) return;

                var state = cm.DebugOnlyGetState();

                if (state == null || state.Players == null || state.Players.Count < 2) return;



                var player2 = state.Players[state.Players.Count - 1];

                if (player2 == null || player2.Creature == null || !player2.Creature.IsAlive) return;



                if (PendingP2EndTurn || !cm.IsPlayerReadyToEndTurn(player2))

                {

                    ModEntry.SuppressControlSwitchUi = true;

                    try

                    {

                        GD.Print("[AiTeammate][Coord] Dispatching P2 end turn after hold release.");

                        PlayerCmd.EndTurn(player2, false, null);

                    }

                    catch (Exception exCmd)

                    {

                        GD.PrintErr("[AiTeammate][Coord] PlayerCmd.EndTurn on hold release failed: " + exCmd);

                        try { cm.SetReadyToEndTurn(player2, true, null); } catch { }

                    }

                    finally

                    {

                        ModEntry.SuppressControlSwitchUi = false;

                    }

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] ReleaseHold fail-open exception: " + ex);

            }

        }



        public static void LogTurnSummary(string source)

        {

            try

            {

                var cm = CombatManager.Instance;

                var state = cm != null ? cm.DebugOnlyGetState() : null;

                int turn = TurnNumber;

                int p2EnergySpent = 0;

                int p2HandRemaining = 0;

                int p2PayableAttacksLeft = 0;



                if (state != null)

                {

                    turn = state.RoundNumber;

                    if (state.Players != null && state.Players.Count >= 2)

                    {

                        var p2 = state.Players[state.Players.Count - 1];

                        if (p2 != null && p2.PlayerCombatState != null)

                        {

                            int endEnergy = p2.PlayerCombatState.Energy;

                            if (P2TurnStartEnergy >= 0)

                            {

                                p2EnergySpent = Math.Max(0, P2TurnStartEnergy - endEnergy);

                            }

                            var handPile = PileType.Hand.GetPile(p2);

                            if (handPile != null && handPile.Cards != null)

                            {

                                p2HandRemaining = handPile.Cards.Count;

                                foreach (var c in handPile.Cards)

                                {

                                    if (c != null && IsAttack(c) && CheckIsPlayable(c) && IsAffordable(c, p2))

                                    {

                                        p2PayableAttacksLeft++;

                                    }

                                }

                            }

                        }

                    }

                }



                string summary = string.Format("[AiTeammate][Coord] turn-summary turn={0} p2_energy_spent={1} p2_hand_remaining={2} p2_payable_attacks_left={3}", turn, p2EnergySpent, p2HandRemaining, p2PayableAttacksLeft);

                if (!string.IsNullOrEmpty(TurnStartIntentsSummary))

                {

                    summary += " " + TurnStartIntentsSummary;

                }

                if (p2EnergySpent == 0 && p2HandRemaining > 0)

                {

                    summary += " suspicious-empty-turn";

                }

                if (p2EnergySpent == 0 && p2PayableAttacksLeft > 0)

                {

                    summary += " solver-held-attacks";

                }

                GD.Print(summary);

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] LogTurnSummary error: " + ex);

            }

        }

        public static string DescribeP2Hand(Player p2)
        {
            try
            {
                var hand = p2 != null ? PileType.Hand.GetPile(p2) : null;
                if (hand == null || hand.Cards == null) return "[]";
                var ids = new List<string>();
                foreach (var card in hand.Cards) if (card != null) ids.Add(card.Id.ToString());
                return "[" + string.Join(",", ids.ToArray()) + "]";
            }
            catch { return "[error]"; }
        }

        public static void BeginP2TurnTelemetry(CombatState state, Player p2)
        {
            try
            {
                if (state == null || p2 == null) return;
                int turn = state.RoundNumber;
                if (P2TelemetryTurn == turn && P2TurnStartEnergy >= 0) return;
                P2TelemetryTurn = turn;
                P2PlayedCardsThisTurn = 0;
                P2PlanLengthThisTurn = -1;
                P2SearchStatusThisTurn = "unknown";
                P2SearchNodesThisTurn = -1;
                P2PlanHasAttackThisTurn = false;
                P2PlanHasCardActionThisTurn = false;
                HasRetriedEmptyPlanThisTurn = false;
                HasRetriedSolverEarlyEndThisTurn = false;
                SolverEarlyEndReportedThisTurn = false;
                P2FallbackGreedyUsedThisTurn = false;
                P2TurnEndReason = "normal";
                P2SolverActionCountThisTurn = -1;
                P2FullAutoActionCountThisTurn = -1;
                P2DeployStartActionCountThisTurn = -1;
                P2DeployHookObservedThisTurn = false;
                P2EndTurnEnqueuedThisTurn = false;
                P2ZeroActionRetryUsedThisTurn = false;
                P2SummaryFinalizedThisTurn = false;
                P2FallbackActionPendingThisTurn = false;
                P2FallbackActionEnqueuedThisTurn = false;
                P2FallbackActionExecutedThisTurn = false;
                P2FallbackEndTurnQueuedThisTurn = false;
                P2FallbackEndTurnBlockUsedThisTurn = false;
                P2ZeroActionProtectionFinalizedThisTurn = false;
                P2DeployHookMissLoggedThisTurn = false;
                P2TurnStartEnergy = p2.PlayerCombatState != null ? p2.PlayerCombatState.Energy : 0;
                P2TurnStartHandCount = GetPlayerHandCardCount(p2);
                object phase = GetMemberValue(state, "Phase") ?? GetMemberValue(state, "CombatPhase");
                object side = GetMemberValue(state, "Side") ?? GetMemberValue(state, "ActiveSide");
                GD.Print(string.Format("[AiTeammate][Coord] p2-turn-start turn={0} owner=P2({1}) phase={2} side={3} hand-count={4} hand-ids={5} energy={6} full-auto={7}",
                    turn, p2.NetId, phase ?? "unknown", side ?? "unknown", P2TurnStartHandCount, DescribeP2Hand(p2), P2TurnStartEnergy, IsFullAutoEnabled()));

                // Directive #5.34 T0.1: Comprehensive P2 Turn Telemetry
                bool fullAutoVal = IsFullAutoEnabled();
                string uiToggle = GetSolverUiToggleStatus();
                string sessionStat = GetSolverSessionStatus();
                string searchStat = GetSolverSearchStatus();
                string deployStat = GetSolverDeploymentStatus();
                string gen = FlowCoordinator.CurrentRunGeneration();
                int p1Q = GetActionQueueCount(GetPlayer1NetId());
                int p2Q = GetActionQueueCount(GetPlayer2NetId());
                int depActions = P2DeployStartActionCountThisTurn >= 0 ? P2DeployStartActionCountThisTurn : (P2FullAutoActionCountThisTurn >= 0 ? P2FullAutoActionCountThisTurn : P2SolverActionCountThisTurn);

                GD.Print(string.Format("[AiTeammate][Coord] p2-turn-telemetry turn={0} gen={1} full-auto={2} ui-toggle={3} session={4} search={5} deploy={6} p1-queue={7} p2-queue={8} deploy-actions={9}",
                    turn, gen, fullAutoVal, uiToggle, sessionStat, searchStat, deployStat, p1Q, p2Q, depActions));
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][Coord] p2-turn-start telemetry failed: " + ex.Message); }
        }

        public static void RecordDeploymentEntryTelemetry(string entry, object[] args, object result, CombatState state, object solverInstance = null)
        {
            try
            {
                if (state == null)
                {
                    var cm = CombatManager.Instance;
                    state = cm != null ? cm.DebugOnlyGetState() : null;
                }
                Player p2 = state != null && state.Players != null && state.Players.Count > 1 ? state.Players[state.Players.Count - 1] : null;
                int count = result != null ? GetPlanActionCount(new object[] { result }) : GetPlanActionCount(args);
                P2SolverActionCountThisTurn = count;
                if (string.Equals(entry, "full-auto", StringComparison.OrdinalIgnoreCase)) P2FullAutoActionCountThisTurn = count;
                if (string.Equals(entry, "start-deploy", StringComparison.OrdinalIgnoreCase)) P2DeployStartActionCountThisTurn = count;
                P2DeployHookObservedThisTurn = true;
                UpdateSearchTelemetry("deploy-entry-" + entry);
                object phase = GetMemberValue(state, "Phase") ?? GetMemberValue(state, "CombatPhase");
                object side = GetMemberValue(state, "Side") ?? GetMemberValue(state, "ActiveSide");
                GD.Print(string.Format("[AiTeammate][Coord] p2-deploy-entry entry={0} turn={1} owner=P2({2}) phase={3} side={4} hand-count={5} hand-ids={6} energy={7} full-auto={8} solver-action-count={9} full-auto-received-action-count={10} deploy-start-action-count={11} played-actions={12} end-turn-enqueued={13} instance-type={14} instance-assembly={15} instance-alc={16} result-type={17} arg-types={18}",
                    entry, TurnNumber, p2 != null ? p2.NetId.ToString() : "unknown", phase ?? "unknown", side ?? "unknown",
                    p2 != null ? GetPlayerHandCardCount(p2) : 0, DescribeP2Hand(p2), p2 != null && p2.PlayerCombatState != null ? p2.PlayerCombatState.Energy : 0,
                    IsFullAutoEnabled(), P2SolverActionCountThisTurn, P2FullAutoActionCountThisTurn, P2DeployStartActionCountThisTurn, P2PlayedCardsByTurn.ContainsKey(P2TelemetryTurn) ? P2PlayedCardsByTurn[P2TelemetryTurn] : 0, P2EndTurnEnqueuedThisTurn,
                    solverInstance != null ? solverInstance.GetType().AssemblyQualifiedName : "unknown",
                    solverInstance != null ? solverInstance.GetType().Assembly.FullName : "unknown",
                    DescribeAssemblyLoadContext(solverInstance != null ? solverInstance.GetType().Assembly : null),
                    result != null ? result.GetType().AssemblyQualifiedName : "unknown", DescribeArgTypes(args)));
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][Coord] p2-deploy-entry telemetry failed: " + ex.Message); }
        }

        private static string DescribeAssemblyLoadContext(Assembly assembly)
        {
            if (assembly == null) return "unknown";
            try
            {
                Type alcType = Type.GetType("System.Runtime.Loader.AssemblyLoadContext, System.Runtime.Loader");
                MethodInfo getContext = alcType != null ? alcType.GetMethod("GetLoadContext", new Type[] { typeof(Assembly) }) : null;
                object context = getContext != null ? getContext.Invoke(null, new object[] { assembly }) : null;
                return context != null ? context.ToString() : (assembly.IsDynamic ? "dynamic" : "default-or-unavailable");
            }
            catch { return assembly.IsDynamic ? "dynamic" : "unavailable"; }
        }

        public static string DescribeArgTypes(object[] args)
        {
            try
            {
                if (args == null) return "[]";
                var types = new List<string>();
                foreach (object arg in args) types.Add(arg == null ? "null" : arg.GetType().AssemblyQualifiedName);
                return "[" + string.Join("|", types.ToArray()) + "]";
            }
            catch { return "[error]"; }
        }

        public static bool P2HasUsableCard(Player p2, out bool hasAttack)
        {
            hasAttack = false;
            try
            {
                if (p2 == null || p2.PlayerCombatState == null || p2.PlayerCombatState.Energy <= 0) return false;
                var hand = PileType.Hand.GetPile(p2);
                if (hand == null || hand.Cards == null) return false;
                foreach (var card in hand.Cards)
                {
                    if (card == null || !CheckIsPlayable(card) || !IsAffordable(card, p2)) continue;
                    if (IsAttack(card)) hasAttack = true;
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static void LogP2DeployHookMissed(string reason)
        {
            GD.PrintErr(string.Format("[AiTeammate][Coord] p2-deploy-hook-missed turn={0} reason={1} full-auto={2} solver-action-count={3} full-auto-action-count={4} deploy-start-action-count={5}",
                TurnNumber, reason ?? "unknown", IsFullAutoEnabled(), P2SolverActionCountThisTurn, P2FullAutoActionCountThisTurn, P2DeployStartActionCountThisTurn));
            // Directive #5.34 T0.2: Required pair output on deploy-hook-missed
            GD.PrintErr(string.Format("[AiTeammate][Coord] solver-state-mismatch turn={0} reason={1} full-auto={2}", TurnNumber, reason ?? "unknown", IsFullAutoEnabled()));
            GD.PrintErr(string.Format("[AiTeammate][Coord] deployment-hook-missed turn={0} reason={1}", TurnNumber, reason ?? "unknown"));

            // Directive #5.34 T0.2: Do not wait silently; kick single recovery recalculation if not yet attempted
            if (!HasRetriedSolverEarlyEndThisTurn)
            {
                HasRetriedSolverEarlyEndThisTurn = true;
                CombatState state = CombatManager.Instance != null ? CombatManager.Instance.DebugOnlyGetState() : null;
                RequestRecalculation(state, "deploy-hook-missed-recovery");
            }
        }

        public static void LogP2TurnSummary(string reason)
        {
            try
            {
                if (P2SummaryFinalizedThisTurn || P2SummaryLoggedTurn == TurnNumber) return;
                var cm = CombatManager.Instance;
                var state = cm != null ? cm.DebugOnlyGetState() : null;
                Player p2 = state != null && state.Players != null && state.Players.Count > 1 ? state.Players[state.Players.Count - 1] : null;
                int energy = p2 != null && p2.PlayerCombatState != null ? p2.PlayerCombatState.Energy : 0;
                int hand = p2 != null ? GetPlayerHandCardCount(p2) : 0;
                int summaryTurn = P2TelemetryTurn > 0 ? P2TelemetryTurn : TurnNumber;
                int playedSnapshot = P2PlayedCardsByTurn.ContainsKey(summaryTurn) ? P2PlayedCardsByTurn[summaryTurn] : P2PlayedCardsThisTurn;
                GD.Print(string.Format("[AiTeammate][Coord] p2-turn-summary turn={0} played={1} end-reason={2} hand={3} energy={4} plan-len={5} search-status={6} search-nodes={7} protection-finalized={8}", summaryTurn, playedSnapshot, reason ?? "normal", hand, energy, P2PlanLengthThisTurn, P2SearchStatusThisTurn, P2SearchNodesThisTurn, P2ZeroActionProtectionFinalizedThisTurn));
                P2SummaryLoggedTurn = TurnNumber;
                P2SummaryFinalizedThisTurn = true;
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][Coord] p2-turn-summary failed: " + ex.Message); }
        }

        public static bool TryFallbackGreedy(Player p2)
        {
            try
            {
                if (p2 == null || P2FallbackGreedyUsedThisTurn || p2.PlayerCombatState == null || p2.PlayerCombatState.Energy <= 0) return false;
                var hand = PileType.Hand.GetPile(p2);
                if (hand == null || hand.Cards == null) return false;

                CombatState state = CombatManager.Instance != null ? CombatManager.Instance.DebugOnlyGetState() : null;
                var aqs = RunManager.Instance != null ? RunManager.Instance.ActionQueueSet : null;
                var pt = typeof(MegaCrit.Sts2.Core.GameActions.PlayCardAction);
                MethodInfo enq = aqs != null ? aqs.GetType().GetMethod("EnqueueWithoutSynchronizing", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { pt }, null) : null;
                if (enq == null)
                {
                    GD.PrintErr("[AiTeammate][Coord] fallback-greedy-failed reason=exact-enqueue-overload-missing");
                    return false;
                }

                HashSet<CardModel> triedCards = new HashSet<CardModel>();
                int cardsPlayedInLoop = 0;
                int remainingEnergy = p2.PlayerCombatState.Energy;

                IsEnqueuingFallbackBatch = true;
                try
                {
                    for (int iter = 0; iter < 6; iter++)
                    {
                        if (remainingEnergy <= 0) break;
                        if (p2.PlayerCombatState == null) break;
                        if (hand.Cards == null || hand.Cards.Count == 0) break;

                        List<CardModel> affordablePlayable = new List<CardModel>();
                        foreach (var c in hand.Cards)
                        {
                            if (c != null && CheckIsPlayable(c) && IsAffordable(c, p2) && !triedCards.Contains(c))
                            {
                                int cCost = GetCardEnergyCost(c);
                                if (cCost > remainingEnergy) continue;
                                affordablePlayable.Add(c);
                            }
                        }

                        if (affordablePlayable.Count == 0) break;

                        // Prioritize playing IsAttack(c) cards first, then Skills/Powers (c.Type == CardType.Skill || c.Type == CardType.Power),
                        // and prefer single-player target types over TargetType.AnyPlayer/TargetType.AnyAlly first
                        affordablePlayable.Sort(delegate(CardModel a, CardModel b)
                        {
                            bool aAttack = IsAttack(a);
                            bool bAttack = IsAttack(b);
                            if (aAttack != bAttack) return aAttack ? -1 : 1;

                            bool aAlly = (a.TargetType == TargetType.AnyPlayer || a.TargetType == TargetType.AnyAlly);
                            bool bAlly = (b.TargetType == TargetType.AnyPlayer || b.TargetType == TargetType.AnyAlly);
                            if (aAlly != bAlly) return aAlly ? 1 : -1;

                            bool aSkillOrPower = (a.Type == CardType.Skill || a.Type == CardType.Power);
                            bool bSkillOrPower = (b.Type == CardType.Skill || b.Type == CardType.Power);
                            if (aSkillOrPower != bSkillOrPower) return aSkillOrPower ? -1 : 1;

                            double sa = FlowCoordinator.GetCardScore(a.Id != null ? a.Id.ToString() : "");
                            double sb = FlowCoordinator.GetCardScore(b.Id != null ? b.Id.ToString() : "");
                            return sb.CompareTo(sa);
                        });

                        CardModel chosen = affordablePlayable[0];
                        triedCards.Add(chosen);

                        bool isXCost = false;
                        try { if (chosen.EnergyCost != null && chosen.EnergyCost.CostsX) isXCost = true; } catch { }
                        int chosenCost = isXCost ? -1 : GetCardEnergyCost(chosen);
                        if (chosenCost > 0) remainingEnergy -= chosenCost;
                        else if (chosenCost < 0) remainingEnergy = 0; // X-cost card consumes all remaining energy

                        Creature target = ResolveFallbackTarget(chosen, p2, state);
                        object action = new MegaCrit.Sts2.Core.GameActions.PlayCardAction(chosen, target);

                        // Set flags BEFORE enq.Invoke so synchronous execution inside EnqueueWithoutSynchronizing sees them!
                        P2FallbackGreedyUsedThisTurn = true;
                        P2FallbackActionPendingThisTurn = true;
                        P2FallbackActionEnqueuedThisTurn = true;

                        GD.Print(string.Format("[AiTeammate][Coord] fallback-greedy-requested turn={0} card={1} target={2} iter={3} cost={4} remEnergy={5} action-enqueued=true",
                            TurnNumber, chosen.Id, target != null ? target.ToString() : "none", iter, chosenCost, remainingEnergy));

                        using (EnterP2ActionSenderScope("TryFallbackGreedy"))
                        {
                            enq.Invoke(aqs, new object[] { action });
                        }
                        cardsPlayedInLoop++;
                    }
                }
                finally
                {
                    IsEnqueuingFallbackBatch = false;
                }

                // Crucially: always ensure P2 ends its turn at the end of fallback!
                var cm = CombatManager.Instance;
                bool queued = EnqueueP2EndTurn(p2);
                if (queued)
                {
                    P2FallbackEndTurnQueuedThisTurn = true;
                }
                else if (cardsPlayedInLoop == 0 && !IsPlayer2ActionInFlight())
                {
                    if (cm != null && !cm.IsPlayerReadyToEndTurn(p2))
                    {
                        ForceEndTurnForPlayer2(cm, state, p2);
                    }
                }

                HasCompletedDeploymentThisTurn = true;
                P2ZeroActionProtectionFinalizedThisTurn = true;

                return cardsPlayedInLoop > 0;
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Coord] fallback-greedy fail-open: " + ex.Message);
            }
            finally
            {
                IsEnqueuingFallbackBatch = false;
            }
            return false;
        }

        private static Creature ResolveFallbackTarget(CardModel card, Player owner, CombatState state)
        {
            if (card == null || owner == null) return null;
            TargetType targetType = card.TargetType;
            if (targetType == TargetType.None || targetType == TargetType.TargetedNoCreature || targetType == TargetType.AllEnemies || targetType == TargetType.AllAllies || targetType == TargetType.RandomEnemy || targetType == TargetType.Osty) return null;
            if (targetType == TargetType.Self) return owner.Creature;
            if (targetType == TargetType.AnyAlly || targetType == TargetType.AnyPlayer)
            {
                if (state != null && state.Players != null)
                    foreach (Player player in state.Players) if (player != null && player.Creature != null && player.Creature.IsAlive && player.NetId != owner.NetId) return player.Creature;
                return owner.Creature;
            }
            if (targetType == TargetType.AnyEnemy)
            {
                if (state != null && state.Enemies != null)
                    foreach (Creature enemy in state.Enemies) if (enemy != null && enemy.IsAlive && enemy.CurrentHp > 0) return enemy;
                return null;
            }
            return null;
        }

        public static bool EnqueueP2EndTurnAfterFallback(Player p2)
        {
            try
            {
                if (p2 == null || P2FallbackEndTurnQueuedThisTurn || !P2FallbackActionExecutedThisTurn) return false;
                var aqs = RunManager.Instance != null ? RunManager.Instance.ActionQueueSet : null;
                if (aqs == null) return false;
                var cm = CombatManager.Instance;
                CombatState cs = cm != null ? cm.DebugOnlyGetState() : null;
                int currentRound = (cs != null && cs.RoundNumber > 0) ? cs.RoundNumber : (TurnNumber > 0 ? TurnNumber : 1);
                Type pt = typeof(MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction);
                foreach (var ctor in pt.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var ps = ctor.GetParameters(); var args = new object[ps.Length]; bool ownerBound = false; bool ok = true;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var t = ps[i].ParameterType;
                        if (t == typeof(Player)) { args[i] = p2; ownerBound = true; }
                        else if (t == typeof(ulong)) { args[i] = p2.NetId; ownerBound = true; }
                        else if (t == typeof(int)) args[i] = currentRound;
                        else if (t == typeof(bool)) args[i] = false;
                        else if (!t.IsValueType) args[i] = null;
                        else { ok = false; break; }
                    }
                    if (!ok || !ownerBound) continue;
                    object action = ctor.Invoke(args);
                    var enq = aqs.GetType().GetMethod("EnqueueWithoutSynchronizing", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (enq == null) continue;
                    var ep = enq.GetParameters(); var ea = new object[ep.Length];
                    for (int i = 0; i < ep.Length; i++) ea[i] = ep[i].ParameterType.IsInstanceOfType(action) ? action : (ep[i].ParameterType == typeof(ulong) ? (object)p2.NetId : null);
                    using (EnterP2ActionSenderScope("EnqueueP2EndTurnAfterFallback"))
                    {
                        enq.Invoke(aqs, ea);
                    }
                    P2FallbackEndTurnQueuedThisTurn = true;
                    GD.Print("[AiTeammate][Coord] fallback-greedy-end-turn-enqueued turn=" + currentRound + " owner=P2");
                    return true;
                }
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][Coord] fallback-greedy-end-turn enqueue failed: " + ex.Message); }
            return false;
        }

        // Directive #5.30: Direct P2 End Turn Enqueue
        public static bool EnqueueP2EndTurn(Player p2)
        {
            try
            {
                if (p2 == null) return false;
                var cm = CombatManager.Instance;
                if (cm != null && cm.IsPlayerReadyToEndTurn(p2)) return true;
                var aqs = RunManager.Instance != null ? RunManager.Instance.ActionQueueSet : null;
                if (aqs == null) return false;
                CombatState cs = cm != null ? cm.DebugOnlyGetState() : null;
                int currentRound = (cs != null && cs.RoundNumber > 0) ? cs.RoundNumber : (TurnNumber > 0 ? TurnNumber : 1);
                Type pt = typeof(MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction);
                foreach (var ctor in pt.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var ps = ctor.GetParameters(); var args = new object[ps.Length]; bool ownerBound = false; bool ok = true;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var t = ps[i].ParameterType;
                        if (t == typeof(Player)) { args[i] = p2; ownerBound = true; }
                        else if (t == typeof(ulong)) { args[i] = p2.NetId; ownerBound = true; }
                        else if (t == typeof(int)) args[i] = currentRound;
                        else if (t == typeof(bool)) args[i] = false;
                        else if (!t.IsValueType) args[i] = null;
                        else { ok = false; break; }
                    }
                    if (!ok || !ownerBound) continue;
                    object action = ctor.Invoke(args);
                    var enq = aqs.GetType().GetMethod("EnqueueWithoutSynchronizing", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (enq == null) continue;
                    var ep = enq.GetParameters(); var ea = new object[ep.Length];
                    for (int i = 0; i < ep.Length; i++) ea[i] = ep[i].ParameterType.IsInstanceOfType(action) ? action : (ep[i].ParameterType == typeof(ulong) ? (object)p2.NetId : null);
                    using (EnterP2ActionSenderScope("EnqueueP2EndTurn"))
                    {
                        enq.Invoke(aqs, ea);
                    }
                    P2EndTurnEnqueuedThisTurn = true;
                    GD.Print("[AiTeammate][Coord] p2-end-turn-enqueued turn=" + currentRound + " owner=P2");
                    return true;
                }
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][Coord] EnqueueP2EndTurn failed: " + ex.Message); }
            return false;
        }

        public static bool HasPlayableAttackOrSkill(Player p2, out bool hasAttack)
        {
            hasAttack = false;
            try
            {
                if (p2 == null || p2.PlayerCombatState == null || p2.PlayerCombatState.Energy <= 0) return false;
                var hand = PileType.Hand.GetPile(p2);
                if (hand == null || hand.Cards == null) return false;
                foreach (var card in hand.Cards)
                {
                    if (card == null || !CheckIsPlayable(card) || !IsAffordable(card, p2)) continue;
                    if (IsAttack(card)) { hasAttack = true; return true; }
                    object type = GetMemberValue(card, "Type");
                    string typeName = type == null ? "" : type.ToString();
                    string cardName = card.GetType().Name + " " + Convert.ToString(card.Id);
                    if (typeName.IndexOf("Skill", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (cardName.IndexOf("Status", StringComparison.OrdinalIgnoreCase) < 0 && cardName.IndexOf("Curse", StringComparison.OrdinalIgnoreCase) < 0 && !IsDefense(card))) return true;
                }
            }
            catch { }
            return false;
        }

        public static string DetectSolverEarlyEnd(Player p2, out bool hasPlayable, out bool hasAttack)
        {
            hasPlayable = HasPlayableAttackOrSkill(p2, out hasAttack);
            if (!hasPlayable || P2PlayedCardsThisTurn > 0) return null;
            if (P2PlanLengthThisTurn <= 0) return P2SearchStatusThisTurn == "timeout" ? "search-timeout" : "plan-empty";
            if (hasAttack && P2PlanHasCardActionThisTurn && !P2PlanHasAttackThisTurn) return "objective";
            return null;
        }

        static bool IsDefense(CardModel c) { try { string n = c.GetType().Name + " " + c.Id; return n.IndexOf("Block", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Defend", StringComparison.OrdinalIgnoreCase) >= 0; } catch { return false; } }



        public static bool IsPlayer2ActionInFlight(bool checkScopeDepth)
        {
            try
            {
                ulong p2NetId = GetPlayer2NetId();
                if (p2NetId == 0) return false;
                if (CurrentExecutingActionOwnerNetId == p2NetId) return true;
                if (checkScopeDepth && P2ActionSenderScopeDepth > 0) return true;

                var aqs = RunManager.Instance != null ? RunManager.Instance.ActionQueueSet : null;
                if (aqs != null && aqs.ActionQueueIsPaused(p2NetId)) return true;

                int queueCount = GetActionQueueCount(p2NetId);
                if (queueCount > 0) return true;
            }
            catch { }
            return false;
        }

        public static bool IsPlayer2ActionInFlight()
        {
            return IsPlayer2ActionInFlight(true);
        }

        public static void OnProcessTick()

        {

            try

            {

                if (!IsInCombat())
                {
                    if (IndependentLifelineArmed)
                    {
                        DisarmIndependentP2Lifeline("process-tick-not-in-combat");
                    }
                    if (IsFullRunP1ControlLockActive())
                    {
                        ulong p2Id = GetPlayer2NetId();
                        if (p2Id != 0 && TurnFlowPatches.GetDraCurrentControlledPlayerId() == p2Id)
                        {
                            TurnFlowPatches.RestorePlayer1ControlIfActive("out-of-combat-tick-guard", true);
                        }
                    }
                    return;
                }

                var now = DateTime.UtcNow;



                // Directive #5.14: Watchdog timeout for room enter window (fail-open after 5.0s)

                if (IsEnteringRoom && (now - RoomEnteringStartedTime).TotalSeconds > 5.0)

                {

                    OnRoomSettled("timeout-failopen");

                }



                if ((now - LastProcessCheckTime).TotalMilliseconds < 450)

                {

                    return;

                }

                LastProcessCheckTime = now;



                // Directive #5.42 Task 4: Self-heal leaked P2ActionSenderScopeDepth when no P2 action in flight
                if (P2ActionSenderScopeDepth > 0 && !IsPlayer2ActionInFlight(false))
                {
                    GD.Print(string.Format("[AiTeammate][Coord] p2-scope-self-heal source=OnProcessTick leaked_depth={0}", P2ActionSenderScopeDepth));
                    AttributionPatches.ClearAllP2SenderScopes();
                    P2ActionSenderScopeDepth = 0;
                    ulong p1 = GetPlayer1NetId();
                    if (p1 != 0) LocalContext.NetId = p1;
                    TurnFlowPatches.RestorePlayer1ControlIfActive("p2-scope-self-heal", false);
                }

                // 1. Turn-level Solver Search Stall Watchdog (>12s stall kick recalc, >20s CRITICAL force P2 end turn)

                try

                {

                    CheckSolverStallWatchdog(now);

                CheckMidDeploymentAndSearchWatchdog(now);
                CheckNativeChoiceContinuation("process-tick");
                if (HasRetriedNativeChoiceRecoveryThisTurn && !IsDeploying && !IsSolverSearching() && NativeChoiceFailureTime != DateTime.MinValue && (now - NativeChoiceFailureTime).TotalSeconds >= 2.5)
                {
                    if (P2ActionSenderScopeDepth == 0)
                    {
                        Player p2Rec = FlowCoordinator.GetPlayer2();
                        var cmRec = CombatManager.Instance;
                        if (p2Rec != null && cmRec != null && !cmRec.IsPlayerReadyToEndTurn(p2Rec))
                        {
                            GD.Print("[AiTeammate][Coord] solver-native-choice-recovery: 2.5s recalc timeout, running greedy fallback turn=" + TurnNumber);
                            NativeChoiceFailureTime = DateTime.MinValue;
                            TryFallbackGreedy(p2Rec);
                            EnqueueP2EndTurn(p2Rec);
                        }
                    }
                }

                }

                catch (Exception exStall)

                {

                    GD.PrintErr("[AiTeammate][Coord] CheckSolverStallWatchdog error: " + exStall);

                }

                // Directive #5.41 Task 3: Fast-finish stalled P2 deployment after card play/draw (e.g. BURNING_PACT)
                if (IsDeploying && HasExecutedActionInDeployment && P2ActionSenderScopeDepth == 0 &&
                    LastActionExecutedTime != DateTime.MinValue && (now - LastActionExecutedTime).TotalSeconds > 1.8 &&
                    !IsPlayer2ActionInFlight())
                {
                    GD.Print(string.Format("[AiTeammate][Coord] deployment-draw-stall-fast-finish: elapsed={0:F2}s after action execution, finishing deployment and running greedy continuation", (now - LastActionExecutedTime).TotalSeconds));
                    IsDeploying = false;
                    HoldActive = false;
                    HasCompletedDeploymentThisTurn = true;
                    DeployStartTime = DateTime.MinValue;
                    CleanHangingDeployment();

                    var cm = CombatManager.Instance;
                    var state = cm != null ? cm.DebugOnlyGetState() : null;
                    Player p2 = (state != null && state.Players != null && state.Players.Count > 1) ? state.Players[state.Players.Count - 1] : FlowCoordinator.GetPlayer2();

                    if (FixedP1CombatControl)
                    {
                        TurnFlowPatches.RefreshPlayer1CombatUiMinimal();
                    }
                    TurnFlowPatches.RestorePlayer1ControlIfActive("deployment-draw-stall-finish");

                    if (p2 != null && cm != null && !cm.IsPlayerReadyToEndTurn(p2))
                    {
                        bool hasAttack;
                        if (P2HasUsableCard(p2, out hasAttack))
                        {
                            TryFallbackGreedy(p2);
                        }
                        if (!cm.IsPlayerReadyToEndTurn(p2))
                        {
                            EnqueueP2EndTurn(p2);
                        }
                    }
                }

                // 1b. Directive #3.8: FullAuto Guard & Autonomous Evaluation

                CheckFullAutoGuard();

                CheckAutonomousTurnEvaluation(now);

                // #5.26: explicit evidence when the live solver deploys without
                // entering any Harmony-observed deployment method.
                if (P2TelemetryTurn == TurnNumber && !P2DeployHookObservedThisTurn && !P2DeployHookMissLoggedThisTurn &&
                    TurnStartTime != DateTime.MinValue && (now - TurnStartTime).TotalSeconds >= 3.5 &&
                    !IsSolverSearching() && !IsSolverDeploying())
                {
                    var telemetryState = CombatManager.Instance != null ? CombatManager.Instance.DebugOnlyGetState() : null;
                    Player telemetryP2 = telemetryState != null && telemetryState.Players != null && telemetryState.Players.Count > 1 ? telemetryState.Players[telemetryState.Players.Count - 1] : null;
                    bool hasAttack;
                    if (telemetryP2 != null && P2HasUsableCard(telemetryP2, out hasAttack))
                    {
                        P2DeployHookMissLoggedThisTurn = true;
                        LogP2DeployHookMissed("solver-active-without-harmony-entry");
                    }
                }



                // 1c. Directive #3.9: Control divergence self-healing guard

                CheckControlDivergenceGuard(now);



                // 1d. Directive #3.9.1: Fallback empty plan guard (1s after DEPLOY_START with no DEPLOY_ACTION)

                if (IsDeploying && DeployStartTime != DateTime.MinValue && (now - DeployStartTime).TotalSeconds >= 1.0 && !HasExecutedActionInDeployment && !HasRetriedEmptyPlanThisTurn)

                {

                    var cm = CombatManager.Instance;

                    var state = cm != null ? cm.DebugOnlyGetState() : null;

                    if (state != null && state.Players != null && state.Players.Count >= 2)

                    {

                        var p2 = state.Players[state.Players.Count - 1];

                        int p2Cards = GetPlayerHandCardCount(p2);

                        if (p2Cards > 0)

                        {

                            HasRetriedEmptyPlanThisTurn = true;

                            GD.PrintErr("[AiTeammate][Coord] empty-plan-with-cards (1s without DEPLOY_ACTION, physicalHand=" + p2Cards + "), aborting empty deployment and requesting recalculation");

                            IsDeploying = false;

                            DeployStartTime = DateTime.MinValue;

                            CleanHangingDeployment();

                            RequestRecalculation(state, "empty-plan-retry");

                            return;

                        }

                    }

                }



                // 1e. Directive #3.9.4: Mid-turn replan & Hold release checks

                if (HoldActive && !HasReleasedHoldThisTurn)

                {

                    CheckMidturnReplan(null);

                    CheckHoldRelease(now);

                }



                // 2. Watchdog B: 10s Deployment Stall Watchdog

                if (IsDeploying && DeployStartTime != DateTime.MinValue && (now - DeployStartTime).TotalSeconds >= 10.0)

                {

                    GD.PrintErr("[AiTeammate][Coord] CRITICAL stall: deployment timed out after 10s without CompleteDeployment/CancelDeployment, restoring Player 1 control.");

                    IsDeploying = false;

                    DeployStartTime = DateTime.MinValue;

                    CleanHangingDeployment();

                    TurnFlowPatches.RestorePlayer1ControlIfActive("wakuu-stall-watchdog");

                    ResetTurnState("StallWatchdog");

                    return;

                }



                // 3. Watchdog A: 3.0s Release / Deployment Watchdog

                if (!IsDeploying && HasReleasedThisTurn && !RecalcSucceeded && ReleaseRequestedTime != DateTime.MinValue)

                {

                    if (!Watchdog3sFired && (now - ReleaseRequestedTime).TotalSeconds >= 3.0)

                    {

                        Watchdog3sFired = true;

                        GD.PrintErr("[AiTeammate][Coord] Fresh recalc watchdog timeout: watchdog-fire (no deployment 3s after release trigger=" + (LastReleaseTrigger ?? "none") + ", falling back to TriggerCachedDeployment)");

                        RecalcSucceeded = true;

                        TriggerCachedDeployment();

                        return;

                    }

                }



                // 4. Hand ownership self-healing guard

                CheckHandOwnershipGuard(now);



                // 5. Refresh Player 1 hand card energy cost colors so they are always accurate

                if (!IsDeploying && LocalContext.NetId == GetPlayer1NetId())

                {

                    RefreshPlayerHandVisuals(null);

                }



                // 6. Wait state checks

                if (!IsWaiting) return;



                if (WaitStartTime == DateTime.MinValue)

                {

                    WaitStartTime = now;

                }

                else if ((now - WaitStartTime).TotalSeconds >= WaitTimeoutSeconds)

                {

                    GD.Print("[AiTeammate][Coord] Wait timeout reached (" + WaitTimeoutSeconds.ToString("F1") + "s), releasing wait (trigger=timeout).");

                    ReleaseWait("timeout");

                    return;

                }



                CheckTriggers("process-tick");

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] OnProcessTick exception: " + ex);

                if (IsWaiting)

                {

                    ReleaseWait("timeout");

                }

            }

        }

    }



    public static class FlowActions

    {

        public static object Invoke(object target, string method, params object[] args)

        {

            if (target == null) return null;

            MethodInfo m = AccessTools.Method(target.GetType(), method);

            if (m != null)

            {

                return m.Invoke(target, args);

            }

            return null;

        }

    }



    public static class FlowCoordinator

    {

        public static bool AutoRouteFollowEnabled = true;

        public static bool AutoCardRewardEnabled = true;

        public static bool AutoEventEnabled = true;

        public static bool AutoRestSiteEnabled = true;

        public static bool AutoNeowEnabled = true;

        public static bool ShopCardRemovalEnabled = true;
        public static bool AutoShopEnabled = true;

        public static volatile bool IsExecutingP2AutoRemoval = false;
        public static volatile bool IsExecutingP2AutoShop = false;
        public static string LastShopRemovalTargetCardId = null;
        private static readonly HashSet<string> ExecutedShopRemovals = new HashSet<string>();
        private static readonly HashSet<string> HandledShopVisits = new HashSet<string>();
        private static string LastShopEntryRoomId = null;
        private static string LastShopRunGeneration = null;

        // Directive #5.32: Event Sub-Choice & Event Reward Gate tracking
        public static string CurrentEventId = null;
        public static int CurrentEventPage = 0;
        public static bool LastChildCardChoiceCompleted = false;

        // Directive #5.33: In-flight event choice idempotency & state machine tracking
        public static string InFlightEventKey = null;
        public static string InFlightEventId = null;
        public static int InFlightEventPage = 0;
        public static string InFlightOptionsFingerprint = null;
        public static int InFlightChosenIndex = -1;
        public static string InFlightPickId = null;
        public static string InFlightEventKind = null;
        public static string InFlightReason = null;
        public static string InFlightSource = null;
        public static string InFlightGeneration = null;
        public static DateTime InFlightEventSubmitTime = DateTime.MinValue;
        public static int InFlightEventRetryCount = 0;
        public static bool InFlightHadPendingChoice = false;
        private static readonly HashSet<string> AbortedEventKeys = new HashSet<string>();
        private static readonly HashSet<string> ConfirmedEventKeys = new HashSet<string>();

        public static void ResetInFlightEventState()
        {
            InFlightEventKey = null;
            CurrentEventPage = 0;
            CurrentEventId = null;
            LastChildCardChoiceCompleted = false;
            InFlightEventRetryCount = 0;
            InFlightHadPendingChoice = false;
        }

        public static string ComputeOptionsFingerprint(System.Collections.IList options)
        {
            if (options == null || options.Count == 0) return "empty";
            List<string> parts = new List<string>();
            for (int i = 0; i < options.Count; i++)
            {
                var opt = options[i];
                if (opt == null) continue;
                string t = GetOptionTitle(opt) ?? "";
                bool locked = GetOptionIsLocked(opt);
                parts.Add(i + ":" + t + ":" + (locked ? "1" : "0"));
            }
            return string.Join(";", parts.ToArray());
        }

        public static void OnRefreshShopRoom(ulong playerId)
        {
            OnMerchantRoomEnteredOrSettled("RefreshShopRoomForPlayer");
        }

        public static void OnMerchantRoomEnteredOrSettled(string trigger)
        {
            try
            {
                if (!AutoShopEnabled && !ShopCardRemovalEnabled) return;
                if (IsExecutingP2AutoShop) return;

                Player p2 = GetPlayer2();
                if (p2 == null) return;

                MegaCrit.Sts2.Core.Rooms.MerchantRoom room = null;
                if (MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom.Instance != null && MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom.Instance.Room != null)
                {
                    room = MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom.Instance.Room;
                }
                if (room == null)
                {
                    object state = RunManager.Instance != null ? RunManager.Instance.DebugOnlyGetState() : null;
                    if (state != null)
                    {
                        PropertyInfo cp = AccessTools.Property(state.GetType(), "CurrentRoom");
                        room = cp != null ? cp.GetValue(state, null) as MegaCrit.Sts2.Core.Rooms.MerchantRoom : null;
                    }
                }
                if (room == null) return;

                string roomId = room.Id.HasValue ? room.Id.Value.ToString() : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(room).ToString();
                string generation = CurrentRunGeneration();

                if (!string.Equals(LastShopRunGeneration, generation, StringComparison.Ordinal))
                {
                    LastShopRunGeneration = generation;
                    ExecutedShopRemovals.Clear();
                    HandledShopVisits.Clear();
                    LastShopEntryRoomId = null;
                }

                // Retrieve/ensure P2 inventory in background FIRST before marking handled!
                MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory p2Inventory = null;
                Type lmirType = AccessTools.TypeByName("LocalMultiControl.Scripts.Runtime.LocalMerchantInventoryRuntime");
                MethodInfo goci = lmirType != null ? AccessTools.Method(lmirType, "GetOrCreateInventory") : null;
                if (goci != null)
                {
                    p2Inventory = goci.Invoke(null, new object[] { room, p2 }) as MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory;
                }

                if (p2Inventory == null)
                {
                    GD.Print("[AiTeammate][Shop] shop-skip reason=null-p2-inventory");
                    return;
                }

                string visitKey = generation + "|room=" + roomId + "|entered";
                if (HandledShopVisits.Contains(visitKey))
                {
                    return;
                }
                HandledShopVisits.Add(visitKey);

                // Directive #5.31: shop-foreground-assert
                ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                ulong actual = TurnFlowPatches.GetDraCurrentControlledPlayerId();
                string actualStr = (p1Id != 0 && actual == p1Id) ? "P1" : (actual == TurnCoordinator.GetPlayer2NetId() ? "P2" : actual.ToString());
                GD.Print(string.Format("[AiTeammate][Shop] shop-foreground-assert expected=P1 actual={0}", actualStr));
                if (p1Id != 0 && actual != p1Id)
                {
                    TurnFlowPatches.SwitchControlToPlayer(p1Id, "wakuu-p1-restore:shop-room-enter", false);
                }

                // Directive #5.31: shop-entry owner=P2 trigger=merchant-room-enter room={roomId}
                GD.Print(string.Format("[AiTeammate][Shop] shop-entry owner=P2 trigger=merchant-room-enter room={0}", roomId));

                // Launch sequential shopping pipeline
                ExecuteP2ShopVisitAsync(p2, p2Inventory, roomId);
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Shop] OnMerchantRoomEnteredOrSettled exception: " + ex);
            }
        }

        private static async void ExecuteP2ShopVisitAsync(Player p2, MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory p2Inventory, string roomId)
        {
            IsExecutingP2AutoShop = true;
            int initialGold = p2.Gold;
            int removedCount = 0;
            int relicsBought = 0;
            int cardsBought = 0;
            int potionsBought = 0;

            try
            {
                // Stage 1: Card Removal
                if (ShopCardRemovalEnabled && p2Inventory.CardRemovalEntry != null)
                {
                    var removalEntry = p2Inventory.CardRemovalEntry;
                    if (!removalEntry.Used && removalEntry.IsStocked && p2.Gold >= removalEntry.Cost)
                    {
                        CardModel bestRemovalCandidate = null;
                        double bestRemovalScore = double.MaxValue;
                        if (p2.Deck != null && p2.Deck.Cards != null && p2.Deck.Cards.Count >= 8)
                        {
                            foreach (CardModel c in p2.Deck.Cards)
                            {
                                if (c == null || !IsCardRemovable(c)) continue;
                                double s = GetCardRemovalPriorityScore(c);
                                if (s < bestRemovalScore)
                                {
                                    bestRemovalScore = s;
                                    bestRemovalCandidate = c;
                                }
                            }
                        }

                        if (bestRemovalCandidate != null && bestRemovalScore <= 50.0)
                        {
                            int cost = removalEntry.Cost;
                            GD.Print(string.Format("[AiTeammate][Shop] shop-offer kind=removal id=card-removal cost={0} gold_before={1}", cost, p2.Gold));
                            IsExecutingP2AutoRemoval = true;
                            LastShopRemovalTargetCardId = null;
                            bool ok = false;
                            try
                            {
                                ok = await removalEntry.OnTryPurchaseWrapper(p2Inventory, false, false);
                            }
                            catch (Exception ex)
                            {
                                GD.PrintErr("[AiTeammate][Shop] removal purchase exception: " + ex);
                            }
                            finally
                            {
                                IsExecutingP2AutoRemoval = false;
                            }

                            if (ok)
                            {
                                removalEntry.SetUsed();
                                removedCount++;
                                string targetCardId = !string.IsNullOrEmpty(LastShopRemovalTargetCardId) ? LastShopRemovalTargetCardId : bestRemovalCandidate.Id.ToString();
                                GD.Print(string.Format("[AiTeammate][Shop] shop-resolved kind=removal id=card-removal cost={0} target={1} gold_after={2}", cost, targetCardId, p2.Gold));
                            }
                            else
                            {
                                GD.Print("[AiTeammate][Shop] shop-failed kind=removal reason=purchase-returned-false");
                            }
                        }
                    }
                }

                // Stage 2: Relics (Up to 2)
                if (AutoShopEnabled && p2Inventory.RelicEntries != null)
                {
                    for (int pass = 0; pass < 2; pass++)
                    {
                        MerchantRelicEntry bestRelicEntry = null;
                        double bestRelicScore = -1.0;

                        foreach (MerchantRelicEntry entry in p2Inventory.RelicEntries)
                        {
                            if (entry == null || !entry.IsStocked || entry.Model == null) continue;
                            if (p2.Gold < entry.Cost) continue;
                            if (!IsShopRelicSafeForBackgroundPurchase(entry.Model)) continue;

                            double score = EvaluateShopRelicScore(p2, entry.Model);
                            if (score >= 15.0 && score > bestRelicScore)
                            {
                                bestRelicScore = score;
                                bestRelicEntry = entry;
                            }
                        }

                        if (bestRelicEntry != null)
                        {
                            string rid = bestRelicEntry.Model.Id.ToString();
                            int cost = bestRelicEntry.Cost;
                            GD.Print(string.Format(System.Globalization.CultureInfo.InvariantCulture, "[AiTeammate][Shop] shop-offer kind=relic id={0} cost={1} score={2:F1} gold_before={3}", rid, cost, bestRelicScore, p2.Gold));
                            bool ok = false;
                            try
                            {
                                ok = await bestRelicEntry.OnTryPurchaseWrapper(p2Inventory, false);
                            }
                            catch (Exception ex)
                            {
                                GD.PrintErr("[AiTeammate][Shop] relic purchase exception: " + ex);
                            }
                            if (ok)
                            {
                                relicsBought++;
                                GD.Print(string.Format("[AiTeammate][Shop] shop-resolved kind=relic id={0} cost={1} gold_after={2}", rid, cost, p2.Gold));
                            }
                            else
                            {
                                GD.Print(string.Format("[AiTeammate][Shop] shop-failed kind=relic id={0} reason=purchase-returned-false", rid));
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                // Stage 3: Cards (Up to 2)
                if (AutoShopEnabled)
                {
                    List<MerchantCardEntry> availableCards = new List<MerchantCardEntry>();
                    if (p2Inventory.CardEntries != null)
                    {
                        foreach (var c in p2Inventory.CardEntries) if (c != null) availableCards.Add(c);
                    }
                    else
                    {
                        if (p2Inventory.CharacterCardEntries != null)
                            foreach (var c in p2Inventory.CharacterCardEntries) if (c != null) availableCards.Add(c);
                        if (p2Inventory.ColorlessCardEntries != null)
                            foreach (var c in p2Inventory.ColorlessCardEntries) if (c != null) availableCards.Add(c);
                    }

                    for (int cardPass = 0; cardPass < 2; cardPass++)
                    {
                        if (cardPass == 1 && p2.Gold < 100) break;
                        double threshold = (cardPass == 0) ? 60.0 : 68.0;

                        MerchantCardEntry bestCardEntry = null;
                        double bestCardScore = -9999.0;

                        foreach (MerchantCardEntry entry in availableCards)
                        {
                            if (entry == null || !entry.IsStocked || entry.CreationResult == null || entry.CreationResult.Card == null) continue;
                            if (p2.Gold < entry.Cost) continue;

                            double s = EvaluateShopCardEntryScore(p2, entry);
                            double effThreshold = (cardPass == 0 && entry.IsOnSale) ? 52.0 : threshold;

                            if (s >= effThreshold && s > bestCardScore)
                            {
                                bestCardScore = s;
                                bestCardEntry = entry;
                            }
                        }

                        if (bestCardEntry != null)
                        {
                            CardModel card = bestCardEntry.CreationResult.Card;
                            if (card.Owner == null) card.Owner = p2;
                            string cid = card.Id.ToString();
                            int cost = bestCardEntry.Cost;
                            GD.Print(string.Format(System.Globalization.CultureInfo.InvariantCulture, "[AiTeammate][Shop] shop-offer kind=card id={0} cost={1} score={2:F1} on_sale={3} gold_before={4}", cid, cost, bestCardScore, bestCardEntry.IsOnSale ? "True" : "False", p2.Gold));
                            bool ok = false;
                            try
                            {
                                ok = await bestCardEntry.OnTryPurchaseWrapper(p2Inventory, false);
                            }
                            catch (Exception ex)
                            {
                                GD.PrintErr("[AiTeammate][Shop] card purchase exception: " + ex);
                            }
                            if (ok)
                            {
                                cardsBought++;
                                GD.Print(string.Format("[AiTeammate][Shop] shop-resolved kind=card id={0} cost={1} gold_after={2}", cid, cost, p2.Gold));
                            }
                            else
                            {
                                GD.Print(string.Format("[AiTeammate][Shop] shop-failed kind=card id={0} reason=purchase-returned-false", cid));
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                // Stage 4: Potions (Up to 2 while slots open)
                if (AutoShopEnabled && p2Inventory.PotionEntries != null)
                {
                    for (int potPass = 0; potPass < 2; potPass++)
                    {
                        if (!HasOpenPotionSlot(p2)) break;

                        MerchantPotionEntry bestPotEntry = null;
                        double bestPotScore = -1.0;

                        foreach (MerchantPotionEntry entry in p2Inventory.PotionEntries)
                        {
                            if (entry == null || !entry.IsStocked || entry.Model == null) continue;
                            if (p2.Gold < entry.Cost) continue;
                            string pid = entry.Model.Id.ToString().ToUpperInvariant();
                            if (pid.Contains("FOUL_POTION")) continue;

                            double s = EvaluateShopPotionScore(entry.Model);
                            if (s >= 50.0 && s > bestPotScore)
                            {
                                bestPotScore = s;
                                bestPotEntry = entry;
                            }
                        }

                        if (bestPotEntry != null)
                        {
                            string pid = bestPotEntry.Model.Id.ToString();
                            int cost = bestPotEntry.Cost;
                            GD.Print(string.Format("[AiTeammate][Shop] shop-offer kind=potion id={0} cost={1} gold_before={2}", pid, cost, p2.Gold));
                            bool ok = false;
                            try
                            {
                                ok = await bestPotEntry.OnTryPurchaseWrapper(p2Inventory, false);
                            }
                            catch (Exception ex)
                            {
                                GD.PrintErr("[AiTeammate][Shop] potion purchase exception: " + ex);
                            }
                            if (ok)
                            {
                                potionsBought++;
                                GD.Print(string.Format("[AiTeammate][Shop] shop-resolved kind=potion id={0} cost={1} gold_after={2}", pid, cost, p2.Gold));
                            }
                            else
                            {
                                GD.Print(string.Format("[AiTeammate][Shop] shop-failed kind=potion id={0} reason=purchase-returned-false", pid));
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Shop] ExecuteP2ShopVisitAsync exception: " + ex);
            }
            finally
            {
                IsExecutingP2AutoRemoval = false;
                IsExecutingP2AutoShop = false;

                // Directive #5.40: shop-foreground-assert
                ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                ulong actual = TurnFlowPatches.GetDraCurrentControlledPlayerId();
                string actualStr = (p1Id != 0 && actual == p1Id) ? "P1" : (actual == TurnCoordinator.GetPlayer2NetId() ? "P2" : actual.ToString());
                GD.Print(string.Format("[AiTeammate][Shop] shop-foreground-assert expected=P1 actual={0}", actualStr));
                if (p1Id != 0 && actual != p1Id)
                {
                    TurnFlowPatches.SwitchControlToPlayer(p1Id, "wakuu-p1-restore:shop-room-exit", false);
                }

                GD.Print(string.Format("[AiTeammate][Shop] shop-visit-summary owner=P2 room={0} gold_before={1} gold_after={2} removed={3} relics={4} cards={5} potions={6}",
                    roomId, initialGold, p2.Gold, removedCount, relicsBought, cardsBought, potionsBought));
            }
        }

        public static bool IsShopRelicSafeForBackgroundPurchase(RelicModel relic)
        {
            if (relic == null) return false;
            string rid = relic.Id.ToString().ToUpperInvariant();
            string[] blacklist = new string[] {
                "ORRERY", "CAULDRON", "DOLLYS_MIRROR", "BOTTLED_", "CALLING_BELL",
                "CURSED_PEARL", "MASSIVE_SCROLL", "PANDORAS_BOX", "ASTROLABE", "EMPTY_CAGE"
            };
            for (int i = 0; i < blacklist.Length; i++)
            {
                if (rid.Contains(blacklist[i])) return false;
            }
            return true;
        }

        public static double EvaluateShopRelicScore(Player p2, RelicModel relic)
        {
            if (relic == null) return -1.0;
            string rid = relic.Id.ToString().ToUpperInvariant();

            double val = 0.0;
            try
            {
                Assembly liveAsm = FindLiveSpireStrategistAssembly();
                if (liveAsm != null && p2 != null)
                {
                    IRunState run = GetRunState();
                    if (run != null)
                    {
                        Type snapReaderType = liveAsm.GetType("SpireStrategist.Mod.SnapshotReader");
                        Type relicValType = liveAsm.GetType("SpireStrategist.Core.RelicValue");
                        if (snapReaderType != null && relicValType != null)
                        {
                            MethodInfo readMethod = null;
                            foreach (var m in snapReaderType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                            {
                                if (m.Name == "Read" && m.GetParameters().Length == 2) { readMethod = m; break; }
                            }
                            MethodInfo evalMethod = null;
                            foreach (var m in relicValType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                            {
                                if (m.Name == "Evaluate" && m.GetParameters().Length >= 2) { evalMethod = m; break; }
                            }
                            if (readMethod != null && evalMethod != null)
                            {
                                object snapshot = readMethod.Invoke(null, new object[] { run, p2 });
                                if (snapshot != null)
                                {
                                    object[] evalArgs = evalMethod.GetParameters().Length == 2 ?
                                        new object[] { relic.Id.ToString(), snapshot } :
                                        new object[] { relic.Id.ToString(), snapshot, false };
                                    object assessment = evalMethod.Invoke(null, evalArgs);
                                    if (assessment != null)
                                    {
                                        PropertyInfo valProp = AccessTools.Property(assessment.GetType(), "Value");
                                        if (valProp != null) val = Convert.ToDouble(valProp.GetValue(assessment, null));
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch {}

            if (val <= 0.0)
            {
                val = 25.0;
                if (relic.Rarity == RelicRarity.Rare || relic.Rarity == RelicRarity.Shop) val += 15.0;
                else if (relic.Rarity == RelicRarity.Uncommon) val += 10.0;
                else if (relic.Rarity == RelicRarity.Common) val += 5.0;

                string[] bonusRelics = new string[] {
                    "ANCHOR", "HORN_CLEAT", "CAPTAINS_WHEEL", "VAJRA", "SMOOTH_STONE",
                    "BAG_OF_PREP", "LANTERN", "HAPPY_FLOWER", "PEN_NIB", "AKABEKO",
                    "MARBLES", "PRESERVED_INSECT", "THREAD_AND_NEEDLE", "TORII", "TUNGSTEN_ROD",
                    "ICE_CREAM", "MUMMY_HAND", "PAPER_PHROG", "MEAT_ON_THE_BONE", "ETERNAL_FEATHER",
                    "TOXIC_EGG", "MOLTEN_EGG", "FROZEN_EGG", "MEMBERSHIP_CARD", "COURIER"
                };
                for (int i = 0; i < bonusRelics.Length; i++)
                {
                    if (rid.Contains(bonusRelics[i]))
                    {
                        val += 15.0;
                        break;
                    }
                }
            }

            return val;
        }

        public static double EvaluateShopCardEntryScore(Player p2, MerchantCardEntry entry)
        {
            if (entry == null || !entry.IsStocked || entry.CreationResult == null || entry.CreationResult.Card == null)
            {
                return -9999.0;
            }
            CardModel card = entry.CreationResult.Card;

            if (!TurnCoordinator.CheckIsPlayable(card) || card.Type == CardType.Curse || card.Type == CardType.Status || card.Type == CardType.Quest)
            {
                return -9999.0;
            }
            if (card.TargetType == TargetType.AnyPlayer || card.TargetType == TargetType.AnyAlly)
            {
                return -9999.0;
            }

            string cid = card.Id.ToString();
            double score = CardScores.ContainsKey(cid) || (cid.StartsWith("CARD.") && CardScores.ContainsKey(cid.Substring(5))) ?
                GetCardScore(cid) :
                (40.0 + CalculateCardDefaultScore(p2, card) * 0.5);

            if (entry.IsOnSale)
            {
                score += 8.0;
            }

            int copies = 0;
            if (p2 != null && p2.Deck != null && p2.Deck.Cards != null)
            {
                foreach (CardModel c in p2.Deck.Cards)
                {
                    if (c != null && string.Equals(c.Id.ToString(), cid, StringComparison.OrdinalIgnoreCase))
                    {
                        copies++;
                    }
                }
            }
            if (copies >= 2)
            {
                score -= 20.0;
            }
            else if (copies == 1)
            {
                score -= 5.0;
            }

            if (p2 != null && p2.Deck != null && p2.Deck.Cards != null && p2.Deck.Cards.Count >= 28)
            {
                score -= 12.0;
            }

            return score;
        }

        public static double EvaluateShopPotionScore(PotionModel potion)
        {
            if (potion == null) return -1.0;
            string pid = potion.Id.ToString().ToUpperInvariant();

            double score = 50.0;
            if (potion.Rarity == PotionRarity.Rare) score = 80.0;
            else if (potion.Rarity == PotionRarity.Uncommon) score = 65.0;
            else if (potion.Rarity == PotionRarity.Common) score = 50.0;

            string[] impactPotions = new string[] {
                "FAIRY", "GHOST", "STRENGTH", "DEXTERITY", "CULTIST",
                "HEART_OF_IRON", "FRUIT", "BLOOD", "REGEN", "FIRE",
                "EXPLOSIVE", "ENERGY", "SWIFT", "GAMBLERS", "LIQUID_MEMORIES"
            };
            for (int i = 0; i < impactPotions.Length; i++)
            {
                if (pid.Contains(impactPotions[i]))
                {
                    score += 10.0;
                    break;
                }
            }

            return score;
        }

        public static bool HasOpenPotionSlot(Player p2)
        {
            if (p2 == null) return false;
            try
            {
                PropertyInfo pi = p2.GetType().GetProperty("HasOpenPotionSlots", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi != null && pi.PropertyType == typeof(bool))
                {
                    return (bool)pi.GetValue(p2, null);
                }
            }
            catch {}

            try
            {
                var slots = TurnCoordinator.GetMemberValue(p2, "PotionSlots") as IEnumerable;
                if (slots != null)
                {
                    foreach (object slot in slots)
                    {
                        if (slot == null) return true;
                        object pot = TurnCoordinator.GetMemberValue(slot, "Potion") ?? TurnCoordinator.GetMemberValue(slot, "Model");
                        if (pot == null) return true;
                    }
                    return false;
                }
            }
            catch {}

            try
            {
                var potions = TurnCoordinator.GetMemberValue(p2, "Potions") as ICollection;
                object maxObj = TurnCoordinator.GetMemberValue(p2, "MaxPotionCount");
                if (potions != null && maxObj != null)
                {
                    return potions.Count < Convert.ToInt32(maxObj);
                }
            }
            catch {}

            return false;
        }

        public static bool IsCardRemovable(CardModel card)
        {
            if (card == null) return false;
            if (card.Type == CardType.Quest) return false;

            string cid = card.Id.ToString().ToUpperInvariant();
            if (cid.Contains("ASCENDERS_BANE") || cid.Contains("NECRONOMICURSE") || cid.Contains("CURSE_OF_THE_BELL"))
            {
                return false;
            }

            try
            {
                var keywords = TurnCoordinator.GetMemberValue(card, "Keywords") as IEnumerable;
                if (keywords != null)
                {
                    foreach (object kw in keywords)
                    {
                        if (kw != null && kw.ToString().IndexOf("Eternal", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return false;
                        }
                    }
                }
            }
            catch {}

            try
            {
                PropertyInfo pi = card.GetType().GetProperty("IsRemovable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi != null && pi.PropertyType == typeof(bool))
                {
                    if (!(bool)pi.GetValue(card, null)) return false;
                }
                PropertyInfo pi2 = card.GetType().GetProperty("CanBeRemovedFromDeck", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi2 != null && pi2.PropertyType == typeof(bool))
                {
                    if (!(bool)pi2.GetValue(card, null)) return false;
                }
                MethodInfo mi = card.GetType().GetMethod("CanBeRemovedFromDeck", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (mi != null && mi.ReturnType == typeof(bool))
                {
                    if (!(bool)mi.Invoke(card, null)) return false;
                }
            }
            catch {}

            return true;
        }

        public static double GetCardRemovalPriorityScore(CardModel card)
        {
            if (card == null) return double.MaxValue;
            if (card.Type == CardType.Curse) return -1000.0;
            if (card.Type == CardType.Status) return -500.0;

            string cid = card.Id.ToString().ToUpperInvariant();
            double baseScore = GetCardScore(card.Id.ToString());

            if (card.Rarity == CardRarity.Basic || cid.Contains("STRIKE"))
            {
                double s = Math.Min(baseScore, 15.0);
                if (!card.IsUpgraded) s -= 5.0;
                return s;
            }
            if (cid.Contains("DEFEND"))
            {
                double s = Math.Min(baseScore, 22.0);
                if (!card.IsUpgraded) s -= 3.0;
                return s;
            }
            if (card.IsUpgraded)
            {
                baseScore += 15.0;
            }

            return baseScore;
        }

        public static bool FloatingEndTurnEnabled = false;



        public static ulong ActiveCardRewardPlayerId = 0;

        public static bool ActiveCardRewardCircuitOpen = false;

        public static IReadOnlyList<CardCreationResult> PendingP2RewardOptions = null;

        public static bool HasPurchasedShopRemovalThisRoom = false;

        public static bool IsSubPageManualInteracting = false;

        // #5.27-BS2: reward backend completion gate.  This gate is deliberately
        // fail-open to the reward screen and fail-closed to room/map movement.
        private static readonly object RewardGateSync = new object();
        private static bool RewardGateActive = false;
        private static bool RewardGateScreenCompleted = false;
        private static bool RewardGateBackendCompleted = false;
        private static bool RewardGateSetCompleted = false;
        private static bool RewardGateCompletedSignal = false;
        private static string RewardGateKey = null;
        private static NRewardsScreen RewardGateScreen = null;
        private static object RewardGateSet = null;
        private static readonly Dictionary<string, int> RewardGateExpected = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> RewardGateConfirmed = new Dictionary<string, int>();
        private static readonly Dictionary<object, byte> RewardGateRewardObjects = new Dictionary<object, byte>();
        private static readonly Dictionary<object, string> RewardGateStableIds = new Dictionary<object, string>();
        private static readonly Dictionary<object, TaskCompletionSource<bool>> RewardSelectionWaiters = new Dictionary<object, TaskCompletionSource<bool>>();
        private static readonly Dictionary<object, byte> ConfirmedRewardObjects = new Dictionary<object, byte>();
        private static readonly Dictionary<string, byte> RouteRequests = new Dictionary<string, byte>();
        private static readonly Dictionary<string, byte> MoveRequests = new Dictionary<string, byte>();
        private static string RouteStateGeneration = null;
        private static bool RewardGateExitReplay = false;
        private static TaskCompletionSource<bool> RewardGateReadyWaiter = null;
        private static TaskCompletionSource<MegaCrit.Sts2.Core.Rooms.AbstractRoom> RewardGateExitWaiter = null;
        private static readonly Dictionary<object, byte> MoveReplayActions = new Dictionary<object, byte>();
        private static readonly Dictionary<string, byte> DeferredMapVotes = new Dictionary<string, byte>();
        private static bool RewardBatchActive = false;
        private static string RewardTransactionGeneration = null;
        private static readonly Dictionary<NRewardsScreen, TaskCompletionSource<bool>> RewardScreenWaiters = new Dictionary<NRewardsScreen, TaskCompletionSource<bool>>();
        private static readonly Dictionary<NRewardsScreen, NRewardsScreen.CompletedEventHandler> RewardScreenHandlers = new Dictionary<NRewardsScreen, NRewardsScreen.CompletedEventHandler>();
        private static readonly HashSet<object> DeferredMapMoveProducers = new HashSet<object>();

        public static void BeginRewardBatch()
        {
            lock (RewardGateSync)
            {
                ResetAbandonedRewardTransaction();
                if (RewardBatchActive || (RewardGateActive && !RewardGateBackendCompleted)) throw new InvalidOperationException("Previous reward transaction incomplete");
                RewardTransactionGeneration = CurrentRunGeneration();
                RewardBatchActive = true;
                RewardGateActive = false;
                RewardGateBackendCompleted = false;
                RewardGateReadyWaiter = new TaskCompletionSource<bool>();
                RewardGateExitWaiter = null;
                RewardScreenWaiters.Clear();
                GD.Print("[AiTeammate][Flow] reward-set-begin scope=per-owner-batch");
            }
        }

        public static void TrackRewardScreen(NRewardsScreen screen)
        {
            if (RewardScreenWaiters.ContainsKey(screen)) return;
            var screenCompletion = new TaskCompletionSource<bool>();
            RewardScreenWaiters[screen] = screenCompletion;
            NRewardsScreen.CompletedEventHandler handler = delegate
            {
                if (!RewardScreenWaiters.ContainsKey(screen)) return;
                RewardBackendFlow.LogScreen("Completed", screen, true);
                MarkRewardScreenCompleted(screen, "native-Completed");
                MarkRewardCompletedSignal(screen);
                if (screen.IsComplete && IsRewardSetCompleteInBackend(AccessTools.Field(typeof(NRewardsScreen), "_rewardsSet").GetValue(screen)))
                    screenCompletion.TrySetResult(true);
                else GD.PrintErr("[AiTeammate][Flow] reward-completed-rejected backend=false");
            };
            RewardScreenHandlers[screen] = handler;
            screen.Completed += handler;
        }

        public static void CancelRewardBatch(string reason)
        {
            lock (RewardGateSync)
            {
                RewardBatchActive = false; RewardGateActive = false; RewardGateBackendCompleted = false;
                RewardGateScreen = null; RewardGateSet = null; RewardGateExitReplay = false;
                if (RewardGateReadyWaiter != null) RewardGateReadyWaiter.TrySetCanceled();
                if (RewardGateExitWaiter != null) RewardGateExitWaiter.TrySetCanceled();
                foreach (var pair in RewardScreenHandlers)
                    if (GodotObject.IsInstanceValid(pair.Key)) pair.Key.Completed -= pair.Value;
                RewardScreenHandlers.Clear();
                foreach (var waiter in RewardScreenWaiters.Values) waiter.TrySetCanceled();
                foreach (var waiter in RewardSelectionWaiters.Values) waiter.TrySetCanceled();
                RewardScreenWaiters.Clear(); RewardSelectionWaiters.Clear();
                RewardGateExpected.Clear(); RewardGateConfirmed.Clear(); RewardGateStableIds.Clear();
                RewardGateRewardObjects.Clear(); ConfirmedRewardObjects.Clear();
                DeferredMapVotes.Clear(); DeferredMapMoveProducers.Clear();
                GD.Print("[AiTeammate][Flow] reward-waiters-cancelled reason=" + reason);
            }
        }

        public static Task WaitForRewardScreen(NRewardsScreen screen) { return RewardScreenWaiters[screen].Task; }

        private static void ResetAbandonedRewardTransaction()
        {
            if (RewardTransactionGeneration == null || RewardTransactionGeneration == CurrentRunGeneration()) return;
            RewardBatchActive = false; RewardGateActive = false; RewardGateBackendCompleted = false;
            RewardGateScreen = null; RewardGateSet = null; RewardGateExitReplay = false;
            if (RewardGateReadyWaiter != null) RewardGateReadyWaiter.TrySetCanceled();
            if (RewardGateExitWaiter != null) RewardGateExitWaiter.TrySetCanceled();
            foreach (var waiter in RewardScreenWaiters.Values) waiter.TrySetCanceled();
            GD.Print("[AiTeammate][Flow] reward-set-abandoned reason=run-generation-changed");
            RewardTransactionGeneration = CurrentRunGeneration();
        }

        public static void CompleteRewardBatch()
        {
            lock (RewardGateSync)
            {
                if (!RewardGateBackendCompleted) throw new InvalidOperationException("Final reward screen not confirmed");
                RewardBatchActive = false;
                GD.Print("[AiTeammate][Flow] reward-set-complete scope=per-owner-batch");
                GD.Print("[AiTeammate][Flow] room-exit-gate-release source=per-owner-backend-complete");
                RewardGateReadyWaiter.TrySetResult(true);
                Callable.From(ReplayBlockedRoomExit).CallDeferred();
            }
        }

        public static async void DeferMapMoveProducer(object synchronizer)
        {
            if (!DeferredMapMoveProducers.Add(synchronizer)) return;
            string generation = CurrentRunGeneration();
            GD.Print("[AiTeammate][Flow] room-exit-gate-wait source=MapSelectionSynchronizer.MoveToMapCoord");
            try
            {
                await WaitForRewardGate();
                Callable.From(delegate
                {
                    try { if (generation == CurrentRunGeneration()) AccessTools.Method(synchronizer.GetType(), "MoveToMapCoord").Invoke(synchronizer, null); }
                    catch (Exception ex) { GD.PrintErr("[AiTeammate][Flow] route-follow-rejected reason=producer-replay exception=" + ex); }
                    finally { DeferredMapMoveProducers.Remove(synchronizer); }
                }).CallDeferred();
            }
            catch (Exception ex) { DeferredMapMoveProducers.Remove(synchronizer); GD.PrintErr("[AiTeammate][Flow] route-follow-rejected exception=" + ex); }
        }

        public static void BeginRewardGate(NRewardsScreen screen)
        {
            lock (RewardGateSync)
            {
                // A new reward screen supersedes only stale, already-completed state.
                // Never silently replace an unfinished gate: that could release the
                // room-exit path without backend confirmation.
                if (RewardGateActive && !RewardGateBackendCompleted && RewardGateScreen != null && !Object.ReferenceEquals(RewardGateScreen, screen))
                {
                    GD.PrintErr("[AiTeammate][Flow] reward-set-begin-rejected reason=previous-gate-incomplete key=" + (RewardGateKey ?? "unknown"));
                    return;
                }
                RewardGateActive = true;
                RewardTransactionGeneration = CurrentRunGeneration();
                RewardGateScreenCompleted = false;
                RewardGateBackendCompleted = false;
                RewardGateSetCompleted = false;
                RewardGateCompletedSignal = false;
                RewardGateConfirmed.Clear();
                RewardGateRewardObjects.Clear();
                RewardGateExpected.Clear();
                RewardGateStableIds.Clear();
                RewardSelectionWaiters.Clear();
                ConfirmedRewardObjects.Clear();
                if (!RewardBatchActive) RewardGateReadyWaiter = new TaskCompletionSource<bool>();
                if (!RewardBatchActive) RewardGateExitWaiter = null;
                RewardGateExitReplay = false;
                RewardGateScreen = screen;
                RewardGateSet = null;
                string key = "unknown";
                try
                {
                    FieldInfo setField = AccessTools.Field(typeof(NRewardsScreen), "_rewardsSet");
                    RewardGateSet = setField != null ? setField.GetValue(screen) : null;
                    var actualSet = RewardGateSet as MegaCrit.Sts2.Core.Rewards.RewardsSet;
                    if (actualSet != null)
                    {
                        foreach (var reward in actualSet.Rewards)
                        {
                            if (reward == null) continue;
                            RewardGateRewardObjects[reward] = 0;
                            // CardReward contents change during native selection. Identity is
                            // captured once, never recomputed from the selected card/options.
                            string fp = "reward=" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(reward);
                            RewardGateStableIds[reward] = fp;
                            int n;
                            RewardGateExpected.TryGetValue(fp, out n);
                            RewardGateExpected[fp] = n + 1;
                        }
                    }
                    key = "set=" + (RewardGateSet != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(RewardGateSet).ToString() : "unknown") + " rewards=" + RewardGateExpected.Count;
                }
                catch { }
                RewardGateKey = key;
                GD.Print("[AiTeammate][Flow] reward-set-begin key=" + key);
            }
        }

        public static void MarkRewardBackendConfirmed(object reward, string source)
        {
            lock (RewardGateSync)
            {
                if (!RewardGateActive) return;
                if (reward == null || !RewardGateRewardObjects.ContainsKey(reward))
                {
                    GD.PrintErr("[AiTeammate][Flow] reward-selection-backend-confirmation-ignored reason=reward-not-in-active-set source=" + (source ?? "unknown"));
                    return;
                }
                if (ConfirmedRewardObjects.ContainsKey(reward))
                {
                    GD.Print("[AiTeammate][Flow] reward-selection-backend-confirmed-deduped source=" + (source ?? "unknown") + " fingerprint=" + RewardFingerprint(reward));
                    return;
                }
                string fp;
                if (!RewardGateStableIds.TryGetValue(reward, out fp)) return;
                int expected;
                int confirmed;
                RewardGateExpected.TryGetValue(fp, out expected);
                RewardGateConfirmed.TryGetValue(fp, out confirmed);
                if (confirmed < expected) RewardGateConfirmed[fp] = confirmed + 1;
                if (reward != null) ConfirmedRewardObjects[reward] = 0;
                GD.Print("[AiTeammate][Flow] reward-selection-backend-confirmed owner=" + (source ?? "unknown") + " fingerprint=" + fp);
                TaskCompletionSource<bool> waiter;
                if (reward != null && RewardSelectionWaiters.TryGetValue(reward, out waiter))
                {
                    RewardSelectionWaiters.Remove(reward);
                    waiter.TrySetResult(true);
                }
                TryCompleteRewardGateLocked("backend-task");
            }
        }

        public static void ObserveRewardSelectionStart(MegaCrit.Sts2.Core.Rewards.Reward reward)
        {
            if (reward == null) return;
            string owner = "unknown";
            try
            {
                Player player = GetMember(reward, "Player") as Player;
                if (player != null)
                {
                    Player p2 = GetPlayer2();
                    owner = p2 != null && player.NetId == p2.NetId ? "P2" : "P1";
                }
            }
            catch { }
            GD.Print("[AiTeammate][Flow] reward-selection-start owner=" + owner + " type=" + reward.GetType().Name + " fingerprint=" + RewardFingerprint(reward));
        }

        public static void ObserveRewardSelectionTask(MegaCrit.Sts2.Core.Rewards.Reward reward, Task task)
        {
            if (reward == null || task == null) return;
            lock (RewardGateSync)
            {
                if (!RewardSelectionWaiters.ContainsKey(reward))
                    RewardSelectionWaiters[reward] = new TaskCompletionSource<bool>();
            }
            ObserveRewardSelectionStart(reward);
            ObserveRewardSelectionTaskAsync(reward, task);
        }

        private static async void ObserveRewardSelectionTaskAsync(MegaCrit.Sts2.Core.Rewards.Reward reward, Task task)
        {
            try
            {
                await task;
                string owner = "unknown";
                Player player = GetMember(reward, "Player") as Player;
                Player p2 = GetPlayer2();
                if (player != null) owner = p2 != null && player.NetId == p2.NetId ? "P2" : "P1";
                if (owner == "P1" && !reward.SuccessfullySelected)
                {
                    TaskCompletionSource<bool> waiter;
                    lock (RewardGateSync)
                    {
                        if (RewardSelectionWaiters.TryGetValue(reward, out waiter))
                        {
                            RewardSelectionWaiters.Remove(reward);
                            if (waiter != null) waiter.TrySetResult(false);
                        }
                    }
                    GD.Print("[AiTeammate][Flow] p1-unmanaged-reward-unclaimed-reset fingerprint=" + RewardFingerprint(reward));
                    return;
                }
                Callable.From(delegate { MarkRewardBackendConfirmed(reward, owner); }).CallDeferred();
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Flow] reward-selection-failed fingerprint=" + RewardFingerprint(reward) + " exception=" + ex);
                TaskCompletionSource<bool> waiter;
                lock (RewardGateSync)
                {
                    if (RewardSelectionWaiters.TryGetValue(reward, out waiter))
                    {
                        RewardSelectionWaiters.Remove(reward);
                        waiter.TrySetResult(false);
                    }
                }
            }
        }

        private static bool IsRewardSetCompleteInBackend(object set)
        {
            try
            {
                if (set == null || RunManager.Instance == null) return false;
                object sync = RunManager.Instance.RewardsSetSynchronizer;
                if (sync == null) return false;
                MethodInfo[] methods = sync.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method.Name != "IsRewardsSetCompleted" || method.ReturnType != typeof(bool)) continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(set))
                        return (bool)method.Invoke(sync, new object[] { set });
                }
                return false;
            }
            catch { return false; }
        }

        public static void MarkRewardSynchronizerCompleted(object set)
        {
            lock (RewardGateSync)
            {
                if (!RewardGateActive || RewardGateSet == null || !Object.ReferenceEquals(set, RewardGateSet)) return;
                RewardGateSetCompleted = true;
                TryCompleteRewardGateLocked("synchronizer");
            }
        }

        public static void RecheckRewardGate(string source)
        {
            lock (RewardGateSync) TryCompleteRewardGateLocked(source ?? "synchronizer-change");
        }

        public static void ForceCompleteRewardScreenFromProceed(NRewardsScreen screen, string reason)
        {
            lock (RewardGateSync)
            {
                if (screen != null)
                {
                    try
                    {
                        FieldInfo setField = AccessTools.Field(typeof(NRewardsScreen), "_rewardsSet");
                        var actualSet = (setField != null ? setField.GetValue(screen) : null) as MegaCrit.Sts2.Core.Rewards.RewardsSet;
                        if (actualSet != null && actualSet.Rewards != null)
                        {
                            foreach (var r in actualSet.Rewards)
                            {
                                if (r != null) MarkRewardBackendConfirmed(r, reason ?? "P1-proceed");
                            }
                        }
                    }
                    catch { }
                }

                if (RewardGateActive && (RewardGateScreen == null || Object.ReferenceEquals(screen, RewardGateScreen)))
                {
                    RewardGateScreenCompleted = true;
                    RewardGateCompletedSignal = true;
                    GD.Print("[AiTeammate][Flow] reward-screen-completed reason=" + (reason ?? "P1-proceed"));
                    TryCompleteRewardGateLocked(reason ?? "P1-proceed");
                }

                TaskCompletionSource<bool> screenWaiter;
                if (screen != null && RewardScreenWaiters.TryGetValue(screen, out screenWaiter))
                {
                    if (screenWaiter != null && !screenWaiter.Task.IsCompleted)
                    {
                        screenWaiter.TrySetResult(true);
                    }
                }
            }
        }

        private static void TryCompleteRewardGateLocked(string reason)
        {
            if (!RewardGateActive || !RewardGateScreenCompleted || !RewardGateCompletedSignal) return;
            foreach (KeyValuePair<string, int> pair in RewardGateExpected)
            {
                int n;
                if (!RewardGateConfirmed.TryGetValue(pair.Key, out n) || n < pair.Value) return;
            }
            RewardGateSetCompleted = IsRewardSetCompleteInBackend(RewardGateSet);
            if (!RewardGateSetCompleted) return;
            if (RewardGateBackendCompleted) return;
            RewardGateBackendCompleted = true;
            GD.Print("[AiTeammate][Flow] " + (RewardBatchActive ? "reward-owner-set-confirmed" : "reward-set-complete") + " key=" + (RewardGateKey ?? "unknown") + " reason=" + reason);
            if (!RewardBatchActive)
            {
                if (RewardGateReadyWaiter != null) RewardGateReadyWaiter.TrySetResult(true);
                Callable.From(ReplayBlockedRoomExit).CallDeferred();
            }
        }

        public static bool AreAllRewardButtonsResolved(NRewardsScreen screen)
        {
            try
            {
                FieldInfo f = AccessTools.Field(typeof(NRewardsScreen), "_rewardButtons");
                IList list = f != null ? f.GetValue(screen) as IList : null;
                if (list == null) return false;
                if (list.Count == 0) return true;
                for (int i = 0; i < list.Count; i++)
                {
                    object b = list[i];
                    if (b == null) continue;
                    bool resolved = false;
                    foreach (string n in new string[] { "IsResolved", "Resolved", "IsClaimed", "Claimed", "IsSkipped", "Skipped" })
                    {
                        PropertyInfo p = AccessTools.Property(b.GetType(), n);
                        if (p != null && p.PropertyType == typeof(bool) && (bool)p.GetValue(b, null)) { resolved = true; break; }
                    }
                    if (!resolved) return false;
                }
                return true;
            }
            catch { return false; }
        }

        public static void MarkRewardScreenCompleted(NRewardsScreen screen, string reason)
        {
            lock (RewardGateSync)
            {
                if (!RewardGateActive || !Object.ReferenceEquals(screen, RewardGateScreen)) return;
                if (!AreAllRewardButtonsResolved(screen)) return;
                bool complete = false;
                try { complete = screen.IsComplete; } catch { }
                if (!complete) return;
                RewardGateScreenCompleted = true;
                GD.Print("[AiTeammate][Flow] reward-screen-completed reason=" + (reason ?? "unknown"));
                TryCompleteRewardGateLocked(reason ?? "screen");
            }
        }

        public static void MarkRewardCompletedSignal(NRewardsScreen screen)
        {
            lock (RewardGateSync)
            {
                if (!RewardGateActive || !Object.ReferenceEquals(screen, RewardGateScreen)) return;
                RewardGateCompletedSignal = true;
                TryCompleteRewardGateLocked("completed-signal");
            }
        }

        // Directive #5.32: Dedicated completion path for event card rewards
        public static void TryCompleteEventRewardGate(string trigger)
        {
            lock (RewardGateSync)
            {
                if (!RewardGateActive) return;
                if (RewardGateBackendCompleted) return;

                // Check whether this is an event reward screen (managed == false)
                bool isManaged = false;
                if (RewardGateScreen != null)
                {
                    isManaged = RewardBackendFlow.IsManagedScreen(RewardGateScreen);
                }
                else if (RewardBatchActive)
                {
                    isManaged = true;
                }

                if (isManaged)
                {
                    // Managed (combat) rewards must use the strict gate, not this event path!
                    return;
                }

                // Verify the 6 conditions
                // Condition 1: RewardsSet backend state is complete
                bool setComplete = false;
                object set = RewardGateSet;
                if (set == null && RewardGateScreen != null)
                {
                    try
                    {
                        FieldInfo f = AccessTools.Field(typeof(NRewardsScreen), "_rewardsSet");
                        set = f != null ? f.GetValue(RewardGateScreen) : null;
                    }
                    catch { }
                }
                if (set != null)
                {
                    setComplete = IsRewardSetCompleteInBackend(set);
                }
                else
                {
                    setComplete = true;
                }

                if (!setComplete)
                {
                    GD.Print(string.Format("[AiTeammate][Flow] event-reward-gate-waiting trigger={0} missing=rewards-set-incomplete", trigger ?? "unknown"));
                    return;
                }

                // Condition 2 & 3: All opened card selection Tasks are backend-confirmed and CardReward selection completed
                bool tasksConfirmed = true;
                foreach (KeyValuePair<string, int> pair in RewardGateExpected)
                {
                    int n;
                    if (!RewardGateConfirmed.TryGetValue(pair.Key, out n) || n < pair.Value)
                    {
                        tasksConfirmed = false;
                        break;
                    }
                }
                if (tasksConfirmed)
                {
                    foreach (var waiter in RewardSelectionWaiters.Values)
                    {
                        if (waiter != null && !waiter.Task.IsCompleted)
                        {
                            tasksConfirmed = false;
                            break;
                        }
                    }
                }
                if (!tasksConfirmed)
                {
                    GD.Print(string.Format("[AiTeammate][Flow] event-reward-gate-waiting trigger={0} missing=reward-tasks-unconfirmed", trigger ?? "unknown"));
                    return;
                }

                // Condition 4: NCardRewardSelectionScreen has been removed/popped
                // Condition 5: NRewardsScreen has been removed/popped or IsComplete
                bool cardScreenRemoved = true;
                bool rewardScreenRemovedOrComplete = true;

                try
                {
                    if (NOverlayStack.Instance != null)
                    {
                        FieldInfo fOverlays = AccessTools.Field(typeof(NOverlayStack), "_overlays");
                        IList overlays = fOverlays != null ? fOverlays.GetValue(NOverlayStack.Instance) as IList : null;
                        if (overlays != null)
                        {
                            for (int i = 0; i < overlays.Count; i++)
                            {
                                object ov = overlays[i];
                                if (ov is NCardRewardSelectionScreen)
                                {
                                    cardScreenRemoved = false;
                                }
                                if (ov is NRewardsScreen)
                                {
                                    NRewardsScreen rs = (NRewardsScreen)ov;
                                    if (!rs.IsComplete)
                                    {
                                        rewardScreenRemovedOrComplete = false;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Flow] check overlay stack exception: " + ex);
                }

                if (!cardScreenRemoved)
                {
                    GD.Print(string.Format("[AiTeammate][Flow] event-reward-gate-waiting trigger={0} missing=card-selection-screen-open", trigger ?? "unknown"));
                    return;
                }
                if (!rewardScreenRemovedOrComplete)
                {
                    GD.Print(string.Format("[AiTeammate][Flow] event-reward-gate-waiting trigger={0} missing=rewards-screen-incomplete", trigger ?? "unknown"));
                    return;
                }

                // Condition 6: P2 has no pending choice
                if (TurnCoordinator.P2PendingChoiceCount > 0)
                {
                    GD.Print(string.Format("[AiTeammate][Flow] event-reward-gate-waiting trigger={0} missing=p2-pending-choices({1})", trigger ?? "unknown", TurnCoordinator.P2PendingChoiceCount));
                    return;
                }

                // All conditions met: Idempotently complete gate!
                RewardGateScreenCompleted = true;
                RewardGateCompletedSignal = true;
                RewardGateSetCompleted = true;
                RewardGateBackendCompleted = true;

                string gateKey = RewardGateKey ?? "event-reward";
                GD.Print(string.Format("[AiTeammate][Flow] event-reward-gate-complete owner=P1 key={0}", gateKey));
                GD.Print(string.Format("[AiTeammate][Flow] reward-set-complete owner=P1 key={0} reason=event-card-overlay-removed", gateKey));
                GD.Print("[AiTeammate][Flow] room-exit-gate-release source=event-card-reward");

                if (RewardGateReadyWaiter != null && !RewardGateReadyWaiter.Task.IsCompleted)
                {
                    RewardGateReadyWaiter.TrySetResult(true);
                }
                Callable.From(ReplayBlockedRoomExit).CallDeferred();
            }
        }

        public static bool IsRewardGateReleased(string source)
        {
            lock (RewardGateSync)
            {
                TryCompleteRewardGateLocked(source ?? "gate-check");
                if (!RewardBatchActive && (!RewardGateActive || RewardGateBackendCompleted)) return true;
                GD.Print("[AiTeammate][Flow] room-exit-gate-wait source=" + (source ?? "unknown") + " expected=" + RewardGateExpected.Count + " screen=" + RewardGateScreenCompleted + " signal=" + RewardGateCompletedSignal + " sync=" + RewardGateSetCompleted);
                return false;
            }
        }

        public static void ReleaseRewardGate(string source)
        {
            lock (RewardGateSync)
            {
                if (RewardBatchActive || !RewardGateActive || !RewardGateBackendCompleted) return;
                RewardGateActive = false;
                RewardGateScreen = null;
                RewardGateSet = null;
                GD.Print("[AiTeammate][Flow] room-exit-gate-release source=" + (source ?? "unknown"));
            }
        }

        public static Task WaitForRewardGate()
        {
            lock (RewardGateSync)
            {
                if (!RewardBatchActive && (!RewardGateActive || RewardGateBackendCompleted)) return Task.CompletedTask;
                if (RewardGateReadyWaiter == null) RewardGateReadyWaiter = new TaskCompletionSource<bool>();
                return RewardGateReadyWaiter.Task;
            }
        }

        public static bool BeforeExitCurrentRoom(ref Task<MegaCrit.Sts2.Core.Rooms.AbstractRoom> result)
        {
            lock (RewardGateSync)
            {
                if (RewardGateExitReplay)
                {
                    RewardGateExitReplay = false;
                    RewardGateActive = false;
                    RewardGateScreen = null;
                    RewardGateSet = null;
                    GD.Print("[AiTeammate][Flow] room-exit-gate-release source=exit-replay");
                    return true;
                }
                if (!RewardBatchActive && (!RewardGateActive || RewardGateBackendCompleted))
                {
                    if (RewardGateActive && RewardGateBackendCompleted)
                    {
                        RewardGateActive = false;
                        RewardGateScreen = null;
                        RewardGateSet = null;
                        GD.Print("[AiTeammate][Flow] room-exit-gate-release source=backend-complete");
                    }
                    return true;
                }
                if (RewardGateExitWaiter == null) RewardGateExitWaiter = new TaskCompletionSource<MegaCrit.Sts2.Core.Rooms.AbstractRoom>();
                result = RewardGateExitWaiter.Task;
                GD.Print("[AiTeammate][Flow] room-exit-gate-wait source=RunManager.ExitCurrentRoom key=" + (RewardGateKey ?? "unknown"));
                return false;
            }
        }

        public static Task<MegaCrit.Sts2.Core.Rooms.AbstractRoom> BlockRoomExitOnGateError(Exception exception)
        {
            return Task.FromException<MegaCrit.Sts2.Core.Rooms.AbstractRoom>(exception ?? new InvalidOperationException("Reward room-exit gate failed"));
        }

        private static async void ReplayBlockedRoomExit()
        {
            TaskCompletionSource<MegaCrit.Sts2.Core.Rooms.AbstractRoom> waiter;
            lock (RewardGateSync)
            {
                waiter = RewardGateExitWaiter;
                if (waiter == null || waiter.Task.IsCompleted || RewardGateExitReplay) return;
                RewardGateExitReplay = true;
            }
            try
            {
                RunManager manager = RunManager.Instance;
                if (manager == null) throw new InvalidOperationException("RunManager unavailable after reward completion");
                MethodInfo exitMethod = AccessTools.Method(manager.GetType(), "ExitCurrentRoom");
                if (exitMethod == null) throw new MissingMethodException(manager.GetType().FullName, "ExitCurrentRoom");
                Task replayTask = exitMethod.Invoke(manager, null) as Task;
                if (replayTask == null) throw new InvalidOperationException("ExitCurrentRoom did not return Task");
                await replayTask;
                MegaCrit.Sts2.Core.Rooms.AbstractRoom room = null;
                PropertyInfo replayResult = replayTask.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
                if (replayResult != null) room = replayResult.GetValue(replayTask, null) as MegaCrit.Sts2.Core.Rooms.AbstractRoom;
                waiter.TrySetResult(room);
                ReleaseRewardGate("exit-replay");
            }
            catch (Exception ex)
            {
                lock (RewardGateSync) RewardGateExitReplay = false;
                waiter.TrySetException(ex);
                GD.PrintErr("[AiTeammate][Flow] room-exit-gate-replay-failed exception=" + ex);
            }
        }

        public static async Task ReplayMoveAfterRewardGate(object action, MethodInfo executeMethod, string key)
        {
            string requestedGeneration = CurrentRunGeneration();
            await WaitForRewardGate();
            if (requestedGeneration != CurrentRunGeneration()) { GD.Print("[AiTeammate][Flow] route-follow-rejected reason=stale-generation key=" + key); return; }
            bool reserved = false;
            lock (RewardGateSync)
            {
                if (MoveRequests.ContainsKey(key))
                {
                    GD.Print("[AiTeammate][Flow] route-follow-deduped action=MoveToMapCoordAction key=" + key);
                    return;
                }
                MoveRequests[key] = 0;
                MoveReplayActions[action] = 0;
                reserved = true;
                string destKey = DestinationKey(action) ?? "unknown";
                GD.Print("[AiTeammate][Flow] route-follow-replay owner=P1 destination=" + destKey + " key=" + key);
                GD.Print("[AiTeammate][Flow] route-follow-request action=MoveToMapCoordAction key=" + key);
            }
            try
            {
                Task task = executeMethod.Invoke(action, null) as Task;
                if (task != null) await task;
            }
            finally
            {
                lock (RewardGateSync)
                {
                    MoveReplayActions.Remove(action);
                    if (!reserved) MoveRequests.Remove(key);
                }
            }
        }

        public static bool IsMoveReplay(object action)
        {
            lock (RewardGateSync) return MoveReplayActions.ContainsKey(action);
        }

        public static string CurrentRunGeneration()
        {
            try
            {
                object state = RunManager.Instance != null ? RunManager.Instance.DebugOnlyGetState() : null;
                return state != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(state).ToString() + ":act=" + GetMember(state, "CurrentActIndex") + ":reward-epoch=" + RewardBackendFlow.RouteEpoch : "no-run";
            }
            catch { return "unknown-run"; }
        }

        public static void ResetRewardRouteState()
        {
            lock (RewardGateSync)
            {
                ResetAbandonedRewardTransaction();
                string generation = CurrentRunGeneration();
                if (!string.Equals(RouteStateGeneration, generation, StringComparison.Ordinal))
                {
                    RouteRequests.Clear();
                    MoveRequests.Clear();
                    MoveReplayActions.Clear();
                    DeferredMapVotes.Clear();
                    RouteStateGeneration = generation;
                }
                if (!RewardBatchActive && RewardGateBackendCompleted)
                {
                    RewardGateActive = false;
                    RewardGateScreen = null;
                    RewardGateSet = null;
                }
            }
        }

        public static bool TryReserveRoute(string generation, string destination)
        {
            lock (RewardGateSync)
            {
                if (!string.Equals(RouteStateGeneration, generation, StringComparison.Ordinal))
                {
                    RouteRequests.Clear();
                    MoveRequests.Clear();
                    MoveReplayActions.Clear();
                    DeferredMapVotes.Clear();
                    RouteStateGeneration = generation;
                }
                if (RewardBatchActive || (RewardGateActive && !RewardGateBackendCompleted))
                {
                    GD.Print("[AiTeammate][Flow] route-follow-rejected reason=reward-set-incomplete destination=" + (destination ?? "unknown"));
                    return false;
                }
                string key = (generation ?? "unknown") + "|" + (destination ?? "unknown");
                if (RouteRequests.ContainsKey(key))
                {
                    GD.Print("[AiTeammate][Flow] route-follow-deduped key=" + key);
                    return false;
                }
                RouteRequests[key] = 0;
                GD.Print("[AiTeammate][Flow] route-follow-request key=" + key);
                return true;
            }
        }

        public static bool TryReserveMove(string generation, string destination)
        {
            lock (RewardGateSync)
            {
                if (!string.Equals(RouteStateGeneration, generation, StringComparison.Ordinal))
                {
                    RouteRequests.Clear();
                    MoveRequests.Clear();
                    MoveReplayActions.Clear();
                    DeferredMapVotes.Clear();
                    RouteStateGeneration = generation;
                }
                if (RewardBatchActive || (RewardGateActive && !RewardGateBackendCompleted))
                {
                    GD.Print("[AiTeammate][Flow] route-follow-rejected action=MoveToMapCoordAction reason=reward-set-incomplete destination=" + (destination ?? "unknown"));
                    return false;
                }
                string key = (generation ?? "unknown") + "|" + (destination ?? "unknown");
                if (MoveRequests.ContainsKey(key))
                {
                    GD.Print("[AiTeammate][Flow] route-follow-deduped action=MoveToMapCoordAction key=" + key);
                    return false;
                }
                MoveRequests[key] = 0;
                GD.Print("[AiTeammate][Flow] MoveToMapCoordAction key=" + key);
                return true;
            }
        }

        public static bool BeforeMoveToMapCoordAction(object action, ref Task result)
        {
            if (action == null) return true;
            if (IsMoveReplay(action)) { GD.Print("[AiTeammate][Flow] MoveToMapCoordAction source=gate-replay"); return true; }
            TryCompleteEventRewardGate("before-move-action");
            string destination = DestinationKey(action);
            if (string.IsNullOrEmpty(destination)) destination = action.ToString();
            string generation = CurrentRunGeneration();
            string key = generation + "|" + destination;
            if (IsRewardGateActive())
            {
                GD.Print("[AiTeammate][Flow] route-follow-rejected action=MoveToMapCoordAction reason=reward-set-incomplete destination=" + destination);
                MethodInfo execute = AccessTools.Method(action.GetType(), "ExecuteAction");
                if (execute == null)
                {
                    GD.PrintErr("[AiTeammate][Flow] MoveToMapCoordAction blocked reason=execute-method-unavailable destination=" + destination);
                    result = Task.CompletedTask;
                    return false;
                }
                result = ReplayMoveAfterRewardGate(action, execute, key);
                return false;
            }
            if (!TryReserveMove(generation, destination))
            {
                result = Task.CompletedTask;
                return false;
            }
            return true;
        }

        public static bool BeforeMapVote(object synchronizer, Player player, MegaCrit.Sts2.Core.Runs.MapLocation source, object destination)
        {
            TryCompleteEventRewardGate("before-map-vote");
            if (!IsRewardGateActive()) return true;
            string target = DestinationKey(destination) ?? "unknown";
                // Preserve the first pending vote per owner and transaction. Repeated map
                // clicks must not enqueue competing destinations while rewards are held.
                string key = CurrentRunGeneration() + "|" + (player != null ? player.NetId.ToString() : "unknown") + "|pending-vote";
            lock (RewardGateSync)
            {
                if (DeferredMapVotes.ContainsKey(key))
                {
                    GD.Print("[AiTeammate][Flow] route-follow-deduped action=PlayerVotedForMapCoord key=" + key);
                    return false;
                }
                DeferredMapVotes[key] = 0;
            }
            GD.Print("[AiTeammate][Flow] route-follow-request state=pending action=PlayerVotedForMapCoord reason=reward-set-incomplete source=" + source + " destination=" + target);
            ReplayMapVoteAfterRewardGate(synchronizer, player, source, destination, key);
            return false;
        }

        private static async void ReplayMapVoteAfterRewardGate(object synchronizer, Player player, MegaCrit.Sts2.Core.Runs.MapLocation source, object destination, string key)
        {
            string generation = CurrentRunGeneration();
            try { await WaitForRewardGate(); }
            catch (OperationCanceledException) { lock (RewardGateSync) DeferredMapVotes.Remove(key); return; }
            Callable.From(delegate
            {
                try
                {
                    if (generation != CurrentRunGeneration()) { GD.Print("[AiTeammate][Flow] route-follow-rejected reason=stale-generation key=" + key); return; }
                    if (synchronizer != null && player != null)
                    {
                        MethodInfo method = AccessTools.Method(synchronizer.GetType(), "PlayerVotedForMapCoord");
                        if (method != null)
                        {
                            GD.Print("[AiTeammate][Flow] route-follow-replay owner=" + (player != null ? player.NetId.ToString() : "unknown") + " destination=" + DestinationKey(destination) + " key=" + key);
                            method.Invoke(synchronizer, new object[] { player, source, destination });
                        }
                    }
                }
                catch (Exception ex) { GD.PrintErr("[AiTeammate][Flow] route-vote-replay-failed key=" + key + " exception=" + ex); }
                finally { lock (RewardGateSync) DeferredMapVotes.Remove(key); }
            }).CallDeferred();
        }

        public static string DestinationKey(object value)
        {
            if (value == null) return null;
            try
            {
                PropertyInfo hasValue = value.GetType().GetProperty("HasValue");
                if (hasValue != null && hasValue.PropertyType == typeof(bool) && !(bool)hasValue.GetValue(value, null)) return null;
                PropertyInfo valueProp = value.GetType().GetProperty("Value");
                if (valueProp != null)
                {
                    object inner = valueProp.GetValue(value, null);
                    if (inner != null) value = inner;
                }
                object coord = GetMember(value, "Coord") ?? GetMember(value, "coord") ?? GetMember(value, "Destination") ?? GetMember(value, "destination") ?? GetMember(value, "_destination");
                return coord != null ? coord.ToString() : value.ToString();
            }
            catch { return value.ToString(); }
        }

        public static bool IsRewardGateActive()
        {
            lock (RewardGateSync) return RewardBatchActive || (RewardGateActive && !RewardGateBackendCompleted);
        }

        public static void BeginRewardSelectionFromButton(object button, Task task)
        {
            try
            {
                object reward = button != null ? GetMember(button, "Reward") : null;
                if (reward == null || task == null) return;
                if (RewardBackendFlow.Owns(reward as MegaCrit.Sts2.Core.Rewards.Reward)) return;
                ObserveRewardSelectionTask(reward as MegaCrit.Sts2.Core.Rewards.Reward, task);
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][Flow] reward-selection-observer-failed exception=" + ex); }
        }

        public static Task<bool> WaitForRewardSelection(object reward)
        {
            lock (RewardGateSync)
            {
                TaskCompletionSource<bool> waiter;
                if (reward == null) return Task.FromResult(false);
                if (ConfirmedRewardObjects.ContainsKey(reward)) return Task.FromResult(true);
                if (!RewardSelectionWaiters.TryGetValue(reward, out waiter))
                {
                    waiter = new TaskCompletionSource<bool>();
                    RewardSelectionWaiters[reward] = waiter;
                }
                return waiter.Task;
            }
        }

        public static bool SpireStrategistAvailable = false;

        public static Assembly SpireStrategistAssembly = null;



        public static object FindRestSiteSynchronizer() { try { var rm=RunManager.Instance; if(rm==null)return null; var rt=AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.RestSiteSynchronizer"); if(rt==null)return null; foreach(var p in rm.GetType().GetProperties(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)) if(rt.IsAssignableFrom(p.PropertyType)){try{return p.GetValue(rm,null);}catch{}} foreach(var f in rm.GetType().GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)) if(rt.IsAssignableFrom(f.FieldType)){try{return f.GetValue(rm);}catch{}} }catch{} return null; }
        public static bool HasPendingP2Choice(Player p2) { try { if(p2==null)return false; if(TurnCoordinator.P2PendingChoiceCount>0)return true; object r=FindRestSiteSynchronizer(); if(r!=null){var m=AccessTools.Method(r.GetType(),"GetOptionsForPlayer",new Type[]{typeof(Player)}); var x=m!=null?m.Invoke(r,new object[]{p2}) as ICollection:null; if(x!=null&&x.Count>0)return true;} }catch{} return false; }
        public static bool ResolveAllPendingP2Choices(string reason) { bool any=false; try { Player p2=GetPlayer2(); if(p2==null)return false; if(RunManager.Instance!=null&&RunManager.Instance.EventSynchronizer!=null) any=ResolveP2PendingEvent(RunManager.Instance.EventSynchronizer,p2)||any; any=ResolvePendingP2RestSiteOnSettle(reason)||any; }catch(Exception ex){GD.PrintErr("[AiTeammate][Flow] ResolveAllPendingP2Choices exception: "+ex);} return any||TurnCoordinator.P2PendingChoiceCount==0; }
        public static bool ResolvePendingP2RestSiteOnSettle(string reason) { try { if(!AutoRestSiteEnabled)return false; Player p2=GetPlayer2(); object sync=FindRestSiteSynchronizer(); if(p2==null||sync==null)return false; bool ok=ResolveP2RestSite(sync,p2); if(ok)GD.Print("[AiTeammate][Flow] rest-settle-probe resolved reason="+(reason??"unknown")); return ok; }catch(Exception ex){GD.PrintErr("[AiTeammate][Flow] rest-settle-probe exception: "+ex); return false;} }

        // Directive #5.0.8 T1: Eradicate cross-ALC RunState/IRunState conversion exceptions

        // by resolving the SpireStrategist assembly loaded in the same ALC as live RunState.

        public static Assembly FindLiveSpireStrategistAssembly()

        {

            try

            {

                var liveRun = GetRunState();

                Type liveRunType = (liveRun != null) ? liveRun.GetType() : typeof(MegaCrit.Sts2.Core.Runs.RunState);



                // Priority 1: Match loaded assembly where SnapshotReader.Read accepts liveRunType

                Assembly bestMatch = null;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())

                {

                    if (asm.GetName().Name == "SpireStrategist")

                    {

                        bestMatch = asm;

                        Type readerType = asm.GetType("SpireStrategist.Mod.SnapshotReader");

                        if (readerType != null)

                        {

                            foreach (var m in readerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))

                            {

                                if (m.Name == "Read" && m.GetParameters().Length == 2)

                                {

                                    var pars = m.GetParameters();

                                    if (pars[0].ParameterType.IsAssignableFrom(liveRunType) || pars[0].ParameterType.FullName == liveRunType.FullName)

                                    {

                                        SpireStrategistAvailable = true;

                                        SpireStrategistAssembly = asm;

                                        return asm;

                                    }

                                }

                            }

                        }

                    }

                }



                // Priority 2: If none compatible in AppDomain, load into the live RunState's AssemblyLoadContext

                if (liveRunType != null && liveRunType.Assembly != null)

                {

                    try

                    {

                        Type alcType = Type.GetType("System.Runtime.Loader.AssemblyLoadContext, System.Runtime.Loader");

                        if (alcType != null)

                        {

                            MethodInfo getContextMethod = alcType.GetMethod("GetLoadContext", new Type[] { typeof(Assembly) });

                            if (getContextMethod != null)

                            {

                                object gameAlc = getContextMethod.Invoke(null, new object[] { liveRunType.Assembly });

                                if (gameAlc != null)

                                {

                                    string targetDll = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "mods", "SpireStrategist", "SpireStrategist.dll");

                                    if (System.IO.File.Exists(targetDll))

                                    {

                                        MethodInfo loadPathMethod = gameAlc.GetType().GetMethod("LoadFromAssemblyPath", new Type[] { typeof(string) });

                                        if (loadPathMethod != null)

                                        {

                                            Assembly loaded = loadPathMethod.Invoke(gameAlc, new object[] { System.IO.Path.GetFullPath(targetDll) }) as Assembly;

                                            if (loaded != null)

                                            {

                                                SpireStrategistAvailable = true;

                                                SpireStrategistAssembly = loaded;

                                                return loaded;

                                            }

                                        }

                                    }

                                }

                            }

                        }

                    }

                    catch (Exception exAlc)

                    {

                        GD.PrintErr("[AiTeammate][Flow] FindLiveSpireStrategistAssembly gameAlc load exception: " + exAlc.Message);

                    }

                }



                if (bestMatch != null)

                {

                    SpireStrategistAvailable = true;

                    SpireStrategistAssembly = bestMatch;

                    return bestMatch;

                }



                return SpireStrategistAssembly;

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Flow] FindLiveSpireStrategistAssembly exception: " + ex);

                return SpireStrategistAssembly;

            }

        }

        public static bool HiddenReaderFailed = false;



        public static Dictionary<string, int> OfferedCardSetCounts = new Dictionary<string, int>();

        public static Dictionary<string, double> CardScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        public static Dictionary<string, List<string>> CardTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);



        public static bool HasCardTag(string cardId, string tag)

        {

            if (string.IsNullOrEmpty(cardId) || string.IsNullOrEmpty(tag)) return false;

            List<string> tags;

            if (CardTags.TryGetValue(cardId, out tags) && tags.Contains(tag)) return true;

            if (cardId.StartsWith("CARD.") && CardTags.TryGetValue(cardId.Substring(5), out tags) && tags.Contains(tag)) return true;

            if (!cardId.StartsWith("CARD.") && CardTags.TryGetValue("CARD." + cardId, out tags) && tags.Contains(tag)) return true;

            return false;

        }



        public static List<string> GetCardTags(string cardId)

        {

            if (string.IsNullOrEmpty(cardId)) return new List<string>();

            List<string> tags;

            if (CardTags.TryGetValue(cardId, out tags)) return tags;

            if (cardId.StartsWith("CARD.") && CardTags.TryGetValue(cardId.Substring(5), out tags)) return tags;

            if (!cardId.StartsWith("CARD.") && CardTags.TryGetValue("CARD." + cardId, out tags)) return tags;

            return new List<string>();

        }



        public static void LoadCardScores()

        {

            try

            {

                var baseDir = AppDomain.CurrentDomain.BaseDirectory;

                string[] searchPaths = new string[]

                {

                    System.IO.Path.Combine(baseDir ?? "", "mods", "AiTeammate", "card_scores.json"),

                    System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "mods", "AiTeammate", "card_scores.json"),

                    System.IO.Path.Combine("mods", "AiTeammate", "card_scores.json"),

                    System.IO.Path.Combine("scratch", "card_scores.json")

                };

                string filePath = null;

                for (int i = 0; i < searchPaths.Length; i++)

                {

                    if (System.IO.File.Exists(searchPaths[i]))

                    {

                        filePath = searchPaths[i];

                        break;

                    }

                }

                if (filePath != null)

                {

                    string json = System.IO.File.ReadAllText(filePath);

                    int idx = 0;

                    while ((idx = json.IndexOf("\"id\":", idx)) >= 0)

                    {

                        int idStart = json.IndexOf('"', idx + 5);

                        if (idStart < 0) break;

                        int idEnd = json.IndexOf('"', idStart + 1);

                        if (idEnd < 0) break;

                        string id = json.Substring(idStart + 1, idEnd - idStart - 1);



                        int scoreIdx = json.IndexOf("\"score\":", idEnd);

                        if (scoreIdx < 0 || scoreIdx - idEnd > 500) { idx = idEnd; continue; }

                        int valStart = scoreIdx + 8;

                        while (valStart < json.Length && (json[valStart] == ' ' || json[valStart] == '\t')) valStart++;

                        int valEnd = valStart;

                        while (valEnd < json.Length && (char.IsDigit(json[valEnd]) || json[valEnd] == '.' || json[valEnd] == '-')) valEnd++;

                        string scoreStr = json.Substring(valStart, valEnd - valStart);

                        double scoreVal;

                        if (double.TryParse(scoreStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out scoreVal))

                        {

                            CardScores[id] = scoreVal;

                        }



                        // Directive #5.15 T3: Parse synergy tags

                        int tagsIdx = json.IndexOf("\"tags\":", idEnd);

                        if (tagsIdx >= 0 && tagsIdx < idEnd + 500)

                        {

                            int bracketStart = json.IndexOf('[', tagsIdx);

                            int bracketEnd = json.IndexOf(']', bracketStart);

                            if (bracketStart >= 0 && bracketEnd > bracketStart)

                            {

                                string tagsSub = json.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);

                                string[] rawTags = tagsSub.Split(new char[] { ',', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                                List<string> tList = new List<string>();

                                for (int rt = 0; rt < rawTags.Length; rt++)

                                {

                                    string cleanTag = rawTags[rt].Trim().Trim(new char[] { '"', '\'' });

                                    if (!string.IsNullOrEmpty(cleanTag)) tList.Add(cleanTag);

                                }

                                CardTags[id] = tList;

                            }

                        }



                        idx = valEnd;

                    }

                    GD.Print(string.Format("[AiTeammate][Flow] Loaded {0} card scores from {1}", CardScores.Count, filePath));

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Flow] LoadCardScores exception: " + ex.Message);

            }

        }



        public static double GetCardScore(string cardId)

        {

            if (string.IsNullOrEmpty(cardId)) return 50.0;

            double score;

            if (CardScores.TryGetValue(cardId, out score)) return score;

            if (cardId.StartsWith("CARD.") && CardScores.TryGetValue(cardId.Substring(5), out score)) return score;

            if (!cardId.StartsWith("CARD.") && CardScores.TryGetValue("CARD." + cardId, out score)) return score;

            return 60.0;

        }



        public static IRunState GetRunState()

        {

            try

            {

                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;

                if (rm != null)

                {

                    return rm.DebugOnlyGetState();

                }

            }

            catch { }

            return null;

        }



        public static Player GetPlayer1()

        {

            try

            {

                IRunState state = GetRunState();

                if (state != null && state.Players != null && state.Players.Count > 0)

                {

                    return state.Players[0];

                }

            }

            catch { }

            return null;

        }



        public static Player GetPlayer2()

        {

            try

            {

                IRunState state = GetRunState();

                if (state != null && state.Players != null && state.Players.Count > 1)

                {

                    return state.Players[1];

                }

            }

            catch { }

            return null;

        }



        public static bool IsPlayer2Owner(ulong netId)

        {

            Player p2 = GetPlayer2();

            if (p2 != null && p2.NetId == netId) return true;

            return false;

        }



        public static double CalculateCardDefaultScore(Player player, CardModel card)

        {

            if (card == null) return -999.0;



            double score = 0.0;



            string rarity = card.Rarity.ToString().ToLowerInvariant();

            if (rarity.Contains("rare"))

            {

                score += 65.0;

            }

            else if (rarity.Contains("uncommon"))

            {

                score += 45.0;

            }

            else if (rarity.Contains("common"))

            {

                score += 25.0;

            }

            else if (rarity.Contains("basic"))

            {

                score += 10.0;

            }

            else if (rarity.Contains("curse") || rarity.Contains("status"))

            {

                return -50.0;

            }

            else

            {

                score += 20.0;

            }



            string ctype = card.Type.ToString().ToLowerInvariant();

            if (ctype.Contains("power"))

            {

                score += 15.0;

            }

            else if (ctype.Contains("attack"))

            {

                int attackCount = 0;

                if (player != null && player.Deck != null && player.Deck.Cards != null)

                {

                    foreach (CardModel c in player.Deck.Cards)

                    {

                        if (c.Type.ToString().ToLowerInvariant().Contains("attack")) attackCount++;

                    }

                }

                IRunState run = GetRunState();

                bool isAct1 = run == null || run.CurrentActIndex == 0;

                if (isAct1 || attackCount < 6)

                {

                    score += 10.0;

                }

                else

                {

                    score += 5.0;

                }

            }

            else if (ctype.Contains("skill"))

            {

                score += 5.0;

            }



            int cost = card.EnergyCost != null ? card.EnergyCost.Canonical : 1;

            if (cost <= 1)

            {

                score += 5.0;

            }

            else if (cost >= 3)

            {

                score -= 5.0;

            }



            if (player != null && player.Deck != null && player.Deck.Cards != null)

            {

                int deckSize = player.Deck.Cards.Count;

                if (deckSize >= 25)

                {

                    score -= 10.0;

                }



                int duplicates = 0;

                string cardIdStr = card.Id.ToString();

                foreach (CardModel c in player.Deck.Cards)

                {

                    if (c.Id.ToString() == cardIdStr) duplicates++;

                }

                if (duplicates >= 2)

                {

                    score -= 15.0;

                }

            }



            return score;

        }



        public static RelicModel EvaluateRelicSelection(Player p2, IReadOnlyList<RelicModel> relics, out string pickId, out string source)

        {

            int dummyIndex;

            string dummyErr;

            string dummyScores;

            return EvaluateRelicSelection(p2, relics, out pickId, out source, out dummyIndex, out dummyErr, out dummyScores);

        }



        public static RelicModel EvaluateRelicSelection(Player p2, IReadOnlyList<RelicModel> relics, out string pickId, out string source, out int chosenIndex, out string coreError, out string scoresStr)

        {

            pickId = "none";

            source = "fallback";

            chosenIndex = 0;

            coreError = null;

            scoresStr = "none";



            if (relics == null || relics.Count == 0) return null;



            DateTime startTime = DateTime.UtcNow;

            RelicModel bestRelic = null;



            try

            {

                Assembly liveAsm = FindLiveSpireStrategistAssembly();

                if (liveAsm != null && p2 != null)

                {

                    IRunState run = GetRunState();

                    if (run != null)

                    {

                        Type snapReaderType = liveAsm.GetType("SpireStrategist.Mod.SnapshotReader");

                        Type relicValType = liveAsm.GetType("SpireStrategist.Core.RelicValue");

                        if (snapReaderType != null && relicValType != null)

                        {

                            MethodInfo readMethod = null;

                            foreach (var m in snapReaderType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))

                            {

                                if (m.Name == "Read" && m.GetParameters().Length == 2)

                                {

                                    readMethod = m;

                                    break;

                                }

                            }



                            MethodInfo evalMethod = null;

                            foreach (var m in relicValType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))

                            {

                                if (m.Name == "Evaluate" && m.GetParameters().Length >= 2)

                                {

                                    evalMethod = m;

                                    break;

                                }

                            }



                            if (readMethod != null && evalMethod != null)

                            {

                                object snapshot = readMethod.Invoke(null, new object[] { run, p2 });

                                if (snapshot != null)

                                {

                                    double maxVal = double.MinValue;

                                    int bestIdx = 0;

                                    List<string> scoreEntries = new List<string>();



                                    for (int i = 0; i < relics.Count; i++)

                                    {

                                        var r = relics[i];

                                        if (r == null) continue;

                                        string rid = r.Id.ToString();

                                        object[] evalArgs;

                                        if (evalMethod.GetParameters().Length == 2)

                                            evalArgs = new object[] { rid, snapshot };

                                        else

                                            evalArgs = new object[] { rid, snapshot, false };



                                        object assessment = evalMethod.Invoke(null, evalArgs);

                                        double val = 0.0;

                                        if (assessment != null)

                                        {

                                            PropertyInfo valProp = AccessTools.Property(assessment.GetType(), "Value");

                                            val = valProp != null ? Convert.ToDouble(valProp.GetValue(assessment, null)) : 0.0;

                                        }

                                        scoreEntries.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1:F1}", rid, val));



                                        if (val > maxVal)

                                        {

                                            maxVal = val;

                                            bestRelic = r;

                                            bestIdx = i;

                                        }

                                    }



                                    if (scoreEntries.Count > 0)

                                    {

                                        scoresStr = string.Join(",", scoreEntries.ToArray());

                                    }



                                    if (bestRelic != null)

                                    {

                                        chosenIndex = bestIdx;

                                        pickId = bestRelic.Id.ToString();

                                        source = "core";

                                        return bestRelic;

                                    }

                                }

                                else

                                {

                                    coreError = "snapshot-null";

                                }

                            }

                            else

                            {

                                coreError = "methods-missing";

                            }

                        }

                        else

                        {

                            coreError = "types-missing";

                        }

                    }

                    else

                    {

                        coreError = "run-null";

                    }

                }

                else

                {

                    coreError = "assembly-missing";

                }

            }

            catch (Exception ex)

            {

                coreError = ex.Message;

                GD.PrintErr("[AiTeammate][Flow] SpireStrategist relic evaluate error: " + ex.Message);

            }



            // Directive #5.0.8 T2/T3: Deterministic fallback for relic selection

            source = "fallback";

            double bestFallbackScore = double.MinValue;

            int fallbackBestIdx = 0;

            for (int i = 0; i < relics.Count; i++)

            {

                var r = relics[i];

                if (r == null) continue;

                string rid = r.Id.ToString().ToUpperInvariant();

                double score = 10.0;

                if (rid.Contains("CURSE") || rid.Contains("BELL") || rid.Contains("PEARL") || rid.Contains("TABLET") || rid.Contains("SHEARS") || rid.Contains("KALEIDOSCOPE") || rid.Contains("MASSIVE"))

                {

                    score -= 100.0;

                }

                else if (rid.Contains("PYRAMID") || rid.Contains("SNECKO") || rid.Contains("STONE") || rid.Contains("ANCHOR") || rid.Contains("VASE") || rid.Contains("FLOWER"))

                {

                    score += 30.0;

                }



                if (score > bestFallbackScore)

                {

                    bestFallbackScore = score;

                    bestRelic = r;

                    fallbackBestIdx = i;

                }

            }



            chosenIndex = (bestRelic != null && fallbackBestIdx >= 0) ? fallbackBestIdx : 0;

            pickId = (bestRelic != null) ? bestRelic.Id.ToString() : (relics.Count > 0 && relics[0] != null ? relics[0].Id.ToString() : "fallback-0");

            return bestRelic != null ? bestRelic : (relics.Count > 0 ? relics[0] : null);

        }



        public static object CreatePolicyInstance(Type policyType)

        {

            if (policyType == null) return null;

            try

            {

                ConstructorInfo[] ctors = policyType.GetConstructors();

                if (ctors.Length > 0)

                {

                    var ctor = ctors[0];

                    var ps = ctor.GetParameters();

                    object[] ctorArgs = new object[ps.Length];

                    for (int k = 0; k < ps.Length; k++)

                    {

                        var pt = ps[k].ParameterType;

                        if (pt == typeof(bool)) { ctorArgs[k] = false; continue; }

                        // R2-2: Policy(PolicyWeights, bool) needs a REAL weights object; passing null

                        // made the ctor throw (swallowed here) and the whole Core call silently no-op.

                        if (pt.IsValueType || pt.GetConstructor(Type.EmptyTypes) != null)

                        {

                            try

                            {

                                object inst = Activator.CreateInstance(pt);

                                var validate = pt.GetMethod("Validate", Type.EmptyTypes);

                                if (validate != null) validate.Invoke(inst, null);

                                ctorArgs[k] = inst;

                                continue;

                            }

                            catch { }

                        }

                        ctorArgs[k] = null;

                    }

                    return ctor.Invoke(ctorArgs);

                }

            }

            catch { }

            return null;

        }



        // R2-2: prefer the live Policy instance the SpireStrategist mod configured for this run.

        public static object TryGetStrategistPolicy(Assembly liveAsm = null)

        {

            try

            {

                if (liveAsm == null) liveAsm = FindLiveSpireStrategistAssembly();

                if (liveAsm == null) return null;

                Type svcType = liveAsm.GetType("SpireStrategist.Mod.AdvisorService");

                if (svcType == null) return null;

                PropertyInfo curProp = AccessTools.Property(svcType, "Current");

                object svc = curProp != null ? curProp.GetValue(null, null) : null;

                if (svc == null) return null;

                FieldInfo policyField = AccessTools.Field(svc.GetType(), "policy");

                object policy = policyField != null ? policyField.GetValue(svc) : null;

                if (policy != null) return policy;

            }

            catch { }

            return null;

        }



        public static void EvaluateCardReward(Player p2, IReadOnlyList<CardCreationResult> options, out string chosenId, out CardModel chosenCard, out string source)

        {

            int dummyIndex;

            string dummyErr;

            string dummyScores;

            EvaluateCardReward(p2, options, out chosenId, out chosenCard, out source, out dummyIndex, out dummyErr, out dummyScores);

        }



        public static void EvaluateCardReward(Player p2, IReadOnlyList<CardCreationResult> options, out string chosenId, out CardModel chosenCard, out string source, out int chosenIndex)

        {

            string dummyErr;

            string dummyScores;

            EvaluateCardReward(p2, options, out chosenId, out chosenCard, out source, out chosenIndex, out dummyErr, out dummyScores);

        }



        public static void EvaluateCardReward(Player p2, IReadOnlyList<CardCreationResult> options, out string chosenId, out CardModel chosenCard, out string source, out int chosenIndex, out string coreError)

        {

            string dummyScores;

            EvaluateCardReward(p2, options, out chosenId, out chosenCard, out source, out chosenIndex, out coreError, out dummyScores);

        }



        public static void EvaluateCardReward(Player p2, IReadOnlyList<CardCreationResult> options, out string chosenId, out CardModel chosenCard, out string source, out int chosenIndex, out string coreError, out string scoresStr)

        {

            chosenId = "none";

            chosenCard = null;

            source = "fallback";

            chosenIndex = 0;

            coreError = null;

            scoresStr = "none";



            if (options == null || options.Count == 0)

            {

                return;

            }



            // Fallback default: first available card (FromIndex(0) guarantee, never skip)

            chosenCard = options[0].Card;

            chosenId = chosenCard != null ? chosenCard.Id.ToString() : "fallback-0";



            List<string> cids = new List<string>();

            for (int i = 0; i < options.Count; i++)

            {

                if (options[i].Card != null) cids.Add(options[i].Card.Id.ToString());

            }

            string setKey = string.Join(",", cids.ToArray());



            if (AutopilotFuse.IsOpen("card-reward", setKey) || ActiveCardRewardCircuitOpen)

            {

                source = "circuit-open";

                return;

            }



            DateTime startTime = DateTime.UtcNow;



            if (SpireStrategistAvailable && p2 != null)

            {

                try

                {

                    IRunState run = GetRunState();

                    if (run == null)

                    {

                        coreError = "run-null";

                    }

                    else

                    {

                        // Directive #5.0.8 T1: Obtain types from live SpireStrategist assembly to eliminate cross-ALC conversion errors

                        Assembly liveAsm = FindLiveSpireStrategistAssembly();

                        Type snapReaderType = liveAsm != null ? liveAsm.GetType("SpireStrategist.Mod.SnapshotReader") : null;

                        Type policyType = liveAsm != null ? liveAsm.GetType("SpireStrategist.Core.Policy") : null;

                        Type decisionOptType = liveAsm != null ? liveAsm.GetType("SpireStrategist.Core.DecisionOption") : null;



                        if (snapReaderType == null || policyType == null || decisionOptType == null)

                        {

                            coreError = "types-missing";

                        }

                        else

                        {

                            // Directive #5.0.7 T1: Locate methods by name & parameter count to avoid cross-ALC Type identity failures

                            MethodInfo readMethod = null;

                            MethodInfo readCardMethod = null;

                            foreach (var m in snapReaderType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))

                            {

                                if (m.Name == "Read" && m.GetParameters().Length == 2)

                                {

                                    readMethod = m;

                                }

                                else if (m.Name == "ReadCard" && m.GetParameters().Length == 1)

                                {

                                    readCardMethod = m;

                                }

                            }



                            if (readMethod == null)

                            {

                                coreError = "read-method";

                            }

                            else

                            {

                                object snapshot = readMethod.Invoke(null, new object[] { run, p2 });

                                if (snapshot == null)

                                {

                                    coreError = "snapshot-null";

                                }

                                else

                                {

                                    string charName = "Ironclad";

                                    try

                                    {

                                        if (p2.Character != null && !string.IsNullOrEmpty(p2.Character.GetType().Name))

                                        {

                                            charName = p2.Character.GetType().Name;

                                        }

                                    }

                                    catch { }

                                    FieldInfo charField = AccessTools.Field(snapshot.GetType(), "<Character>k__BackingField");

                                    if (charField != null)

                                    {

                                        charField.SetValue(snapshot, charName);

                                    }

                                    FieldInfo pcField = AccessTools.Field(snapshot.GetType(), "<PlayerCount>k__BackingField");

                                    if (pcField != null)

                                    {

                                        pcField.SetValue(snapshot, 1);

                                    }



                                    Type optListType = typeof(List<>).MakeGenericType(decisionOptType);

                                    IList optList = Activator.CreateInstance(optListType) as IList;



                                    for (int i = 0; i < options.Count; i++)

                                    {

                                        CardModel cm = options[i].Card;

                                        if (cm != null)

                                        {

                                            object cSnap = readCardMethod != null ? readCardMethod.Invoke(null, new object[] { cm }) : null;

                                            string cid = cm.Id.ToString();

                                            object cOpt = Activator.CreateInstance(decisionOptType, new object[] { "card:" + i, cid, "card", true, cSnap, 0.0, 1, null });

                                            optList.Add(cOpt);

                                        }

                                    }



                                    object policy = TryGetStrategistPolicy(liveAsm);

                                    if (policy == null)

                                    {

                                        policy = CreatePolicyInstance(policyType);

                                    }



                                    MethodInfo evalMethod = null;

                                    foreach (var m in policyType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))

                                    {

                                        if (m.Name == "Evaluate" && m.GetParameters().Length == 2)

                                        {

                                            evalMethod = m;

                                            break;

                                        }

                                    }



                                    if (policy == null)

                                    {

                                        coreError = "policy-null";

                                    }

                                    else if (evalMethod == null)

                                    {

                                        coreError = "eval-method";

                                    }

                                    else if ((DateTime.UtcNow - startTime).TotalSeconds >= 5.0)

                                    {

                                        coreError = "timeout";

                                    }

                                    else

                                    {

                                        object evalResult = evalMethod.Invoke(policy, new object[] { snapshot, optList });

                                        if (evalResult == null)

                                        {

                                            coreError = "eval-null";

                                        }

                                        else

                                        {

                                            PropertyInfo recProp = evalResult.GetType().GetProperty("RecommendedKey");

                                            string recKey = recProp != null ? recProp.GetValue(evalResult, null) as string : null;

                                            GD.Print(string.Format("[AiTeammate][Flow] core-decision input={0} output={1}", setKey, recKey ?? "null"));



                                            PropertyInfo optsProp = evalResult.GetType().GetProperty("Options");

                                            IEnumerable optScores = optsProp != null ? optsProp.GetValue(evalResult, null) as IEnumerable : null;



                                            List<string> scoreEntries = new List<string>();

                                            double bestScore = double.MinValue;

                                            int bestScoreIdx = 0;



                                            if (optScores != null)

                                            {

                                                int sIdx = 0;

                                                foreach (object item in optScores)

                                                {

                                                    if (item == null) continue;

                                                    var nameProp = item.GetType().GetProperty("Name");

                                                    var keyProp = item.GetType().GetProperty("Key");

                                                    var scoreProp = item.GetType().GetProperty("Score");

                                                    string n = nameProp != null ? nameProp.GetValue(item, null) as string : null;

                                                    string k = keyProp != null ? keyProp.GetValue(item, null) as string : null;

                                                    object sObj = scoreProp != null ? scoreProp.GetValue(item, null) : null;

                                                    double sVal = sObj is double ? (double)sObj : (sObj is float ? (float)sObj : 0.0);

                                                    string cardLabel = !string.IsNullOrEmpty(n) ? n : (k ?? ("opt-" + sIdx));

                                                    scoreEntries.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1:F1}", cardLabel, sVal));



                                                    if (sVal > bestScore)

                                                    {

                                                        bestScore = sVal;

                                                        bestScoreIdx = sIdx;

                                                    }

                                                    sIdx++;

                                                }

                                            }

                                            if (scoreEntries.Count > 0)

                                            {

                                                scoresStr = string.Join(",", scoreEntries.ToArray());

                                            }



                                            // Determine chosen index

                                            int idx = -1;

                                            if (!string.IsNullOrEmpty(recKey) && recKey.StartsWith("card:") && int.TryParse(recKey.Substring(5), out idx) && idx >= 0 && idx < options.Count)

                                            {

                                                chosenIndex = idx;

                                            }

                                            else if (!string.IsNullOrEmpty(recKey) && recKey != "skip")

                                            {

                                                bool matched = false;

                                                for (int j = 0; j < options.Count; j++)

                                                {

                                                    if (options[j].Card != null && (options[j].Card.Id.ToString() == recKey || options[j].Card.GetType().Name == recKey))

                                                    {

                                                        chosenIndex = j;

                                                        matched = true;

                                                        break;

                                                    }

                                                }

                                                if (!matched)

                                                {

                                                    chosenIndex = (bestScoreIdx >= 0 && bestScoreIdx < options.Count) ? bestScoreIdx : 0;

                                                }

                                            }

                                            else

                                            {

                                                // recKey == "skip" or empty -> pick highest score card (FromIndex guarantee, never skip)

                                                chosenIndex = (bestScoreIdx >= 0 && bestScoreIdx < options.Count) ? bestScoreIdx : 0;

                                            }



                                            chosenCard = options[chosenIndex].Card;

                                            chosenId = chosenCard != null ? chosenCard.Id.ToString() : ("idx-" + chosenIndex);

                                            source = "core";

                                            if (chosenCard != null && (chosenCard.TargetType == TargetType.AnyPlayer || chosenCard.TargetType == TargetType.AnyAlly))
                                            {
                                                int altIdx = -1;
                                                double altScore = double.MinValue;
                                                for (int ai = 0; ai < options.Count; ai++)
                                                {
                                                    if (options[ai].Card != null && options[ai].Card.TargetType != TargetType.AnyPlayer && options[ai].Card.TargetType != TargetType.AnyAlly)
                                                    {
                                                        double ascore = GetCardScore(options[ai].Card.Id.ToString());
                                                        if (ascore > altScore)
                                                        {
                                                            altScore = ascore;
                                                            altIdx = ai;
                                                        }
                                                    }
                                                }
                                                if (altIdx >= 0)
                                                {
                                                    chosenIndex = altIdx;
                                                    chosenCard = options[altIdx].Card;
                                                    chosenId = chosenCard != null ? chosenCard.Id.ToString() : ("idx-" + chosenIndex);
                                                    source = "heuristic-avoid-ally-card";
                                                    GD.Print(string.Format("[AiTeammate][Flow] p2-card-ally-avoided altIdx={0} altCard={1}", altIdx, chosenId));
                                                }
                                            }

                                        }

                                    }

                                }

                            }

                        }

                    }

                }

                catch (Exception ex)

                {

                    coreError = ex.Message;

                    source = "core-error";

                    GD.Print(string.Format("[AiTeammate][Flow] core-error: {0}", ex.Message));

                    chosenIndex = 0;

                    chosenCard = options[0].Card;

                    chosenId = chosenCard != null ? chosenCard.Id.ToString() : "fallback-0";

                }

            }

            else

            {

                coreError = "strategist-unavailable";

            }



            if (source != "core")

            {

                // Directive #5.0.8 T2: Deterministic error fallback picking highest score from card_scores.json

                int bestIdx = 0;

                double bestScore = double.MinValue;

                for (int i = 0; i < options.Count; i++)

                {

                    if (options[i].Card != null)

                    {

                        CardModel optCard = options[i].Card;

                        string cid = optCard.Id.ToString();

                        double score = GetCardScore(cid);

                        if (optCard.TargetType == TargetType.AnyPlayer || optCard.TargetType == TargetType.AnyAlly)
                        {
                            score -= 5000.0;
                        }

                        if (score > bestScore)

                        {

                            bestScore = score;

                            bestIdx = i;

                        }

                    }

                }

                chosenIndex = (bestIdx >= 0 && bestIdx < options.Count) ? bestIdx : 0;

                chosenCard = options[chosenIndex].Card;

                chosenId = chosenCard != null ? chosenCard.Id.ToString() : ("fallback-" + chosenIndex);

                if (source != "core-error")

                {

                    source = "fallback";

                }

            }

        }



        public static void EvaluateRestSite(Player p2, NRestSiteRoom room, out string pickAction, out string source)

        {

            pickAction = "heal";

            source = "fallback";

            if (p2 == null || room == null) return;



            DateTime startTime = DateTime.UtcNow;

            bool strategistSuccess = false;



            if (SpireStrategistAvailable)

            {

                try

                {

                    string charName = p2.Character != null ? p2.Character.GetType().Name : "";

                    if (charName == "Ironclad")

                    {

                        IRunState run = GetRunState();

                        if (run != null && room.Options != null)

                        {

                            Type snapReaderType = AccessTools.TypeByName("SpireStrategist.Mod.SnapshotReader");

                            Type policyType = AccessTools.TypeByName("SpireStrategist.Core.Policy");

                            Type decisionOptType = AccessTools.TypeByName("SpireStrategist.Core.DecisionOption");



                            if (snapReaderType != null && policyType != null && decisionOptType != null)

                            {

                                MethodInfo readMethod = AccessTools.Method(snapReaderType, "Read", new Type[] { typeof(IRunState), typeof(Player) });

                                if (readMethod != null)

                                {

                                    object snapshot = readMethod.Invoke(null, new object[] { run, p2 });

                                    if (snapshot != null)

                                    {

                                        FieldInfo pcField = AccessTools.Field(snapshot.GetType(), "<PlayerCount>k__BackingField");

                                        if (pcField != null) pcField.SetValue(snapshot, 1);



                                        Type optListType = typeof(List<>).MakeGenericType(decisionOptType);

                                        IList optList = Activator.CreateInstance(optListType) as IList;



                                        foreach (RestSiteOption opt in room.Options)

                                        {

                                            if (opt == null) continue;

                                            string kind = opt is HealRestSiteOption ? "heal" : (opt is SmithRestSiteOption ? "smith" : "unknown");

                                            double healAmt = opt is HealRestSiteOption ? Convert.ToDouble(HealRestSiteOption.GetHealAmount(p2)) : 0.0;

                                            int smithCnt = opt is SmithRestSiteOption ? (opt as SmithRestSiteOption).SmithCount : 1;

                                            string optId = opt.OptionId != null ? opt.OptionId.ToString().ToLowerInvariant() : kind;



                                            object dOpt = Activator.CreateInstance(decisionOptType, new object[] {

                                                optId,

                                                opt.Title != null ? opt.Title.GetFormattedText() : optId,

                                                kind,

                                                opt.IsEnabled,

                                                null,

                                                healAmt,

                                                smithCnt,

                                                null

                                            });

                                            optList.Add(dOpt);

                                        }



                                        object policy = CreatePolicyInstance(policyType);

                                        MethodInfo evalMethod = AccessTools.Method(policyType, "Evaluate");

                                        if (evalMethod != null && (DateTime.UtcNow - startTime).TotalSeconds < 5.0)

                                        {

                                            object evalResult = evalMethod.Invoke(policy, new object[] { snapshot, optList });

                                            if (evalResult != null)

                                            {

                                                PropertyInfo recProp = AccessTools.Property(evalResult.GetType(), "RecommendedKey");

                                                string recKey = recProp != null ? recProp.GetValue(evalResult, null) as string : null;

                                                if (!string.IsNullOrEmpty(recKey))

                                                {

                                                    pickAction = recKey.ToLowerInvariant().Contains("smith") ? "smith" : "heal";

                                                    source = "strategist";

                                                    strategistSuccess = true;

                                                }

                                            }

                                        }

                                    }

                                }

                            }

                        }

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] SpireStrategist rest evaluate error: " + ex.Message);

                }

            }



            if (strategistSuccess) return;



            // Fallback: HP < 60% rest, otherwise smith

            source = "fallback";

            int maxHp = p2.Creature != null ? Math.Max(1, p2.Creature.MaxHp) : 1;

            int curHp = p2.Creature != null ? p2.Creature.CurrentHp : 0;

            double ratio = (double)curHp / maxHp;



            if (ratio < 0.60)

            {

                pickAction = "heal";

            }

            else

            {

                pickAction = "smith";

            }

        }



        public static void EvaluateEventOption(Player p2, List<NEventOptionButton> buttons, out NEventOptionButton chosenButton, out string pickId, out string source)

        {

            chosenButton = null;

            pickId = "leave";

            source = "fallback";

            if (buttons == null || buttons.Count == 0) return;



            DateTime startTime = DateTime.UtcNow;

            bool strategistSuccess = false;



            if (SpireStrategistAvailable && p2 != null)

            {

                try

                {

                    IRunState run = GetRunState();

                    Type eventPolicyType = AccessTools.TypeByName("SpireStrategist.Core.EventPolicy");

                    Type snapReaderType = AccessTools.TypeByName("SpireStrategist.Mod.SnapshotReader");

                    Type eventChoiceType = AccessTools.TypeByName("SpireStrategist.Core.EventChoice");



                    if (eventPolicyType != null && snapReaderType != null && eventChoiceType != null && run != null)

                    {

                        MethodInfo readMethod = AccessTools.Method(snapReaderType, "Read", new Type[] { typeof(IRunState), typeof(Player) });

                        if (readMethod != null)

                        {

                            object snapshot = readMethod.Invoke(null, new object[] { run, p2 });

                            if (snapshot != null)

                            {

                                FieldInfo pcField = AccessTools.Field(snapshot.GetType(), "<PlayerCount>k__BackingField");

                                if (pcField != null) pcField.SetValue(snapshot, 1);



                                Type listType = typeof(List<>).MakeGenericType(eventChoiceType);

                                IList choiceList = Activator.CreateInstance(listType) as IList;



                                for (int i = 0; i < buttons.Count; i++)

                                {

                                    var b = buttons[i];

                                    string desc = b.Option != null && b.Option.Description != null ? b.Option.Description.GetFormattedText() : "";

                                    string title = b.Option != null && b.Option.Title != null ? b.Option.Title.GetFormattedText() : ("option_" + i);

                                    bool enabled = b != null && b.IsEnabled && (b.Option == null || !b.Option.IsLocked);



                                    object choiceObj = CreateEventChoice(eventChoiceType, i.ToString(), title, enabled);

                                    if (choiceObj != null) choiceList.Add(choiceObj);

                                }



                                MethodInfo evalMethod = AccessTools.Method(eventPolicyType, "Evaluate");

                                if (evalMethod != null && (DateTime.UtcNow - startTime).TotalSeconds < 5.0)

                                {

                                    object evalResult = evalMethod.Invoke(null, new object[] { snapshot, choiceList });

                                    if (evalResult != null)

                                    {

                                        PropertyInfo recProp = AccessTools.Property(evalResult.GetType(), "RecommendedKey");

                                        string recKey = recProp != null ? recProp.GetValue(evalResult, null) as string : null;

                                        if (!string.IsNullOrEmpty(recKey))

                                        {

                                            int idx;

                                            if (int.TryParse(recKey, out idx) && idx >= 0 && idx < buttons.Count)

                                            {

                                                chosenButton = buttons[idx];

                                                pickId = idx.ToString();

                                                source = "strategist";

                                                strategistSuccess = true;

                                            }

                                        }

                                    }

                                }

                            }

                        }

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] SpireStrategist event evaluate error: " + ex.Message);

                }

            }



            if (strategistSuccess) return;



            // Conservative Fallback: choose option with no HP loss and no curse

            source = "fallback";

            List<NEventOptionButton> validButtons = new List<NEventOptionButton>();

            for (int i = 0; i < buttons.Count; i++)

            {

                NEventOptionButton b = buttons[i];

                if (b == null || b.Option == null) continue;

                if (b.Option.IsLocked) continue;

                if (b.Option.WillKillPlayer != null && b.Option.WillKillPlayer(p2)) continue;

                validButtons.Add(b);

            }



            if (validButtons.Count == 0) return;



            string[] dangerKeywords = new string[] { "lose", "damage", "hp", "curse", "生命", "伤害", "失去", "诅咒" };

            NEventOptionButton safeOption = null;

            NEventOptionButton proceedOption = null;



            for (int i = 0; i < validButtons.Count; i++)

            {

                NEventOptionButton b = validButtons[i];

                string title = (b.Option.Title != null ? b.Option.Title.ToString() : "").ToLowerInvariant();

                string desc = (b.Option.Description != null ? b.Option.Description.ToString() : "").ToLowerInvariant();

                string textKey = (b.Option.TextKey != null ? b.Option.TextKey : "").ToLowerInvariant();



                bool isDanger = false;

                for (int k = 0; k < dangerKeywords.Length; k++)

                {

                    if (desc.Contains(dangerKeywords[k]))

                    {

                        isDanger = true;

                        break;

                    }

                }



                if (b.Option.IsProceed || textKey.Contains("leave") || textKey.Contains("skip") || title.Contains("离开"))

                {

                    if (proceedOption == null) proceedOption = b;

                }



                if (!isDanger)

                {

                    if (safeOption == null) safeOption = b;

                }

            }



            chosenButton = safeOption != null ? safeOption : (proceedOption != null ? proceedOption : validButtons[validButtons.Count - 1]);

            pickId = chosenButton.Option.TextKey != null ? chosenButton.Option.TextKey : (chosenButton.Option.Title != null ? chosenButton.Option.Title.ToString() : "option");

        }



        public static bool TryAutoShopP2(Player p2) { try { if(p2==null||!ShopCardRemovalEnabled||p2.Gold<25)return false; object state=RunManager.Instance!=null?RunManager.Instance.DebugOnlyGetState():null; object room=null; if(state!=null){var cp=state.GetType().GetProperty("CurrentRoom",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);room=cp!=null?cp.GetValue(state,null):null;} if(room==null||room.GetType().Name.IndexOf("MerchantRoom",StringComparison.OrdinalIgnoreCase)<0)return false; var ip=room.GetType().GetProperty("Inventories",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic); var list=ip!=null?ip.GetValue(room,null) as IEnumerable:null; object inv=null; if(list!=null)foreach(object x in list){object op=GetMember(x,"Player");if(op is Player && ((Player)op).NetId==p2.NetId){inv=x;break;}} if(inv==null)return false; int before=p2.Gold;GD.Print("[AiTeammate][Flow] shop-offer owner=P2 gold-before="+before); bool did=false; foreach(object e in EnumerateEntries(inv)){string n=e.GetType().Name;int cost=ReadInt(e,"Cost",int.MaxValue);if(n.IndexOf("Removal",StringComparison.OrdinalIgnoreCase)>=0&&ReadBool(e,"IsStocked",true)&&p2.Gold-cost>=25&&InvokePurchase(e,inv,true)){did=true;GD.Print("[AiTeammate][Flow] shop-resolved kind=shop-removal gold-after="+p2.Gold);break;}} foreach(object e in EnumerateEntries(inv)){string n=e.GetType().Name;int cost=ReadInt(e,"Cost",int.MaxValue);if(n.IndexOf("Relic",StringComparison.OrdinalIgnoreCase)>=0&&ReadBool(e,"IsStocked",true)&&p2.Gold-cost>=25&&InvokePurchase(e,inv,false)){did=true;GD.Print("[AiTeammate][Flow] shop-resolved kind=relic gold-after="+p2.Gold);break;}} foreach(object e in EnumerateEntries(inv)){string n=e.GetType().Name;int cost=ReadInt(e,"Cost",int.MaxValue);if(n.IndexOf("CardEntry",StringComparison.OrdinalIgnoreCase)>=0&&ReadBool(e,"IsStocked",true)&&p2.Gold-cost>=25&&InvokePurchase(e,inv,false)){did=true;GD.Print("[AiTeammate][Flow] shop-resolved kind=card gold-after="+p2.Gold);break;}} return did;}catch(Exception ex){GD.PrintErr("[AiTeammate][Flow] shop fail-open: "+ex);return false;} }
        static object GetMember(object o,string n){if(o==null)return null;var p=o.GetType().GetProperty(n,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(p!=null)try{return p.GetValue(o,null);}catch{}var f=o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(f!=null)try{return f.GetValue(o);}catch{}return null;}
        static int ReadInt(object o,string n,int d){try{return Convert.ToInt32(GetMember(o,n));}catch{return d;}}
        static bool ReadBool(object o,string n,bool d){try{return Convert.ToBoolean(GetMember(o,n));}catch{return d;}}
        static IEnumerable<object> EnumerateEntries(object inv){foreach(var p in inv.GetType().GetProperties(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)){if(!typeof(IEnumerable).IsAssignableFrom(p.PropertyType)||p.PropertyType==typeof(string))continue;IEnumerable e=null;try{e=p.GetValue(inv,null) as IEnumerable;}catch{}if(e!=null)foreach(object x in e)if(x!=null&&x.GetType().Name.IndexOf("Merchant",StringComparison.OrdinalIgnoreCase)>=0)yield return x;}}
        static bool InvokePurchase(object e,object inv,bool wrapper){try{var m=e.GetType().GetMethod(wrapper?"OnTryPurchaseWrapper":"OnTryPurchase",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(m==null)return false;var ps=m.GetParameters();var a=new object[ps.Length];for(int i=0;i<ps.Length;i++)a[i]=ps[i].ParameterType==typeof(bool)?(object)false:inv;object r=m.Invoke(e,a);var t=r as Task;if(t!=null)t.GetAwaiter().GetResult();return true;}catch(Exception ex){GD.PrintErr("[AiTeammate][Flow] shop purchase fail-open: "+ex.Message);return false;}}

        public static bool EvaluateShopRemoval(Player p2, out CardModel cardToRemove, out string source)

        {

            cardToRemove = null;

            source = "fallback";

            if (p2 == null || p2.Deck == null || p2.Deck.Cards == null) return false;



            if (!ShopCardRemovalEnabled) return false;



            if (p2.Gold < 150 || p2.Deck.Cards.Count < 20 || HasPurchasedShopRemovalThisRoom) return false;



            foreach (CardModel c in p2.Deck.Cards)

            {

                string cid = c.Id.ToString().ToLowerInvariant();

                if (cid.Contains("strike"))

                {

                    cardToRemove = c;

                    break;

                }

            }



            if (cardToRemove != null)

            {

                source = "fallback";

                return true;

            }

            return false;

        }



        public static async void TryAutoClaimP2Rewards(NRewardsScreen screen)

        {

            try

            {

                if (screen == null || !screen.IsInsideTree()) return;

                Player p2 = GetPlayer2();

                if (p2 == null) return;



                FieldInfo btnField = AccessTools.Field(typeof(NRewardsScreen), "_rewardButtons");

                var buttons = btnField != null ? btnField.GetValue(screen) as System.Collections.IList : null;

                if (buttons == null || buttons.Count == 0) return;



                List<object> p2Buttons = new List<object>();

                List<string> p2Fingerprints = new List<string>();

                for (int i = 0; i < buttons.Count; i++)

                {

                    var b = buttons[i];

                    if (b == null) continue;

                    PropertyInfo rProp = AccessTools.Property(b.GetType(), "Reward");

                    var reward = rProp != null ? rProp.GetValue(b, null) : null;

                    if (reward != null)

                    {

                        // BS3: include P2 card rewards when enabled; the real native selection
                        // screen completes its Task and synchronizer before this loop advances.

                        if (reward is MegaCrit.Sts2.Core.Rewards.CardReward && !AutoCardRewardEnabled)

                        {

                            continue;

                        }



                        PropertyInfo pProp = AccessTools.Property(reward.GetType(), "Player");

                        Player pl = pProp != null ? pProp.GetValue(reward, null) as Player : null;

                        if (pl != null && pl.NetId == p2.NetId)

                        {

                            p2Buttons.Add(b);

                            p2Fingerprints.Add(RewardFingerprint(reward) + " instance=" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(reward));

                        }

                    }

                }



                if (p2Buttons.Count > 0)

                {

                    GD.Print(string.Format("[AiTeammate][Flow] Auto-claiming {0} P2 rewards serially (native backend)...", p2Buttons.Count));

                    for (int i = 0; i < p2Buttons.Count; i++)

                    {

                        var b = p2Buttons[i];

                        // R3-2: fuse check precedes every auto action; a second un-resolved offer

                        // of the same reward content opens the circuit for the rest of the session.

                        if (!AutopilotFuse.TryBeginOffer("reward-claim", p2Fingerprints[i]))

                        {

                            GD.Print("[AiTeammate][Flow] TryAutoClaimP2Rewards blocked by fuse: " + p2Fingerprints[i]);

                            continue;

                        }

                        object reward = GetMember(b, "Reward");
                        Task<bool> completed = WaitForRewardSelection(reward);
                        MethodInfo onRelease = AccessTools.Method(b.GetType(), "OnRelease");
                        if (onRelease == null)
                        {
                            GD.PrintErr("[AiTeammate][Flow] reward-claim-paused reason=OnRelease-unavailable fingerprint=" + p2Fingerprints[i]);
                            continue;
                        }
                        try
                        {
                            GD.Print("[AiTeammate][Flow] reward-selection-start owner=P2 type=" + (reward != null ? reward.GetType().Name : "unknown") + " fingerprint=" + p2Fingerprints[i]);
                            onRelease.Invoke(b, null);
                            bool confirmed = await completed;
                            if (!confirmed)
                            {
                                GD.PrintErr("[AiTeammate][Flow] reward-claim-paused reason=backend-not-confirmed fingerprint=" + p2Fingerprints[i]);
                                continue;
                            }
                            AutopilotFuse.MarkResolved("reward-claim", p2Fingerprints[i]);
                        }
                        catch (Exception exClaim)
                        {
                            GD.PrintErr("[AiTeammate][Flow] reward button invoke/wait failed: " + exClaim);
                            continue;
                        }

                    }

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Flow] TryAutoClaimP2Rewards exception: " + ex);

            }
            finally
            {
                try
                {
                    if (TurnCoordinator.IsInEventRoom() || !TurnCoordinator.IsInCombat())
                    {
                        ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                        ulong currentCtrl = TurnFlowPatches.GetDraCurrentControlledPlayerId();
                        if (p1Id != 0 && currentCtrl != p1Id)
                        {
                            TurnFlowPatches.SwitchControlToPlayer(p1Id, "wakuu-p1-restore:event-rewards-claimed", false);
                            GD.Print("[AiTeammate][Flow] Restored control to P1 after claiming P2 rewards (source=event-rewards-claimed)");
                        }
                    }
                }
                catch { }
            }

        }



        // R3-2: content fingerprint of a reward so repeated offers of the same target are fuse-counted.

        public static string RewardFingerprint(object reward)

        {

            try

            {

                string baseName = reward.GetType().Name;

                var cr = reward as MegaCrit.Sts2.Core.Rewards.CardReward;

                if (cr != null && cr.Cards != null)

                {

                    List<string> cids = new List<string>();

                    foreach (var c in cr.Cards) if (c != null) cids.Add(c.Id.ToString());

                    return baseName + ":" + string.Join(",", cids.ToArray());

                }

                // Best-effort content fields for other reward kinds (gold amount, potion id, ...).

                string[] fields = new string[] { "Amount", "Gold", "GoldAmount", "Potion", "PotionId", "Relic" };

                for (int i = 0; i < fields.Length; i++)

                {

                    PropertyInfo prop = AccessTools.Property(reward.GetType(), fields[i]);

                    if (prop != null)

                    {

                        object val = prop.GetValue(reward, null);

                        if (val != null) return baseName + ":" + val;

                    }

                }

                return baseName;

            }

            catch

            {

                return reward != null ? reward.GetType().Name : "unknown";

            }

        }



        private static Node FindSceneNodeRecursive(Node root, Type targetType)

        {

            if (root == null) return null;

            if (targetType.IsInstanceOfType(root)) return root;

            int childCount = root.GetChildCount();

            for (int i = 0; i < childCount; i++)

            {

                Node child = root.GetChild(i);

                Node hit = FindSceneNodeRecursive(child, targetType);

                if (hit != null) return hit;

            }

            return null;

        }



        public static Node FindSceneNodeOfType(Type targetType)

        {

            try

            {

                SceneTree tree = Engine.GetMainLoop() as SceneTree;

                if (tree == null || tree.Root == null) return null;

                return FindSceneNodeRecursive(tree.Root, targetType);

            }

            catch { }

            return null;

        }



        // R2-4: last-resort resolver for P2 choice overlays. Called on forced end turn and on turn

        // switches so a leftover loot/"搜刮" reward or card-grid overlay can never keep blocking

        // P1's clicks (10-26 soft-lock). Skip is submitted through the game's own skip handler so

        // the game consumes it; stale autopilot state is dropped as well.

        public static void ResolveOrClearP2ChoiceOverlays(string source)

        {

            try

            {

                Player p2 = GetPlayer2();

                if (p2 == null) return;

                int actions = 0;



                Node rewardsNode = FindSceneNodeOfType(typeof(NRewardsScreen));

                NRewardsScreen rewards = rewardsNode as NRewardsScreen;

                if (rewards != null && rewards.IsInsideTree())

                {

                    FieldInfo btnField = AccessTools.Field(typeof(NRewardsScreen), "_rewardButtons");

                    var buttons = btnField != null ? btnField.GetValue(rewards) as System.Collections.IList : null;

                    if (buttons != null)

                    {

                        MethodInfo skipMethod = AccessTools.Method(typeof(NRewardsScreen), "RewardSkippedFrom");

                        for (int i = 0; i < buttons.Count; i++)

                        {

                            var b = buttons[i];

                            if (b == null) continue;

                            PropertyInfo rProp = AccessTools.Property(b.GetType(), "Reward");

                            var reward = rProp != null ? rProp.GetValue(b, null) : null;

                            if (reward == null) continue;

                            PropertyInfo pProp = AccessTools.Property(reward.GetType(), "Player");

                            Player pl = pProp != null ? pProp.GetValue(reward, null) as Player : null;

                            if (pl == null || pl.NetId != p2.NetId) continue;



                            Godot.Control ctrl = b as Godot.Control;

                            if (skipMethod != null && ctrl != null)

                            {

                                try

                                {

                                    skipMethod.Invoke(rewards, new object[] { ctrl });

                                    actions++;

                                    GD.Print("[AiTeammate][Flow] in-combat-choice-fallback-skip source=" + source + " action=reward-skip");

                                }

                                catch (Exception exSkip)

                                {

                                    GD.PrintErr("[AiTeammate][Flow] reward-skip invoke failed: " + exSkip.Message);

                                }

                            }

                        }

                    }

                }



                if (ActiveCardRewardPlayerId != 0)

                {

                    ActiveCardRewardPlayerId = 0;

                    actions++;

                }

                if (PendingP2RewardOptions != null)

                {

                    PendingP2RewardOptions = null;

                    actions++;

                }

                ActiveCardRewardCircuitOpen = false;



                if (actions > 0)

                {

                    GD.Print(string.Format("[AiTeammate][Flow] p2-choice-overlay-cleared source={0} actions={1}", source, actions));

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Flow] ResolveOrClearP2ChoiceOverlays exception: " + ex);

            }

        }



        public static string LastResolvedRestSiteKey = null;

        public static bool ResolveP2RestSite(object synchronizer, Player p2)
        {
            try
            {
                if (synchronizer == null || p2 == null) return false;

                IRunState run = GetRunState();
                int floor = run != null ? run.TotalFloor : 0;
                string generation = CurrentRunGeneration();
                string siteKey = generation + "|floor=" + floor + "|p2";
                if (LastResolvedRestSiteKey == siteKey) return true;

                MethodInfo getOptionsMethod = AccessTools.Method(synchronizer.GetType(), "GetOptionsForPlayer", new Type[] { typeof(Player) });

                try
                {
                    FieldInfo restSitesField = AccessTools.Field(synchronizer.GetType(), "_restSites");
                    System.Collections.IList restSites = restSitesField != null ? restSitesField.GetValue(synchronizer) as System.Collections.IList : null;
                    if (restSites == null || restSites.Count == 0) return false;

                    int playerIndex = -1;
                    PropertyInfo idxProp = AccessTools.Property(typeof(Player), "Index") ?? AccessTools.Property(typeof(Player), "PlayerIndex");
                    if (idxProp != null)
                    {
                        object val = idxProp.GetValue(p2, null);
                        if (val is int) playerIndex = (int)val;
                    }
                    if (playerIndex >= 0 && playerIndex >= restSites.Count) return false;
                }
                catch { }

                System.Collections.IList opts = null;
                try
                {
                    opts = getOptionsMethod != null ? getOptionsMethod.Invoke(synchronizer, new object[] { p2 }) as System.Collections.IList : null;
                }
                catch (TargetInvocationException tie)
                {
                    if (tie.InnerException is ArgumentOutOfRangeException || tie.InnerException is IndexOutOfRangeException) return false;
                    return false;
                }
                catch (Exception)
                {
                    return false;
                }

                if (opts == null || opts.Count == 0) return false;

                int maxHp = p2.Creature != null ? Math.Max(1, p2.Creature.MaxHp) : 1;
                int curHp = p2.Creature != null ? p2.Creature.CurrentHp : 0;
                double hpRatio = (double)curHp / maxHp;

                bool preferHeal = (hpRatio < 0.50);

                bool hasUpgradableCard = false;
                if (p2.Deck != null && p2.Deck.Cards != null)
                {
                    for (int i = 0; i < p2.Deck.Cards.Count; i++)
                    {
                        var c = p2.Deck.Cards[i];
                        if (c != null && c.IsUpgradable) { hasUpgradableCard = true; break; }
                    }
                }

                string desiredAction = preferHeal ? "heal" : (hasUpgradableCard ? "smith" : "heal");
                if (!preferHeal && !hasUpgradableCard)
                {
                    GD.Print("[AiTeammate][Flow] rest-fallback: no upgradable cards in deck, changing smith to heal");
                }

                string fingerprint = string.Format("rest:{0}:{1}:{2}", floor, curHp, desiredAction);
                if (!AutopilotFuse.TryBeginOffer("rest", fingerprint))
                {
                    GD.Print("[AiTeammate][Flow] rest circuit-open: fail-open to manual selection");
                    return false;
                }

                GD.Print(string.Format("[AiTeammate][Flow] p2-choice-offer kind=rest owner=P2 hp={0}/{1} ratio={2:F2} action={3} options={4}", curHp, maxHp, hpRatio, desiredAction, opts.Count));

                int healIdx = -1;
                int smithIdx = -1;
                for (int i = 0; i < opts.Count; i++)
                {
                    var opt = opts[i];
                    if (opt == null) continue;
                    PropertyInfo enProp = AccessTools.Property(opt.GetType(), "IsEnabled");
                    bool isEn = enProp != null ? (bool)enProp.GetValue(opt, null) : true;
                    if (!isEn) continue;

                    string optName = opt.GetType().Name.ToLowerInvariant();
                    PropertyInfo idProp = AccessTools.Property(opt.GetType(), "OptionId");
                    string optId = idProp != null && idProp.GetValue(opt, null) != null ? idProp.GetValue(opt, null).ToString().ToLowerInvariant() : "";
                    string combined = optName + " " + optId;

                    if (combined.Contains("heal") || combined.Contains("rest")) healIdx = i;
                    if (combined.Contains("smith") || combined.Contains("upgrade")) smithIdx = i;
                }

                int chosenIdx = -1;
                string pickAction = "heal";
                string reason = "rule-hp-threshold";
                string source = "rule";

                if (preferHeal)
                {
                    if (healIdx >= 0) { chosenIdx = healIdx; pickAction = "heal"; }
                    else if (smithIdx >= 0 && hasUpgradableCard) { chosenIdx = smithIdx; pickAction = "smith"; }
                }
                else
                {
                    if (smithIdx >= 0 && hasUpgradableCard) { chosenIdx = smithIdx; pickAction = "smith"; }
                    else if (healIdx >= 0)
                    {
                        chosenIdx = healIdx;
                        pickAction = "heal";
                        reason = "rest-fallback";
                        source = "fallback";
                        GD.Print("[AiTeammate][Flow] rest-fallback");
                    }
                }

                if (chosenIdx < 0)
                {
                    for (int i = 0; i < opts.Count; i++)
                    {
                        var opt = opts[i];
                        if (opt == null) continue;
                        PropertyInfo enProp = AccessTools.Property(opt.GetType(), "IsEnabled");
                        bool isEn = enProp != null ? (bool)enProp.GetValue(opt, null) : true;
                        if (isEn) { chosenIdx = i; pickAction = "opt_" + i; break; }
                    }
                }

                if (chosenIdx >= 0)
                {
                    AutopilotFuse.MarkResolved("rest", fingerprint);
                    LastResolvedRestSiteKey = siteKey;
                    GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=rest pick={0} reason={1} source={2} idx={3}", pickAction, reason, source, chosenIdx));

                    MethodInfo chooseMethod = AccessTools.Method(synchronizer.GetType(), "ChooseOption", new Type[] { typeof(Player), typeof(int) });
                    if (chooseMethod != null)
                    {
                        ulong? prevNetId = LocalContext.NetId;
                        try
                        {
                            LocalContext.NetId = p2.NetId;
                            object res = chooseMethod.Invoke(synchronizer, new object[] { p2, chosenIdx });
                            Task task = res as Task;
                            if (task != null)
                            {
                                MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(task);
                            }
                        }
                        finally
                        {
                            LocalContext.NetId = prevNetId;
                        }

                        ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                        ulong actualId = TurnFlowPatches.GetDraCurrentControlledPlayerId();
                        string actualName = (actualId == p1Id || (p1Id != 0 && actualId == 0)) ? "P1" : actualId.ToString();
                        GD.Print(string.Format("[AiTeammate][Flow] rest-foreground-assert expected=P1 actual={0}", actualName));
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Flow] ResolveP2RestSite exception: " + ex);
            }
            return false;
        }

        // R2-5: one offer/resolved pair per pending event; extra hook call sites within the TTL

        // window stay silent instead of re-offering.

        public static Dictionary<string, DateTime> ResolvedEventStamps = new Dictionary<string, DateTime>();



        private static readonly string[] SubPageKeywords = new string[]

        {

            "enchant", "select", "choose", "upgrade", "transform", "purge", "remove", "card", "deck", "grid",

            "读下封底", "附魔", "选择", "移除", "转化", "删除", "卡牌", "牌组", "牌"

        };



        public static bool IsSupportedSubPageOption(object opt)

        {

            if (opt == null) return false;

            try

            {

                string tStr = GetOptionTitle(opt);

                string dStr = GetOptionDescription(opt);

                string tkStr = GetOptionTextKey(opt);

                string full = ((tStr ?? "") + " " + (dStr ?? "") + " " + (tkStr ?? "")).ToLowerInvariant();



                if (full.Contains("remove") || full.Contains("purge") || full.Contains("移除") || full.Contains("删除")) return true;

                if (full.Contains("choose") || full.Contains("select") || full.Contains("obtain a card") || full.Contains("选择") || full.Contains("获得一张牌")) return true;

                if (full.Contains("upgrade") || full.Contains("升级")) return true;

                if (full.Contains("transform") || full.Contains("转化")) return true;

                if (full.Contains("enchant") || full.Contains("附魔") || full.Contains("read_the_back") || full.Contains("read_passage") || full.Contains("读下封底") || full.Contains("读一段")) return true;

            }

            catch { }

            return false;

        }



        public static object CreateEventChoice(Type eventChoiceType, string key, string name, bool enabled)

        {

            return CreateEventChoice(eventChoiceType, key, name, enabled, 0.0, 0.0, 0.0, 0.0, 0, 0, 0, 0, 0.0, 0.0);

        }



        public static object CreateEventChoice(Type eventChoiceType, string key, string name, bool enabled, double hpLoss, double heal, double maxHp, double gold, int remove, int upgrade, int transform, int potions, double benefit, double burden)

        {

            if (eventChoiceType == null) return null;

            try

            {

                object[] args20 = new object[]

                {

                    key ?? "",             // 1. Key: string

                    name ?? "",            // 2. Name: string

                    enabled,               // 3. Enabled: bool

                    true,                  // 4. Supported: bool

                    false,                 // 5. Lethal: bool

                    hpLoss,                // 6. HpLoss: double

                    heal,                  // 7. Heal: double

                    maxHp,                 // 8. MaxHp: double

                    gold,                  // 9. Gold: double

                    remove,                // 10. Remove: int

                    upgrade,               // 11. Upgrade: int

                    0,                     // 12. RandomUpgrade: int

                    transform,             // 13. Transform: int

                    potions,               // 14. Potions: int

                    benefit,               // 15. Benefit: double

                    burden,                // 16. Burden: double

                    "",                    // 17. Selection: string

                    new string[0],         // 18. Notes: string[]

                    0.0,                   // 19. Preference: double

                    ""                     // 20. PreferenceReason: string

                };

                return Activator.CreateInstance(eventChoiceType, args20);

            }

            catch

            {

                try

                {

                    Type formatterServices = Type.GetType("System.Runtime.Serialization.FormatterServices, System.Runtime.Serialization.Formatters")

                        ?? Type.GetType("System.Runtime.Serialization.FormatterServices");

                    MethodInfo mGetObj = formatterServices != null ? formatterServices.GetMethod("GetUninitializedObject", new Type[] { typeof(Type) }) : null;

                    object obj = mGetObj != null ? mGetObj.Invoke(null, new object[] { eventChoiceType }) : null;

                    if (obj != null)

                    {

                        var fKey = AccessTools.Field(eventChoiceType, "<Key>k__BackingField");

                        if (fKey != null) fKey.SetValue(obj, key ?? "");

                        var fName = AccessTools.Field(eventChoiceType, "<Name>k__BackingField");

                        if (fName != null) fName.SetValue(obj, name ?? "");

                        var fEnabled = AccessTools.Field(eventChoiceType, "<Enabled>k__BackingField");

                        if (fEnabled != null) fEnabled.SetValue(obj, enabled);

                        var fSupported = AccessTools.Field(eventChoiceType, "<Supported>k__BackingField");

                        if (fSupported != null) fSupported.SetValue(obj, true);

                        var fHpLoss = AccessTools.Field(eventChoiceType, "<HpLoss>k__BackingField");

                        if (fHpLoss != null) fHpLoss.SetValue(obj, hpLoss);

                        var fHeal = AccessTools.Field(eventChoiceType, "<Heal>k__BackingField");

                        if (fHeal != null) fHeal.SetValue(obj, heal);

                        var fMaxHp = AccessTools.Field(eventChoiceType, "<MaxHp>k__BackingField");

                        if (fMaxHp != null) fMaxHp.SetValue(obj, maxHp);

                        var fGold = AccessTools.Field(eventChoiceType, "<Gold>k__BackingField");

                        if (fGold != null) fGold.SetValue(obj, gold);

                        var fRemove = AccessTools.Field(eventChoiceType, "<Remove>k__BackingField");

                        if (fRemove != null) fRemove.SetValue(obj, remove);

                        var fUpgrade = AccessTools.Field(eventChoiceType, "<Upgrade>k__BackingField");

                        if (fUpgrade != null) fUpgrade.SetValue(obj, upgrade);

                        var fTransform = AccessTools.Field(eventChoiceType, "<Transform>k__BackingField");

                        if (fTransform != null) fTransform.SetValue(obj, transform);

                        var fPotions = AccessTools.Field(eventChoiceType, "<Potions>k__BackingField");

                        if (fPotions != null) fPotions.SetValue(obj, potions);

                        var fBenefit = AccessTools.Field(eventChoiceType, "<Benefit>k__BackingField");

                        if (fBenefit != null) fBenefit.SetValue(obj, benefit);

                        var fBurden = AccessTools.Field(eventChoiceType, "<Burden>k__BackingField");

                        if (fBurden != null) fBurden.SetValue(obj, burden);

                        var fNotes = AccessTools.Field(eventChoiceType, "<Notes>k__BackingField");

                        if (fNotes != null) fNotes.SetValue(obj, new string[0]);

                        return obj;

                    }

                }

                catch { }

            }

            return null;

        }



        public static void ParseOptionEffects(object opt, out double hpLoss, out double heal, out double maxHp, out double gold, out int remove, out int upgrade, out int transform, out int potions, out double benefit, out double burden)

        {

            hpLoss = 0.0; heal = 0.0; maxHp = 0.0; gold = 0.0;

            remove = 0; upgrade = 0; transform = 0; potions = 0;

            benefit = 0.0; burden = 0.0;

            if (opt == null) return;



            try

            {

                string tStr = GetOptionTitle(opt);

                string dStr = GetOptionDescription(opt);

                string tkStr = GetOptionTextKey(opt);

                string full = ((tStr ?? "") + " " + (dStr ?? "") + " " + (tkStr ?? "")).ToLowerInvariant();



                // Directive #5.15 T3: Highly differentiated Neow effect parsing

                bool isRemoval = full.Contains("remove") || full.Contains("purge") || full.Contains("移除") || full.Contains("删除") || full.Contains("fromdeckforremoval") || full.Contains("scissors") || full.Contains("shears") || full.Contains("剪刀");

                bool isChooseCard = full.Contains("choose") || full.Contains("select") || full.Contains("obtain a card") || full.Contains("选择") || full.Contains("获得一张牌") || full.Contains("fromsimplegrid") || full.Contains("scroll") || full.Contains("画卷") || full.Contains("稀有") || full.Contains("rare");

                bool isUpgrade = full.Contains("upgrade") || full.Contains("升级") || full.Contains("fromdeckforupgrade");

                bool isTransform = full.Contains("transform") || full.Contains("转化");

                bool isGold = full.Contains("gold") || full.Contains("金币") || full.Contains("金");

                bool isHp = full.Contains("max hp") || full.Contains("heal") || full.Contains("最大生命") || full.Contains("生命");

                bool isPotion = full.Contains("potion") || full.Contains("药水");

                bool isEnchant = full.Contains("enchant") || full.Contains("附魔") || full.Contains("tress") || full.Contains("丝发");



                if (isGold)

                {

                    Match m = Regex.Match(full, @"\+?(\d+)\s*(?:gold|金币|金)");

                    double parsedGold;

                    if (m.Success && double.TryParse(m.Groups[1].Value, out parsedGold))

                    {

                        gold = parsedGold;

                    }

                    else if (full.Contains("lose all gold") || full.Contains("失去所有金币") || full.Contains("失去金币"))

                    {

                        gold = -100.0;

                        burden += 25.0;

                    }

                    else

                    {

                        gold = 100.0;

                    }

                }



                if (isHp)

                {

                    Match m = Regex.Match(full, @"\+?(\d+)\s*(?:max hp|最大生命)");

                    double parsedMhp;

                    if (m.Success && double.TryParse(m.Groups[1].Value, out parsedMhp))

                    {

                        maxHp = parsedMhp;

                    }

                    else if (full.Contains("heal") || full.Contains("恢复") || full.Contains("治疗"))

                    {

                        heal = 20.0;

                    }

                    else

                    {

                        maxHp = 7.0;

                    }

                }



                if (full.Contains("lose") || full.Contains("damage") || full.Contains("失去") || full.Contains("伤害"))

                {

                    Match m = Regex.Match(full, @"(?:lose|失去|damage|伤害)\s*(\d+)");

                    double parsedLoss;

                    if (m.Success && double.TryParse(m.Groups[1].Value, out parsedLoss))

                    {

                        hpLoss = parsedLoss;

                        burden += parsedLoss;

                    }

                    else

                    {

                        hpLoss = 10.0;

                        burden += 10.0;

                    }

                }



                PropertyInfo relProp = AccessTools.Property(opt.GetType(), "Relic");

                bool hasRelic = (relProp != null && relProp.GetValue(opt, null) != null) || full.Contains("relic") || full.Contains("遗物");



                // Strictly tiered, non-overlapping benefit intervals

                if (isChooseCard)

                {

                    benefit += 55.0;

                }

                else if (isRemoval)

                {

                    remove = 1;

                    benefit += 48.0;

                }

                else if (isUpgrade)

                {

                    upgrade = 1;

                    benefit += 36.0;

                }

                else if (hasRelic)

                {

                    benefit += 40.0;

                }

                else if (isGold && gold > 0)

                {

                    benefit += 32.0;

                }

                else if (maxHp > 0)

                {

                    benefit += 28.0;

                }

                else if (isTransform)

                {

                    transform = 1;

                    benefit += 22.0;

                }

                else if (isPotion)

                {

                    potions = 1;

                    benefit += 16.0;

                }

                else

                {

                    benefit += 20.0;

                }



                if (isEnchant)

                {

                    benefit += 10.0;

                }



                if (full.Contains("curse") || full.Contains("诅咒"))

                {

                    burden += 35.0;

                }

            }

            catch { }

        }



        public static int EvaluateAncientOptionHeuristic(Player p2, System.Collections.IList curOpts, out string pickId, out string sourceRule)

        {

            pickId = "0";

            sourceRule = "neow-heuristic";

            if (curOpts == null || curOpts.Count == 0) return 0;



            int bestIdx = 0;

            double bestScore = double.MinValue;

            string bestRule = "neow-default";



            for (int i = 0; i < curOpts.Count; i++)

            {

                object opt = curOpts[i];

                if (opt == null) continue;

                if (GetOptionIsLocked(opt)) continue;



                string tStr = GetOptionTitle(opt);

                string dStr = GetOptionDescription(opt);

                string tkStr = GetOptionTextKey(opt);

                string full = ((tStr ?? "") + " " + (dStr ?? "") + " " + (tkStr ?? "")).ToLowerInvariant();



                double score = 10.0;

                string rule = "neow-choice";



                bool isRemoval = full.Contains("remove") || full.Contains("purge") || full.Contains("移除") || full.Contains("删除") || full.Contains("fromdeckforremoval");

                bool isChooseCard = full.Contains("choose") || full.Contains("select") || full.Contains("obtain a card") || full.Contains("选择") || full.Contains("获得一张牌") || full.Contains("fromsimplegrid");

                bool isUpgrade = full.Contains("upgrade") || full.Contains("升级") || full.Contains("fromdeckforupgrade");

                bool isTransform = full.Contains("transform") || full.Contains("转化");



                bool isRelic = full.Contains("relic") || full.Contains("遗物");

                bool isGold = full.Contains("gold") || full.Contains("金币");

                bool isHp = full.Contains("max hp") || full.Contains("heal") || full.Contains("最大生命") || full.Contains("生命");



                bool hasCurse = full.Contains("curse") || full.Contains("诅咒");

                bool hasDamage = full.Contains("lose") || full.Contains("damage") || full.Contains("失去") || full.Contains("伤害");



                if (isRelic)

                {

                    score += 35.0;

                    rule = "heuristic-relic";

                }

                else if (isRemoval)

                {

                    score += 28.0;

                    rule = "heuristic-removal";

                }

                else if (isUpgrade)

                {

                    score += 22.0;

                    rule = "heuristic-upgrade";

                }

                else if (isGold)

                {

                    score += 25.0;

                    rule = "heuristic-gold";

                }

                else if (isHp)

                {

                    score += 20.0;

                    rule = "heuristic-hp";

                }

                else if (isChooseCard)

                {

                    score += 18.0;

                    rule = "heuristic-card-choice";

                }

                else if (isTransform)

                {

                    score += 15.0;

                    rule = "heuristic-transform";

                }



                if (hasCurse) score -= 30.0;

                if (hasDamage) score -= 25.0;

                // Directive #5.39: Heavily penalize MASSIVE_SCROLL to avoid multiplayer solver crashes
                if (full.Contains("massive_scroll") || full.Contains("巨大卷轴"))
                {
                    score -= 5000.0;
                }



                if (score > bestScore)

                {

                    bestScore = score;

                    bestIdx = i;

                    bestRule = rule;

                }

            }



            pickId = bestIdx.ToString();

            sourceRule = bestRule;

            return bestIdx;

        }



        public static bool CheckOptionForSubPageKeywords(object opt)

        {

            if (opt == null) return false;

            try

            {

                string tStr = GetOptionTitle(opt);

                string dStr = GetOptionDescription(opt);

                string tkStr = GetOptionTextKey(opt);

                string full = ((tStr ?? "") + " " + (dStr ?? "") + " " + (tkStr ?? "")).ToLowerInvariant();



                for (int i = 0; i < SubPageKeywords.Length; i++)

                {

                    if (full.Contains(SubPageKeywords[i])) return true;

                }



                PropertyInfo onChosenProp = AccessTools.Property(opt.GetType(), "OnChosen");

                if (onChosenProp != null)

                {

                    object onChosen = onChosenProp.GetValue(opt, null);

                    if (onChosen is Delegate)

                    {

                        Delegate del = (Delegate)onChosen;

                        string mName = del.Method != null ? del.Method.Name.ToLowerInvariant() : "";

                        string tName = del.Target != null ? del.Target.GetType().Name.ToLowerInvariant() : "";

                        string delFull = mName + " " + tName;

                        for (int i = 0; i < SubPageKeywords.Length; i++)

                        {

                            if (delFull.Contains(SubPageKeywords[i])) return true;

                        }

                    }

                }

            }

            catch { }

            return false;

        }



        public static string GetOptionTitle(object opt)

        {

            if (opt == null) return "";

            try

            {

                PropertyInfo tProp = AccessTools.Property(opt.GetType(), "Title");

                object val = tProp != null ? tProp.GetValue(opt, null) : null;

                return val != null ? val.ToString() : "";

            }

            catch { return ""; }

        }



        public static string GetOptionDescription(object opt)

        {

            if (opt == null) return "";

            try

            {

                PropertyInfo dProp = AccessTools.Property(opt.GetType(), "Description");

                object val = dProp != null ? dProp.GetValue(opt, null) : null;

                return val != null ? val.ToString() : "";

            }

            catch { return ""; }

        }



        public static string GetOptionTextKey(object opt)

        {

            if (opt == null) return "";

            try

            {

                PropertyInfo tkProp = AccessTools.Property(opt.GetType(), "TextKey");

                object val = tkProp != null ? tkProp.GetValue(opt, null) : null;

                return val != null ? val.ToString() : "";

            }

            catch { return ""; }

        }



        public static bool GetOptionIsLocked(object opt)

        {

            if (opt == null) return false;

            try

            {

                PropertyInfo lProp = AccessTools.Property(opt.GetType(), "IsLocked");

                return lProp != null && (bool)lProp.GetValue(opt, null);

            }

            catch { return false; }

        }



        public static bool AreOptionsChanged(System.Collections.IList oldOpts, System.Collections.IList newOpts)

        {

            if (oldOpts == null && newOpts == null) return false;

            if (oldOpts == null || newOpts == null) return true;

            if (oldOpts.Count != newOpts.Count) return true;

            for (int i = 0; i < oldOpts.Count; i++)

            {

                string t1 = GetOptionTitle(oldOpts[i]);

                string t2 = GetOptionTitle(newOpts[i]);

                if (t1 != t2) return true;

            }

            return false;

        }



        public static void TripSubPageAndHandover(Player p2, string kind, string fingerprint, string subPageKind)

        {
            if (TurnCoordinator.IsInEventRoom())
            {
                GD.Print(string.Format("[AiTeammate][Event] trip-subpage-suppressed in event room (kind={0}, subPage={1}) -> keeping P1 foreground", kind, subPageKind));
                return;
            }

            AutopilotFuse.TripCircuit(kind, fingerprint);

            GD.Print(string.Format("[AiTeammate][Flow] event non-terminal sub-page detected (kind={0}). Circuit tripped open -> yielding control to manual user UI.", subPageKind));

            ModEntry.SuppressControlSwitchUi = false;

            FlowCoordinator.IsSubPageManualInteracting = true;

            if (p2 != null)

            {

                TurnFlowPatches.SwitchControlToPlayer(p2.NetId, "wakuu-subpage-manual", false);

            }

        }



        public static int EvaluateEventOptionFallback(Player p2, System.Collections.IList curOpts, out string pickId)

        {

            pickId = "0";

            if (curOpts == null || curOpts.Count == 0) return 0;

            int chosenIdx = 0;

            double bestScore = double.MinValue;

            for (int i = 0; i < curOpts.Count; i++)

            {

                var opt = curOpts[i];

                if (opt == null) continue;

                if (GetOptionIsLocked(opt)) continue;



                PropertyInfo killProp = AccessTools.Property(opt.GetType(), "WillKillPlayer");

                if (killProp != null)

                {

                    object killFn = killProp.GetValue(opt, null);

                    if (killFn != null)

                    {

                        try

                        {

                            MethodInfo killInvoke = killFn.GetType().GetMethod("Invoke");

                            object killRes = killInvoke != null ? killInvoke.Invoke(killFn, new object[] { p2 }) : null;

                            if (killRes is bool && (bool)killRes) continue;

                        }

                        catch { }

                    }

                }



                string tStr = GetOptionTitle(opt).ToLowerInvariant();

                string dStr = GetOptionDescription(opt).ToLowerInvariant();

                string tkStr = GetOptionTextKey(opt).ToLowerInvariant();

                string full = tStr + " " + dStr + " " + tkStr;



                double score = 10.0;

                if (full.Contains("damage") || full.Contains("lose") || full.Contains("curse") || full.Contains("wound") || full.Contains("pain") ||

                    full.Contains("remove") || full.Contains("kill") || full.Contains("fight") ||

                    full.Contains("损失") || full.Contains("失去") || full.Contains("伤害") || full.Contains("诅咒") || full.Contains("移除") || full.Contains("扣除") || full.Contains("战斗"))

                {

                    score -= 50.0;

                }

                if (full.Contains("leave") || full.Contains("proceed") || full.Contains("walk") || full.Contains("gain") || full.Contains("heal") || full.Contains("upgrade") ||

                    full.Contains("gold") || full.Contains("relic") || full.Contains("card") ||

                    full.Contains("获得") || full.Contains("升级") || full.Contains("治疗") || full.Contains("加入") || full.Contains("离开") || full.Contains("继续") || full.Contains("金币") || full.Contains("遗物"))

                {

                    score += 20.0;

                }



                if (score > bestScore)

                {

                    bestScore = score;

                    chosenIdx = i;

                    pickId = !string.IsNullOrEmpty(tStr) ? tStr : (!string.IsNullOrEmpty(tkStr) ? tkStr : i.ToString());

                }

            }

            return chosenIdx;

        }



        private static string GetEventIdentity(object p2Event)

        {

            try

            {

                PropertyInfo tkProp = AccessTools.Property(p2Event.GetType(), "TextKey");

                if (tkProp != null)

                {

                    object tk = tkProp.GetValue(p2Event, null);

                    if (tk != null) return tk.ToString();

                }

            }

            catch { }

            return p2Event.GetType().FullName;

        }



        public static bool ResolveP2PendingEvent(object synchronizer, Player p2)

        {

            try

            {

                if (synchronizer == null || p2 == null) return false;

                if (FlowCoordinator.IsSubPageManualInteracting) return false;

                if (TurnCoordinator.IsEnteringRoom) return false; // Directive #5.14 T2: Event resolver silent during room enter window



                PropertyInfo isSharedProp = AccessTools.Property(synchronizer.GetType(), "IsShared");

                bool isShared = isSharedProp != null && (bool)isSharedProp.GetValue(synchronizer, null);

                if (isShared)

                {

                    GD.Print("[AiTeammate][Flow] shared-event-auto-proxied");

                    return true;

                }



                PropertyInfo eventsProp = AccessTools.Property(synchronizer.GetType(), "Events");

                var events = eventsProp != null ? eventsProp.GetValue(synchronizer, null) as System.Collections.IEnumerable : null;

                if (events == null) return false;



                object p2Event = null;

                foreach (var ev in events)

                {

                    if (ev == null) continue;

                    PropertyInfo ownerProp = AccessTools.Property(ev.GetType(), "Owner");

                    Player pl = ownerProp != null ? ownerProp.GetValue(ev, null) as Player : null;

                    if (pl != null && (pl == p2 || pl.NetId == p2.NetId || pl.NetId == TurnCoordinator.GetPlayer2NetId()))

                    {

                        p2Event = ev;

                        break;

                    }

                }



                if (p2Event == null) return false;



                PropertyInfo isFinProp = AccessTools.Property(p2Event.GetType(), "IsFinished");

                bool isFin = isFinProp != null && (bool)isFinProp.GetValue(p2Event, null);

                if (isFin)

                {

                    FlowCoordinator.IsSubPageManualInteracting = false;

                    return true;

                }



                // Directive #5.12 H6: Distinguish "sub-page open" from "unconsumed offer":

                // If P2 has a pending choice in flight (e.g. CardSelectCmd subpage open),

                // do NOT re-offer the parent event, and do NOT count towards fuse offers!

                if (TurnCoordinator.P2PendingChoiceCount > 0)

                {

                    GD.Print(string.Format("[AiTeammate][Flow] event-choice-deferred: P2 has pending choice in flight (count={0}), deferring probe without fuse count", TurnCoordinator.P2PendingChoiceCount));

                    return true;

                }



                string eventId = GetEventIdentity(p2Event);
                FlowCoordinator.CurrentEventId = eventId;

                bool isAncient = p2Event is MegaCrit.Sts2.Core.Models.AncientEventModel ||

                                 (p2Event.GetType().Name.Contains("Ancient"));

                string eventKind = isAncient ? "neow" : "event";



                GD.Print(string.Format("[AiTeammate][Flow] probe: EventSynchronizer P2 pending event detected owner=P2 isFinished={0} kind={1}", isFin, eventKind));



                MethodInfo chooseOptMethod = AccessTools.Method(synchronizer.GetType(), "ChooseOptionForEvent", new Type[] { typeof(Player), typeof(int) });
                if (chooseOptMethod == null) return false;

                PropertyInfo curOptsProp = AccessTools.Property(p2Event.GetType(), "CurrentOptions");
                var curOpts = curOptsProp != null ? curOptsProp.GetValue(p2Event, null) as System.Collections.IList : null;

                string curOptsFingerprint = ComputeOptionsFingerprint(curOpts);
                string generation = CurrentRunGeneration();
                int pageIndex = FlowCoordinator.CurrentEventPage;

                // Directive #5.33: Check in-flight choice state machine
                if (InFlightEventKey != null)
                {
                    bool optionsChanged = !string.Equals(curOptsFingerprint, InFlightOptionsFingerprint, StringComparison.Ordinal);
                    bool pendingChoiceCleared = InFlightHadPendingChoice && TurnCoordinator.P2PendingChoiceCount == 0;
                    bool generationChanged = !string.Equals(generation, InFlightGeneration, StringComparison.Ordinal);
                    bool isBackendConfirmed = isFin || optionsChanged || pendingChoiceCleared || generationChanged;

                    if (isBackendConfirmed)
                    {
                        GD.Print(string.Format("[AiTeammate][Event] event-choice-backend-confirmed key={0}", InFlightEventKey));
                        GD.Print(string.Format("[AiTeammate][Event] event-choice-resume key={0}", InFlightEventKey));
                        string childStatus = FlowCoordinator.LastChildCardChoiceCompleted ? "completed" : "none";
                        GD.Print(string.Format("[AiTeammate][Event] event-p2-choice-resolved owner=P2 page={0} child={1}", InFlightEventPage, childStatus));
                        GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind={0} pick={1} reason={2} source={3}", InFlightEventKind ?? "event", InFlightPickId ?? "0", InFlightReason ?? "resolved", InFlightSource ?? "backend"));

                        ConfirmedEventKeys.Add(InFlightEventKey);
                        InFlightEventKey = null;
                        InFlightHadPendingChoice = false;
                        FlowCoordinator.CurrentEventPage++;
                        pageIndex = FlowCoordinator.CurrentEventPage;

                        if (isFin)
                        {
                            FlowCoordinator.IsSubPageManualInteracting = false;
                            GD.Print(string.Format("[AiTeammate][Event] event-finished owner=P2 finalPage={0}", pageIndex));
                            ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                            ulong actual = TurnFlowPatches.GetDraCurrentControlledPlayerId();
                            string actualStr = (p1Id != 0 && actual == p1Id) ? "P1" : actual.ToString();
                            GD.Print(string.Format("[AiTeammate][Event] event-room-final-controller expected=P1 actual={0}", actualStr));
                            return true;
                        }
                    }
                    else
                    {
                        double elapsed = (DateTime.UtcNow - InFlightEventSubmitTime).TotalSeconds;
                        if (elapsed < 3.0)
                        {
                            GD.Print(string.Format("[AiTeammate][Event] event-choice-duplicate-suppressed key={0}", InFlightEventKey));
                            return true;
                        }
                        else
                        {
                            if (InFlightEventRetryCount == 0)
                            {
                                InFlightEventRetryCount = 1;
                                InFlightEventSubmitTime = DateTime.UtcNow;
                                GD.Print(string.Format("[AiTeammate][Event] event-choice-resume key={0} retry=1", InFlightEventKey));
                                try
                                {
                                    chooseOptMethod.Invoke(synchronizer, new object[] { p2, InFlightChosenIndex });
                                }
                                catch (Exception ex)
                                {
                                    if (ex.ToString().IndexOf("Event is not in progress", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        GD.Print("[AiTeammate][Event] event-not-in-progress caught: duplicate callback handled without switching foreground");
                                        InFlightEventKey = null;
                                    }
                                }
                                return true;
                            }
                            else
                            {
                                GD.Print(string.Format("[AiTeammate][Event] event-choice-loop-guard-tripped key={0}", InFlightEventKey));
                                GD.Print(string.Format("[AiTeammate][Event] event-choice-abort key={0} reason=timeout-no-backend-response", InFlightEventKey));
                                AbortedEventKeys.Add(InFlightEventKey);
                                InFlightEventKey = null;
                                FlowCoordinator.IsSubPageManualInteracting = false;
                                return true;
                            }
                        }
                    }
                }

                if (curOpts == null || curOpts.Count == 0) return true;

                int chosenIdx = -1;
                string pickId = "0";
                string source = "fallback";
                string reason = "fallback";
                bool hasCoreDecision = false;

                if (curOpts.Count == 1)
                {
                    var singleOpt = curOpts[0];
                    PropertyInfo isProcProp = AccessTools.Property(singleOpt.GetType(), "IsProceed");
                    bool isProc = isProcProp != null && (bool)isProcProp.GetValue(singleOpt, null);
                    chosenIdx = 0;
                    pickId = "0";
                    reason = isProc ? "proceed" : "single-option";
                    source = "dialogue";
                }
                else
                {
                    try
                    {
                        IRunState run = GetRunState();
                        Assembly liveAsm = FindLiveSpireStrategistAssembly();
                        Type policyType = liveAsm != null ? liveAsm.GetType("SpireStrategist.Core.EventPolicy") : AccessTools.TypeByName("SpireStrategist.Core.EventPolicy");
                        Type snapReaderType = liveAsm != null ? liveAsm.GetType("SpireStrategist.Mod.SnapshotReader") : AccessTools.TypeByName("SpireStrategist.Mod.SnapshotReader");
                        Type eventChoiceType = liveAsm != null ? liveAsm.GetType("SpireStrategist.Core.EventChoice") : AccessTools.TypeByName("SpireStrategist.Core.EventChoice");

                        if (policyType != null && snapReaderType != null && eventChoiceType != null && run != null)
                        {
                            MethodInfo readMethod = null;
                            foreach (var m in snapReaderType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                            {
                                if (m.Name == "Read" && m.GetParameters().Length == 2)
                                {
                                    readMethod = m;
                                    break;
                                }
                            }
                            if (readMethod != null)
                            {
                                object snapshot = readMethod.Invoke(null, new object[] { run, p2 });
                                if (snapshot != null)
                                {
                                    FieldInfo pcField = AccessTools.Field(snapshot.GetType(), "<PlayerCount>k__BackingField");
                                    if (pcField != null) pcField.SetValue(snapshot, 1);

                                    System.Collections.IList choicesList = Activator.CreateInstance(typeof(List<>).MakeGenericType(eventChoiceType)) as System.Collections.IList;
                                    for (int i = 0; i < curOpts.Count; i++)
                                    {
                                        var opt = curOpts[i];
                                        if (opt == null) continue;
                                        bool locked = GetOptionIsLocked(opt);
                                        string title = GetOptionTitle(opt);
                                        string desc = GetOptionDescription(opt);
                                        object choiceObj = Activator.CreateInstance(eventChoiceType, new object[] { i, title ?? "", desc ?? "", locked });
                                        if (choiceObj != null) choicesList.Add(choiceObj);
                                    }

                                    MethodInfo evaluateMethod = policyType.GetMethod("EvaluateChoice", BindingFlags.Public | BindingFlags.Static);
                                    if (evaluateMethod != null)
                                    {
                                        object decision = evaluateMethod.Invoke(null, new object[] { snapshot, choicesList });
                                        if (decision != null)
                                        {
                                            PropertyInfo chosenIdxProp = AccessTools.Property(decision.GetType(), "ChosenIndex");
                                            if (chosenIdxProp != null)
                                            {
                                                int decIdx = (int)chosenIdxProp.GetValue(decision, null);
                                                if (decIdx >= 0 && decIdx < curOpts.Count)
                                                {
                                                    if (isAncient)
                                                    {
                                                        string tStr = GetOptionTitle(curOpts[decIdx]);
                                                        string dStr = GetOptionDescription(curOpts[decIdx]);
                                                        string tkStr = GetOptionTextKey(curOpts[decIdx]);
                                                        string full = ((tStr ?? "") + " " + (dStr ?? "") + " " + (tkStr ?? "")).ToLowerInvariant();
                                                        if (full.Contains("massive_scroll") || full.Contains("巨大卷轴"))
                                                        {
                                                            decIdx = -1; // Veto MASSIVE_SCROLL!
                                                            GD.Print("[AiTeammate][Event] massive_scroll vetoed for P2");
                                                        }
                                                    }
                                                    if (decIdx >= 0)
                                                    {
                                                        chosenIdx = decIdx;
                                                        pickId = decIdx.ToString();
                                                        source = "core";
                                                        reason = "event-policy";
                                                        hasCoreDecision = true;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    if (isAncient)
                    {
                        if (!hasCoreDecision)
                        {
                            string neowSource;
                            int neowIdx = EvaluateAncientOptionHeuristic(p2, curOpts, out pickId, out neowSource);
                            if (neowIdx >= 0 && neowIdx < curOpts.Count)
                            {
                                chosenIdx = neowIdx;
                                reason = "heuristic";
                                source = neowSource;
                            }
                        }
                    }

                    if (chosenIdx < 0)
                    {
                        chosenIdx = EvaluateEventOptionFallback(p2, curOpts, out pickId);
                        source = "fallback";
                        reason = "fallback-safe";
                    }
                }

                if (chosenIdx < 0 || chosenIdx >= curOpts.Count)
                {
                    chosenIdx = 0;
                    pickId = "0";
                    reason = "bounded-fallback";
                    source = "fallback";
                }

                string key = string.Format("{0}|{1}|{2}|{3}|{4}|{5}", generation, p2.NetId, eventId, pageIndex, curOptsFingerprint, chosenIdx);

                if (AbortedEventKeys.Contains(key))
                {
                    return true;
                }

                if (ConfirmedEventKeys.Contains(key))
                {
                    GD.Print(string.Format("[AiTeammate][Event] event-choice-duplicate-suppressed key={0}", key));
                    return true;
                }

                GD.Print(string.Format("[AiTeammate][Event] event-p2-choice-begin owner=P2 page={0}", pageIndex));
                GD.Print(string.Format("[AiTeammate][Flow] p2-choice-offer kind={0} owner=P2 page={1} options={2} pick={3}", eventKind, pageIndex, curOpts.Count, pickId));

                InFlightEventKey = key;
                InFlightEventId = eventId;
                InFlightEventPage = pageIndex;
                InFlightOptionsFingerprint = curOptsFingerprint;
                InFlightChosenIndex = chosenIdx;
                InFlightPickId = pickId;
                InFlightEventKind = eventKind;
                InFlightReason = reason;
                InFlightSource = source;
                InFlightGeneration = generation;
                InFlightEventSubmitTime = DateTime.UtcNow;
                InFlightEventRetryCount = 0;
                InFlightHadPendingChoice = false;
                FlowCoordinator.LastChildCardChoiceCompleted = false;

                GD.Print(string.Format("[AiTeammate][Event] event-choice-submit owner=P2 key={0} pick={1}", key, chosenIdx));
                GD.Print(string.Format("[AiTeammate][Event] event-choice-wait-backend key={0}", key));

                try
                {
                    chooseOptMethod.Invoke(synchronizer, new object[] { p2, chosenIdx });
                }
                catch (Exception ex)
                {
                    string exStr = ex.ToString();
                    if (exStr.IndexOf("Event is not in progress", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        GD.Print("[AiTeammate][Event] event-not-in-progress caught: duplicate callback handled without switching foreground");
                        InFlightEventKey = null;
                        return true;
                    }
                    GD.PrintErr("[AiTeammate][Event] chooseOptMethod exception: " + ex);
                }

                return true;
            }
catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Flow] ResolveP2PendingEvent exception: " + ex);

            }

            return false;

        }

    }



    // ================================================================================

    // Directive #5.0.6 R3-2: session-wide autopilot fuse. Independent component, always on,

    // consulted BEFORE any auto decision submits. Second offer of the same target

    // (kind + content fingerprint) that was never resolved opens the circuit for the rest

    // of the session — the autopilot path is then permanently blocked for that target and

    // the native/manual flow takes over. This is the precondition for re-introducing any

    // future automation.

    // Logging goes through Sink (wired to GD.Print at Init) so the component can also run

    // in out-of-game tests.

    // ================================================================================

    public static class AutopilotFuse

    {

        private static Dictionary<string, int> OfferCounts = new Dictionary<string, int>();

        private static Dictionary<string, bool> OpenCircuits = new Dictionary<string, bool>();

        private static Dictionary<string, DateTime> ResolvedTimestamps = new Dictionary<string, DateTime>();



        // Test seam: when set, log lines go here instead of GD.Print.

        public static Action<string> Sink;



        public static bool IsOpen(string kind, string fingerprint)

        {

            return OpenCircuits.ContainsKey(kind + ":" + fingerprint);

        }



        // Returns true when the auto action may proceed; false when the circuit is open.

        public static bool TryBeginOffer(string kind, string fingerprint)

        {

            string key = kind + ":" + fingerprint;

            if (OpenCircuits.ContainsKey(key))

            {

                Log("[AiTeammate][Fuse] autopilot-circuit-blocked target=" + key + " (session-permanent)");

                return false;

            }



            // Directive #5.0.7 T2: Consumption check - if same fingerprint is offered again

            // within 2.0s of being resolved, the resolution was unconsumed. Trip fuse immediately!

            DateTime lastResolved;

            if (ResolvedTimestamps.TryGetValue(key, out lastResolved))

            {

                double elapsedSec = (DateTime.UtcNow - lastResolved).TotalSeconds;

                if (elapsedSec < 2.0)

                {

                    OpenCircuits[key] = true;

                    Log("[AiTeammate][Fuse] autopilot-circuit-open target=" + key + " offers=2 (session-permanent)");

                    return false;

                }

            }



            int count = 0;

            OfferCounts.TryGetValue(key, out count);

            count++;

            OfferCounts[key] = count;

            if (count >= 2)

            {

                OpenCircuits[key] = true;

                Log("[AiTeammate][Fuse] autopilot-circuit-open target=" + key + " offers=" + count + " (session-permanent)");

                return false;

            }

            return true;

        }



        // Marks a successfully consumed offer so the next legit occurrence of the same content

        // starts counting from zero again. Never re-opens an opened circuit.

        public static void MarkResolved(string kind, string fingerprint)

        {

            string key = kind + ":" + fingerprint;

            if (OpenCircuits.ContainsKey(key)) return;

            OfferCounts.Remove(key);

            ResolvedTimestamps[key] = DateTime.UtcNow;

        }



        public static void TripCircuit(string kind, string fingerprint)

        {

            string key = kind + ":" + fingerprint;

            OpenCircuits[key] = true;

            Log("[AiTeammate][Fuse] autopilot-circuit-open target=" + key + " offers=2 (session-permanent)");

        }



        private static void Log(string line)

        {

            try

            {

                Action<string> d = Sink;

                if (d != null) d(line);

                else LogLines.Add(line);

            }

            catch { }

        }



        // Holds log lines when no sink is attached (e.g. out-of-game tests read these).

        public static List<string> LogLines = new List<string>();



        public static void ResetForTests()

        {

            OfferCounts.Clear();

            OpenCircuits.Clear();

            ResolvedTimestamps.Clear();

            LogLines.Clear();

        }

    }



    public static class FlowPatches

    {

        // Directive #5.15 T1: End Turn Button Force-Visible & Auto-Close Block

        public static class DraReevaluateEndTurnButtonStatePatch

        {

            public static bool Prefix()
            {
                if (!TurnCoordinator.IsPlayPhase()) return false;
                return true;
            }

            public static void Postfix(MegaCrit.Sts2.Core.Nodes.Combat.NCombatUi combatUi, MegaCrit.Sts2.Core.Combat.CombatState combatState, MegaCrit.Sts2.Core.Entities.Players.Player currentPlayer)

            {

                try

                {

                    if (combatUi == null || combatUi.EndTurnButton == null) return;

                    if (!TurnCoordinator.IsInCombat() || TurnCoordinator.IsPlayer1Dead()) return;

                    ulong p1Id = TurnCoordinator.GetPlayer1NetId();

                    ulong localId = LocalContext.NetId.HasValue ? LocalContext.NetId.Value : p1Id;

                    bool isCurrentP1 = (currentPlayer != null && currentPlayer.NetId == p1Id) || (currentPlayer == null && localId == p1Id);
                    if (!isCurrentP1) return;

                    if (EndTurnHandlerFallbackPatch.IsP1TurnActive())

                    {

                        Player p1 = FlowCoordinator.GetPlayer1();

                        if (p1 != null)

                        {

                            var btn = combatUi.EndTurnButton;
                            object canTurnObj = null;
                            try { var cp = AccessTools.Property(btn.GetType(), "CanTurnBeEnded"); if (cp != null) canTurnObj = cp.GetValue(btn, null); } catch { }
                            bool canTurn = canTurnObj is bool && (bool)canTurnObj;
                            if (!canTurn) return;
                            bool wasForced = false;
                            var stateProp = AccessTools.Property(btn.GetType(), "State");
                            object currentState = stateProp != null ? stateProp.GetValue(btn, null) : null;
                            int stateInt = -1;
                            try { if (currentState != null) stateInt = Convert.ToInt32(currentState); } catch { }
                            bool wasDisabled = stateInt != 0;
                            bool wasHidden = !btn.Visible;
                            if (!wasDisabled && !wasHidden) return;

                            if (stateProp != null)

                            {

                                object curState = currentState;

                                if (wasDisabled) // force Enabled state

                                {

                                    var setStateMethod = AccessTools.Method(btn.GetType(), "SetState");

                                    if (setStateMethod != null)

                                    {

                                        Type stateEnumType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton+State");

                                        if (stateEnumType != null)

                                        {

                                            object enabledVal;
                                            try { enabledVal = Enum.Parse(stateEnumType, "Enabled"); }
                                            catch { enabledVal = Enum.ToObject(stateEnumType, 0); }

                                            setStateMethod.Invoke(btn, new object[] { enabledVal });

                                            wasForced = true;

                                        }

                                    }

                                }

                            }



                            if (wasHidden || btn.Modulate.A < 0.9f)
                            {
                                btn.Visible = true;
                                FieldInfo stateField = AccessTools.Field(btn.GetType(), "_state");
                                if (stateField != null)
                                {
                                    stateField.SetValue(btn, Enum.ToObject(stateField.FieldType, 2)); // State.Hidden
                                }
                                MethodInfo setStateMethod = AccessTools.Method(btn.GetType(), "SetState");
                                if (setStateMethod != null)
                                {
                                    ParameterInfo[] ps = setStateMethod.GetParameters();
                                    if (ps.Length > 0)
                                    {
                                        setStateMethod.Invoke(btn, new object[] { Enum.ToObject(ps[0].ParameterType, 0) }); // State.Enabled (triggers AnimIn)
                                    }
                                }
                                wasForced = true;
                            }

                            if (wasForced)

                            {

                                string stateStr = stateProp != null ? (stateProp.GetValue(btn, null) ?? "unknown").ToString() : "unknown";

                                GD.Print("[AiTeammate][Coord] endturn-force-state corrected=True can-turn=True disabled=" + wasDisabled + " hidden=" + wasHidden + " state=" + stateStr);

                            }

                        }

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Coord] DraReevaluateEndTurnButtonStatePatch error: " + ex);

                }

            }

        }



        
        public static class EndTurnButtonSetStateGuardPatch
        {
            public static bool Prefix(object newState)
            {
                try
                {
                    if (!TurnCoordinator.IsPlayPhase())
                    {
                        int stateInt = -1;
                        try { if (newState != null) stateInt = Convert.ToInt32(newState); } catch { }
                        if (stateInt != 2) // 2 = State.Hidden
                        {
                            GD.Print(string.Format("[AiTeammate][Coord] endturn-nonplayphase-setstate-blocked requested={0}", newState != null ? newState.ToString() : "null"));
                            return false;
                        }
                    }
                }
                catch { }
                return true;
            }
        }

        public static class EndTurnButtonRefreshEnabledPostfixPatch
        {
            public static void Postfix(MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton __instance)
            {
                try
                {
                    if (__instance == null) return;
                    if (!TurnCoordinator.IsPlayPhase()) return;
                    Player p1 = FlowCoordinator.GetPlayer1();
                    var cm = CombatManager.Instance;
                    if (p1 == null || (cm != null && cm.IsPlayerReadyToEndTurn(p1))) return;

                    __instance.Visible = true;
                    if (__instance.Modulate.A < 0.9f)
                    {
                        FieldInfo stateField = AccessTools.Field(typeof(MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton), "_state");
                        if (stateField != null)
                        {
                            stateField.SetValue(__instance, Enum.ToObject(stateField.FieldType, 2)); // State.Hidden
                        }
                        MethodInfo setMethod = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton), "SetState");
                        if (setMethod != null)
                        {
                            ParameterInfo[] ps = setMethod.GetParameters();
                            if (ps.Length > 0)
                            {
                                setMethod.Invoke(__instance, new object[] { Enum.ToObject(ps[0].ParameterType, 0) }); // State.Enabled
                            }
                        }
                        GD.Print("[AiTeammate][Coord] endturn-button-refresh-anim-healed");
                    }
                }
                catch { }
            }
        }

        public static class DraTryEndAllPlayersWhenNoCardsPatch

        {

            public static bool Prefix(MegaCrit.Sts2.Core.Combat.CombatState combatState, string source)

            {

                try

                {

                    if (TurnCoordinator.IsInCombat() && !TurnCoordinator.IsPlayer1Dead())

                    {

                        Player p1 = FlowCoordinator.GetPlayer1();

                        if (p1 != null && (MegaCrit.Sts2.Core.Combat.CombatManager.Instance == null || !MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsPlayerReadyToEndTurn(p1)))

                        {

                            GD.Print(string.Format("[AiTeammate][Coord] endturn-auto-close-blocked: blocked auto-ending turn during active P1 turn (source={0})", source ?? "unknown"));

                            return false;

                        }

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Coord] DraTryEndAllPlayersWhenNoCardsPatch error: " + ex);

                }

                return true;

            }

        }



        public static class MapSelectionVotePatch

        {

            public static bool Prefix(object __instance, Player player, ref MegaCrit.Sts2.Core.Runs.MapLocation source, object destination)
            {
                try
                {
                    if (!source.coord.HasValue)
                    {
                        var sourceField = AccessTools.Field(__instance.GetType(), "_acceptingVotesFromSource");
                        if (sourceField != null)
                        {
                            var actual = (MegaCrit.Sts2.Core.Runs.MapLocation)sourceField.GetValue(__instance);
                            if (actual.coord.HasValue) source = actual;
                        }
                        var run = FlowCoordinator.GetRunState();
                        if (!source.coord.HasValue && run != null && run.CurrentMapCoord.HasValue)
                        {
                            GD.PrintErr("[AiTeammate][Flow] route-follow-rejected reason=source-not-aligned-with-active-location");
                            return false;
                        }
                    }
                    if (FlowCoordinator.IsRewardGateActive())
                        return FlowCoordinator.BeforeMapVote(__instance, player, source, destination);
                }
                catch (Exception ex) { GD.PrintErr("[AiTeammate][Flow] MapSelectionVotePatch prefix exception: " + ex); }
                return true;
            }

            public static void Postfix(object __instance, Player player, MegaCrit.Sts2.Core.Runs.MapLocation source, object destination, bool __runOriginal)

            {

                try

                {

                    if (!__runOriginal || !FlowCoordinator.AutoRouteFollowEnabled) return;

                    if (!source.coord.HasValue && FlowCoordinator.GetRunState() != null && FlowCoordinator.GetRunState().CurrentMapCoord.HasValue)
                    {
                        GD.PrintErr("[AiTeammate][Flow] route-follow-rejected reason=source-coord-null");
                        return;
                    }

                    if (!FlowCoordinator.BeforeMapVote(__instance, player, source, destination)) return;



                    Player p1 = FlowCoordinator.GetPlayer1();

                    Player p2 = FlowCoordinator.GetPlayer2();

                    if (p1 == null || p2 == null || player == null) return;



                    if (player.NetId == p1.NetId && destination != null)

                    {

                        string coordStr = "(0,0)";

                        try

                        {

                            PropertyInfo coordProp = destination.GetType().GetProperty("coord");

                            if (coordProp == null) coordProp = destination.GetType().GetProperty("Coord");

                            if (coordProp != null)

                            {

                                object cVal = coordProp.GetValue(destination, null);

                                if (cVal != null) coordStr = cVal.ToString();

                            }

                            else

                            {

                                FieldInfo coordField = destination.GetType().GetField("coord");

                                if (coordField == null) coordField = destination.GetType().GetField("Coord");

                                if (coordField != null)

                                {

                                    object cVal = coordField.GetValue(destination);

                                    if (cVal != null) coordStr = cVal.ToString();

                                }

                            }

                        }

                        catch { }

                        string resolvedDestination = FlowCoordinator.DestinationKey(destination);
                        if (!string.IsNullOrEmpty(resolvedDestination)) coordStr = resolvedDestination;



                        bool p2HasVote = false;

                        try

                        {

                            MethodInfo getVoteMethod = AccessTools.Method(__instance.GetType(), "GetVote", new Type[] { typeof(Player) });

                            if (getVoteMethod != null)

                            {

                                object p2v = getVoteMethod.Invoke(__instance, new object[] { p2 });

                                if (p2v != null)

                                {

                                    PropertyInfo hasValProp = p2v.GetType().GetProperty("HasValue");

                                    if (hasValProp != null)

                                    {

                                        p2HasVote = (bool)hasValProp.GetValue(p2v, null);

                                    }

                                }

                            }

                        }

                        catch { }



                        if (p2HasVote)

                        {

                            GD.Print(string.Format("[AiTeammate][Flow] p2-route-follow coord={0} source=DRA", coordStr));

                        }

                        else

                        {

                            // R3-2: the bridged P2 vote is an auto decision — fuse-check it first.

                            if (!FlowCoordinator.TryReserveRoute(FlowCoordinator.CurrentRunGeneration(), coordStr)) return;

                            if (!AutopilotFuse.TryBeginOffer("route-follow", "coord=" + coordStr))

                            {

                                GD.PrintErr("[AiTeammate][Flow] route-follow blocked by fuse: coord=" + coordStr);

                                return;

                            }

                            try

                            {

                                MethodInfo voteMethod = AccessTools.Method(__instance.GetType(), "PlayerVotedForMapCoord");

                                if (voteMethod != null)

                                {

                                    ParameterInfo[] pars = voteMethod.GetParameters();

                                    if (pars.Length == 3)

                                    {

                                        voteMethod.Invoke(__instance, new object[] { p2, source, destination });

                                    }

                                    else if (pars.Length == 2)

                                    {

                                        voteMethod.Invoke(__instance, new object[] { p2, destination });

                                    }

                                    AutopilotFuse.MarkResolved("route-follow", "coord=" + coordStr);

                                    GD.Print(string.Format("[AiTeammate][Flow] p2-route-follow coord={0} source=AiTeammateBridge", coordStr));

                                }

                            }

                            catch (Exception exBridge)

                            {

                                GD.PrintErr("[AiTeammate][Flow] Bridge P2 route vote failed: " + exBridge.Message);

                            }

                        }

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] MapSelectionVotePatch exception: " + ex);

                }

            }

        }



        public static class CardRewardOnSelectPatch
        {
            public static void Prefix(MegaCrit.Sts2.Core.Rewards.CardReward __instance)
            {
                try
                {
                    RewardBackendFlow.NotifyCardRewardSelectBegin(__instance);
                }
                catch { }
                if (__instance != null && __instance.Player.NetId == TurnCoordinator.GetPlayer2NetId())
                    GD.Print("[AiTeammate][Flow] p2-choice-offer kind=card-reward owner=P2 native=true fingerprint=" + FlowCoordinator.RewardFingerprint(__instance));
            }

            public static void Postfix(MegaCrit.Sts2.Core.Rewards.CardReward __instance, Task __result)
            {
                if (__result != null)
                {
                    __result.ContinueWith(delegate
                    {
                        try
                        {
                            RewardBackendFlow.NotifyCardRewardSelectEnd(__instance);
                        }
                        catch { }
                    });
                }
                else
                {
                    try
                    {
                        RewardBackendFlow.NotifyCardRewardSelectEnd(__instance);
                    }
                    catch { }
                }
            }
        }
        public static class RewardsScreenReadyPatch

        {

            public static void Postfix(NRewardsScreen __instance)

            {

                try

                {

                    if (__instance == null) return;

                    if (!RewardBackendFlow.IsManagedScreen(__instance))
                    {
                        FlowCoordinator.BeginRewardGate(__instance);
                        FlowCoordinator.TrackRewardScreen(__instance);
                        Callable.From(delegate { FlowCoordinator.TryAutoClaimP2Rewards(__instance); }).CallDeferred();
                    }

                }

                catch { }

            }

        }



        public static class RewardRuntimeGatePatches
        {
            public static void RewardButtonGetRewardPostfix(object __instance, Task __result)
            {
                try { FlowCoordinator.BeginRewardSelectionFromButton(__instance, __result); }
                catch (Exception ex) { GD.PrintErr("[AiTeammate][Flow] reward-button-task-observer-failed exception=" + ex); }
            }

            public static void RewardsCompletedSignalPostfix(NRewardsScreen __instance)
            {
                try { FlowCoordinator.MarkRewardCompletedSignal(__instance); }
                catch (Exception ex) { GD.PrintErr("[AiTeammate][Flow] reward-completed-signal-observer-failed exception=" + ex); }
            }

            public static void RewardsScreenUpdatePostfix(NRewardsScreen __instance)
            {
                try { FlowCoordinator.MarkRewardScreenCompleted(__instance, "NRewardsScreen.UpdateScreenState"); }
                catch (Exception ex) { GD.PrintErr("[AiTeammate][Flow] reward-screen-state-observer-failed exception=" + ex); }
            }

            public static void RewardsSetCompletePostfix(object __instance)
            {
                try { FlowCoordinator.RecheckRewardGate("RewardsSetSynchronizer.CompleteRewardsSet"); }
                catch (Exception ex) { GD.PrintErr("[AiTeammate][Flow] reward-set-observer-failed exception=" + ex); }
            }

            public static bool ExitCurrentRoomPrefix(ref Task<MegaCrit.Sts2.Core.Rooms.AbstractRoom> __result)
            {
                try { return FlowCoordinator.BeforeExitCurrentRoom(ref __result); }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Flow] room-exit-gate-blocked exception=" + ex);
                    __result = FlowCoordinator.BlockRoomExitOnGateError(ex);
                    return false;
                }
            }

            public static bool MoveToMapCoordActionPrefix(object __instance, ref Task __result)
            {
                try { return FlowCoordinator.BeforeMoveToMapCoordAction(__instance, ref __result); }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Flow] route-follow-rejected action=MoveToMapCoordAction reason=gate-exception exception=" + ex);
                    __result = Task.CompletedTask;
                    return false;
                }
            }
        }

        public static class RelicSelectFromScreenPatch

        {

            public static bool Prefix(Player player, IReadOnlyList<RelicModel> relics, ref Task<RelicModel> __result)

            {

                try

                {

                    ulong owner = player != null ? player.NetId : 0;

                    GD.Print(string.Format("[AiTeammate][Flow] probe: FromChooseARelicScreen enter player={0} count={1}", owner, relics != null ? relics.Count : 0));

                    Player p2 = FlowCoordinator.GetPlayer2();

                    ulong p2NetId = TurnCoordinator.GetPlayer2NetId();

                    bool isP2 = false;

                    if (p2 != null && player != null && (player == p2 || player.NetId == p2.NetId)) isP2 = true;

                    else if (player != null && p2NetId != 0 && player.NetId == p2NetId) isP2 = true;

                    else if (player != null && player.NetId == 76561199137958002) isP2 = true;

                    else if (owner != 0 && p2 != null && owner == p2.NetId) isP2 = true;

                    else if (owner != 0 && p2NetId != 0 && owner == p2NetId) isP2 = true;



                    if (isP2)

                    {

                        if (p2 == null) p2 = player;

                        int count = relics != null ? relics.Count : 0;

                        List<string> rids = new List<string>();

                        if (relics != null)

                        {

                            for (int i = 0; i < relics.Count; i++)

                            {

                                if (relics[i] != null) rids.Add(relics[i].Id.ToString());

                            }

                        }

                        string fingerprint = string.Join(",", rids.ToArray());



                        // Directive #5.0.8 T3: Fuse protection for Relic Path A

                        if (!AutopilotFuse.TryBeginOffer("relic", fingerprint))

                        {

                            GD.Print("[AiTeammate][Flow] relic circuit-open: fail-open to manual selection");

                            return true;

                        }



                        GD.Print(string.Format("[AiTeammate][Flow] p2-choice-offer kind=relic path=A owner=P2 count={0} relics={1}", count, fingerprint));



                        string pickId;

                        string source;

                        int chosenIndex;

                        string coreError;

                        string scoresStr;

                        RelicModel chosenRelic = FlowCoordinator.EvaluateRelicSelection(p2, relics, out pickId, out source, out chosenIndex, out coreError, out scoresStr);



                        AutopilotFuse.MarkResolved("relic", fingerprint);



                        if (source == "core")

                        {

                            GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=relic path=A pick={0} reason=core scores={1} relic={2}", chosenIndex, scoresStr ?? "none", pickId));

                        }

                        else

                        {

                            string reason = "core-error:" + (coreError ?? "unknown") + " fallback=relic-score-highest";

                            GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=relic path=A pick={0} reason={1} scores=none relic={2}", chosenIndex, reason, pickId));

                        }



                        __result = Task.FromResult(chosenRelic);

                        return false;

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] RelicSelectFromScreenPatch exception: " + ex);

                }

                return true;

            }

        }



        public static class TreasureRoomRelicProbePatch

        {

            private static int _lastHandledTreasureFloor = -1;

            private static bool _p2PickedThisRoom = false;



            public static void CheckAndPickP2TreasureRelic(object __instance, Player triggerPlayer)

            {

                try

                {

                    if (__instance == null) return;

                    IRunState run = FlowCoordinator.GetRunState();

                    int floor = run != null ? run.TotalFloor : 0;

                    if (_lastHandledTreasureFloor != floor)

                    {

                        _lastHandledTreasureFloor = floor;

                        _p2PickedThisRoom = false;

                    }



                    Player p2 = FlowCoordinator.GetPlayer2();

                    if (p2 == null || _p2PickedThisRoom) return;



                    MethodInfo getVoteMethod = AccessTools.Method(__instance.GetType(), "GetPlayerVote", new Type[] { typeof(Player) });

                    if (getVoteMethod != null)

                    {

                        object vote = getVoteMethod.Invoke(__instance, new object[] { p2 });

                        if (vote != null)

                        {

                            FieldInfo recField = AccessTools.Field(vote.GetType(), "voteReceived");

                            if (recField != null && (bool)recField.GetValue(vote))

                            {

                                return; // P2 already voted!

                            }

                        }

                    }



                    var relicsProp = AccessTools.Property(__instance.GetType(), "CurrentRelics");

                    var curRelics = relicsProp != null ? relicsProp.GetValue(__instance, null) as IReadOnlyList<RelicModel> : null;

                    if (curRelics != null && curRelics.Count > 0)

                    {

                        List<string> rids = new List<string>();

                        for (int i = 0; i < curRelics.Count; i++)

                        {

                            if (curRelics[i] != null) rids.Add(curRelics[i].Id.ToString());

                        }

                        string fingerprint = string.Join(",", rids.ToArray());



                        // Directive #5.0.8 T3: Fuse protection for Relic Path B

                        if (!AutopilotFuse.TryBeginOffer("relic", fingerprint))

                        {

                            GD.Print("[AiTeammate][Flow] treasure relic circuit-open: skipping auto-pick");

                            return;

                        }



                        GD.Print(string.Format("[AiTeammate][Flow] probe: TreasureRoomRelicSynchronizer votes={0} player={1}", curRelics.Count, p2.NetId));

                        _p2PickedThisRoom = true;

                        GD.Print(string.Format("[AiTeammate][Flow] p2-choice-offer kind=relic path=B owner=P2 count={0} relics={1}", curRelics.Count, fingerprint));



                        string pickId;

                        string source;

                        int chosenIndex;

                        string coreError;

                        string scoresStr;

                        RelicModel chosen = FlowCoordinator.EvaluateRelicSelection(p2, curRelics, out pickId, out source, out chosenIndex, out coreError, out scoresStr);

                        int pickIdx = chosenIndex;



                        AutopilotFuse.MarkResolved("relic", fingerprint);



                        if (source == "core")

                        {

                            GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=relic path=B pick={0} idx={1} reason=core scores={2} relic={3}", pickId, pickIdx, scoresStr ?? "none", pickId));

                        }

                        else

                        {

                            string reason = "core-error:" + (coreError ?? "unknown") + " fallback=relic-score-highest";

                            GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=relic path=B pick={0} idx={1} reason={2} scores=none relic={3}", pickId, pickIdx, reason, pickId));

                        }



                        var onPickedMethod = AccessTools.Method(__instance.GetType(), "OnPicked", new Type[] { typeof(Player), typeof(Nullable<int>) });

                        if (onPickedMethod != null)

                        {

                            onPickedMethod.Invoke(__instance, new object[] { p2, (int?)pickIdx });

                        }

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] CheckAndPickP2TreasureRelic exception: " + ex);

                }

            }



            public static void Prefix(object __instance, Player player, object index)

            {

                CheckAndPickP2TreasureRelic(__instance, player);

            }



            public static void Postfix(object __instance, Player player)

            {

                CheckAndPickP2TreasureRelic(__instance, player);

            }

        }



        public static class TreasureRoomRelicBeginPatch

        {

            public static void Postfix(object __instance)

            {

                try

                {

                    Player p2 = FlowCoordinator.GetPlayer2();

                    if (p2 == null) return;

                    FlowPatches.TreasureRoomRelicProbePatch.CheckAndPickP2TreasureRelic(__instance, p2);

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] TreasureRoomRelicBeginPatch Postfix exception: " + ex);

                }

            }

        }



        public static class InCombatCardSelectPatch

        {

            public static bool FromChooseACardScreenPrefix(

                object context,

                IReadOnlyList<CardModel> cards,

                Player player,

                bool canSkip,

                ref Task<CardModel> __result)

            {

                try

                {

                    Player p2 = FlowCoordinator.GetPlayer2();

                    if (p2 == null || player == null || player.NetId != p2.NetId)

                    {

                        ChoiceSuppressionPatches.LastP1ChoicePassThroughTime = DateTime.UtcNow;

                        return true;

                    }
                    if (cards != null)
                    {
                        foreach(CardModel c in cards)
                        {
                            if (c != null)
                            {
                                if (c.Owner == null) c.Owner = p2;
                                else if (c.Owner.NetId != p2.NetId)
                                {
                                    if (TurnCoordinator.IsInEventRoom()) { c.Owner = p2; }
                                    else { GD.PrintErr("[AiTeammate][Coord] circuit-open: cross-owner-block candidate pool owner mismatch"); AutopilotFuse.TripCircuit("choice-candidate-pool","cross-owner-block"); return true; }
                                }
                            }
                        }
                    }



                    List<string> cardIds = new List<string>();

                    if (cards != null)

                    {

                        for (int i = 0; i < cards.Count; i++)

                        {

                            if (cards[i] != null) cardIds.Add(cards[i].Id.ToString());

                        }

                    }

                    string cardsStr = string.Join(",", cardIds.ToArray());

                    GD.Print(string.Format("[AiTeammate][Flow] p2-choice-offer kind=in-combat-card-choice owner=P2 cards={0}", cardsStr));



                    DateTime start = DateTime.UtcNow;

                    CardModel chosen = null;

                    string chosenId = "none";

                    string source = "core";

                    string reason = "core";



                    try

                    {

                        if (cards != null && cards.Count > 0)

                        {

                            chosen = cards[0];

                            double bestScore = FlowCoordinator.GetCardScore(chosen.Id.ToString());

                            for (int i = 1; i < cards.Count; i++)

                            {

                                if ((DateTime.UtcNow - start).TotalSeconds >= 3.0) break;

                                double s = FlowCoordinator.GetCardScore(cards[i].Id.ToString());

                                if (s > bestScore)

                                {

                                    bestScore = s;

                                    chosen = cards[i];

                                }

                            }

                            chosenId = chosen != null ? chosen.Id.ToString() : "none";

                        }

                    }

                    catch (Exception ex)

                    {

                        reason = "core-error:" + ex.Message;

                        source = "fallback";

                    }



                    if ((DateTime.UtcNow - start).TotalSeconds >= 3.0 || chosen == null)

                    {

                        GD.Print("[AiTeammate][Flow] in-combat-choice-fallback-skip");

                        if (canSkip)

                        {

                            __result = Task.FromResult<CardModel>(null);

                        }

                        else

                        {

                            __result = Task.FromResult<CardModel>(cards != null && cards.Count > 0 ? cards[0] : null);

                        }

                        return false;

                    }



                    GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=in-combat-card-choice pick={0} reason={1} source={2}", chosenId, reason, source));

                    __result = Task.FromResult<CardModel>(chosen);

                    return false;

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] in-combat-choice-fallback-skip exception: " + ex.Message);

                    GD.Print("[AiTeammate][Flow] in-combat-choice-fallback-skip");

                    __result = Task.FromResult<CardModel>(cards != null && cards.Count > 0 ? cards[0] : null);

                    return false;

                }

            }



            // R2-1/R2-4: real return type is Task<IEnumerable<CardModel>> (not IReadOnlyList);

            // declaring __result with the exact generic type is what fixes the Harmony mismatch.

            public static bool FromSimpleGridPrefix(

                object context,

                IReadOnlyList<CardModel> cardsIn,

                Player player,

                object prefs,

                ref Task<IEnumerable<CardModel>> __result)

            {

                return InCombatGridDecide("in-combat-card-grid", cardsIn, player, prefs, ref __result);

            }



            // R2-4: loot/"add card to deck" grid entry (Scrounge-class reward grids).

            // Creation method documented in locating doc §8.4: CardSelectCmd.FromSimpleGridForRewards.

            // Signature note (verified by reflection): second parameter is literally named "cards"

            // and typed List<CardCreationResult>; the return is Task<IEnumerable<CardModel>>.

            public static bool FromSimpleGridForRewardsPrefix(

                object context,

                List<CardCreationResult> cards,

                Player player,

                object prefs,

                ref Task<IEnumerable<CardModel>> __result)

            {

                List<CardModel> models = new List<CardModel>();

                if (cards != null)

                {

                    for (int i = 0; i < cards.Count; i++)

                    {

                        if (cards[i] != null && cards[i].Card != null) models.Add(cards[i].Card);

                    }

                }

                return InCombatGridDecide("in-combat-choice", models, player, prefs, ref __result);

            }



            private static bool InCombatGridDecide(string kind, IReadOnlyList<CardModel> cardsIn, Player player, object prefs, ref Task<IEnumerable<CardModel>> __result)

            {

                List<CardModel> picked = new List<CardModel>();

                try

                {

                    Player p2 = FlowCoordinator.GetPlayer2();

                    if (p2 == null || player == null || player.NetId != p2.NetId) { ChoiceSuppressionPatches.LastP1ChoicePassThroughTime = DateTime.UtcNow; return true; }
                    if (cardsIn != null)
                    {
                        foreach(CardModel c in cardsIn)
                        {
                            if (c != null)
                            {
                                if (c.Owner == null) c.Owner = p2;
                                else if (c.Owner.NetId != p2.NetId)
                                {
                                    if (TurnCoordinator.IsInEventRoom()) { c.Owner = p2; }
                                    else { GD.PrintErr("[AiTeammate][Coord] circuit-open: cross-owner-block candidate pool owner mismatch"); AutopilotFuse.TripCircuit("choice-candidate-pool","cross-owner-block"); return true; }
                                }
                            }
                        }
                    }



                    List<string> cardIds = new List<string>();

                    if (cardsIn != null)

                    {

                        for (int i = 0; i < cardsIn.Count; i++)

                        {

                            if (cardsIn[i] != null) cardIds.Add(cardsIn[i].Id.ToString());

                        }

                    }

                    string cardsStr = string.Join(",", cardIds.ToArray());

                    GD.Print(string.Format("[AiTeammate][Flow] p2-choice-offer kind={0} owner=P2 cards={1}", kind, cardsStr));



                    DateTime start = DateTime.UtcNow;

                    CardModel best = null;

                    double bestScore = double.MinValue;

                    string coreDetail = "unavailable:no-cards";



                    if (cardsIn != null && cardsIn.Count > 0)

                    {

                        try

                        {

                            best = cardsIn[0];

                            bestScore = FlowCoordinator.GetCardScore(best.Id.ToString());

                            for (int i = 1; i < cardsIn.Count; i++)

                            {

                                if ((DateTime.UtcNow - start).TotalSeconds >= 3.0) break;

                                double s = FlowCoordinator.GetCardScore(cardsIn[i].Id.ToString());

                                if (s > bestScore) { bestScore = s; best = cardsIn[i]; }

                            }

                            coreDetail = "input=" + cardsStr + " output=" + (best != null ? best.Id.ToString() : "none");

                        }

                        catch (Exception ex)

                        {

                            coreDetail = "core-error:" + ex.Message;

                            best = cardsIn[0];

                        }

                    }



                    if (best != null)

                    {

                        picked.Add(best);

                        GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind={0} pick={1} reason=core source=core core-decision {2}", kind, best.Id, coreDetail));

                        __result = Task.FromResult<IEnumerable<CardModel>>(picked);

                        return false;

                    }



                    // Nothing selectable: grid cmds encode skip as an empty selection — but only

                    // when prefs actually allow it (MinSelect == 0), otherwise pick the first card.

                    bool skipAllowed = true;

                    try

                    {

                        PropertyInfo minSelProp = prefs != null ? prefs.GetType().GetProperty("MinSelect") : null;

                        if (minSelProp != null && (int)minSelProp.GetValue(prefs, null) > 0) skipAllowed = false;

                    }

                    catch { }



                    if (!skipAllowed && cardsIn != null && cardsIn.Count > 0)

                    {

                        picked.Add(cardsIn[0]);

                        GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind={0} pick={1} reason=forced-min-select source=fallback", kind, cardsIn[0].Id));

                    }

                    else

                    {

                        GD.Print(string.Format("[AiTeammate][Flow] in-combat-choice-fallback-skip kind={0} cards={1}", kind, cardsStr));

                        GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind={0} pick=skip reason=empty-grid source=fallback", kind));

                    }

                    __result = Task.FromResult<IEnumerable<CardModel>>(picked);

                    return false;

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] in-combat-choice-fallback-skip exception: " + ex.Message);

                    GD.Print("[AiTeammate][Flow] in-combat-choice-fallback-skip");

                    if (cardsIn != null && cardsIn.Count > 0) picked.Add(cardsIn[0]);

                    __result = Task.FromResult<IEnumerable<CardModel>>(picked);

                    return false;

                }

            }

        }



        public static class RelicRewardOnSelectProbePatch

        {

            public static void Prefix(MegaCrit.Sts2.Core.Rewards.RelicReward __instance)

            {

                try

                {

                    Player p2 = FlowCoordinator.GetPlayer2();

                    if (p2 != null && __instance != null && __instance.Player != null && __instance.Player.NetId == p2.NetId)

                    {

                        string relicId = __instance.Relic != null ? __instance.Relic.Id.ToString() : (__instance.ClaimedRelic != null ? __instance.ClaimedRelic.Id.ToString() : "relic");

                        GD.Print(string.Format("[AiTeammate][Flow] p2-choice-offer kind=relic path=C owner=P2 relic={0}", relicId));

                        GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=relic path=C pick={0} reason=auto-claim source=fallback", relicId));

                    }

                }

                catch { }

            }

        }



        public static class SharedEventAutoProxyPatch

        {

            public static void Postfix(object[] __args)

            {

                try

                {

                    if (__args != null && __args.Length > 0 && __args[0] != null)

                    {

                        var synchronizer = __args[0];

                        var prop = AccessTools.Property(synchronizer.GetType(), "IsShared");

                        bool isShared = prop != null && (bool)prop.GetValue(synchronizer, null);

                        if (isShared)

                        {

                            GD.Print("[AiTeammate][Flow] shared-event-auto-proxied");

                        }

                    }

                }

                catch { }

            }

        }



        // Directive #5.30: Event Auto-Next Suppression & P1 Foreground Anchor
        public static class DraTryRunPendingEventAutoSwitchPatch
        {
            public static bool Prefix(string source)
            {
                try
                {
                    try
                    {
                        Type coopType = AccessTools.TypeByName("LocalMultiControl.Scripts.Context.LocalSelfCoopContext");
                        if (coopType != null)
                        {
                            MethodInfo mConsume = AccessTools.Method(coopType, "TryConsumePendingEventAutoSwitch");
                            if (mConsume != null) mConsume.Invoke(null, null);
                        }
                    }
                    catch { }

                    if (FlowCoordinator.AutoEventEnabled)
                    {
                        Player p2 = FlowCoordinator.GetPlayer2();
                        if (p2 != null && RunManager.Instance != null && RunManager.Instance.EventSynchronizer != null)
                        {
                            FlowCoordinator.ResolveP2PendingEvent(RunManager.Instance.EventSynchronizer, p2);
                        }
                    }

                    ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                    ulong actual = TurnFlowPatches.GetDraCurrentControlledPlayerId();
                    GD.Print(string.Format("[AiTeammate][Event] control-context-assert room=event expected=P1 actual={0}", actual));

                    if (p1Id != 0 && actual != p1Id)
                    {
                        TurnFlowPatches.SwitchControlToPlayer(p1Id, "wakuu-p1-restore:event-auto-next", false);
                        GD.Print(string.Format("[AiTeammate][Event] event-auto-next-p1-restore target={0}", p1Id));
                    }
                    else
                    {
                        GD.Print(string.Format("[AiTeammate][Event] event-auto-next-p1-restore already-P1={0}", p1Id));
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Event] DraTryRunPendingEventAutoSwitchPatch error: " + ex);
                    return false;
                }
            }
        }

        public static class EventAutoSwitchPrefixPatch

        {

            public static bool Prefix(object[] __args)

            {

                try

                {

                    if (TurnCoordinator.IsEnteringRoom) return false; // Directive #5.14 T2: Silent during room enter window

                    if (!FlowCoordinator.AutoEventEnabled) return true;

                    if (FlowCoordinator.IsSubPageManualInteracting) return true;

                    Player p2 = FlowCoordinator.GetPlayer2();

                    if (p2 == null) return true;



                    object synchronizer = null;

                    if (RunManager.Instance != null)

                    {

                        synchronizer = RunManager.Instance.EventSynchronizer;

                    }

                    if (synchronizer == null) return true;



                    bool handled = FlowCoordinator.ResolveP2PendingEvent(synchronizer, p2);

                    if (handled)

                    {

                        return false; // Handled programmatically, skip switching UI to P2!

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] EventAutoSwitchPrefixPatch exception: " + ex);

                }

                return true;

            }

        }



        public static class EventSynchronizerPendingProbePatch

        {

            public static void Postfix(object __instance)

            {

                try

                {

                    if (!FlowCoordinator.AutoEventEnabled || __instance == null) return;

                    if (FlowCoordinator.IsSubPageManualInteracting) return;

                    Player p2 = FlowCoordinator.GetPlayer2();

                    if (p2 == null) return;



                    FlowCoordinator.ResolveP2PendingEvent(__instance, p2);

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] EventSynchronizerPendingProbePatch exception: " + ex);

                }

            }

        }



        public static class RestSiteNextPlayerPatch

        {

            public static void Postfix(object synchronizer, ulong currentPlayerId, ref ulong nextPlayerId, ref bool __result)

            {

                try

                {

                    if (!FlowCoordinator.AutoRestSiteEnabled) return;

                    Player p2 = FlowCoordinator.GetPlayer2();

                    if (p2 == null) return;



                    if (__result && nextPlayerId == p2.NetId)

                    {

                        bool resolved = FlowCoordinator.ResolveP2RestSite(synchronizer, p2);

                        if (resolved)

                        {

                            __result = false; // Resolved programmatically! Don't let DRA switch to P2!

                        }

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] RestSiteNextPlayerPatch exception: " + ex);

                }

            }

        }



        public static class RestSiteAutoSwitchPrefixPatch

        {

            // R2-1: DRA's SwitchToPlayerAndEnsureOptions is (ulong targetPlayerId, string source);

            // the prefix parameter name must match "targetPlayerId" exactly or Harmony rejects the patch.

            public static bool Prefix(ulong targetPlayerId)

            {

                Player p2 = FlowCoordinator.GetPlayer2();

                if (p2 != null && targetPlayerId == p2.NetId)

                {

                    return false; // Suppress switching UI to P2!

                }

                return true;

            }

        }



        public static class RestSiteProbePatch

        {

            public static void Postfix(object __instance, int index, object __result)

            {

                try

                {

                    Player p2 = FlowCoordinator.GetPlayer2();

                    if (p2 == null || __instance == null) return;

                    var method = AccessTools.Method(__instance.GetType(), "GetOptionsForPlayer", new Type[] { typeof(Player) });

                    var opts = method != null ? method.Invoke(__instance, new object[] { p2 }) as System.Collections.ICollection : null;

                    int optCount = opts != null ? opts.Count : 0;

                    GD.Print(string.Format("[AiTeammate][Flow] probe: RestSite nextPlayerId==P2 options={0}", optCount));

                }

                catch { }

            }

        }



        
        public static class CardSelectFromDeckForTransformationPatch
        {
            public static bool Prefix(Player player, object prefs, Func<CardModel, CardModel> cardToTransformation, ref Task<IEnumerable<CardModel>> __result)
            {
                DateTime startTime = DateTime.UtcNow;
                try
                {
                    if (player == null) return true;
                    Player p2 = FlowCoordinator.GetPlayer2();
                    if (p2 == null || player.NetId != p2.NetId) { ChoiceSuppressionPatches.LastP1ChoicePassThroughTime = DateTime.UtcNow; return true; } // P1 100% manual!

                    int targetCount = 1;
                    try
                    {
                        if (prefs != null)
                        {
                            var mp = AccessTools.Property(prefs.GetType(), "MaxSelect");
                            if (mp != null) targetCount = Math.Max(1, Convert.ToInt32(mp.GetValue(prefs, null)));
                        }
                    }
                    catch { }

                    string fingerprint = "transform:" + (player.Deck != null && player.Deck.Cards != null ? player.Deck.Cards.Count : 0) + ":n=" + targetCount;
                    if (!AutopilotFuse.TryBeginOffer("transform", fingerprint))
                    {
                        GD.Print("[AiTeammate][Flow] transform circuit-open: fail-open to manual selection");
                        return true;
                    }

                    GD.Print("[AiTeammate][Flow] p2-choice-offer kind=transform owner=P2");

                    List<CardModel> selectedCards = new List<CardModel>();
                    List<CardModel> candidates = new List<CardModel>();

                    if (p2.Deck != null && p2.Deck.Cards != null)
                    {
                        Dictionary<string, int> tagCounts = new Dictionary<string, int>();
                        foreach (CardModel c in p2.Deck.Cards)
                        {
                            if (c == null) continue;
                            string cid = c.Id.ToString();
                            foreach (string tag in FlowCoordinator.GetCardTags(cid))
                            {
                                if (tagCounts.ContainsKey(tag)) tagCounts[tag]++;
                                else tagCounts[tag] = 1;
                            }
                        }

                        string[] protectedTags = new string[] { "vulnerable-source", "card-draw-engine", "block-scaling" };
                        List<CardModel> allEligible = new List<CardModel>();

                        foreach (CardModel card in p2.Deck.Cards)
                        {
                            if (card == null) continue;

                            if (cardToTransformation != null)
                            {
                                try
                                {
                                    if (cardToTransformation(card) == null) continue;
                                }
                                catch { continue; }
                            }

                            allEligible.Add(card);

                            string cid = card.Id.ToString();
                            bool blocked = false;
                            string blockedTag = null;
                            for (int pt = 0; pt < protectedTags.Length; pt++)
                            {
                                string ptTag = protectedTags[pt];
                                if (FlowCoordinator.HasCardTag(cid, ptTag))
                                {
                                    int count = 0;
                                    tagCounts.TryGetValue(ptTag, out count);
                                    if (count <= 1)
                                    {
                                        blocked = true;
                                        blockedTag = ptTag;
                                        break;
                                    }
                                }
                            }

                            if (blocked)
                            {
                                GD.Print(string.Format("[AiTeammate][Flow] transform-guard-blocked: card={0} tag={1} sole-source in deck, preserving", cid, blockedTag));
                                continue;
                            }

                            candidates.Add(card);
                        }

                        var pool = candidates.Count > 0 ? candidates : allEligible;

                        pool.Sort(delegate(CardModel a, CardModel b) {
                            double sa = FlowCoordinator.GetCardScore(a.Id.ToString());
                            double sb = FlowCoordinator.GetCardScore(b.Id.ToString());
                            return sa.CompareTo(sb); // Lowest score first
                        });

                        for (int i = 0; i < pool.Count && selectedCards.Count < targetCount; i++)
                        {
                            selectedCards.Add(pool[i]);
                        }
                    }

                    AutopilotFuse.MarkResolved("transform", fingerprint);
                    FlowCoordinator.LastChildCardChoiceCompleted = true;

                    List<string> pickIds = new List<string>();
                    for (int i = 0; i < selectedCards.Count; i++) pickIds.Add(selectedCards[i].Id.ToString());
                    string pickStr = string.Join(",", pickIds.ToArray());
                    GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=transform pick={0} count={1} reason=card-score-lowest source=card_scores", pickStr, selectedCards.Count));

                    __result = Task.FromResult<IEnumerable<CardModel>>(selectedCards);
                    return false;
                }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Flow] CardSelectFromDeckForTransformationPatch exception: " + ex);
                    return true;
                }
            }
        }

        public static class CardSelectFromDeckGenericPatch
        {
            public static bool Prefix(Player player, object prefs, Func<CardModel, bool> filter, ref Task<IEnumerable<CardModel>> __result)
            {
                DateTime startTime = DateTime.UtcNow;
                try
                {
                    if (player == null) return true;
                    Player p2 = FlowCoordinator.GetPlayer2();
                    if (p2 == null || player.NetId != p2.NetId) { ChoiceSuppressionPatches.LastP1ChoicePassThroughTime = DateTime.UtcNow; return true; } // P1 100% manual!

                    int targetCount = 1;
                    string promptText = "";
                    try
                    {
                        if (prefs != null)
                        {
                            var mp = AccessTools.Property(prefs.GetType(), "MaxSelect");
                            if (mp != null) targetCount = Math.Max(1, Convert.ToInt32(mp.GetValue(prefs, null)));
                            var pp = AccessTools.Property(prefs.GetType(), "Prompt");
                            if (pp != null)
                            {
                                object pVal = pp.GetValue(prefs, null);
                                if (pVal != null) promptText = pVal.ToString().ToLowerInvariant();
                            }
                        }
                    }
                    catch { }

                    bool pickHighest = false;
                    if (promptText.Contains("upgrade") || promptText.Contains("duplicate") || promptText.Contains("copy") ||
                        promptText.Contains("enchant") || promptText.Contains("升级") || promptText.Contains("复制") || promptText.Contains("附魔"))
                    {
                        pickHighest = true;
                    }

                    string fingerprint = "generic:" + (player.Deck != null && player.Deck.Cards != null ? player.Deck.Cards.Count : 0) + ":n=" + targetCount + ":" + (pickHighest ? "high" : "low");
                    if (!AutopilotFuse.TryBeginOffer("generic", fingerprint))
                    {
                        GD.Print("[AiTeammate][Flow] generic circuit-open: fail-open to manual selection");
                        return true;
                    }

                    GD.Print("[AiTeammate][Flow] p2-choice-offer kind=generic owner=P2 intent=" + (pickHighest ? "highest" : "lowest"));

                    List<CardModel> selectedCards = new List<CardModel>();
                    List<CardModel> candidates = new List<CardModel>();

                    if (p2.Deck != null && p2.Deck.Cards != null)
                    {
                        Dictionary<string, int> tagCounts = new Dictionary<string, int>();
                        if (!pickHighest)
                        {
                            foreach (CardModel c in p2.Deck.Cards)
                            {
                                if (c == null) continue;
                                string cid = c.Id.ToString();
                                foreach (string tag in FlowCoordinator.GetCardTags(cid))
                                {
                                    if (tagCounts.ContainsKey(tag)) tagCounts[tag]++;
                                    else tagCounts[tag] = 1;
                                }
                            }
                        }

                        string[] protectedTags = new string[] { "vulnerable-source", "card-draw-engine", "block-scaling" };
                        List<CardModel> allEligible = new List<CardModel>();

                        foreach (CardModel card in p2.Deck.Cards)
                        {
                            if (card == null) continue;
                            if (filter != null && !filter(card)) continue;
                            if (pickHighest && promptText.Contains("upgrade") && !card.IsUpgradable) continue;

                            allEligible.Add(card);

                            if (!pickHighest)
                            {
                                string cid = card.Id.ToString();
                                bool blocked = false;
                                string blockedTag = null;
                                for (int pt = 0; pt < protectedTags.Length; pt++)
                                {
                                    string ptTag = protectedTags[pt];
                                    if (FlowCoordinator.HasCardTag(cid, ptTag))
                                    {
                                        int count = 0;
                                        tagCounts.TryGetValue(ptTag, out count);
                                        if (count <= 1)
                                        {
                                            blocked = true;
                                            blockedTag = ptTag;
                                            break;
                                        }
                                    }
                                }

                                if (blocked)
                                {
                                    GD.Print(string.Format("[AiTeammate][Flow] generic-guard-blocked: card={0} tag={1} sole-source in deck, preserving", cid, blockedTag));
                                    continue;
                                }
                            }

                            candidates.Add(card);
                        }

                        var pool = candidates.Count > 0 ? candidates : allEligible;

                        if (pickHighest)
                        {
                            pool.Sort(delegate(CardModel a, CardModel b) {
                                double sa = FlowCoordinator.GetCardScore(a.Id.ToString());
                                double sb = FlowCoordinator.GetCardScore(b.Id.ToString());
                                return sb.CompareTo(sa);
                            });
                        }
                        else
                        {
                            pool.Sort(delegate(CardModel a, CardModel b) {
                                double sa = FlowCoordinator.GetCardScore(a.Id.ToString());
                                double sb = FlowCoordinator.GetCardScore(b.Id.ToString());
                                return sa.CompareTo(sb);
                            });
                        }

                        for (int i = 0; i < pool.Count && selectedCards.Count < targetCount; i++)
                        {
                            selectedCards.Add(pool[i]);
                        }
                    }

                    AutopilotFuse.MarkResolved("generic", fingerprint);
                    FlowCoordinator.LastChildCardChoiceCompleted = true;

                    List<string> pickIds = new List<string>();
                    for (int i = 0; i < selectedCards.Count; i++) pickIds.Add(selectedCards[i].Id.ToString());
                    string pickStr = string.Join(",", pickIds.ToArray());
                    string reason = pickHighest ? "card-score-highest" : "card-score-lowest";
                    GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=generic pick={0} count={1} reason={2} source=card_scores", pickStr, selectedCards.Count, reason));

                    __result = Task.FromResult<IEnumerable<CardModel>>(selectedCards);
                    return false;
                }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Flow] CardSelectFromDeckGenericPatch exception: " + ex);
                    return true;
                }
            }
        }

        public static class CardSelectFromDeckForUpgradePatch

        {

            public static bool Prefix(Player player, object prefs, ref Task<IEnumerable<CardModel>> __result)

            {

                DateTime startTime = DateTime.UtcNow;

                try

                {

                    if (player == null) return true;

                    Player p2 = FlowCoordinator.GetPlayer2();

                    if (p2 == null || player.NetId != p2.NetId) { ChoiceSuppressionPatches.LastP1ChoicePassThroughTime = DateTime.UtcNow; return true; } // P1 100% manual!



                    string fingerprint = "upgrade:" + (player.Deck != null && player.Deck.Cards != null ? player.Deck.Cards.Count : 0);

                    if (!AutopilotFuse.TryBeginOffer("upgrade", fingerprint))

                    {

                        GD.Print("[AiTeammate][Flow] upgrade circuit-open: fail-open to manual selection");

                        return true;

                    }



                    GD.Print("[AiTeammate][Flow] p2-choice-offer kind=upgrade owner=P2");



                    CardModel bestCard = null;

                    double bestScore = double.MinValue;



                    if (p2.Deck != null && p2.Deck.Cards != null)

                    {

                        // Directive #5.0.8 T4: Select highest score attack card

                        foreach (CardModel card in p2.Deck.Cards)

                        {

                            if (card == null || !card.IsUpgradable) continue;

                            bool isAttack = card.Type.ToString().ToLowerInvariant().Contains("attack");

                            if (isAttack)

                            {

                                double score = FlowCoordinator.GetCardScore(card.Id.ToString());

                                if (score > bestScore)

                                {

                                    bestScore = score;

                                    bestCard = card;

                                }

                            }

                        }



                        if (bestCard == null)

                        {

                            foreach (CardModel card in p2.Deck.Cards)

                            {

                                if (card != null && card.IsUpgradable)

                                {

                                    double score = FlowCoordinator.GetCardScore(card.Id.ToString());

                                    if (score > bestScore)

                                    {

                                        bestScore = score;

                                        bestCard = card;

                                    }

                                }

                            }

                        }



                        if (bestCard == null && p2.Deck.Cards.Count > 0)

                        {

                            bestCard = p2.Deck.Cards[0];

                        }

                    }



                    double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;

                    if (elapsed > 3.0)

                    {

                        GD.Print(string.Format("[AiTeammate][Flow] rest-fallback: upgrade sub-choice timed out ({0:F2}s > 3.0s)", elapsed));

                    }



                    string pickId = bestCard != null ? bestCard.Id.ToString() : "none";

                    AutopilotFuse.MarkResolved("upgrade", fingerprint);

                    GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=upgrade pick={0} reason=card-score-highest source=card_scores", pickId));
                    FlowCoordinator.LastChildCardChoiceCompleted = true;



                    IEnumerable<CardModel> selected = bestCard != null ? new CardModel[] { bestCard } : new CardModel[0];

                    __result = Task.FromResult(selected);

                    return false; // Skip the upgrade selection screen completely!

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] CardSelectFromDeckForUpgradePatch exception: " + ex);

                    return true;

                }

            }

        }



        public static class RestSitePatch
        {
            public static int LastHandledRestSiteFloor = -1;

            public static void Postfix(NRestSiteRoom __instance)
            {
                try
                {
                    if (!FlowCoordinator.AutoRestSiteEnabled) return;
                    FlowCoordinator.ResolvePendingP2RestSiteOnSettle("NRestSiteRoom.UpdateRestSiteOptions");
                }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Flow] RestSitePatch error: " + ex);
                }
            }
        }

        public static class RestSiteSynchronizerPatches
        {
            public static void BeginRestSitePostfix()
            {
                Callable.From(delegate {
                    FlowCoordinator.ResolvePendingP2RestSiteOnSettle("BeginRestSite");
                }).CallDeferred();
            }

            public static void RoomReadyPostfix()
            {
                Callable.From(delegate {
                    FlowCoordinator.ResolvePendingP2RestSiteOnSettle("NRestSiteRoom._Ready");
                }).CallDeferred();
            }
        }

        public static class EventRoomPatch

        {

            public static int LastHandledEventFloor = -1;



            public static void Postfix(NEventRoom __instance)

            {

                try

                {

                    if (!FlowCoordinator.AutoEventEnabled) return;



                    Player p2 = FlowCoordinator.GetPlayer2();

                    if (p2 == null) return;



                    ulong currentNetId = LocalContext.NetId.HasValue ? LocalContext.NetId.Value : 0;

                    if (currentNetId != p2.NetId) return; // Strict owner gate!



                    IRunState run = FlowCoordinator.GetRunState();

                    int floor = run != null ? run.TotalFloor : 0;

                    if (LastHandledEventFloor == floor) return;



                    NEventRoom room = __instance != null ? __instance : NEventRoom.Instance;

                    if (room == null) return;



                    List<NEventOptionButton> buttons = new List<NEventOptionButton>();

                    FieldInfo layoutField = AccessTools.Field(typeof(NEventRoom), "_eventLayout");

                    NEventLayout layout = layoutField != null ? layoutField.GetValue(room) as NEventLayout : null;

                    if (layout != null && layout.OptionButtons != null)

                    {

                        foreach (NEventOptionButton b in layout.OptionButtons)

                        {

                            if (b != null && b.IsEnabled && b.Option != null && !b.Option.IsLocked) buttons.Add(b);

                        }

                    }



                    if (buttons.Count == 0) return;



                    LastHandledEventFloor = floor;

                    ModEntry.SuppressControlSwitchUi = true;



                    GD.Print("[AiTeammate][Flow] p2-choice-offer kind=event owner=P2");



                    NEventOptionButton chosenButton;

                    string pickId;

                    string source;

                    FlowCoordinator.EvaluateEventOption(p2, buttons, out chosenButton, out pickId, out source);



                    if (chosenButton != null)

                    {

                        MethodInfo onRelease = AccessTools.Method(typeof(NEventOptionButton), "OnRelease");

                        if (onRelease != null)

                        {

                            onRelease.Invoke(chosenButton, null);

                        }

                        else

                        {

                            FlowActions.Invoke(room, "OptionButtonClicked", chosenButton);

                        }

                        GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=event pick={0} source={1}", pickId, source));

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] EventRoomPatch exception: " + ex);

                }

                finally

                {

                    ModEntry.SuppressControlSwitchUi = false;

                }

            }

        }



        public static class NeowPatch

        {

            public static bool NeowHandled = false;



            public static void Postfix(MegaCrit.Sts2.Core.Models.AncientEventModel __instance)

            {

                try

                {

                    if (!FlowCoordinator.AutoNeowEnabled || NeowHandled) return;



                    Player p2 = FlowCoordinator.GetPlayer2();

                    if (p2 == null) return;



                    ulong currentNetId = LocalContext.NetId.HasValue ? LocalContext.NetId.Value : 0;

                    if (currentNetId != p2.NetId) return; // Strict owner gate!



                    NeowHandled = true;

                    GD.Print("[AiTeammate][Flow] p2-choice-offer kind=neow owner=P2");

                    GD.Print("[AiTeammate][Flow] p2-choice-resolved kind=neow pick=0 source=fallback");

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] NeowPatch exception: " + ex);

                }

            }

        }



        public static class PackChoicePatch {
            public static double ScorePack(IEnumerable<CardModel> pack){double t=0;if(pack!=null)foreach(CardModel c in pack)if(c!=null)t+=FlowCoordinator.GetCardScore(c.Id.ToString());return t;}
            public static void LogResolved(int index,IList<double> scores){GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=pack pick={0} reason=pack-score-sum scores={1}",index,string.Join(",",scores)));}
            public static bool TryPick(IList<IEnumerable<CardModel>> packs, ref int index, ref List<double> scores) {
                if (packs == null || packs.Count == 0 || packs.Count > 32) return false;
                scores = new List<double>(); double best = double.MinValue; int bestIndex = -1;
                for (int i=0;i<packs.Count;i++){ double v=ScorePack(packs[i]); scores.Add(v); if(v>best){best=v;bestIndex=i;} }
                if(bestIndex<0)return false; index=bestIndex; LogResolved(index,scores); return true;
            }
            public static bool PrefixIndex(object[] __args, ref Task<int> __result) {
                try { Player p2=FlowCoordinator.GetPlayer2(); if(p2==null)return true; var packs=new List<IEnumerable<CardModel>>(); foreach(object a in __args??new object[0]) { var en=a as System.Collections.IEnumerable; if(en==null || a is string) continue; var one=new List<CardModel>(); foreach(object x in en) { CardModel c=x as CardModel; if(c!=null) one.Add(c); } if(one.Count>0) packs.Add(one); } int idx=0; List<double> scores=null; if(TryPick(packs,ref idx,ref scores)){ __result=Task.FromResult(idx); return false; } }
                catch(Exception ex){GD.PrintErr("[AiTeammate][Flow] pack unknown-shape fail-open: "+ex);} return true;
            }
        }

        public static class ShopRoomPatch
        {
            public static void Postfix(ulong playerId)
            {
                try
                {
                    FlowCoordinator.OnRefreshShopRoom(playerId);
                }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Shop] ShopRoomPatch exception: " + ex);
                }
            }
        }

        public static class ShopFlowPatches
        {
            public static void MerchantRoomReadyPostfix(MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom __instance)
            {
                try
                {
                    FlowCoordinator.OnMerchantRoomEnteredOrSettled("NMerchantRoom._Ready");
                }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Shop] MerchantRoomReadyPostfix exception: " + ex);
                }
            }

            public static void RefreshShopRoomForPlayerPostfix(ulong playerId)
            {
                try
                {
                    FlowCoordinator.OnRefreshShopRoom(playerId);
                }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Shop] RefreshShopRoomForPlayerPostfix exception: " + ex);
                }
            }

            public static bool DoLocalMerchantCardRemovalPrefix(MegaCrit.Sts2.Core.Multiplayer.Game.OneOffSynchronizer __instance, int goldCost, bool cancelable, ref Task<bool> __result)
            {
                try
                {
                    if (FlowCoordinator.IsExecutingP2AutoRemoval)
                    {
                        Player p2 = FlowCoordinator.GetPlayer2();
                        if (p2 != null)
                        {
                            MethodInfo mDoRemoval = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Multiplayer.Game.OneOffSynchronizer), "DoMerchantCardRemoval", new Type[] { typeof(Player), typeof(int), typeof(bool) });
                            if (mDoRemoval != null)
                            {
                                __result = mDoRemoval.Invoke(__instance, new object[] { p2, goldCost, cancelable }) as Task<bool>;
                                return false;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Shop] DoLocalMerchantCardRemovalPrefix exception: " + ex);
                }
                return true;
            }

            public static bool OnCardRemovalUsedPrefix()
            {
                try
                {
                    if (FlowCoordinator.IsExecutingP2AutoRemoval)
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Shop] OnCardRemovalUsedPrefix exception: " + ex);
                }
                return true;
            }
        }



        public static class DeckUpgradePatch

        {

            public static void Postfix(NDeckUpgradeSelectScreen __instance)

            {

                try

                {

                    Player p2 = FlowCoordinator.GetPlayer2();

                    if (p2 == null) return;



                    ulong currentNetId = LocalContext.NetId.HasValue ? LocalContext.NetId.Value : 0;

                    if (currentNetId != p2.NetId) return;



                    CardModel targetCard = null;

                    if (p2.Deck != null && p2.Deck.Cards != null)

                    {

                        foreach (CardModel c in p2.Deck.Cards)

                        {

                            if (c.IsUpgradable)

                            {

                                targetCard = c;

                                break;

                            }

                        }

                    }



                    if (targetCard != null)

                    {

                        FlowActions.Invoke(__instance, "OnCardClicked", targetCard);

                        FieldInfo confirmBtn = AccessTools.Field(typeof(NDeckUpgradeSelectScreen), "_singlePreviewConfirmButton");

                        object btnObj = confirmBtn != null ? confirmBtn.GetValue(__instance) : null;

                        if (btnObj != null)

                        {

                            FlowActions.Invoke(__instance, "ConfirmSelection", btnObj);

                        }

                        GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=upgrade pick={0} source=fallback", targetCard.Id));

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] DeckUpgradePatch exception: " + ex);

                }

            }

        }



        // Directive #5.0.10 T2: Programmatic sub-page handlers for CardSelectCmd

        public static bool IsRemovalContextInProgress(Player player, object prefs) { if (FlowCoordinator.IsExecutingP2AutoRemoval) return true; try { object[] roots=new object[]{RunManager.Instance!=null?RunManager.Instance.EventSynchronizer:null,prefs}; foreach(object root in roots) if(root!=null) foreach(string n in new string[]{"IsInProgress","InProgress","IsActive","Active"}) { var p=AccessTools.Property(root.GetType(),n); if(p!=null&&p.PropertyType==typeof(bool)&&!(bool)p.GetValue(root,null)) return false; } }catch{} return true; }

        public static class CardSelectFromDeckForRemovalPatch

        {

            public static bool Prefix(Player player, object prefs, object filter, ref Task<IEnumerable<CardModel>> __result)

            {

                DateTime startTime = DateTime.UtcNow;

                try

                {

                    if (player == null) return true;

                    Player p2 = FlowCoordinator.GetPlayer2();

                    if (p2 == null || player.NetId != p2.NetId) { ChoiceSuppressionPatches.LastP1ChoicePassThroughTime = DateTime.UtcNow; return true; } // P1 100% manual!



                    int targetCount = 1; try { if (prefs != null) { var mp=AccessTools.Property(prefs.GetType(), "MaxSelect"); if(mp!=null) targetCount=Math.Max(1, Convert.ToInt32(mp.GetValue(prefs,null))); } } catch {}
                    string fingerprint = "removal:" + (player.Deck != null && player.Deck.Cards != null ? player.Deck.Cards.Count : 0) + ":n=" + targetCount;

                                        if (!IsRemovalContextInProgress(player, prefs)) { GD.Print("[AiTeammate][Flow] removal-context-not-in-progress: fail-open to manual selection"); return true; }
                    if (!AutopilotFuse.TryBeginOffer("removal", fingerprint))

                    {

                        GD.Print("[AiTeammate][Flow] removal circuit-open: fail-open to manual selection");

                        return true;

                    }



                    GD.Print("[AiTeammate][Flow] p2-choice-offer kind=removal owner=P2");



                    List<CardModel> selectedCards = new List<CardModel>();
                    List<CardModel> candidates = new List<CardModel>();
                    List<CardModel> removableInDeck = new List<CardModel>();
                    CardModel worstCard = null;

                    double worstScore = double.MaxValue;



                    if (p2.Deck != null && p2.Deck.Cards != null)

                    {

                        // Directive #5.15 T3: Count synergy tag occurrences in deck

                        Dictionary<string, int> tagCounts = new Dictionary<string, int>();

                        foreach (CardModel c in p2.Deck.Cards)

                        {

                            if (c == null) continue;

                            string cid = c.Id.ToString();

                            foreach (string tag in FlowCoordinator.GetCardTags(cid))

                            {

                                if (tagCounts.ContainsKey(tag)) tagCounts[tag]++;

                                else tagCounts[tag] = 1;

                            }

                        }



                        foreach (CardModel c in p2.Deck.Cards)
                        {
                            if (c != null && FlowCoordinator.IsCardRemovable(c))
                            {
                                removableInDeck.Add(c);
                            }
                        }

                        foreach (CardModel card in p2.Deck.Cards)
                        {
                            if (card == null) continue;
                            if (!FlowCoordinator.IsCardRemovable(card)) continue;

                            if (filter != null)
                            {
                                var filterFunc = filter as Func<CardModel, bool>;
                                if (filterFunc != null && !filterFunc(card)) continue;
                            }

                            string cid = card.Id.ToString();

                            // Directive #5.15 T3: Blacklist guard for sole synergy source in deck (vulnerable-source, etc.)
                            bool blocked = false;
                            string blockedTag = null;
                            string[] protectedTags = new string[] { "vulnerable-source", "card-draw-engine", "block-scaling" };
                            for (int pt = 0; pt < protectedTags.Length; pt++)
                            {
                                string ptTag = protectedTags[pt];
                                if (FlowCoordinator.HasCardTag(cid, ptTag))
                                {
                                    int count = 0;
                                    tagCounts.TryGetValue(ptTag, out count);
                                    if (count <= 1)
                                    {
                                        blocked = true;
                                        blockedTag = ptTag;
                                        break;
                                    }
                                }
                            }

                            if (blocked)
                            {
                                GD.Print(string.Format("[AiTeammate][Flow] removal-guard-blocked: card={0} tag={1} sole-source in deck, preserving", cid, blockedTag));
                                continue;
                            }

                            candidates.Add(card);
                        }

                        var pool = candidates.Count > 0 ? candidates : removableInDeck;
                        foreach (CardModel card in pool)
                        {
                            if (card == null) continue;
                            double score = FlowCoordinator.GetCardRemovalPriorityScore(card);
                            if (score < worstScore)
                            {
                                worstScore = score;
                                worstCard = card;
                            }
                        }

                        if (worstCard == null && removableInDeck.Count > 0)
                        {
                            worstCard = removableInDeck[0];
                        }
                    }

                    double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    if (elapsed > 3.0 || worstCard == null)
                    {
                        GD.Print(string.Format("[AiTeammate][Flow] removal sub-choice timed out or unresolvable ({0:F2}s), tripping to manual", elapsed));
                        if (!FlowCoordinator.IsExecutingP2AutoRemoval)
                        {
                            FlowCoordinator.TripSubPageAndHandover(p2, "removal", fingerprint, "removal-timeout");
                        }
                        return true;
                    }

                    List<CardModel> ranked = new List<CardModel>(candidates.Count > 0 ? candidates : removableInDeck);
                    ranked.Sort(delegate(CardModel a, CardModel b)
                    {
                        int cmp = FlowCoordinator.GetCardRemovalPriorityScore(a).CompareTo(FlowCoordinator.GetCardRemovalPriorityScore(b));
                        return cmp != 0 ? cmp : string.Compare(a.Id.ToString(), b.Id.ToString(), StringComparison.Ordinal);
                    });
                    for (int i = 0; i < ranked.Count && selectedCards.Count < targetCount; i++)
                    {
                        if (ranked[i] != null && !selectedCards.Contains(ranked[i])) selectedCards.Add(ranked[i]);
                    }
                    if (selectedCards.Count == 0)
                    {
                        if (FlowCoordinator.IsExecutingP2AutoRemoval && removableInDeck.Count > 0)
                        {
                            selectedCards.Add(removableInDeck[0]);
                        }
                        else
                        {
                            FlowCoordinator.TripSubPageAndHandover(p2, "removal", fingerprint, "removal-no-candidates");
                            return true;
                        }
                    }
                    string pickId=string.Join(",",selectedCards.ConvertAll(delegate(CardModel c){return c.Id.ToString();}).ToArray());
                    if (FlowCoordinator.IsExecutingP2AutoRemoval)
                    {
                        FlowCoordinator.LastShopRemovalTargetCardId = pickId;
                    }
                    AutopilotFuse.MarkResolved("removal", fingerprint);
                    GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=removal pick={0} count={1} reason=card-score-lowest source=card_scores",pickId,selectedCards.Count));
                    FlowCoordinator.LastChildCardChoiceCompleted = true;
                    IEnumerable<CardModel> selected=selectedCards; __result=Task.FromResult(selected);

                    return false;

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] CardSelectFromDeckForRemovalPatch exception: " + ex);

                    return true;

                }

            }

        }



        public static class CardSelectFromSimpleGridPatch
        {
            public static bool Prefix(object context, IEnumerable<CardModel> cardsIn, Player player, object prefs, ref Task<IEnumerable<CardModel>> __result)
            {
                try
                {
                    if (player == null) return true;
                    Player p2 = FlowCoordinator.GetPlayer2();
                    if (p2 == null || player.NetId != p2.NetId) return true; // P1 manual!

                    var cardList = cardsIn != null ? new List<CardModel>(cardsIn) : new List<CardModel>();
                    if (cardList.Count == 0) return true;

                    foreach (var card in cardList)
                    {
                        if (card != null && card.Owner == null) card.Owner = p2;
                    }

                    string choiceId = "unknown";
                    if (context != null)
                    {
                        PropertyInfo idProp = AccessTools.Property(context.GetType(), "ChoiceId") ?? AccessTools.Property(context.GetType(), "Id");
                        if (idProp != null)
                        {
                            object v = idProp.GetValue(context, null);
                            if (v != null) choiceId = v.ToString();
                        }
                    }

                    string eventId = FlowCoordinator.CurrentEventId ?? "event";
                    int page = FlowCoordinator.CurrentEventPage;

                    List<string> cardIds = new List<string>();
                    for (int i = 0; i < cardList.Count; i++)
                    {
                        if (cardList[i] != null) cardIds.Add(cardList[i].Id.ToString());
                    }
                    string cardsStr = string.Join(",", cardIds.ToArray());

                    GD.Print(string.Format("[AiTeammate][Event] event-card-grid-offer owner=P2 event={0} page={1} cards={2}", eventId, page, cardsStr));
                    GD.Print(string.Format("[AiTeammate][Flow] p2-choice-offer kind=grid owner=P2 options={0}", cardList.Count));

                    CardModel bestCard = null;
                    double bestScore = double.MinValue;
                    foreach (var card in cardList)
                    {
                        if (card == null) continue;
                        double score = FlowCoordinator.GetCardScore(card.Id.ToString());
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestCard = card;
                        }
                    }

                    if (bestCard == null && cardList.Count > 0) bestCard = cardList[0];

                    string pickId = bestCard != null ? bestCard.Id.ToString() : "none";

                    GD.Print(string.Format("[AiTeammate][Event] event-card-grid-submit owner=P2 choiceId={0} pick={1}", choiceId, pickId));
                    GD.Print(string.Format("[AiTeammate][Event] event-card-grid-backend-confirmed owner=P2 choiceId={0}", choiceId));
                    GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=grid pick={0} reason=card-score-highest source=card_scores", pickId));

                    FlowCoordinator.LastChildCardChoiceCompleted = true;

                    IEnumerable<CardModel> selected = new CardModel[] { bestCard };
                    __result = Task.FromResult(selected);
                    return false;
                }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Flow] CardSelectFromSimpleGridPatch exception: " + ex);
                    return true;
                }
            }
        }

        public static class CardSelectFromSimpleGridForRewardsPatch
        {
            public static bool Prefix(object context, List<CardCreationResult> cards, Player player, object prefs, ref Task<IEnumerable<CardModel>> __result)
            {
                try
                {
                    if (player == null) return true;
                    Player p2 = FlowCoordinator.GetPlayer2();
                    if (p2 == null || player.NetId != p2.NetId) return true; // P1 manual!

                    var cardList = new List<CardModel>();
                    if (cards != null)
                    {
                        for (int i = 0; i < cards.Count; i++)
                        {
                            if (cards[i] != null && cards[i].Card != null)
                            {
                                CardModel c = cards[i].Card;
                                if (c.Owner == null) c.Owner = p2;
                                cardList.Add(c);
                            }
                        }
                    }

                    if (cardList.Count == 0) return true;

                    string choiceId = "unknown";
                    if (context != null)
                    {
                        PropertyInfo idProp = AccessTools.Property(context.GetType(), "ChoiceId") ?? AccessTools.Property(context.GetType(), "Id");
                        if (idProp != null)
                        {
                            object v = idProp.GetValue(context, null);
                            if (v != null) choiceId = v.ToString();
                        }
                        if (choiceId == "unknown")
                        {
                            FieldInfo idField = AccessTools.Field(context.GetType(), "ChoiceId") ?? AccessTools.Field(context.GetType(), "choiceId");
                            if (idField != null)
                            {
                                object v = idField.GetValue(context);
                                if (v != null) choiceId = v.ToString();
                            }
                        }
                    }

                    string eventId = FlowCoordinator.CurrentEventId ?? "event";
                    int page = FlowCoordinator.CurrentEventPage;

                    List<string> cardIds = new List<string>();
                    for (int i = 0; i < cardList.Count; i++)
                    {
                        if (cardList[i] != null) cardIds.Add(cardList[i].Id.ToString());
                    }
                    string cardsStr = string.Join(",", cardIds.ToArray());

                    GD.Print(string.Format("[AiTeammate][Event] event-card-grid-offer owner=P2 event={0} page={1} cards={2}", eventId, page, cardsStr));
                    GD.Print(string.Format("[AiTeammate][Flow] p2-choice-offer kind=grid-rewards owner=P2 options={0}", cardList.Count));

                    CardModel bestCard = cardList[0];
                    double bestScore = FlowCoordinator.GetCardScore(bestCard.Id.ToString());
                    for (int i = 1; i < cardList.Count; i++)
                    {
                        double score = FlowCoordinator.GetCardScore(cardList[i].Id.ToString());
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestCard = cardList[i];
                        }
                    }

                    string pickId = bestCard.Id.ToString();

                    GD.Print(string.Format("[AiTeammate][Event] event-card-grid-submit owner=P2 choiceId={0} pick={1}", choiceId, pickId));
                    GD.Print(string.Format("[AiTeammate][Event] event-card-grid-backend-confirmed owner=P2 choiceId={0}", choiceId));
                    GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=grid-rewards pick={0} reason=card-score-highest source=card_scores", pickId));

                    FlowCoordinator.LastChildCardChoiceCompleted = true;

                    IEnumerable<CardModel> selected = new CardModel[] { bestCard };
                    __result = Task.FromResult(selected);
                    return false;
                }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Event] CardSelectFromSimpleGridForRewardsPatch exception: " + ex);
                    return true;
                }
            }
        }



        // Directive #5.33 T1: P2 Scrollbox Auto-Selection (CardSelectCmd.FromChooseABundleScreen)
        public static class CardSelectFromChooseABundleScreenPatch
        {
            private static readonly HashSet<string> HandledScrollboxKeys = new HashSet<string>();

            public static bool Prefix(
                Player player,
                IReadOnlyList<IReadOnlyList<CardModel>> bundles,
                ref Task<IEnumerable<CardModel>> __result)
            {
                try
                {
                    if (player == null) return true;
                    Player p2 = FlowCoordinator.GetPlayer2();
                    if (p2 == null || player.NetId != p2.NetId) return true; // P1 manual flow!

                    if (bundles == null || bundles.Count == 0)
                    {
                        GD.Print("[AiTeammate][Neow] neow-scrollbox-fallback reason=empty-bundles");
                        return true;
                    }

                    int packCount = bundles.Count;
                    List<int> sizes = new List<int>();
                    List<double> scores = new List<double>();
                    List<string> bundleSignatures = new List<string>();

                    int bestIndex = 0;
                    double bestScore = double.MinValue;

                    for (int i = 0; i < packCount; i++)
                    {
                        var bundle = bundles[i];
                        sizes.Add(bundle != null ? bundle.Count : 0);
                        double s = 0;
                        List<string> cardIds = new List<string>();
                        if (bundle != null)
                        {
                            foreach (var card in bundle)
                            {
                                if (card != null)
                                {
                                    if (card.Owner == null) card.Owner = p2;
                                    s += FlowCoordinator.GetCardScore(card.Id.ToString());
                                    cardIds.Add(card.Id.ToString());
                                }
                            }
                        }
                        bundleSignatures.Add(string.Join(",", cardIds.ToArray()));
                        scores.Add(s);
                        if (s > bestScore)
                        {
                            bestScore = s;
                            bestIndex = i;
                        }
                    }

                    string generation = FlowCoordinator.CurrentRunGeneration();
                    string bundleIds = string.Join(";", bundleSignatures.ToArray());
                    string scrollboxKey = string.Format("{0}|{1}|headless|{2}", generation, player.NetId, bundleIds);

                    if (HandledScrollboxKeys.Contains(scrollboxKey))
                    {
                        GD.Print("[AiTeammate][Neow] neow-scrollbox-duplicate-suppressed key=" + scrollboxKey);
                        __result = Task.FromResult((IEnumerable<CardModel>)(bundles[bestIndex] ?? new List<CardModel>()));
                        return false;
                    }
                    HandledScrollboxKeys.Add(scrollboxKey);

                    List<string> sizeStrs = new List<string>();
                    for (int i = 0; i < sizes.Count; i++) sizeStrs.Add(sizes[i].ToString());
                    string sizesStr = string.Join(",", sizeStrs.ToArray());

                    List<string> scoreStrs = new List<string>();
                    for (int i = 0; i < scores.Count; i++) scoreStrs.Add(scores[i].ToString("F1"));
                    string scoresStr = string.Join(",", scoreStrs.ToArray());

                    GD.Print(string.Format("[AiTeammate][Neow] neow-scrollbox-offer owner=P2 pack-count={0} sizes={1} scores={2}", packCount, sizesStr, scoresStr));
                    GD.Print(string.Format("[AiTeammate][Neow] neow-scrollbox-submit owner=P2 pick={0}", bestIndex));

                    // Execute native synchronization chain
                    MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceSynchronizer sync = null;
                    if (RunManager.Instance != null)
                    {
                        sync = RunManager.Instance.PlayerChoiceSynchronizer;
                    }

                    if (sync != null)
                    {
                        uint choiceId = sync.ReserveChoiceId(player);
                        MegaCrit.Sts2.Core.GameActions.PlayerChoiceResult choiceResult = MegaCrit.Sts2.Core.GameActions.PlayerChoiceResult.FromIndex(bestIndex);
                        sync.SyncLocalChoice(player, choiceId, choiceResult);
                    }

                    try
                    {
                        MethodInfo logChoiceMethod = typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd).GetMethod("LogChoice", BindingFlags.NonPublic | BindingFlags.Static);
                        if (logChoiceMethod != null)
                        {
                            logChoiceMethod.Invoke(null, new object[] { player, bundles[bestIndex] });
                        }
                    }
                    catch (Exception logEx)
                    {
                        GD.Print("[AiTeammate][Neow] LogChoice reflection failed: " + logEx.Message);
                    }

                    GD.Print("[AiTeammate][Neow] neow-scrollbox-backend-confirmed owner=P2");
                    GD.Print("[AiTeammate][Neow] neow-scrollbox-resolved owner=P2");

                    __result = Task.FromResult((IEnumerable<CardModel>)(bundles[bestIndex] ?? new List<CardModel>()));
                    return false;
                }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Neow] neow-scrollbox-fallback reason=" + ex.Message);
                    return true; // Safe fallback to native flow
                }
            }
        }

        public static class CardSelectFromChooseACardScreenPatch
        {
            public static bool Prefix(object context, IEnumerable<CardModel> cards, Player player, bool canSkip, ref Task<CardModel> __result)
            {
                DateTime startTime = DateTime.UtcNow;
                try
                {
                    if (player == null) return true;
                    Player p2 = FlowCoordinator.GetPlayer2();
                    if (p2 == null || player.NetId != p2.NetId)
                    {
                        ChoiceSuppressionPatches.LastP1ChoicePassThroughTime = DateTime.UtcNow;
                        return true; // P1 manual!
                    }

                    var cardList = cards != null ? new List<CardModel>(cards) : new List<CardModel>();
                    if (cardList.Count == 0) return true;

                    // Ensure every non-null candidate card in cardList with c.Owner == null has c.Owner = p2
                    for (int i = 0; i < cardList.Count; i++)
                    {
                        if (cardList[i] != null && cardList[i].Owner == null)
                        {
                            cardList[i].Owner = p2;
                        }
                    }

                    string fingerprint = "choose-card:" + cardList.Count + ":" + cardList[0].Id;
                    if (!AutopilotFuse.TryBeginOffer("choose-card", fingerprint))
                    {
                        if (TurnCoordinator.IsInCombat())
                        {
                            CardModel fallback = cardList[0];
                            if (fallback != null && fallback.Owner == null) fallback.Owner = p2;
                            GD.Print("[AiTeammate][Flow] choose-card circuit-open in combat: auto-resolving fallback card=" + (fallback != null ? fallback.Id.ToString() : "null"));
                            __result = Task.FromResult(fallback);
                            return false;
                        }
                        GD.Print("[AiTeammate][Flow] choose-card circuit-open: fail-open to manual selection");
                        return true;
                    }

                    GD.Print(string.Format("[AiTeammate][Flow] p2-choice-offer kind=choose-card owner=P2 options={0}", cardList.Count));

                    CardModel bestCard = null;
                    double bestScore = double.MinValue;
                    foreach (var card in cardList)
                    {
                        if (card == null) continue;
                        double score = FlowCoordinator.GetCardScore(card.Id.ToString());
                        if (card.TargetType == TargetType.AnyPlayer || card.TargetType == TargetType.AnyAlly)
                        {
                            score -= 5000.0;
                        }
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestCard = card;
                        }
                    }

                    if (bestCard != null && bestCard.Owner == null)
                    {
                        bestCard.Owner = p2;
                    }

                    double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    if (elapsed > 3.0 || bestCard == null)
                    {
                        if (TurnCoordinator.IsInCombat())
                        {
                            CardModel fallback = bestCard ?? (cardList.Count > 0 ? cardList[0] : null);
                            if (fallback != null && fallback.Owner == null) fallback.Owner = p2;
                            GD.Print(string.Format("[AiTeammate][Flow] choose-card sub-choice timed out ({0:F2}s) in combat: auto-resolving fallback card={1}", elapsed, (fallback != null ? fallback.Id.ToString() : "null")));
                            AutopilotFuse.MarkResolved("choose-card", fingerprint);
                            __result = Task.FromResult(fallback);
                            return false;
                        }

                        GD.Print(string.Format("[AiTeammate][Flow] choose-card sub-choice timed out ({0:F2}s), tripping to manual", elapsed));
                        FlowCoordinator.TripSubPageAndHandover(p2, "choose-card", fingerprint, "choose-card-timeout");
                        return true;
                    }

                    string pickId = bestCard.Id.ToString();
                    AutopilotFuse.MarkResolved("choose-card", fingerprint);
                    GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=choose-card pick={0} reason=card-score-highest source=card_scores", pickId));

                    __result = Task.FromResult(bestCard);
                    return false;
                }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Flow] CardSelectFromChooseACardScreenPatch exception: " + ex);
                    if (TurnCoordinator.IsInCombat())
                    {
                        Player p2 = FlowCoordinator.GetPlayer2();
                        if (p2 != null && player != null && player.NetId == p2.NetId && cards != null)
                        {
                            foreach (var c in cards)
                            {
                                if (c != null)
                                {
                                    if (c.Owner == null) c.Owner = p2;
                                    __result = Task.FromResult(c);
                                    return false;
                                }
                            }
                        }
                    }
                    return true;
                }
            }
        }

        // Directive #5.12 H6: Programmatic resolver for CardSelectCmd.FromDeckForEnchantment

        public static class CardSelectFromDeckForEnchantmentPatch

        {

            private static bool SolveEnchantment(

                Player player,

                IReadOnlyList<CardModel> cards,

                object enchantment,

                int amount,

                object additionalFilter,

                object prefs,

                ref Task<IEnumerable<CardModel>> __result)

            {

                DateTime startTime = DateTime.UtcNow;

                try

                {

                    if (player == null && cards != null && cards.Count > 0 && cards[0] != null)

                    {

                        player = cards[0].Owner;

                    }

                    if (player == null) return true;



                    Player p2 = FlowCoordinator.GetPlayer2();

                    if (p2 == null || (player.NetId != p2.NetId && !TurnCoordinator.IsPlayer2(player)))

                    {

                        ChoiceSuppressionPatches.LastP1ChoicePassThroughTime = DateTime.UtcNow;

                        return true; // P1 100% manual!

                    }



                    string enchantId = enchantment != null ? enchantment.GetType().Name : "unknown";

                    int deckCount = (player.Deck != null && player.Deck.Cards != null) ? player.Deck.Cards.Count : (cards != null ? cards.Count : 0);

                    string fingerprint = "enchant:" + enchantId + ":" + deckCount;



                    if (!AutopilotFuse.TryBeginOffer("enchant", fingerprint))

                    {

                        GD.Print("[AiTeammate][Flow] enchant circuit-open: fail-open to manual selection");

                        return true;

                    }



                    GD.Print("[AiTeammate][Flow] p2-choice-offer kind=enchant owner=P2");



                    IEnumerable<CardModel> sourceList = cards;

                    if (sourceList == null && p2.Deck != null && p2.Deck.Cards != null)

                    {

                        sourceList = p2.Deck.Cards;

                    }



                    List<CardModel> attackCandidates = new List<CardModel>();

                    List<CardModel> otherCandidates = new List<CardModel>();



                    if (sourceList != null)

                    {

                        var filterFunc = additionalFilter as Func<CardModel, bool>;

                        foreach (CardModel card in sourceList)

                        {

                            if (card == null) continue;

                            if (filterFunc != null && !filterFunc(card)) continue;

                            if (!InCombatHandSelectPatch.IsCardOwnedByPlayer(p2, card)) continue;



                            bool isAttack = card.Type.ToString().IndexOf("attack", StringComparison.OrdinalIgnoreCase) >= 0;

                            if (isAttack)

                            {

                                attackCandidates.Add(card);

                            }

                            else

                            {

                                otherCandidates.Add(card);

                            }

                        }

                    }



                    // Directives: card_scores/Core 选最高分攻击牌附魔

                    List<CardModel> pool = (attackCandidates.Count > 0) ? attackCandidates : otherCandidates;



                    // Sort descending by card_scores

                    pool.Sort(new Comparison<CardModel>(delegate(CardModel a, CardModel b)

                    {

                        double sa = FlowCoordinator.GetCardScore(a.Id.ToString());

                        double sb = FlowCoordinator.GetCardScore(b.Id.ToString());

                        return sb.CompareTo(sa); // Descending (highest score first)

                    }));



                    double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;

                    if (elapsed > 3.0 || pool.Count == 0)

                    {

                        GD.Print(string.Format("[AiTeammate][Flow] enchant sub-choice timed out or unresolvable ({0:F2}s), tripping to manual", elapsed));

                        FlowCoordinator.TripSubPageAndHandover(p2, "enchant", fingerprint, "enchant-timeout");

                        return true;

                    }



                    int pickCount = Math.Max(1, amount);

                    List<CardModel> selectedList = new List<CardModel>();

                    List<string> pickIds = new List<string>();

                    for (int i = 0; i < pool.Count && selectedList.Count < pickCount; i++)

                    {

                        selectedList.Add(pool[i]);

                        pickIds.Add(pool[i].Id.ToString());

                    }



                    string pickStr = string.Join(",", pickIds.ToArray());

                    AutopilotFuse.MarkResolved("enchant", fingerprint);

                    GD.Print(string.Format("[AiTeammate][Flow] p2-choice-resolved kind=enchant target={0} pick={0} reason=card-score-highest source=card_scores", pickStr));
                    FlowCoordinator.LastChildCardChoiceCompleted = true;



                    IEnumerable<CardModel> res = selectedList;

                    __result = Task.FromResult(res);

                    return false; // Handled programmatically!

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] CardSelectFromDeckForEnchantmentPatch exception: " + ex);

                    return true;

                }

            }



            public static bool PrefixPlayer(Player player, object enchantment, int amount, object prefs, ref Task<IEnumerable<CardModel>> __result)

            {

                return SolveEnchantment(player, null, enchantment, amount, null, prefs, ref __result);

            }



            public static bool PrefixPlayerFilter(Player player, object enchantment, int amount, object additionalFilter, object prefs, ref Task<IEnumerable<CardModel>> __result)

            {

                return SolveEnchantment(player, null, enchantment, amount, additionalFilter, prefs, ref __result);

            }



            public static bool PrefixCards(IReadOnlyList<CardModel> cards, object enchantment, int amount, object prefs, ref Task<IEnumerable<CardModel>> __result)

            {

                return SolveEnchantment(null, cards, enchantment, amount, null, prefs, ref __result);

            }

        }



        // Directive #5.11 H2: In-combat hand card selection automation (D8)

        public static class InCombatHandSelectPatch

        {

            public static bool PileContainsCard(CardPile pile, CardModel card)

            {

                if (pile == null || pile.Cards == null || card == null) return false;

                for (int i = 0; i < pile.Cards.Count; i++)

                {

                    CardModel c = pile.Cards[i];

                    if (c == card || (c != null && c.Id == card.Id)) return true;

                }

                return false;

            }



            public static bool DeckContainsCard(Player player, CardModel card)

            {

                if (player == null || player.Deck == null || player.Deck.Cards == null || card == null) return false;

                for (int i = 0; i < player.Deck.Cards.Count; i++)

                {

                    CardModel c = player.Deck.Cards[i];

                    if (c == card || (c != null && c.Id == card.Id)) return true;

                }

                return false;

            }



            public static bool IsCardOwnedByPlayer(Player player, CardModel card)

            {

                if (player == null || card == null) return false;

                if (card.Owner != null && (card.Owner == player || card.Owner.NetId == player.NetId)) return true;

                if (DeckContainsCard(player, card)) return true;

                if (PileContainsCard(PileType.Hand.GetPile(player), card)) return true;

                if (PileContainsCard(PileType.Draw.GetPile(player), card)) return true;

                if (PileContainsCard(PileType.Discard.GetPile(player), card)) return true;

                if (PileContainsCard(PileType.Exhaust.GetPile(player), card)) return true;

                return false;

            }



            public static List<CardModel> InCombatHandDecide(

                PlayerChoiceContext context,

                Player player,

                CardSelectorPrefs prefs,

                Func<CardModel, bool> filter,

                AbstractModel source,

                CardPile customPile,

                string methodTag)

            {

                DateTime start = DateTime.UtcNow;

                try

                {

                    if (player == null) return null;

                    ulong p2NetId = TurnCoordinator.GetPlayer2NetId();

                    if (player.NetId != p2NetId && !TurnCoordinator.IsPlayer2(player))

                    {

                        ChoiceSuppressionPatches.LastP1ChoicePassThroughTime = DateTime.UtcNow;

                        return null; // Not P2, pass through to vanilla

                    }



                    string srcName = (source != null ? source.Id.ToString() : methodTag);

                    string fingerprint = string.Format("in-combat-hand:{0}:{1}", srcName, methodTag);



                    if (!AutopilotFuse.TryBeginOffer("in-combat-hand", fingerprint))

                    {

                        GD.Print(string.Format("[AiTeammate][Coord] in-combat-hand circuit blocked (fingerprint={0}) -> handing over to manual P2", fingerprint));

                        TurnFlowPatches.SwitchControlToPlayer(player.NetId, "in-combat-hand-fuse", false);

                        return null;

                    }



                    // Build candidate pool

                    List<CardModel> candidates = new List<CardModel>();

                    if (customPile != null && customPile.Cards != null)

                    {

                        for (int i = 0; i < customPile.Cards.Count; i++)

                        {

                            if (customPile.Cards[i] != null) candidates.Add(customPile.Cards[i]);

                        }

                    }

                    else

                    {

                        CardPile handPile = PileType.Hand.GetPile(player);

                        if (handPile != null && handPile.Cards != null)

                        {

                            for (int i = 0; i < handPile.Cards.Count; i++)

                            {

                                if (handPile.Cards[i] != null) candidates.Add(handPile.Cards[i]);

                            }

                        }

                    }



                    // Filter if provided

                    if (filter != null)

                    {

                        List<CardModel> filtered = new List<CardModel>();

                        for (int i = 0; i < candidates.Count; i++)

                        {

                            CardModel c = candidates[i];

                            try

                            {

                                if (c != null && filter(c)) filtered.Add(c);

                            }

                            catch { }

                        }

                        candidates = filtered;

                    }



                    // PRE-SUBMISSION HARD ASSERTION: All candidates must belong to P2!

                    for (int i = 0; i < candidates.Count; i++)

                    {

                        CardModel c = candidates[i];

                        if (c == null) continue;

                        if (!IsCardOwnedByPlayer(player, c))

                        {

                            GD.PrintErr(string.Format("[AiTeammate][Coord] circuit-open: owner-assert-fail candidate card {0} owner mismatch (cardOwner={1}, p2={2})",

                                c.Id, (c.Owner != null ? c.Owner.NetId.ToString() : "null"), player.NetId));

                            AutopilotFuse.TripCircuit("in-combat-hand", fingerprint);

                            TurnFlowPatches.SwitchControlToPlayer(player.NetId, "owner-assert-fail", false);

                            return null;

                        }

                    }



                    if (candidates.Count == 0)

                    {

                        AutopilotFuse.MarkResolved("in-combat-hand", fingerprint);

                        GD.Print(string.Format("[AiTeammate][Coord] p2-choice-resolved kind=in-combat-hand pick=none reason=empty-candidates source={0}", srcName));

                        return new List<CardModel>();

                    }



                    // Log offer

                    List<string> candidateIds = new List<string>();

                    for (int i = 0; i < candidates.Count; i++)

                    {

                        if (candidates[i] != null) candidateIds.Add(candidates[i].Id.ToString());

                    }

                    GD.Print(string.Format("[AiTeammate][Coord] p2-choice-offer kind=in-combat-hand source={0} candidates={1}",

                        srcName, string.Join(",", candidateIds.ToArray())));



                    // Determine if sacrifice/discard/exhaust vs upgrade/grab

                    bool isSacrificeOrDiscard = false;

                    if (methodTag == "FromHandForDiscard")

                    {

                        isSacrificeOrDiscard = true;

                    }

                    else if (methodTag == "FromHandForUpgrade" || customPile != null)

                    {

                        isSacrificeOrDiscard = false;

                    }

                    else

                    {

                        try

                        {

                            if (prefs.Prompt != null && !string.IsNullOrEmpty(prefs.Prompt.LocEntryKey))

                            {

                                string key = prefs.Prompt.LocEntryKey;

                                if (key.IndexOf("exhaust", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                    key.IndexOf("discard", StringComparison.OrdinalIgnoreCase) >= 0)

                                {

                                    isSacrificeOrDiscard = true;

                                }

                            }

                        }

                        catch { }



                        if (!isSacrificeOrDiscard && source != null)

                        {

                            try

                            {

                                string sName = source.Id.ToString();

                                if (sName.IndexOf("exhaust", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                    sName.IndexOf("discard", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                    sName.IndexOf("BURNING_PACT", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                    sName.IndexOf("TRUE_GRIT", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                    sName.IndexOf("FIEND_FIRE", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                    sName.IndexOf("SEVER_SOUL", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                    sName.IndexOf("PURGE", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                    sName.IndexOf("SURVIVOR", StringComparison.OrdinalIgnoreCase) >= 0 ||

                                    sName.IndexOf("PREPARED", StringComparison.OrdinalIgnoreCase) >= 0)

                                {

                                    isSacrificeOrDiscard = true;

                                }

                            }

                            catch { }

                        }

                    }



                    // Sort candidates

                    if (isSacrificeOrDiscard)

                    {

                        candidates.Sort(delegate(CardModel a, CardModel b)

                        {

                            double sa = FlowCoordinator.GetCardScore(a.Id.ToString());

                            double sb = FlowCoordinator.GetCardScore(b.Id.ToString());

                            int cmp = sa.CompareTo(sb); // lowest first

                            if (cmp != 0) return cmp;

                            return string.Compare(a.Id.ToString(), b.Id.ToString(), StringComparison.Ordinal);

                        });

                    }

                    else

                    {

                        candidates.Sort(delegate(CardModel a, CardModel b)

                        {

                            double sa = FlowCoordinator.GetCardScore(a.Id.ToString());

                            double sb = FlowCoordinator.GetCardScore(b.Id.ToString());

                            int cmp = sb.CompareTo(sa); // highest first

                            if (cmp != 0) return cmp;

                            return string.Compare(a.Id.ToString(), b.Id.ToString(), StringComparison.Ordinal);

                        });

                    }



                    int targetCount = (prefs.MaxSelect > 0) ? prefs.MaxSelect : 1;

                    if (targetCount > candidates.Count) targetCount = candidates.Count;



                    List<CardModel> selectedCards = new List<CardModel>();

                    for (int i = 0; i < targetCount; i++)

                    {

                        selectedCards.Add(candidates[i]);

                    }



                    // Check 3.0s timeout

                    double elapsed = (DateTime.UtcNow - start).TotalSeconds;

                    if (elapsed > 3.0)

                    {

                        GD.PrintErr(string.Format("[AiTeammate][Coord] circuit-open: in-combat-hand decision timed out ({0:F2}s > 3.0s)", elapsed));

                        AutopilotFuse.TripCircuit("in-combat-hand", fingerprint);

                        TurnFlowPatches.SwitchControlToPlayer(player.NetId, "in-combat-hand-timeout", false);

                        return null;

                    }



                    // Mark resolved and log

                    AutopilotFuse.MarkResolved("in-combat-hand", fingerprint);



                    List<string> pickIds = new List<string>();

                    List<string> scoreStrs = new List<string>();

                    for (int i = 0; i < selectedCards.Count; i++)

                    {

                        CardModel sc = selectedCards[i];

                        pickIds.Add(sc.Id.ToString());

                        scoreStrs.Add(FlowCoordinator.GetCardScore(sc.Id.ToString()).ToString("F1"));

                    }



                    GD.Print(string.Format("[AiTeammate][Coord] p2-choice-resolved kind=in-combat-hand pick={0} scores={1} reason=card-score",

                        string.Join(",", pickIds.ToArray()),

                        string.Join(",", scoreStrs.ToArray())));



                    return selectedCards;

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Coord] circuit-open: in-combat-hand exception: " + ex);

                    string fp = "in-combat-hand:" + (source != null ? source.Id.ToString() : methodTag) + ":" + methodTag;

                    AutopilotFuse.TripCircuit("in-combat-hand", fp);

                    if (player != null)

                    {

                        TurnFlowPatches.SwitchControlToPlayer(player.NetId, "in-combat-hand-exception", false);

                    }

                    return null;

                }

            }



            public static bool FromHandPrefix(PlayerChoiceContext context, Player player, CardSelectorPrefs prefs, Func<CardModel, bool> filter, AbstractModel source, ref Task<IEnumerable<CardModel>> __result)

            {

                var picked = InCombatHandDecide(context, player, prefs, filter, source, null, "FromHand");

                if (picked != null)

                {

                    IEnumerable<CardModel> res = picked;

                    __result = Task.FromResult(res);

                    return false;

                }

                return true;

            }



            public static bool FromHandForDiscardPrefix(PlayerChoiceContext context, Player player, CardSelectorPrefs prefs, Func<CardModel, bool> filter, AbstractModel source, ref Task<IEnumerable<CardModel>> __result)

            {

                var picked = InCombatHandDecide(context, player, prefs, filter, source, null, "FromHandForDiscard");

                if (picked != null)

                {

                    IEnumerable<CardModel> res = picked;

                    __result = Task.FromResult(res);

                    return false;

                }

                return true;

            }



            public static bool FromHandForUpgradePrefix(PlayerChoiceContext context, Player player, AbstractModel source, ref Task<CardModel> __result)

            {

                var picked = InCombatHandDecide(context, player, default(CardSelectorPrefs), null, source, null, "FromHandForUpgrade");

                if (picked != null)

                {

                    CardModel best = (picked.Count > 0 ? picked[0] : null);

                    __result = Task.FromResult(best);

                    return false;

                }

                return true;

            }



            public static bool FromCombatPile4Prefix(PlayerChoiceContext context, CardPile pile, Player player, CardSelectorPrefs prefs, ref Task<IEnumerable<CardModel>> __result)

            {

                var picked = InCombatHandDecide(context, player, prefs, null, null, pile, "FromCombatPile");

                if (picked != null)

                {

                    IEnumerable<CardModel> res = picked;

                    __result = Task.FromResult(res);

                    return false;

                }

                return true;

            }



            public static bool FromCombatPile5Prefix(PlayerChoiceContext context, CardPile pile, Player player, CardSelectorPrefs prefs, Func<CardModel, bool> filter, ref Task<IEnumerable<CardModel>> __result)

            {

                var picked = InCombatHandDecide(context, player, prefs, filter, null, pile, "FromCombatPile");

                if (picked != null)

                {

                    IEnumerable<CardModel> res = picked;

                    __result = Task.FromResult(res);

                    return false;

                }

                return true;

            }

        }



        // Directive #5.11 H1: Track reserved choices for P2

        public static class PlayerChoiceReservePatch

        {

            public static void Postfix(Player player, uint __result)

            {

                try

                {

                    if (player != null)

                    {

                        ulong p2NetId = TurnCoordinator.GetPlayer2NetId();

                        if (player.NetId == p2NetId || TurnCoordinator.IsPlayer2(player))

                        {

                            TurnCoordinator.P2PendingChoiceCount++;

                        }

                    }

                }

                catch { }

            }

        }



        // Directive #5.11 H3: Submission hard gate against cross-owner consumption

        public static class PlayerChoiceSyncLocalPatch

        {

            public static bool Prefix(Player player, uint choiceId, PlayerChoiceResult result)

            {

                try

                {

                    if (player != null)

                    {

                        ulong p2NetId = TurnCoordinator.GetPlayer2NetId();

                        if (player.NetId == p2NetId || TurnCoordinator.IsPlayer2(player))

                        {

                            if (TurnCoordinator.P2PendingChoiceCount > 0)

                            {

                                TurnCoordinator.P2PendingChoiceCount--;

                            }



                            // H3 HARD GATE: check all cards submitted

                            if (result != null)

                            {

                                IEnumerable<CardModel> cards = null;

                                try

                                {

                                    string choiceTypeName = result.ChoiceType.ToString();

                                    if (choiceTypeName.IndexOf("Combat", StringComparison.OrdinalIgnoreCase) >= 0)

                                    {

                                        cards = result.AsCombatCards();

                                    }

                                    else if (choiceTypeName.IndexOf("Deck", StringComparison.OrdinalIgnoreCase) >= 0)

                                    {

                                        cards = result.AsDeckCards();

                                    }

                                    else if (choiceTypeName.IndexOf("Canonical", StringComparison.OrdinalIgnoreCase) >= 0)

                                    {

                                        cards = result.AsCanonicalCards();

                                    }

                                    else if (choiceTypeName.IndexOf("Mutable", StringComparison.OrdinalIgnoreCase) >= 0)

                                    {

                                        cards = result.AsMutableCards();

                                    }

                                }

                                catch { cards = null; }



                                if (cards != null)

                                {

                                    foreach (var c in cards)

                                    {

                                        if (c == null) continue;

                                        if (!InCombatHandSelectPatch.IsCardOwnedByPlayer(player, c))

                                        {

                                            GD.PrintErr(string.Format("[AiTeammate][Coord] circuit-open: cross-owner-block rejected choiceId={0} card={1} (card owner={2}, submitting player={3})",

                                                choiceId, c.Id, (c.Owner != null ? c.Owner.NetId.ToString() : "null"), player.NetId));

                                            AutopilotFuse.TripCircuit("choice-submission", "cross-owner-block");

                                            TurnFlowPatches.SwitchControlToPlayer(player.NetId, "cross-owner-block", false);

                                            return false; // REJECT AND BLOCK SUBMISSION!

                                        }

                                    }

                                }

                            }

                        }

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Coord] PlayerChoiceSyncLocalPatch error: " + ex);

                }

                return true;

            }

        }



        public static class RoomChangePatch

        {

            public static void Postfix()

            {

                try

                {

                    TurnCoordinator.OnRoomEnterBegin("RunState.PushRoom");

                    TurnCoordinator.P2PendingChoiceCount = 0;

                    TurnCoordinator.RoomEnterTime = DateTime.UtcNow;

                    TurnCoordinator.IsPreCombatOrLoading = true;

                    TurnCoordinator.TurnStartTime = DateTime.MinValue;

                    TurnCoordinator.StallRecalcTriggered = false;

                    TurnCoordinator.StallRecalcTime = DateTime.MinValue;

                    TurnCoordinator.StallForceEndTurnTriggered = false;

                    TurnCoordinator.PotionRebuildMs = 0.0;

                    TurnCoordinator.RelicRebuildMs = 0.0;

                    TurnCoordinator.TopBarRebuildMs = 0.0;

                    FlowCoordinator.ResetRewardRouteState();



                    FlowCoordinator.HasPurchasedShopRemovalThisRoom = false;

                    FlowCoordinator.IsSubPageManualInteracting = false;

                    NeowPatch.NeowHandled = false;

                    RestSitePatch.LastHandledRestSiteFloor = -1;

                    EventRoomPatch.LastHandledEventFloor = -1;

                }

                catch { }

            }

        }



        // Directive #5.14 T2: NEventRoom._Ready Guard & Conflict Fail-Open

        public static class NEventRoomReadyGuardPatch

        {

            public static void Prefix(MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom __instance)

            {

                try

                {
                    ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                    ulong actual = TurnFlowPatches.GetDraCurrentControlledPlayerId();
                    GD.Print(string.Format("[AiTeammate][Event] control-context-assert room=event expected=P1 actual={0}", actual));
                    if (p1Id != 0 && actual != p1Id)
                    {
                        TurnFlowPatches.SwitchControlToPlayer(p1Id, "wakuu-p1-restore:event-entry", false);
                        GD.Print(string.Format("[AiTeammate][Event] event-entry-p1 restored target={0}", p1Id));
                    }
                    else
                    {
                        GD.Print(string.Format("[AiTeammate][Event] event-entry-p1 confirmed target={0}", p1Id));
                    }

                    if (__instance != null)

                    {

                        var field = HarmonyLib.AccessTools.Field(typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom), "_event");

                        if (field != null)

                        {

                            var ev = field.GetValue(__instance) as MegaCrit.Sts2.Core.Models.EventModel;

                            if (ev != null && ev.Node != null)

                            {

                                GD.PrintErr("[AiTeammate][Flow] room-node-conflict: NEventRoom._Ready detected event already has a node, resetting node reference to allow safe recreation and fail-open to manual");

                                var nodeField = HarmonyLib.AccessTools.Field(typeof(MegaCrit.Sts2.Core.Models.EventModel), "<Node>k__BackingField");

                                if (nodeField != null)

                                {

                                    nodeField.SetValue(ev, null);

                                }

                                else

                                {

                                    var setProp = HarmonyLib.AccessTools.PropertySetter(typeof(MegaCrit.Sts2.Core.Models.EventModel), "Node");

                                    if (setProp != null) setProp.Invoke(ev, new object[] { null });

                                }

                                FlowCoordinator.IsSubPageManualInteracting = true;

                            }

                        }

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] NEventRoomReadyGuardPatch Prefix error: " + ex);

                }

            }



            public static Exception Finalizer(Exception __exception, MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom __instance)

            {

                try

                {

                    if (__exception != null)

                    {

                        string msg = __exception.Message ?? "";

                        if (msg.Contains("already has a node"))

                        {

                            GD.PrintErr("[AiTeammate][Flow] room-node-conflict: " + msg + " (fail-open to manual, suppressing crash)");

                            FlowCoordinator.IsSubPageManualInteracting = true;

                            TurnCoordinator.OnRoomSettled("room-node-conflict-failopen");

                            return null;

                        }

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] NEventRoomReadyGuardPatch Finalizer error: " + ex);

                }

                return __exception;

            }



            public static void Postfix(MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom __instance)

            {

                try

                {
                    ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                    ulong actual = TurnFlowPatches.GetDraCurrentControlledPlayerId();
                    string actualStr = (p1Id != 0 && actual == p1Id) ? "P1" : actual.ToString();
                    GD.Print(string.Format("[AiTeammate][Event] event-room-enter expected=P1 actual={0}", actualStr));
                    GD.Print(string.Format("[AiTeammate][Event] control-context-assert room=event expected=P1 actual={0}", actual));
                    if (p1Id != 0 && actual != p1Id)
                    {
                        TurnFlowPatches.SwitchControlToPlayer(p1Id, "wakuu-p1-restore:event-entry-settle", false);
                        GD.Print(string.Format("[AiTeammate][Event] event-auto-next-p1-restore target={0}", p1Id));
                    }
                    else
                    {
                        GD.Print(string.Format("[AiTeammate][Event] event-entry-p1 settled target={0}", p1Id));
                    }

                    string finalCtrl = (actual == p1Id || (p1Id != 0 && TurnFlowPatches.GetDraCurrentControlledPlayerId() == p1Id)) ? "P1" : actual.ToString();
                    GD.Print(string.Format("[AiTeammate][Event] event-room-final-controller expected=P1 actual={0}", finalCtrl));

                    TurnCoordinator.OnRoomSettled("NEventRoom._Ready");

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Flow] NEventRoomReadyGuardPatch Postfix error: " + ex);

                }

            }

        }



        // Directive #5.14 T1: Room Enter Lifecycle Hooks

        public static class RoomEnterLifecyclePatches

        {

            public static void RunManagerEnterRoomPrefix(MegaCrit.Sts2.Core.Rooms.AbstractRoom room)

            {

                TurnCoordinator.GuardPendingChoicesBeforeLeave("RunManager.EnterRoom");
                TurnCoordinator.OnRoomEnterBegin("RunManager.EnterRoom");

            }



            public static void RunManagerEnterRoomPostfix(MegaCrit.Sts2.Core.Rooms.AbstractRoom room)

            {

                TurnCoordinator.OnRoomSettled("RunManager.EnterRoom");
                if (room is MegaCrit.Sts2.Core.Rooms.MerchantRoom)
                {
                    FlowCoordinator.OnMerchantRoomEnteredOrSettled("RunManager.EnterRoom");
                }

            }



            public static void AbstractRoomEnterPrefix(MegaCrit.Sts2.Core.Rooms.AbstractRoom __instance)

            {

                TurnCoordinator.GuardPendingChoicesBeforeLeave("AbstractRoom.Enter");
                TurnCoordinator.OnRoomEnterBegin("AbstractRoom.Enter");

            }



            public static void AbstractRoomEnterPostfix(MegaCrit.Sts2.Core.Rooms.AbstractRoom __instance)

            {

                TurnCoordinator.OnRoomSettled("AbstractRoom.Enter");

            }

        }

    }



    // Directive #5.0.10 T1: Defer Rebuild Timing Patches

    public static class TimingPatches

    {

        public static void PotionRebuildPrefix(out DateTime __state)

        {

            __state = DateTime.UtcNow; TurnCoordinator.P1InputLockDepth++; GD.Print("[AiTeammate][Coord] input-lock-window begin=potion");

        }

        public static void PotionRebuildPostfix(DateTime __state)

        {

            double ms = (DateTime.UtcNow - __state).TotalMilliseconds;

            TurnCoordinator.PotionRebuildMs += ms;

        }



        public static void RelicRebuildPrefix(out DateTime __state)

        {

            __state = DateTime.UtcNow;

        }

        public static void RelicRebuildPostfix(DateTime __state)

        {

            double ms = (DateTime.UtcNow - __state).TotalMilliseconds;

            TurnCoordinator.RelicRebuildMs += ms;

        }



        public static void TopBarRebuildPrefix(out DateTime __state)

        {

            __state = DateTime.UtcNow;

        }

        public static void TopBarRebuildPostfix(DateTime __state)

        {

            double ms = (DateTime.UtcNow - __state).TotalMilliseconds;

            TurnCoordinator.TopBarRebuildMs += ms;

        }

    }



    [ModInitializer("Init")]

    public static class ModEntry

    {

        private static bool _suppressControlSwitchUi = false;

        public static bool SuppressControlSwitchUi

        {

            get {

                if (FlowCoordinator.IsSubPageManualInteracting) return false;

                return _suppressControlSwitchUi || TurnCoordinator.IsDeploying;

            }

            set { _suppressControlSwitchUi = value; }

        }



        public static bool HookedWakuu = false;

        public static bool HookedCombatSolver = false;

        public static bool HookedFullAutoGuard = false;

        public static bool HookedRootControl = false;

        public static bool HookedChoiceSuppression = false;

        public static bool HookedChoiceCandidatePool = false;

        public static bool HookedDeploymentInputLock = false;

        public static bool HookedTurnCoordinator = false;

        public static bool HookedAutonomousGatekeeper = false;



        public static bool InitIntegrityChecked = false;



        public static List<string> HookedGroups = new List<string>();

        public static List<string> FailedGroups = new List<string>();



        private static Harmony _harmonyInstance = null;

        private static readonly object _solverPatchLock = new object();

        public static bool CombatSolverActuallyPatched = false;



        private static void TryPreloadSpireStrategist()

        {

            try

            {

                // Directive #5.0.8 T1: Avoid loading isolated duplicate copy via Assembly.LoadFrom.

                // Check if already in AppDomain first:

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())

                {

                    if (asm.GetName().Name == "SpireStrategist")

                    {

                        FlowCoordinator.SpireStrategistAvailable = true;

                        FlowCoordinator.SpireStrategistAssembly = asm;

                        return;

                    }

                }



                var baseDir = AppDomain.CurrentDomain.BaseDirectory;

                var searchDirs = new string[]

                {

                    System.IO.Path.Combine(baseDir ?? "", "mods", "SpireStrategist"),

                    System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "mods", "SpireStrategist"),

                    System.IO.Path.Combine("mods", "SpireStrategist")

                };



                string targetDll = null;

                foreach (var dir in searchDirs)

                {

                    if (!System.IO.Directory.Exists(dir)) continue;

                    var files = System.IO.Directory.GetFiles(dir, "SpireStrategist.dll", System.IO.SearchOption.AllDirectories);

                    if (files != null && files.Length > 0)

                    {

                        targetDll = files[0];

                        break;

                    }

                }



                if (targetDll != null)

                {

                    Assembly loaded = null;

                    try

                    {

                        Type alcType = Type.GetType("System.Runtime.Loader.AssemblyLoadContext, System.Runtime.Loader");

                        if (alcType != null)

                        {

                            MethodInfo getContextMethod = alcType.GetMethod("GetLoadContext", new Type[] { typeof(Assembly) });

                            if (getContextMethod != null)

                            {

                                object gameAlc = getContextMethod.Invoke(null, new object[] { typeof(MegaCrit.Sts2.Core.Runs.RunState).Assembly });

                                if (gameAlc != null)

                                {

                                    MethodInfo loadPathMethod = gameAlc.GetType().GetMethod("LoadFromAssemblyPath", new Type[] { typeof(string) });

                                    if (loadPathMethod != null)

                                    {

                                        loaded = loadPathMethod.Invoke(gameAlc, new object[] { System.IO.Path.GetFullPath(targetDll) }) as Assembly;

                                    }

                                }

                            }

                        }

                    }

                    catch (Exception exAlc)

                    {

                        GD.PrintErr("[AiTeammate] TryPreloadSpireStrategist ALC load failed: " + exAlc.Message);

                    }



                    if (loaded == null)

                    {

                        loaded = Assembly.LoadFrom(targetDll);

                    }



                    if (loaded != null)

                    {

                        FlowCoordinator.SpireStrategistAvailable = true;

                        FlowCoordinator.SpireStrategistAssembly = loaded;

                        GD.Print("[AiTeammate] Preloaded SpireStrategist assembly into game context: " + targetDll);

                    }

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate] TryPreloadSpireStrategist exception: " + ex);

                FlowCoordinator.SpireStrategistAvailable = false;

            }

        }



        private static void TryPreloadCombatSolver()

        {

            try

            {

                if (AccessTools.TypeByName("CombatSolver.SolverController") != null) return;



                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())

                {

                    if (asm.GetName().Name == "CombatSolver") return;

                }



                var baseDir = AppDomain.CurrentDomain.BaseDirectory;

                var searchDirs = new string[]

                {

                    System.IO.Path.Combine(baseDir ?? "", "mods"),

                    System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "mods")

                };



                foreach (var dir in searchDirs)

                {

                    if (!System.IO.Directory.Exists(dir)) continue;

                    var files = System.IO.Directory.GetFiles(dir, "CombatSolver*.dll", System.IO.SearchOption.AllDirectories);

                    if (files != null && files.Length > 0)

                    {

                        GD.Print("[AiTeammate] Preloading CombatSolver assembly from: " + files[0]);

                        Assembly.LoadFrom(files[0]);

                        break;

                    }

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate] TryPreloadCombatSolver exception: " + ex);

            }

        }



        public static void EnsureCombatSolverPatched()

        {

            if (CombatSolverActuallyPatched) return;

            lock (_solverPatchLock)

            {

                if (CombatSolverActuallyPatched) return;

                TryPreloadCombatSolver();

            TryPreloadSpireStrategist();

                if (_harmonyInstance == null) _harmonyInstance = new Harmony("com.antigravity.sts2.aiteammate");

                PatchCombatSolverAndTurnFlow(_harmonyInstance);

                PatchFullAutoGuard(_harmonyInstance);

                if (CombatSolverActuallyPatched)

                {

                    GD.Print("[AiTeammate] CombatSolver dynamically hooked successfully via EnsureCombatSolverPatched.");

                }

            }

        }



        private static bool ParseBoolSetting(string content, string key, bool defaultValue)

        {

            if (string.IsNullOrEmpty(content)) return defaultValue;

            try

            {

                var match = System.Text.RegularExpressions.Regex.Match(content, "\"" + key + "\"\\s*:\\s*(true|false)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (match.Success)

                {

                    return bool.Parse(match.Groups[1].Value.ToLowerInvariant());

                }

            }

            catch { }

            return defaultValue;

        }



        public static void LoadSettings()

        {

            try

            {

                var baseDir = AppDomain.CurrentDomain.BaseDirectory;

                var candidates = new string[]

                {

                    System.IO.Path.Combine(baseDir ?? "", "mods", "AiTeammate", "aiteammate_settings.json"),

                    System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "mods", "AiTeammate", "aiteammate_settings.json"),

                    System.IO.Path.Combine("mods", "AiTeammate", "aiteammate_settings.json")

                };



                string settingsPath = null;

                foreach (var p in candidates)

                {

                    if (System.IO.File.Exists(p))

                    {

                        settingsPath = p;

                        break;

                    }

                }



                if (settingsPath == null)

                {

                    settingsPath = candidates[0];

                    try

                    {

                        var dir = System.IO.Path.GetDirectoryName(settingsPath);

                        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))

                        {

                            System.IO.Directory.CreateDirectory(dir);

                        }

                         string defaultJson = "{\n  \"coordinationEnabled\": false,\n  \"waitEnabled\": false,\n  \"holdEnabled\": false,\n  \"midturnEnabled\": false,\n  \"autoRouteFollowEnabled\": true,\n  \"autoCardRewardEnabled\": true,\n  \"autoEventEnabled\": true,\n  \"autoRestSiteEnabled\": true,\n  \"autoNeowEnabled\": true,\n  \"shopCardRemovalEnabled\": true,\n  \"floatingEndTurnEnabled\": false,\n  \"fixedP1CombatControl\": true\n}";

                        System.IO.File.WriteAllText(settingsPath, defaultJson);

                        GD.Print("[AiTeammate] Created default settings file: " + settingsPath);

                    }

                    catch (Exception exCreate)

                    {

                        GD.PrintErr("[AiTeammate] Failed to write default settings file: " + exCreate.Message);

                    }

                }



                bool coord = false;

                bool wait = false;

                bool hold = false;

                bool midturn = false;



                bool autoRoute = true;

                bool autoCard = true;

                bool autoEvent = true;

                bool autoRest = true;

                bool autoNeow = true;

                bool autoShop = true;
                bool shopRemoval = true;
                 bool floatingEndTurn = false;
                 bool fixedP1CombatControl = true;



                if (settingsPath != null && System.IO.File.Exists(settingsPath))

                {

                    string content = System.IO.File.ReadAllText(settingsPath);

                    coord = ParseBoolSetting(content, "coordinationEnabled", false);

                    wait = ParseBoolSetting(content, "waitEnabled", false);

                    hold = ParseBoolSetting(content, "holdEnabled", false);

                    midturn = ParseBoolSetting(content, "midturnEnabled", false);

                    autoRoute = ParseBoolSetting(content, "autoRouteFollowEnabled", true);

                    autoCard = ParseBoolSetting(content, "autoCardRewardEnabled", true);

                    autoEvent = ParseBoolSetting(content, "autoEventEnabled", true);

                    autoRest = ParseBoolSetting(content, "autoRestSiteEnabled", true);

                    autoNeow = ParseBoolSetting(content, "autoNeowEnabled", true);

                    autoShop = ParseBoolSetting(content, "autoShopEnabled", true);
                    shopRemoval = ParseBoolSetting(content, "shopCardRemovalEnabled", true);
                     floatingEndTurn = ParseBoolSetting(content, "floatingEndTurnEnabled", false);
                     fixedP1CombatControl = ParseBoolSetting(content, "fixedP1CombatControl", true);

                }



                TurnCoordinator.RawCoordinationEnabled = coord;

                TurnCoordinator.RawWaitEnabled = wait;

                TurnCoordinator.RawHoldEnabled = hold;

                TurnCoordinator.RawMidturnEnabled = midturn;



                FlowCoordinator.AutoRouteFollowEnabled = autoRoute;

                FlowCoordinator.AutoCardRewardEnabled = autoCard;

                FlowCoordinator.AutoEventEnabled = autoEvent;

                FlowCoordinator.AutoRestSiteEnabled = autoRest;

                FlowCoordinator.AutoNeowEnabled = autoNeow;

                FlowCoordinator.AutoShopEnabled = autoShop;
                FlowCoordinator.ShopCardRemovalEnabled = shopRemoval;
                FlowCoordinator.FloatingEndTurnEnabled = floatingEndTurn;
                 TurnCoordinator.FixedP1CombatControl = fixedP1CombatControl;



                 GD.Print(string.Format("[AiTeammate] config: coordination={0} wait={1} hold={2} midturn={3} autoRoute={4} autoCard={5} autoEvent={6} autoRest={7} autoNeow={8} shopRemoval={9} floatingEndTurn={10} fixedP1CombatControl={11}",

                    TurnCoordinator.CoordinationEnabled,

                    TurnCoordinator.WaitEnabled,

                    TurnCoordinator.HoldEnabled,

                    TurnCoordinator.MidturnEnabled,

                    FlowCoordinator.AutoRouteFollowEnabled,

                    FlowCoordinator.AutoCardRewardEnabled,

                    FlowCoordinator.AutoEventEnabled,

                    FlowCoordinator.AutoRestSiteEnabled,

                    FlowCoordinator.AutoNeowEnabled,

                     FlowCoordinator.ShopCardRemovalEnabled,
                     FlowCoordinator.FloatingEndTurnEnabled,
                     TurnCoordinator.FixedP1CombatControl));
                 GD.Print("[AiTeammate][Coord] fixed-p1-combat-control enabled=" + TurnCoordinator.FixedP1CombatControl.ToString().ToLowerInvariant());

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate] LoadSettings fail-open exception: " + ex);

                TurnCoordinator.RawCoordinationEnabled = false;

                TurnCoordinator.RawWaitEnabled = false;

                TurnCoordinator.RawHoldEnabled = false;

                TurnCoordinator.RawMidturnEnabled = false;

                FlowCoordinator.AutoRouteFollowEnabled = true;

                FlowCoordinator.AutoCardRewardEnabled = true;

                FlowCoordinator.AutoEventEnabled = true;

                FlowCoordinator.AutoRestSiteEnabled = true;

                FlowCoordinator.AutoNeowEnabled = true;

                FlowCoordinator.AutoShopEnabled = true;
                FlowCoordinator.ShopCardRemovalEnabled = true;
                 TurnCoordinator.FixedP1CombatControl = true;
                 GD.Print("[AiTeammate][Coord] fixed-p1-combat-control enabled=true");

                GD.Print(string.Format("[AiTeammate] config: coordination={0} wait={1} hold={2} midturn={3} autoRoute={4} autoCard={5} autoEvent={6} autoRest={7} autoNeow={8} shopRemoval={9}",

                    TurnCoordinator.CoordinationEnabled,

                    TurnCoordinator.WaitEnabled,

                    TurnCoordinator.HoldEnabled,

                    TurnCoordinator.MidturnEnabled,

                    FlowCoordinator.AutoRouteFollowEnabled,

                    FlowCoordinator.AutoCardRewardEnabled,

                    FlowCoordinator.AutoEventEnabled,

                    FlowCoordinator.AutoRestSiteEnabled,

                    FlowCoordinator.AutoNeowEnabled,

                    FlowCoordinator.ShopCardRemovalEnabled));

            }

        }



        public delegate void PatchAction();



        private static void RunPatchGroup(string groupName, PatchAction action)

        {

            try

            {

                action();

            }

            catch (Exception ex)

            {

                string firstLine = ex.Message;

                if (!string.IsNullOrEmpty(firstLine))

                {

                    int idx = firstLine.IndexOf('\n');

                    if (idx >= 0) firstLine = firstLine.Substring(0, idx).Trim();

                }

                else

                {

                    firstLine = ex.GetType().Name;

                }

                GD.PrintErr("[AiTeammate] INIT-FAIL: " + groupName + ": " + firstLine);

                if (!FailedGroups.Contains(groupName))

                {

                    FailedGroups.Add(groupName);

                }

            }

        }



                private static void PatchShopFlow(Harmony harmony)
        {
            try
            {
                var merchantType = AccessTools.TypeByName("LocalMultiControl.Scripts.Runtime.LocalMerchantInventoryRuntime");
                if (merchantType != null)
                {
                    var mShop = AccessTools.Method(merchantType, "RefreshShopRoomForPlayer");
                    if (mShop != null)
                    {
                        harmony.Patch(mShop, null, new HarmonyMethod(typeof(FlowPatches.ShopFlowPatches), "RefreshShopRoomForPlayerPostfix"));
                        GD.Print("[AiTeammate] Hooked LocalMerchantInventoryRuntime.RefreshShopRoomForPlayer (P2 Shop Auto-Removal Postfix).");
                    }
                }

                var mMerchantRoomReady = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom), "_Ready");
                if (mMerchantRoomReady != null)
                {
                    harmony.Patch(mMerchantRoomReady, null, new HarmonyMethod(typeof(FlowPatches.ShopFlowPatches), "MerchantRoomReadyPostfix"));
                    GD.Print("[AiTeammate] Hooked NMerchantRoom._Ready (P2 Shop Room Enter Hook).");
                }

                var mDoLocal = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Multiplayer.Game.OneOffSynchronizer), "DoLocalMerchantCardRemoval");
                if (mDoLocal != null)
                {
                    harmony.Patch(mDoLocal, new HarmonyMethod(typeof(FlowPatches.ShopFlowPatches), "DoLocalMerchantCardRemovalPrefix"), null);
                    GD.Print("[AiTeammate] Hooked OneOffSynchronizer.DoLocalMerchantCardRemoval (P2 Safe Removal Prefix).");
                }

                var mRemovalUsed = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory), "OnCardRemovalUsed");
                if (mRemovalUsed != null)
                {
                    harmony.Patch(mRemovalUsed, new HarmonyMethod(typeof(FlowPatches.ShopFlowPatches), "OnCardRemovalUsedPrefix"), null);
                    GD.Print("[AiTeammate] Hooked NMerchantInventory.OnCardRemovalUsed (P1 UI Guard Prefix).");
                }

                if (!HookedGroups.Contains("shop-flow")) HookedGroups.Add("shop-flow");
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate] PatchShopFlow exception: " + ex);
            }
        }

private static void PatchFlowCoordinator(Harmony harmony)

        {

            try

            {

                RewardBackendFlow.Install(harmony);

                Type mapSyncType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.MapSelectionSynchronizer");

                if (mapSyncType != null)

                {

                    try

                    {

                        MethodInfo mapVoteMethod = AccessTools.Method(mapSyncType, "PlayerVotedForMapCoord");

                        if (mapVoteMethod != null)

                        {

                            harmony.Patch(mapVoteMethod, new HarmonyMethod(typeof(FlowPatches.MapSelectionVotePatch), "Prefix"), new HarmonyMethod(typeof(FlowPatches.MapSelectionVotePatch), "Postfix"));

                            GD.Print("[AiTeammate] Patched MapSelectionSynchronizer.PlayerVotedForMapCoord (Route Follow).");

                        }

                    }

                    catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: MapSelectionSynchronizer.PlayerVotedForMapCoord: " + ex.Message); }

                }

                try
                {
                    Type rewardButtonType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton");
                    MethodInfo getReward = rewardButtonType != null ? AccessTools.Method(rewardButtonType, "GetReward") : null;
                    if (getReward != null)
                    {
                        harmony.Patch(getReward, null, new HarmonyMethod(typeof(FlowPatches.RewardRuntimeGatePatches), "RewardButtonGetRewardPostfix"));
                        GD.Print("[AiTeammate] Patched NRewardButton.GetReward (backend completion observer).");
                    }
                }
                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: NRewardButton.GetReward: " + ex); }

                try
                {
                    MethodInfo completedSignal = AccessTools.Method(typeof(NRewardsScreen), "EmitSignalCompleted");
                    if (completedSignal != null)
                    {
                        harmony.Patch(completedSignal, null, new HarmonyMethod(typeof(FlowPatches.RewardRuntimeGatePatches), "RewardsCompletedSignalPostfix"));
                        GD.Print("[AiTeammate] Patched NRewardsScreen.EmitSignalCompleted (reward gate observer).");
                    }
                }
                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: NRewardsScreen.EmitSignalCompleted: " + ex); }

                try
                {
                    MethodInfo exitRoom = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Runs.RunManager), "ExitCurrentRoom");
                    if (exitRoom != null)
                    {
                        harmony.Patch(exitRoom, new HarmonyMethod(typeof(FlowPatches.RewardRuntimeGatePatches), "ExitCurrentRoomPrefix"));
                        GD.Print("[AiTeammate] Patched RunManager.ExitCurrentRoom (reward exit gate).");
                    }
                }
                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: RunManager.ExitCurrentRoom: " + ex); }

                try
                {
                    Type moveType = AccessTools.TypeByName("MegaCrit.Sts2.Core.GameActions.MoveToMapCoordAction");
                    MethodInfo executeMove = moveType != null ? AccessTools.Method(moveType, "ExecuteAction") : null;
                    if (executeMove != null)
                    {
                        harmony.Patch(executeMove, new HarmonyMethod(typeof(FlowPatches.RewardRuntimeGatePatches), "MoveToMapCoordActionPrefix"));
                        GD.Print("[AiTeammate] Patched MoveToMapCoordAction.ExecuteAction (reward route gate).");
                    }
                }
                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: MoveToMapCoordAction.ExecuteAction: " + ex); }



                try

                {

                    MethodInfo rewardsReadyMethod = AccessTools.Method(typeof(NRewardsScreen), "_Ready");

                    if (rewardsReadyMethod != null)

                    {

                        harmony.Patch(rewardsReadyMethod, null, new HarmonyMethod(typeof(FlowPatches.RewardsScreenReadyPatch), "Postfix"));

                        GD.Print("[AiTeammate] Patched NRewardsScreen._Ready (Auto-Claim P2 Combat Rewards).");

                    }

                }

                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: NRewardsScreen._Ready: " + ex.Message); }



                try
                {
                    MethodInfo updateState = AccessTools.Method(typeof(NRewardsScreen), "UpdateScreenState");
                    if (updateState != null)
                        harmony.Patch(updateState, null, new HarmonyMethod(typeof(FlowPatches.RewardRuntimeGatePatches), "RewardsScreenUpdatePostfix"));
                }
                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: NRewardsScreen.UpdateScreenState: " + ex); }

                try
                {
                    Type syncType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.RewardsSetSynchronizer");
                    MethodInfo completeSet = syncType != null ? AccessTools.Method(syncType, "CompleteRewardsSet") : null;
                    if (completeSet != null)
                        harmony.Patch(completeSet, null, new HarmonyMethod(typeof(FlowPatches.RewardRuntimeGatePatches), "RewardsSetCompletePostfix"));
                }
                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: RewardsSetSynchronizer.CompleteRewardsSet: " + ex); }

                try

                {

                    MethodInfo pushRoomMethod = AccessTools.Method(typeof(RunState), "PushRoom", new Type[] { typeof(AbstractRoom) });

                    if (pushRoomMethod != null)

                    {

                        harmony.Patch(pushRoomMethod, null, new HarmonyMethod(typeof(FlowPatches.RoomChangePatch), "Postfix"));

                        GD.Print("[AiTeammate] Patched RunState.PushRoom (Room State Reset).");

                    }

                }

                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: RunState.PushRoom: " + ex.Message); }



                // Directive #5.14 T1 & T2: Room Enter Lifecycle & NEventRoom._Ready Guard

                try

                {

                    MethodInfo mReady = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom), "_Ready");

                    if (mReady != null)

                    {

                        harmony.Patch(mReady,

                            new HarmonyMethod(typeof(FlowPatches.NEventRoomReadyGuardPatch), "Prefix"),

                            new HarmonyMethod(typeof(FlowPatches.NEventRoomReadyGuardPatch), "Postfix"),

                            null,

                            new HarmonyMethod(typeof(FlowPatches.NEventRoomReadyGuardPatch), "Finalizer"));

                        GD.Print("[AiTeammate] Patched NEventRoom._Ready (Event Room Ready Guard & Settle).");

                    }

                }

                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: NEventRoom._Ready: " + ex.Message); }



                try

                {

                    MethodInfo mArEnter = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Rooms.AbstractRoom), "Enter", new Type[] { typeof(MegaCrit.Sts2.Core.Runs.IRunState), typeof(bool) });

                    if (mArEnter != null)

                    {

                        harmony.Patch(mArEnter,

                            new HarmonyMethod(typeof(FlowPatches.RoomEnterLifecyclePatches), "AbstractRoomEnterPrefix"),

                            new HarmonyMethod(typeof(FlowPatches.RoomEnterLifecyclePatches), "AbstractRoomEnterPostfix"));

                        GD.Print("[AiTeammate] Patched AbstractRoom.Enter (Room Enter Lifecycle Tracking).");

                    }

                }

                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: AbstractRoom.Enter: " + ex.Message); }



                try

                {

                    MethodInfo mRmEnter = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Runs.RunManager), "EnterRoom", new Type[] { typeof(MegaCrit.Sts2.Core.Rooms.AbstractRoom) });

                    if (mRmEnter != null)

                    {

                        harmony.Patch(mRmEnter,

                            new HarmonyMethod(typeof(FlowPatches.RoomEnterLifecyclePatches), "RunManagerEnterRoomPrefix"),

                            new HarmonyMethod(typeof(FlowPatches.RoomEnterLifecyclePatches), "RunManagerEnterRoomPostfix"));

                        GD.Print("[AiTeammate] Patched RunManager.EnterRoom (Room Enter Lifecycle Tracking).");

                    }

                }

                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: RunManager.EnterRoom: " + ex.Message); }



                // Directive #5.0.7 T1: CardReward 3-pick re-enabled with fuse protection (the ONLY permitted kind)

                try

                {

                    MethodInfo crOnSelect = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Rewards.CardReward), "OnSelect");

                    if (crOnSelect != null)

                    {

                        harmony.Patch(crOnSelect, new HarmonyMethod(typeof(FlowPatches.CardRewardOnSelectPatch), "Prefix"), new HarmonyMethod(typeof(FlowPatches.CardRewardOnSelectPatch), "Postfix"));

                        GD.Print("[AiTeammate] Patched CardReward.OnSelect (P2 Choice Offer Probe).");

                    }

                }

                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: CardReward.OnSelect: " + ex.Message); }






                // --- Directive #5.0.8 T3: Relic re-enabled (Path A, B, C) ---

                try

                {

                    MethodInfo relicScreenMethod = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.RelicSelectCmd), "FromChooseARelicScreen", new Type[] { typeof(Player), typeof(IReadOnlyList<RelicModel>) });

                    if (relicScreenMethod != null)

                    {

                        harmony.Patch(relicScreenMethod, new HarmonyMethod(typeof(FlowPatches.RelicSelectFromScreenPatch), "Prefix"), null);

                        GD.Print("[AiTeammate] Patched RelicSelectCmd.FromChooseARelicScreen (Relic Autopilot Path A).");

                    }

                }

                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: RelicSelectCmd.FromChooseARelicScreen: " + ex.Message); }



                Type treasureSyncType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.TreasureRoomRelicSynchronizer");

                if (treasureSyncType != null)

                {

                    try

                    {

                        MethodInfo beginPickMethod = AccessTools.Method(treasureSyncType, "BeginRelicPicking");

                        if (beginPickMethod != null)

                        {

                            harmony.Patch(beginPickMethod, null, new HarmonyMethod(typeof(FlowPatches.TreasureRoomRelicBeginPatch), "Postfix"));

                            GD.Print("[AiTeammate] Patched TreasureRoomRelicSynchronizer.BeginRelicPicking (Relic Autopilot Path B).");

                        }

                    }

                    catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: TreasureRoomRelicSynchronizer.BeginRelicPicking: " + ex.Message); }

                }



                // --- Directive #5.0.8 T4: Campfire (Rest site) re-enabled ---

                Type draRestPatchType = AccessTools.TypeByName("LocalMultiControl.Scripts.Patch.RestSiteSynchronizerChooseLocalOptionPatch");

                if (draRestPatchType != null)

                {

                    try

                    {

                        MethodInfo tryFindNextMethod = AccessTools.Method(draRestPatchType, "TryFindNextSelectablePlayer");

                        if (tryFindNextMethod != null)

                        {

                            harmony.Patch(tryFindNextMethod, null, new HarmonyMethod(typeof(FlowPatches.RestSiteNextPlayerPatch), "Postfix"));

                            GD.Print("[AiTeammate] Patched RestSiteSynchronizerChooseLocalOptionPatch.TryFindNextSelectablePlayer (Campfire Auto-Resolve).");

                        }

                    }

                    catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: RestSiteSynchronizerChooseLocalOptionPatch.TryFindNextSelectablePlayer: " + ex.Message); }

                }



                try

                {

                    MethodInfo restUpdate = AccessTools.Method(typeof(NRestSiteRoom), "UpdateRestSiteOptions");
                    if (restUpdate != null) { harmony.Patch(restUpdate, null, new HarmonyMethod(typeof(FlowPatches.RestSitePatch), "Postfix")); GD.Print("[AiTeammate] Patched NRestSiteRoom.UpdateRestSiteOptions (Campfire settle autopilot #5.17)."); }

                    }
                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: NRestSiteRoom.UpdateRestSiteOptions: " + ex.Message); }

                // Directive #5.38 T1: Hook all FromDeck* methods on CardSelectCmd
                try
                {
                    Type cmdType = typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd);
                    foreach (MethodInfo m in cmdType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (m.Name == "FromDeckForTransformation")
                        {
                            harmony.Patch(m, new HarmonyMethod(typeof(FlowPatches.CardSelectFromDeckForTransformationPatch), "Prefix"), null);
                            GD.Print("[AiTeammate] Patched CardSelectCmd.FromDeckForTransformation (Transformation Card Autopilot).");
                        }
                        else if (m.Name == "FromDeckGeneric")
                        {
                            harmony.Patch(m, new HarmonyMethod(typeof(FlowPatches.CardSelectFromDeckGenericPatch), "Prefix"), null);
                            GD.Print("[AiTeammate] Patched CardSelectCmd.FromDeckGeneric (Generic Card Autopilot).");
                        }
                        else if (m.Name == "FromDeckForUpgrade")
                        {
                            harmony.Patch(m, new HarmonyMethod(typeof(FlowPatches.CardSelectFromDeckForUpgradePatch), "Prefix"), null);
                            GD.Print("[AiTeammate] Patched CardSelectCmd.FromDeckForUpgrade (Campfire Upgrade Card Autopilot).");
                        }
                        else if (m.Name == "FromDeckForRemoval")
                        {
                            harmony.Patch(m, new HarmonyMethod(typeof(FlowPatches.CardSelectFromDeckForRemovalPatch), "Prefix"), null);
                            GD.Print("[AiTeammate] Patched CardSelectCmd.FromDeckForRemoval (Neow Removal Autopilot).");
                        }
                    }
                }
                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: CardSelectCmd deck patches: " + ex.Message); }

                try

                {

                    MethodInfo simpleGridMethod = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), "FromSimpleGrid", new Type[] {
                        typeof(object), typeof(IEnumerable<CardModel>), typeof(Player), typeof(object)
                    }) ?? AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), "FromSimpleGrid");

                    if (simpleGridMethod != null)
                    {
                        harmony.Patch(simpleGridMethod, new HarmonyMethod(typeof(FlowPatches.CardSelectFromSimpleGridPatch), "Prefix"), null);
                        GD.Print("[AiTeammate] Patched CardSelectCmd.FromSimpleGrid (Grid Card Autopilot).");
                    }

                    MethodInfo simpleGridRewardsMethod = null;
                    foreach (var m in typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (m.Name == "FromSimpleGridForRewards")
                        {
                            var p = m.GetParameters();
                            if (p.Length == 4 && p[1].ParameterType.Name.Contains("List") && p[2].ParameterType == typeof(Player))
                            {
                                simpleGridRewardsMethod = m;
                                break;
                            }
                        }
                    }
                    if (simpleGridRewardsMethod == null)
                    {
                        simpleGridRewardsMethod = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), "FromSimpleGridForRewards");
                    }
                    if (simpleGridRewardsMethod != null)
                    {
                        harmony.Patch(simpleGridRewardsMethod, new HarmonyMethod(typeof(FlowPatches.CardSelectFromSimpleGridForRewardsPatch), "Prefix"), null);
                        GD.Print("[AiTeammate] Patched CardSelectCmd.FromSimpleGridForRewards (Grid Rewards Autopilot).");
                    }
                    else
                    {
                        GD.PrintErr("[AiTeammate] Could not find CardSelectCmd.FromSimpleGridForRewards via reflection!");
                    }

                }

                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: CardSelectCmd.FromSimpleGrid: " + ex.Message); }

                // Directive #5.33 T1: Hook CardSelectCmd.FromChooseABundleScreen
                try
                {
                    MethodInfo bundleMethod = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), "FromChooseABundleScreen");
                    if (bundleMethod != null)
                    {
                        harmony.Patch(bundleMethod, new HarmonyMethod(typeof(FlowPatches.CardSelectFromChooseABundleScreenPatch), "Prefix"), null);
                        GD.Print("[AiTeammate] Patched CardSelectCmd.FromChooseABundleScreen (Scrollbox Autopilot).");
                    }
                    else
                    {
                        GD.PrintErr("[AiTeammate] Could not find CardSelectCmd.FromChooseABundleScreen via reflection!");
                    }
                }
                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: CardSelectCmd.FromChooseABundleScreen: " + ex.Message); }



                try

                {

                    MethodInfo chooseCardMethod = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), "FromChooseACardScreen", new Type[] {

                        typeof(object), typeof(IEnumerable<CardModel>), typeof(Player), typeof(bool)

                    }) ?? AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd), "FromChooseACardScreen");

                    if (chooseCardMethod != null)

                    {

                        harmony.Patch(chooseCardMethod, new HarmonyMethod(typeof(FlowPatches.CardSelectFromChooseACardScreenPatch), "Prefix"), null);

                        GD.Print("[AiTeammate] Patched CardSelectCmd.FromChooseACardScreen (Choose Card Autopilot).");

                    }

                }

                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: CardSelectCmd.FromChooseACardScreen: " + ex.Message); }



                // Directive #5.12 H6: Enchantment subpage programmatic solver

                try

                {

                    Type cmdType = typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd);

                    Type enchType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.EnchantmentModel");

                    Type prefsType = AccessTools.TypeByName("MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs");



                    MethodInfo m1 = AccessTools.Method(cmdType, "FromDeckForEnchantment", new Type[] {

                        typeof(Player), enchType, typeof(int), prefsType

                    });

                    if (m1 != null)

                    {

                        harmony.Patch(m1, new HarmonyMethod(typeof(FlowPatches.CardSelectFromDeckForEnchantmentPatch), "PrefixPlayer"), null);

                        GD.Print("[AiTeammate] Patched CardSelectCmd.FromDeckForEnchantment (Player, EnchantmentModel, int, prefs).");

                    }



                    MethodInfo m2 = AccessTools.Method(cmdType, "FromDeckForEnchantment", new Type[] {

                        typeof(Player), enchType, typeof(int), typeof(Func<CardModel, bool>), prefsType

                    });

                    if (m2 != null)

                    {

                        harmony.Patch(m2, new HarmonyMethod(typeof(FlowPatches.CardSelectFromDeckForEnchantmentPatch), "PrefixPlayerFilter"), null);

                        GD.Print("[AiTeammate] Patched CardSelectCmd.FromDeckForEnchantment (Player, EnchantmentModel, int, filter, prefs).");

                    }



                    MethodInfo m3 = AccessTools.Method(cmdType, "FromDeckForEnchantment", new Type[] {

                        typeof(IReadOnlyList<CardModel>), enchType, typeof(int), prefsType

                    });

                    if (m3 != null)

                    {

                        harmony.Patch(m3, new HarmonyMethod(typeof(FlowPatches.CardSelectFromDeckForEnchantmentPatch), "PrefixCards"), null);

                        GD.Print("[AiTeammate] Patched CardSelectCmd.FromDeckForEnchantment (IReadOnlyList, EnchantmentModel, int, prefs).");

                    }

                }

                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: CardSelectCmd.FromDeckForEnchantment: " + ex.Message); }



                // --- Directive #5.0.9 T1 & T3: Event & Ancient re-enabled with Three Defenses ---

                Type eventSyncType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer");

                if (eventSyncType != null)

                {

                    try

                    {

                        MethodInfo chooseOptionMethod = AccessTools.Method(eventSyncType, "ChooseOptionForEvent", new Type[] { typeof(Player), typeof(int) });

                        if (chooseOptionMethod != null)

                        {

                            harmony.Patch(chooseOptionMethod, null, new HarmonyMethod(typeof(FlowPatches.EventSynchronizerPendingProbePatch), "Postfix"));

                            GD.Print("[AiTeammate] Patched EventSynchronizer.ChooseOptionForEvent (Event Autopilot Probe).");

                        }

                    }

                    catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: EventSynchronizer.ChooseOptionForEvent: " + ex.Message); }



                    try

                    {

                        MethodInfo beginEventMethod = AccessTools.Method(eventSyncType, "BeginEvent");

                        if (beginEventMethod != null)

                        {

                            harmony.Patch(beginEventMethod, null, new HarmonyMethod(typeof(FlowPatches.EventSynchronizerPendingProbePatch), "Postfix"));

                            GD.Print("[AiTeammate] Patched EventSynchronizer.BeginEvent (Event Autopilot Begin Probe).");

                        }

                    }

                    catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: EventSynchronizer.BeginEvent: " + ex.Message); }



                    try

                    {

                        MethodInfo resumeEventsMethod = AccessTools.Method(eventSyncType, "ResumeEvents");

                        if (resumeEventsMethod != null)

                        {

                            harmony.Patch(resumeEventsMethod, null, new HarmonyMethod(typeof(FlowPatches.EventSynchronizerPendingProbePatch), "Postfix"));

                            GD.Print("[AiTeammate] Patched EventSynchronizer.ResumeEvents (Event Autopilot Resume Probe).");

                        }

                    }

                    catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: EventSynchronizer.ResumeEvents: " + ex.Message); }

                }



                Type draEventPatchType = AccessTools.TypeByName("LocalMultiControl.Scripts.Patch.EventSynchronizerPatch");

                if (draEventPatchType != null)

                {

                    try

                    {

                        MethodInfo autoProxyMethod = AccessTools.Method(draEventPatchType, "TryAutoProxyEventChoice");

                        if (autoProxyMethod != null)

                        {

                            harmony.Patch(autoProxyMethod, null, new HarmonyMethod(typeof(FlowPatches.SharedEventAutoProxyPatch), "Postfix"));

                            GD.Print("[AiTeammate] Patched EventSynchronizerPatch.TryAutoProxyEventChoice (Shared Event Proxy).");

                        }

                    }

                    catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: EventSynchronizerPatch.TryAutoProxyEventChoice: " + ex.Message); }

                }



                Type multiRuntimeType = AccessTools.TypeByName("LocalMultiControl.Scripts.Runtime.LocalMultiControlRuntime");

                if (multiRuntimeType != null)

                {

                    try

                    {

                        MethodInfo mReeval = AccessTools.Method(multiRuntimeType, "ReevaluateEndTurnButtonState", new Type[] { typeof(MegaCrit.Sts2.Core.Nodes.Combat.NCombatUi), typeof(MegaCrit.Sts2.Core.Combat.CombatState), typeof(MegaCrit.Sts2.Core.Entities.Players.Player) });

                        if (mReeval != null)

                        {

                            harmony.Patch(mReeval, new HarmonyMethod(typeof(FlowPatches.DraReevaluateEndTurnButtonStatePatch), "Prefix"), new HarmonyMethod(typeof(FlowPatches.DraReevaluateEndTurnButtonStatePatch), "Postfix"));

                            GD.Print("[AiTeammate] Patched LocalMultiControlRuntime.ReevaluateEndTurnButtonState (EndTurn Button Force-Visible).");

                        }

                    }

                    catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: ReevaluateEndTurnButtonState: " + ex.Message); }
            try
            {
                var endTurnBtnType = typeof(MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton);
                var setStateMethod = AccessTools.Method(endTurnBtnType, "SetState");
                if (setStateMethod != null)
                {
                    harmony.Patch(setStateMethod, new HarmonyMethod(typeof(FlowPatches.EndTurnButtonSetStateGuardPatch), "Prefix"), null);
                    GD.Print("[AiTeammate] Patched NEndTurnButton.SetState (Non-play-phase state guard).");
                }
                var refreshEnabledMethod = AccessTools.Method(endTurnBtnType, "RefreshEnabled");
                if (refreshEnabledMethod != null)
                {
                    harmony.Patch(refreshEnabledMethod, null, new HarmonyMethod(typeof(FlowPatches.EndTurnButtonRefreshEnabledPostfixPatch), "Postfix"));
                    GD.Print("[AiTeammate] Patched NEndTurnButton.RefreshEnabled (Play-phase visibility self-healing).");
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate] INIT-FAIL: NEndTurnButton patches: " + ex);
            }




                    try

                    {

                        MethodInfo mEndAll = AccessTools.Method(multiRuntimeType, "TryEndAllPlayersWhenNoCards", new Type[] { typeof(MegaCrit.Sts2.Core.Combat.CombatState), typeof(string) });

                        if (mEndAll != null)

                        {

                            harmony.Patch(mEndAll, new HarmonyMethod(typeof(FlowPatches.DraTryEndAllPlayersWhenNoCardsPatch), "Prefix"), null);

                            GD.Print("[AiTeammate] Patched LocalMultiControlRuntime.TryEndAllPlayersWhenNoCards (EndTurn Auto-Close Block).");

                        }

                    }

                    catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: TryEndAllPlayersWhenNoCards: " + ex.Message); }

                    try
                    {
                        MethodInfo mEventAuto = AccessTools.Method(multiRuntimeType, "TryRunPendingEventAutoSwitch", new Type[] { typeof(string) });
                        if (mEventAuto != null)
                        {
                            harmony.Patch(mEventAuto, new HarmonyMethod(typeof(FlowPatches.DraTryRunPendingEventAutoSwitchPatch), "Prefix"), null);
                            GD.Print("[AiTeammate] Patched LocalMultiControlRuntime.TryRunPendingEventAutoSwitch (Event Auto-Next Suppression).");
                        }
                    }
                    catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: TryRunPendingEventAutoSwitch: " + ex.Message); }

                }



                Type draNEventRoomPatchType = AccessTools.TypeByName("LocalMultiControl.Scripts.Patch.NEventRoomPatch");

                if (draNEventRoomPatchType != null)

                {

                    try

                    {

                        MethodInfo autoSwitchEventMethod = AccessTools.Method(draNEventRoomPatchType, "TryAutoSwitchToNextPendingEvent");

                        if (autoSwitchEventMethod != null)

                        {

                            harmony.Patch(autoSwitchEventMethod, new HarmonyMethod(typeof(FlowPatches.EventAutoSwitchPrefixPatch), "Prefix"), null);

                            GD.Print("[AiTeammate] Patched NEventRoomPatch.TryAutoSwitchToNextPendingEvent (Event Auto-Switch Suppression).");

                        }

                    }

                    catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: NEventRoomPatch.TryAutoSwitchToNextPendingEvent: " + ex.Message); }

                }



                // Directive #5.11 H2: In-combat hand card selection automation (D8)

                try

                {

                    MethodInfo mFromHand = AccessTools.Method(typeof(CardSelectCmd), "FromHand");

                    if (mFromHand != null)

                    {

                        harmony.Patch(mFromHand, new HarmonyMethod(typeof(FlowPatches.InCombatHandSelectPatch), "FromHandPrefix"), null);

                        GD.Print("[AiTeammate] Patched CardSelectCmd.FromHand (In-Combat Hand Autopilot).");

                    }

                }

                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: CardSelectCmd.FromHand: " + ex.Message); }



                try

                {

                    MethodInfo mFromHandDiscard = AccessTools.Method(typeof(CardSelectCmd), "FromHandForDiscard");

                    if (mFromHandDiscard != null)

                    {

                        harmony.Patch(mFromHandDiscard, new HarmonyMethod(typeof(FlowPatches.InCombatHandSelectPatch), "FromHandForDiscardPrefix"), null);

                        GD.Print("[AiTeammate] Patched CardSelectCmd.FromHandForDiscard (In-Combat Hand Discard Autopilot).");

                    }

                }

                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: CardSelectCmd.FromHandForDiscard: " + ex.Message); }



                try

                {

                    MethodInfo mFromHandUpgrade = AccessTools.Method(typeof(CardSelectCmd), "FromHandForUpgrade");

                    if (mFromHandUpgrade != null)

                    {

                        harmony.Patch(mFromHandUpgrade, new HarmonyMethod(typeof(FlowPatches.InCombatHandSelectPatch), "FromHandForUpgradePrefix"), null);

                        GD.Print("[AiTeammate] Patched CardSelectCmd.FromHandForUpgrade (In-Combat Hand Upgrade Autopilot).");

                    }

                }

                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: CardSelectCmd.FromHandForUpgrade: " + ex.Message); }



                try

                {

                    foreach (MethodInfo m in typeof(CardSelectCmd).GetMethods(BindingFlags.Public | BindingFlags.Static))

                    {

                        if (m.Name == "FromCombatPile")

                        {

                            var ps = m.GetParameters();

                            if (ps.Length == 4)

                            {

                                harmony.Patch(m, new HarmonyMethod(typeof(FlowPatches.InCombatHandSelectPatch), "FromCombatPile4Prefix"), null);

                                GD.Print("[AiTeammate] Patched CardSelectCmd.FromCombatPile [4 params] (In-Combat Pile Autopilot).");

                            }

                            else if (ps.Length == 5)

                            {

                                harmony.Patch(m, new HarmonyMethod(typeof(FlowPatches.InCombatHandSelectPatch), "FromCombatPile5Prefix"), null);

                                GD.Print("[AiTeammate] Patched CardSelectCmd.FromCombatPile [5 params] (In-Combat Pile Filtered Autopilot).");

                            }

                        }

                    }

                }

                catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: CardSelectCmd.FromCombatPile: " + ex.Message); }



                // Directive #5.11 H1 & H3: Player choice reserve tracking & submission hard gate

                Type pcsType = AccessTools.TypeByName("MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceSynchronizer");

                if (pcsType != null)

                {

                    try

                    {

                        MethodInfo mReserve = AccessTools.Method(pcsType, "ReserveChoiceId");

                        if (mReserve != null)

                        {

                            harmony.Patch(mReserve, null, new HarmonyMethod(typeof(FlowPatches.PlayerChoiceReservePatch), "Postfix"));

                            GD.Print("[AiTeammate] Patched PlayerChoiceSynchronizer.ReserveChoiceId (Pending Choice Tracker).");

                        }

                    }

                    catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: PlayerChoiceSynchronizer.ReserveChoiceId: " + ex.Message); }



                    try

                    {

                        MethodInfo mSync = AccessTools.Method(pcsType, "SyncLocalChoice");

                        if (mSync != null)

                        {

                            harmony.Patch(mSync, new HarmonyMethod(typeof(FlowPatches.PlayerChoiceSyncLocalPatch), "Prefix"), null);

                            GD.Print("[AiTeammate] Patched PlayerChoiceSynchronizer.SyncLocalChoice (Cross-Owner Hard Gate).");

                        }

                    }

                    catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: PlayerChoiceSynchronizer.SyncLocalChoice: " + ex.Message); }

                }



                // --- Directive #5.0.9 T5: shop MUST NOT be registered (kept frozen) ---

                GD.Print("[AiTeammate] R3-1: non-combat autopilot hooks disabled (event/relic/rest/neow/shop/in-combat-cmd).");

                GD.Print("[AiTeammate] T3: non-card autopilot hooks kept frozen (event/relic/rest/neow/shop/in-combat-cmd).");

                GD.Print("[AiTeammate] T6: non-relic/rest autopilot hooks kept frozen (event/neow/shop).");

                GD.Print("[AiTeammate] T5: non-event autopilot hooks kept frozen (shop/in-combat-cmd).");



                if (!HookedGroups.Contains("flow-coordinator"))

                {

                    
            try
            {
                var restSyncType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.RestSiteSynchronizer");
                if (restSyncType != null)
                {
                    var beginMethod = AccessTools.Method(restSyncType, "BeginRestSite");
                    if (beginMethod != null)
                    {
                        harmony.Patch(beginMethod, null, new HarmonyMethod(typeof(FlowPatches.RestSiteSynchronizerPatches), "BeginRestSitePostfix"));
                        GD.Print("[AiTeammate] Hooked RestSiteSynchronizer.BeginRestSite.");
                    }
                }

                var restRoomType = typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom);
                var readyMethod = AccessTools.Method(restRoomType, "_Ready");
                if (readyMethod != null)
                {
                    harmony.Patch(readyMethod, null, new HarmonyMethod(typeof(FlowPatches.RestSiteSynchronizerPatches), "RoomReadyPostfix"));
                    GD.Print("[AiTeammate] Hooked NRestSiteRoom._Ready.");
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate] INIT-FAIL: RestSiteSynchronizer hooks: " + ex);
            }

            HookedGroups.Add("flow-coordinator");

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate] PatchFlowCoordinator error: " + ex);

                throw;

            }

        }



        public static void Init()

        {

            GD.Print("[AiTeammate] ==========================================");

            GD.Print("[AiTeammate] Build Fingerprint: Directive #5.42 | Built: 2026-11-01");

            GD.Print("[AiTeammate] Initializing CombatSolver 2P Background AI v3.1 (Directive #5.24; button-lineage=#5.22-R2)");
            GD.Print("[AiTeammate][Coord] lifeline-armed mode=independent-timer timeout=20s heartbeat=5s");



            // Directive #4.0: Load settings before any patches or coordinator execution

            LoadSettings();
            GD.Print("[AiTeammate][Coord] fixed-p1-combat-control enabled=" + TurnCoordinator.FixedP1CombatControl.ToString().ToLowerInvariant());

            FlowCoordinator.LoadCardScores();



            // #5.0.6 R3-2: fuse logs go straight to the godot log.

            AutopilotFuse.Sink = new Action<string>(delegate(string fuseLine) { GD.Print(fuseLine); });



            var harmony = new Harmony("com.antigravity.sts2.aiteammate");

            _harmonyInstance = harmony;
            FloatingEndTurnOverlay.Initialize();



            // Preload CombatSolver assembly before running patch groups

            TryPreloadCombatSolver();

            TryPreloadSpireStrategist();



            // Register AssemblyLoad handler in case CombatSolver loads dynamically later

            AppDomain.CurrentDomain.AssemblyLoad += (sender, args) =>

            {

                try

                {

                    if (args.LoadedAssembly != null && args.LoadedAssembly.GetName().Name == "CombatSolver")

                    {

                        GD.Print("[AiTeammate] Detected CombatSolver assembly load via AppDomain event. Hooking now...");

                        EnsureCombatSolverPatched();

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate] AssemblyLoad hook exception: " + ex);

                }

            };



            HookedGroups.Clear();

            FailedGroups.Clear();



            RunPatchGroup("wakuu-suppression", delegate { PatchWakuuRelicAndRuntime(harmony); });

            RunPatchGroup("combat-solver-flow", delegate { PatchCombatSolverAndTurnFlow(harmony); });

            RunPatchGroup("full-auto-guard", delegate { PatchFullAutoGuard(harmony); });

            RunPatchGroup("root-control-suppression", delegate { PatchRootControlSuppression(harmony); });

            RunPatchGroup("choice-suppression", delegate { PatchChoiceSuppression(harmony); });

            RunPatchGroup("deployment-input-lock", delegate { PatchDeploymentInputLock(harmony); });

            RunPatchGroup("turn-coordinator", delegate { PatchTurnCoordinator(harmony); });

            RunPatchGroup("flow-coordinator", delegate { PatchFlowCoordinator(harmony); });

            RunPatchGroup("shop-flow", delegate { PatchShopFlow(harmony); });



            string hookedStr = string.Join(",", HookedGroups.ToArray());

            string failedStr = FailedGroups.Count > 0 ? string.Join(",", FailedGroups.ToArray()) : "none";

            GD.Print("[AiTeammate] INIT-SUMMARY hooked=" + hookedStr + " failed=" + failedStr);

            GD.Print("[AiTeammate] ==========================================");

        }



        private static void PatchWakuuRelicAndRuntime(Harmony harmony)

        {

            var multiRuntimeType = AccessTools.TypeByName("LocalMultiControl.Scripts.Runtime.LocalMultiControlRuntime");

            if (multiRuntimeType != null)

            {

                var grantMethod = AccessTools.Method(multiRuntimeType, "GrantWakuuRelicsAsync");

                if (grantMethod != null)

                {

                    harmony.Patch(grantMethod, new HarmonyMethod(typeof(WakuuPatches), "SkipGrantWakuuRelicsPrefix"));

                    GD.Print("[AiTeammate] Blocked GrantWakuuRelicsAsync (瓦库耳环不会在开局发放).");

                }

            }



            var wakuuRelicType = AccessTools.TypeByName("LocalMultiControl.Scripts.Models.Relics.LocalWakuuStarterRelic");

            if (wakuuRelicType != null)

            {

                var modEnergyMethod = AccessTools.Method(wakuuRelicType, "ModifyMaxEnergy");

                if (modEnergyMethod != null)

                {

                    harmony.Patch(modEnergyMethod, new HarmonyMethod(typeof(WakuuPatches), "DisableModifyMaxEnergyPrefix"));

                    GD.Print("[AiTeammate] Neutralized LocalWakuuStarterRelic.ModifyMaxEnergy (+0 energy, 恢复正常3点能量).");

                }



                var hoverTipsGetter = AccessTools.PropertyGetter(wakuuRelicType, "ExtraHoverTips");

                if (hoverTipsGetter != null)

                {

                    harmony.Patch(hoverTipsGetter, new HarmonyMethod(typeof(WakuuPatches), "SafeExtraHoverTipsPrefix"));

                    GD.Print("[AiTeammate] Patched LocalWakuuStarterRelic.ExtraHoverTips to prevent inspect crash.");

                }

            }



            var wakuuRuntimeType = AccessTools.TypeByName("LocalMultiControl.Scripts.Runtime.LocalWakuuRelicRuntime");

            if (wakuuRuntimeType != null)

            {

                var execMethod = AccessTools.Method(wakuuRuntimeType, "ExecuteBeforePlayPhaseStartAsync");

                if (execMethod != null)

                {

                    harmony.Patch(execMethod, new HarmonyMethod(typeof(WakuuPatches), "DisableWakuuAutoPlayPrefix"));

                    GD.Print("[AiTeammate] Disabled Wakuu left-to-right autoplay.");

                }



                var methods = wakuuRuntimeType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                for (int i = 0; i < methods.Length; i++)

                {

                    if (methods[i].Name == "TryScheduleWatchdog")

                    {

                        harmony.Patch(methods[i], new HarmonyMethod(typeof(WakuuPatches), "DisableWatchdogPrefix"));

                    }

                }

                GD.Print("[AiTeammate] Disabled Wakuu watchdog timers.");

            }



            HookedWakuu = true;

            if (!HookedGroups.Contains("wakuu-suppression")) HookedGroups.Add("wakuu-suppression");

        }



        private static void LogCombatSolverBinding(string label, MethodInfo method)
        {
            try
            {
                if (method == null) return;
                Assembly asm = method.DeclaringType != null ? method.DeclaringType.Assembly : null;
                string alc = "unknown";
                try
                {
                    Type alcType = Type.GetType("System.Runtime.Loader.AssemblyLoadContext, System.Runtime.Loader");
                    MethodInfo getContext = alcType != null ? alcType.GetMethod("GetLoadContext", new Type[] { typeof(Assembly) }) : null;
                    object ctx = getContext != null && asm != null ? getContext.Invoke(null, new object[] { asm }) : null;
                    if (ctx != null) alc = ctx.ToString();
                }
                catch { }
                var names = new List<string>();
                foreach (var p in method.GetParameters()) names.Add(p.ParameterType.AssemblyQualifiedName);
                GD.Print(string.Format("[AiTeammate][Coord] solver-binding label={0} declaring={1} assembly={2} location={3} aqn={4} token={5} static={6} params=[{7}] alc={8}",
                    label, method.DeclaringType != null ? method.DeclaringType.FullName : "unknown",
                    asm != null ? asm.FullName : "unknown", asm != null ? asm.Location : "unknown",
                    method.DeclaringType != null ? method.DeclaringType.AssemblyQualifiedName : "unknown",
                    method.MetadataToken, method.IsStatic, string.Join("|", names.ToArray()), alc));
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][Coord] solver-binding log failed " + label + ": " + ex.Message); }
        }

        private static void PatchCombatSolverAndTurnFlow(Harmony harmony)

        {

            var solverControllerType = AccessTools.TypeByName("CombatSolver.SolverController");

            if (solverControllerType == null)

            {

                TryPreloadCombatSolver();

            TryPreloadSpireStrategist();

                solverControllerType = AccessTools.TypeByName("CombatSolver.SolverController");

            }



            if (solverControllerType != null)

            {

                // Directive #3.9.5: Triple-hooking deployment pipeline (StartFullAutoDeployment, StartDeployment, DeployCurrentTurn)

                var fullAutoMethod = AccessTools.Method(solverControllerType, "StartFullAutoDeployment");

                if (fullAutoMethod != null)

                {

                    LogCombatSolverBinding("StartFullAutoDeployment", fullAutoMethod);

                    harmony.Patch(fullAutoMethod, new HarmonyMethod(typeof(TurnFlowPatches), "StartFullAutoDeploymentPrefix"));

                    GD.Print("[AiTeammate] Hooked SolverController.StartFullAutoDeployment.");

                }



                var startDeployMethod = AccessTools.Method(solverControllerType, "StartDeployment");

                if (startDeployMethod != null)

                {

                    LogCombatSolverBinding("StartDeployment", startDeployMethod);

                    harmony.Patch(startDeployMethod, new HarmonyMethod(typeof(TurnFlowPatches), "StartDeploymentPrefix"));

                    GD.Print("[AiTeammate] Hooked SolverController.StartDeployment (prefix entry empirical hook).");

                }



                var deployMethod = AccessTools.Method(solverControllerType, "DeployCurrentTurn");

                if (deployMethod != null)

                {

                    LogCombatSolverBinding("DeployCurrentTurn", deployMethod);

                    TurnCoordinator.DeployCurrentTurnMethod = deployMethod;

                    harmony.Patch(deployMethod, new HarmonyMethod(typeof(TurnFlowPatches), "DeployCurrentTurnPrefix"));

                var pauseMethod = AccessTools.Method(solverControllerType, "PauseAfterNativeChoiceFailure");
                if (pauseMethod != null)
                {
                    harmony.Patch(pauseMethod, null, new HarmonyMethod(typeof(TurnFlowPatches), "PauseAfterNativeChoiceFailurePostfix"));
                    GD.Print("[AiTeammate] Hooked SolverController.PauseAfterNativeChoiceFailure (native choice failure recovery).");
                }

                    GD.Print("[AiTeammate] Hooked SolverController.DeployCurrentTurn.");

                }



                var completeDeployMethod = AccessTools.Method(solverControllerType, "CompleteDeployment");

                if (completeDeployMethod != null)

                {

                    LogCombatSolverBinding("CompleteDeployment", completeDeployMethod);

                    harmony.Patch(completeDeployMethod, null, new HarmonyMethod(typeof(TurnFlowPatches), "CompleteDeploymentPostfix"));

                    GD.Print("[AiTeammate] Hooked SolverController.CompleteDeployment.");

                }



                var cancelDeployMethod = AccessTools.Method(solverControllerType, "CancelDeployment");

                if (cancelDeployMethod != null)

                {

                    LogCombatSolverBinding("CancelDeployment", cancelDeployMethod);

                    TurnCoordinator.CancelDeploymentMethod = cancelDeployMethod;

                    harmony.Patch(cancelDeployMethod, null, new HarmonyMethod(typeof(TurnFlowPatches), "CompleteDeploymentPostfix"));

                    GD.Print("[AiTeammate] Hooked SolverController.CancelDeployment.");

                }





                TurnCoordinator.DeploymentField = AccessTools.Field(solverControllerType, "_deployment");

                TurnCoordinator.CombatField = AccessTools.Field(solverControllerType, "_combat");



                TurnCoordinator.RequestSearchMethod = TurnCoordinator.FindRequestSearchMethod(solverControllerType);

                if (TurnCoordinator.RequestSearchMethod != null)

                {

                    LogCombatSolverBinding("RequestSearch", TurnCoordinator.RequestSearchMethod);

                    harmony.Patch(TurnCoordinator.RequestSearchMethod, new HarmonyMethod(typeof(TurnFlowPatches), "RequestSearchPrefix"));

                    GD.Print("[AiTeammate] Discovered and Hooked SolverController.RequestSearch: " + TurnCoordinator.RequestSearchMethod + " (static=" + TurnCoordinator.RequestSearchMethod.IsStatic + ", params=" + TurnCoordinator.RequestSearchMethod.GetParameters().Length + ")");

                }



                CombatSolverActuallyPatched = true;

                HookedCombatSolver = true;

                if (!HookedGroups.Contains("combat-solver-flow")) HookedGroups.Add("combat-solver-flow");

            }

            else

            {

                GD.PrintErr("[AiTeammate] CombatSolver.SolverController type not found, will hook upon assembly load.");

            }

        }



        private static void PatchFullAutoGuard(Harmony harmony)

        {

            var sessionType = AccessTools.TypeByName("CombatSolver.SolverCombatSession");

            if (sessionType != null)

            {

                var setFullAutoMethod = AccessTools.Method(sessionType, "set_FullAutoEnabled");

                if (setFullAutoMethod != null)

                {

                    harmony.Patch(setFullAutoMethod, new HarmonyMethod(typeof(FullAutoPatches), "SetFullAutoSessionPrefix"));

                    GD.Print("[AiTeammate] Hooked SolverCombatSession.set_FullAutoEnabled for attribution logging.");

                }

            }



            var solverControllerType = AccessTools.TypeByName("CombatSolver.SolverController");

            if (solverControllerType != null)

            {

                var setFullAutoCtrl = AccessTools.Method(solverControllerType, "SetFullAuto");

                if (setFullAutoCtrl != null)

                {

                    harmony.Patch(setFullAutoCtrl, new HarmonyMethod(typeof(FullAutoPatches), "SetFullAutoControllerPrefix"));

                    GD.Print("[AiTeammate] Hooked SolverController.SetFullAuto for attribution logging.");

                }

            }



            HookedFullAutoGuard = true;

            if (!HookedGroups.Contains("full-auto-guard")) HookedGroups.Add("full-auto-guard");

        }



        private static void PatchRootControlSuppression(Harmony harmony)

        {

            var multiRuntimeType = AccessTools.TypeByName("LocalMultiControl.Scripts.Runtime.LocalMultiControlRuntime");

            if (multiRuntimeType != null)

            {

                var alignMethod = AccessTools.Method(multiRuntimeType, "AlignContextForActionOwner", new Type[] { typeof(ulong), typeof(string) })

                    ?? AccessTools.Method(multiRuntimeType, "AlignContextForActionOwner");

                if (alignMethod != null)

                {

                    harmony.Patch(alignMethod,

                        new HarmonyMethod(typeof(DraControlSwitchPatches), "AlignContextForActionOwnerPrefix"),

                        null,

                        null,

                        null);

                    GD.Print("[AiTeammate] Hooked LocalMultiControlRuntime.AlignContextForActionOwner (root suppression).");

                }



                var switchMethod = AccessTools.Method(multiRuntimeType, "SwitchControlledPlayerTo", new Type[] { typeof(ulong), typeof(string) })

                    ?? AccessTools.Method(multiRuntimeType, "SwitchControlledPlayerTo");

                if (switchMethod != null)

                {

                    harmony.Patch(switchMethod,

                        new HarmonyMethod(typeof(DraControlSwitchPatches), "SwitchControlledPlayerToPrefix"),

                        null,

                        null,

                        new HarmonyMethod(typeof(DraControlSwitchPatches), "SwitchControlledPlayerToFinalizer"));

                    GD.Print("[AiTeammate] Hooked LocalMultiControlRuntime.SwitchControlledPlayerTo (root suppression).");

                }



                var applyMethod = AccessTools.Method(multiRuntimeType, "ApplyControlContext", new Type[] { typeof(string) })

                    ?? AccessTools.Method(multiRuntimeType, "ApplyControlContext");

                if (applyMethod != null)

                {

                    harmony.Patch(applyMethod,

                        new HarmonyMethod(typeof(DraControlSwitchPatches), "ApplyControlContextPrefix"),

                        new HarmonyMethod(typeof(DraControlSwitchPatches), "ApplyControlContextPostfix"),

                        null,

                        new HarmonyMethod(typeof(DraControlSwitchPatches), "ApplyControlContextFinalizer"));

                    GD.Print("[AiTeammate] Hooked LocalMultiControlRuntime.ApplyControlContext (root suppression).");

                }



                var refreshCombatUi = AccessTools.Method(multiRuntimeType, "RefreshCombatUiForControlledPlayer");

                if (refreshCombatUi != null)

                {

                    harmony.Patch(refreshCombatUi, new HarmonyMethod(typeof(UiSuppressionPatches), "RefreshCombatUiPrefix"));

                    GD.Print("[AiTeammate] Hooked LocalMultiControlRuntime.RefreshCombatUiForControlledPlayer.");

                }



                var voidRefreshMethods = new string[]

                {

                    "RefreshTopBarForControlledPlayer",

                    "RefreshTopBarDeck",

                    "RefreshDeckViewForControlledPlayer",

                    "RefreshRestSiteForControlledPlayer",

                    "RefreshEventRoomForControlledPlayer",

                    "RefreshCombatEnergyForCurrentPlayer",

                    "RefreshSharedTopBarForCombat",

                    "RefreshCombatEnergyUi",

                    "ReevaluateEndTurnButtonState",

                    "EnsureTreasureCursorVisibleAfterSwitch"

                };



                for (int i = 0; i < voidRefreshMethods.Length; i++)

                {

                    var m = AccessTools.Method(multiRuntimeType, voidRefreshMethods[i]);

                    if (m != null)

                    {

                        harmony.Patch(m, new HarmonyMethod(typeof(UiSuppressionPatches), "SuppressVoidPrefix"));

                        GD.Print("[AiTeammate] Hooked LocalMultiControlRuntime." + voidRefreshMethods[i] + ".");

                    }

                }

            }



            var merchantType = AccessTools.TypeByName("LocalMultiControl.Scripts.Runtime.LocalMerchantInventoryRuntime");

            if (merchantType != null)

            {

                var mShop = AccessTools.Method(merchantType, "RefreshShopRoomForPlayer");

                if (mShop != null)

                {

                    harmony.Patch(mShop, new HarmonyMethod(typeof(UiSuppressionPatches), "SuppressVoidPrefix"));

                    GD.Print("[AiTeammate] Hooked LocalMerchantInventoryRuntime.RefreshShopRoomForPlayer.");

                }

            }



            HookedRootControl = true;

            if (!HookedGroups.Contains("root-control-suppression")) HookedGroups.Add("root-control-suppression");

        }



        private static void PatchChoiceSuppression(Harmony harmony)

        {

            var choicePatchType = AccessTools.TypeByName("LocalMultiControl.Scripts.Patch.CardSelectForegroundSwitchPatch");

            if (choicePatchType != null)

            {

                var ensureFgMethod = AccessTools.Method(choicePatchType, "EnsureForegroundForCombatChoice", new Type[] { typeof(Player), typeof(string) })

                    ?? AccessTools.Method(choicePatchType, "EnsureForegroundForCombatChoice");

                if (ensureFgMethod != null)

                {

                    harmony.Patch(ensureFgMethod, new HarmonyMethod(typeof(UiSuppressionPatches), "EnsureForegroundForCombatChoicePrefix"));

                    GD.Print("[AiTeammate] Hooked CardSelectForegroundSwitchPatch.EnsureForegroundForCombatChoice for choice foreground suppression.");

                }

            }



            var cardSelectCmdType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.CardSelectCmd") ?? typeof(CardSelectCmd);

            if (cardSelectCmdType != null)

            {

                var shouldSelectLocal = AccessTools.Method(cardSelectCmdType, "ShouldSelectLocalCard");

                if (shouldSelectLocal != null)

                {

                    harmony.Patch(shouldSelectLocal, null, new HarmonyMethod(typeof(ChoiceSuppressionPatches), "ShouldSelectLocalCardPostfix") { priority = Priority.Last });

                    GD.Print("[AiTeammate] Hooked CardSelectCmd.ShouldSelectLocalCard (Insurance A: force false for P2).");

                }

            }



            var handType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand") ?? typeof(NPlayerHand);

            if (handType != null)

            {

                var selectCardsMethod = AccessTools.Method(handType, "SelectCards");

                if (selectCardsMethod != null)

                {

                    harmony.Patch(selectCardsMethod, new HarmonyMethod(typeof(ChoiceSuppressionPatches), "PlayerHandSelectCardsPrefix"));

                    GD.Print("[AiTeammate] Hooked NPlayerHand.SelectCards (Insurance B: redirect P2 candidate pool to owner's hand).");

                    HookedChoiceCandidatePool = true;

                    if (!HookedGroups.Contains("choice-candidate-pool")) HookedGroups.Add("choice-candidate-pool");

                }

            }



            // Directive #3.9.5: Hook NOverlayStack.Push (Overlay Master Gate)

            var overlayStackType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack");

            if (overlayStackType != null)

            {

                var pushMethod = AccessTools.Method(overlayStackType, "Push");

                if (pushMethod != null)

                {

                    harmony.Patch(pushMethod, new HarmonyMethod(typeof(ChoiceSuppressionPatches), "OverlayStackPushPrefix"));

                    GD.Print("[AiTeammate] Hooked NOverlayStack.Push (Overlay Master Gate).");

                }

            }



            HookedChoiceSuppression = true;

            if (!HookedGroups.Contains("choice-suppression")) HookedGroups.Add("choice-suppression");

        }



        private static void PatchDeploymentInputLock(Harmony harmony)

        {

            var handType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand") ?? typeof(NPlayerHand);

            if (handType != null)

            {

                var canPlayMethod = AccessTools.Method(handType, "CanPlayCards");

                if (canPlayMethod != null)

                {

                    harmony.Patch(canPlayMethod, new HarmonyMethod(typeof(UiSuppressionPatches), "CanPlayCardsPrefix"));

                    GD.Print("[AiTeammate] Hooked NPlayerHand.CanPlayCards for deployment input lock.");

                }

                // Directive #5.36 T2.2: Hard gate preventing P2 cards from entering NPlayerHand
                try
                {
                    foreach (var m in AccessTools.GetDeclaredMethods(handType))
                    {
                        if (m.Name == "Add" || m.Name == "AddCardHolder")
                        {
                            harmony.Patch(m, new HarmonyMethod(typeof(CoordinatorPatches), "NPlayerHandAddPrefix"));
                        }
                    }
                    GD.Print("[AiTeammate][Coord] Hooked NPlayerHand.Add & AddCardHolder (P2 card hand hard gate #5.36).");
                }
                catch (Exception exHand) { GD.PrintErr("[AiTeammate] INIT-FAIL: NPlayerHand.Add hooks: " + exHand.Message); }

                // Directive #5.42 Task 3: Hook NCardPlayQueue.RemoveCardFromQueueForCancellation
                try
                {
                    var playQueueType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCardPlayQueue");
                    if (playQueueType != null)
                    {
                        foreach (var m in AccessTools.GetDeclaredMethods(playQueueType))
                        {
                            if (m.Name == "RemoveCardFromQueueForCancellation")
                            {
                                harmony.Patch(m, new HarmonyMethod(typeof(CoordinatorPatches), "RemoveCardFromQueueForCancellationPrefix"));
                            }
                        }
                        GD.Print("[AiTeammate][Coord] Hooked NCardPlayQueue.RemoveCardFromQueueForCancellation (#5.42).");
                    }
                }
                catch (Exception exQueue) { GD.PrintErr("[AiTeammate] INIT-FAIL: NCardPlayQueue hooks: " + exQueue.Message); }

            }



            HookedDeploymentInputLock = true;

            if (!HookedGroups.Contains("deployment-input-lock")) HookedGroups.Add("deployment-input-lock");

        }



        private static void PatchTurnCoordinator(Harmony harmony)

        {

            var onEndedLocally = AccessTools.Method(typeof(CombatManager), "OnEndedTurnLocally");

            if (onEndedLocally != null)

            {

                harmony.Patch(onEndedLocally, 

                    new HarmonyMethod(typeof(CoordinatorPatches), "OnEndedTurnLocallyPrefix"),

                    new HarmonyMethod(typeof(TurnFlowPatches), "OnEndedTurnLocallyPostfix"));

                GD.Print("[AiTeammate][Coord] Hooked CombatManager.OnEndedTurnLocally (Prefix & Postfix).");

            }



            var setupMethod = AccessTools.Method(typeof(CombatManager), "SetupPlayerTurn");

            if (setupMethod != null)

            {

                harmony.Patch(setupMethod, null, new HarmonyMethod(typeof(CoordinatorPatches), "SetupPlayerTurnPostfix"));

                GD.Print("[AiTeammate][Coord] Hooked CombatManager.SetupPlayerTurn.");

            }



            var endCardEffect = AccessTools.Method(typeof(CombatManager), "EndCardOrPotionEffect");

            if (endCardEffect != null)

            {

                harmony.Patch(endCardEffect, null, new HarmonyMethod(typeof(CoordinatorPatches), "EndCardOrPotionEffectPostfix"));

                GD.Print("[AiTeammate][Coord] Hooked CombatManager.EndCardOrPotionEffect.");

            }



            var necType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NEnergyCounter");

            if (necType != null)

            {

                var procMethod = AccessTools.Method(necType, "_Process");

                if (procMethod != null)

                {

                    harmony.Patch(procMethod, null, new HarmonyMethod(typeof(CoordinatorPatches), "EnergyCounterProcessPostfix"));

                    GD.Print("[AiTeammate][Coord] Hooked NEnergyCounter._Process for 500ms watchdog.");

                }

            }



            var sdType = AccessTools.TypeByName("CombatSolver.SolverDispatcher");

            if (sdType != null)

            {

                var procMethod = AccessTools.Method(sdType, "_Process");

                if (procMethod != null)

                {

                    harmony.Patch(procMethod, null, new HarmonyMethod(typeof(CoordinatorPatches), "EnergyCounterProcessPostfix"));

                    GD.Print("[AiTeammate][Coord] Hooked SolverDispatcher._Process.");

                }

            }



            // Directive #5.33: PackChoicePatch scanning removed in favor of CardSelectCmd.FromChooseABundleScreen
            // (Old Task<int> / PrefixIndex scan deprecated and disabled)

            try {
                Type bt = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton");
                bool inputHooked = false;
                if (bt != null) foreach (MethodInfo mm in bt.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                    string n = mm.Name.ToLowerInvariant();
                    if ((n.IndexOf("press", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("click", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("release", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("refreshenabled", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("setstate", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("callreleaselogic", StringComparison.OrdinalIgnoreCase) >= 0) && mm.ReturnType == typeof(void)) {
                        harmony.Patch(mm, new HarmonyMethod(typeof(EndTurnHandlerFallbackPatch), "ButtonPrefix"));
                        GD.Print("[AiTeammate][R1] Hooked end-turn button method " + mm.DeclaringType.FullName + "." + mm.Name);
                    }
                    if (n == "_guiinput" || n == "guiinput" || n == "_input" || n == "_unhandledinput") {
                        harmony.Patch(mm, new HarmonyMethod(typeof(EndTurnR1Telemetry), "InputLayerPrefix"));
                        inputHooked = true;
                        GD.Print("[AiTeammate][R1] Hooked end-turn input layer " + mm.DeclaringType.FullName + "." + mm.Name);
                    }
                }
                if (!inputHooked) GD.Print("[AiTeammate][R1] endturn-input-layer-hook none on NEndTurnButton");
                Type ct = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCombatUi");
                if (ct != null) foreach (MethodInfo mm in ct.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                    string n = mm.Name.ToLowerInvariant();
                    if (n == "_input" || n == "_unhandledinput" || n == "guiinput") {
                        harmony.Patch(mm, new HarmonyMethod(typeof(EndTurnHandlerFallbackPatch), "InputPrefix"));
                        GD.Print("[AiTeammate][Coord] Hooked F9 end-turn input " + mm.Name);
                    }
                }
                GD.Print("[AiTeammate][Coord] end-turn hotkey F9 enabled for active P1");
            } catch (Exception ex) { GD.PrintErr("[AiTeammate][Coord] end-turn handler/hotkey hook fail-open: " + ex.Message); }

            // Directive #5.34 T0.5: Hook NCardPlayQueue.OnActionEnqueued
            try
            {
                var cpqType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCardPlayQueue");
                if (cpqType != null)
                {
                    var oae = AccessTools.Method(cpqType, "OnActionEnqueued", new Type[] { typeof(MegaCrit.Sts2.Core.GameActions.GameAction) });
                    if (oae != null)
                    {
                        harmony.Patch(oae,
                            new HarmonyMethod(typeof(CoordinatorPatches), "CardPlayQueueOnActionEnqueuedPrefix"),
                            null, null,
                            new HarmonyMethod(typeof(CoordinatorPatches), "CardPlayQueueOnActionEnqueuedFinalizer"));
                        GD.Print("[AiTeammate][Coord] Hooked NCardPlayQueue.OnActionEnqueued (UI null guard #5.34).");
                    }
                }
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: NCardPlayQueue.OnActionEnqueued: " + ex.Message); }

            // Directive #5.34 T0.5: Hook EnqueueAction & EnqueueWithoutSynchronizing for backend confirmation
            try
            {
                var aqsyncType = typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSynchronizer);
                var enqAct = AccessTools.Method(aqsyncType, "EnqueueAction", new Type[] { typeof(MegaCrit.Sts2.Core.GameActions.GameAction), typeof(ulong) });
                if (enqAct != null)
                {
                    harmony.Patch(enqAct, null, new HarmonyMethod(typeof(CoordinatorPatches), "BackendEnqueueConfirmedPostfix"));
                    GD.Print("[AiTeammate][Coord] Hooked ActionQueueSynchronizer.EnqueueAction (backend confirmation #5.34).");
                }

                var aqsetType = typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSet);
                var enqWithout = AccessTools.Method(aqsetType, "EnqueueWithoutSynchronizing", new Type[] { typeof(MegaCrit.Sts2.Core.GameActions.GameAction) });
                if (enqWithout != null)
                {
                    harmony.Patch(enqWithout, null, new HarmonyMethod(typeof(CoordinatorPatches), "BackendEnqueueConfirmedPostfix"));
                    GD.Print("[AiTeammate][Coord] Hooked ActionQueueSet.EnqueueWithoutSynchronizing (backend confirmation #5.34).");
                }
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: BackendEnqueueConfirmed: " + ex.Message); }

            // Directive #5.34 T1: Hook CardPileCmd.Add overloads and DrawInternal
            try
            {
                foreach (var m in AccessTools.GetDeclaredMethods(typeof(CardPileCmd)))
                {
                    if (m.Name == "Add")
                    {
                        harmony.Patch(m,
                            new HarmonyMethod(typeof(CoordinatorPatches), "CardPileAddPrefix"),
                            null, null,
                            new HarmonyMethod(typeof(CoordinatorPatches), "CardPileAddFinalizer"));
                    }
                }
                GD.Print("[AiTeammate][Coord] Hooked CardPileCmd.Add overloads (P2 hand transition guard #5.34).");

                var drawInt = AccessTools.Method(typeof(CardPileCmd), "DrawInternal");
                if (drawInt != null)
                {
                    harmony.Patch(drawInt,
                        new HarmonyMethod(typeof(CoordinatorPatches), "DrawInternalPrefix"),
                        null, null,
                        new HarmonyMethod(typeof(CoordinatorPatches), "DrawInternalFinalizer"));
                    GD.Print("[AiTeammate][Coord] Hooked CardPileCmd.DrawInternal (P2 draw transition guard #5.34).");
                }
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: CardPileCmd hooks: " + ex.Message); }

            // Directive #5.34 T1: Hook LiquidMemories.OnUse and UsePotionAction
            try
            {
                var lmType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Potions.LiquidMemories");
                if (lmType != null)
                {
                    var onUse = AccessTools.Method(lmType, "OnUse");
                    if (onUse != null)
                    {
                        harmony.Patch(onUse,
                            new HarmonyMethod(typeof(CoordinatorPatches), "LiquidMemoriesOnUsePrefix"),
                            null, null,
                            new HarmonyMethod(typeof(CoordinatorPatches), "LiquidMemoriesOnUseFinalizer"));
                        GD.Print("[AiTeammate][Coord] Hooked LiquidMemories.OnUse (potion transition guard #5.34).");
                    }
                }

                var upaType = typeof(MegaCrit.Sts2.Core.GameActions.UsePotionAction);
                var upaExec = AccessTools.Method(upaType, "ExecuteAction");
                if (upaExec != null)
                {
                    harmony.Patch(upaExec,
                        new HarmonyMethod(typeof(CoordinatorPatches), "UsePotionActionPrefix"),
                        null, null,
                        new HarmonyMethod(typeof(CoordinatorPatches), "UsePotionActionFinalizer"));
                    GD.Print("[AiTeammate][Coord] Hooked UsePotionAction.ExecuteAction (potion transition guard #5.34).");
                }
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate] INIT-FAIL: Potion hooks: " + ex.Message); }

            try { var rq=AccessTools.Method(typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSynchronizer),"RequestEnqueue",new Type[]{typeof(MegaCrit.Sts2.Core.GameActions.GameAction)}); if(rq!=null){ harmony.Patch(rq,new HarmonyMethod(typeof(ActionQueueOwnerPatch),"Prefix")); GD.Print("[AiTeammate][Coord] Hooked ActionQueueSynchronizer.RequestEnqueue (owner hard gate #5.18)."); } } catch(Exception ex){ GD.PrintErr("[AiTeammate] INIT-FAIL: ActionQueueSynchronizer.RequestEnqueue: "+ex.Message); }

            var setupCombatMethod = AccessTools.Method(typeof(CombatManager), "SetUpCombat");

            if (setupCombatMethod != null)

            {

                harmony.Patch(setupCombatMethod, null, new HarmonyMethod(typeof(CoordinatorPatches), "SetUpCombatPostfix"));

                GD.Print("[AiTeammate][Coord] Hooked CombatManager.SetUpCombat.");

            }



            // Directive #5.0.10 T1: Hook DRA defer rebuild methods for transition timing

            try

            {

                Type potionPatchType = AccessTools.TypeByName("LocalMultiControl.Scripts.Patch.NPotionContainerPatch");

                if (potionPatchType != null)

                {

                    MethodInfo mPotion = AccessTools.Method(potionPatchType, "TryBindPotionContainerToPlayer");

                    if (mPotion != null)

                    {

                        harmony.Patch(mPotion,

                            new HarmonyMethod(typeof(TimingPatches), "PotionRebuildPrefix"),

                            new HarmonyMethod(typeof(TimingPatches), "PotionRebuildPostfix"));

                        GD.Print("[AiTeammate] Patched NPotionContainerPatch.TryBindPotionContainerToPlayer (Timing).");

                    }

                }

            }

            catch (Exception ex) { GD.PrintErr("[AiTeammate] Timing NPotionContainerPatch: " + ex.Message); }



            try

            {

                Type relicPatchType = AccessTools.TypeByName("LocalMultiControl.Scripts.Patch.NRelicInventoryPatch");

                if (relicPatchType != null)

                {

                    MethodInfo mRelic = AccessTools.Method(relicPatchType, "TryRebuildRelicInventoryToPlayer");

                    if (mRelic != null)

                    {

                        harmony.Patch(mRelic,

                            new HarmonyMethod(typeof(TimingPatches), "RelicRebuildPrefix"),

                            new HarmonyMethod(typeof(TimingPatches), "RelicRebuildPostfix"));

                        GD.Print("[AiTeammate] Patched NRelicInventoryPatch.TryRebuildRelicInventoryToPlayer (Timing).");

                    }

                }

            }

            catch (Exception ex) { GD.PrintErr("[AiTeammate] Timing NRelicInventoryPatch: " + ex.Message); }



            try

            {

                Type multiRuntimeType = AccessTools.TypeByName("LocalMultiControl.Scripts.Runtime.LocalMultiControlRuntime");

                if (multiRuntimeType != null)

                {

                    MethodInfo mTopBar = AccessTools.Method(multiRuntimeType, "RefreshTopBarForControlledPlayer");

                    if (mTopBar != null)

                    {

                        harmony.Patch(mTopBar,

                            new HarmonyMethod(typeof(TimingPatches), "TopBarRebuildPrefix"),

                            new HarmonyMethod(typeof(TimingPatches), "TopBarRebuildPostfix"));

                        GD.Print("[AiTeammate] Patched LocalMultiControlRuntime.RefreshTopBarForControlledPlayer (Timing).");

                    }

                }

            }

            catch (Exception ex) { GD.PrintErr("[AiTeammate] Timing RefreshTopBar: " + ex.Message); }



            // Directive #3.9.1: Hook PlayCardAction to record LastActionExecutedTime

            var playCardActionType = typeof(MegaCrit.Sts2.Core.GameActions.PlayCardAction);

            var execMethod = AccessTools.Method(playCardActionType, "ExecuteAction");

            if (execMethod != null)

            {

                harmony.Patch(execMethod, null, new HarmonyMethod(typeof(CoordinatorPatches), "PlayCardActionPostfix"));

                GD.Print("[AiTeammate][Coord] Hooked PlayCardAction.ExecuteAction.");

            }



            // Directive #3.9.4: Hook EndPlayerTurnAction to intercept and delay P2 end turn (Hold)

            var endTurnActionType = typeof(MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction);

            var endTurnExecMethod = AccessTools.Method(endTurnActionType, "ExecuteAction");

            if (endTurnExecMethod != null)

            {

                harmony.Patch(endTurnExecMethod,
                    new HarmonyMethod(typeof(CoordinatorPatches), "EndPlayerTurnActionPrefix"),
                    new HarmonyMethod(typeof(CoordinatorPatches), "EndPlayerTurnActionPostfix"));

                GD.Print("[AiTeammate][Coord] Hooked EndPlayerTurnAction.ExecuteAction (Hold).");

            }



            // Directive #5.13 T2: Action execution window nail & attribution guard

            try

            {

                var gameActionType = typeof(MegaCrit.Sts2.Core.GameActions.GameAction);

                var actionExecMethod = AccessTools.Method(gameActionType, "Execute");

                if (actionExecMethod != null)

                {

                    harmony.Patch(actionExecMethod, new HarmonyMethod(typeof(AttributionPatches.ActionExecutionWindowPatch), "Prefix"), new HarmonyMethod(typeof(AttributionPatches.ActionExecutionWindowPatch), "Postfix"));

                    GD.Print("[AiTeammate][Attribution] Patched GameAction.Execute (execution window nail & continue).");

                }



                var actionExecutorType = typeof(MegaCrit.Sts2.Core.GameActions.ActionExecutor);

                var afterFinishedMethod = AccessTools.Method(actionExecutorType, "AfterActionFinished");

                if (afterFinishedMethod != null)

                {

                    harmony.Patch(afterFinishedMethod, null, new HarmonyMethod(typeof(AttributionPatches.ActionExecutionWindowExitPatch), "Postfix"));

                    GD.Print("[AiTeammate][Attribution] Patched ActionExecutor.AfterActionFinished (execution window exit).");

                }



                var pcaType = typeof(MegaCrit.Sts2.Core.GameActions.PlayCardAction);

                var pcaExecMethod = AccessTools.Method(pcaType, "ExecuteAction");

                if (pcaExecMethod != null)

                {

                    harmony.Patch(pcaExecMethod, new HarmonyMethod(typeof(AttributionPatches.PlayCardActionAttributionPatch), "Prefix"), null);

                    GD.Print("[AiTeammate][Attribution] Patched PlayCardAction.ExecuteAction (submission target guard).");

                }



                var powerCmdType = typeof(MegaCrit.Sts2.Core.Commands.PowerCmd);

                var powerApplyMethod = AccessTools.Method(powerCmdType, "Apply", new Type[] {

                    typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext),

                    typeof(MegaCrit.Sts2.Core.Models.PowerModel),

                    typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature),

                    typeof(decimal),

                    typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature),

                    typeof(MegaCrit.Sts2.Core.Models.CardModel),

                    typeof(bool)

                });

                if (powerApplyMethod != null)

                {

                    harmony.Patch(powerApplyMethod, new HarmonyMethod(typeof(AttributionPatches.PowerAttributionPatch), "Prefix"), null);

                    GD.Print("[AiTeammate][Attribution] Patched PowerCmd.Apply (power attribution guard).");

                }



                var creatureCmdType = typeof(MegaCrit.Sts2.Core.Commands.CreatureCmd);

                var gainBlockMethod1 = AccessTools.Method(creatureCmdType, "GainBlock", new Type[] {

                    typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature),

                    typeof(MegaCrit.Sts2.Core.Localization.DynamicVars.BlockVar),

                    typeof(MegaCrit.Sts2.Core.Entities.Cards.CardPlay),

                    typeof(bool)

                });

                if (gainBlockMethod1 != null)

                {

                    harmony.Patch(gainBlockMethod1, new HarmonyMethod(typeof(AttributionPatches.BlockAttributionPatch), "PrefixBlockVar"), null);

                    GD.Print("[AiTeammate][Attribution] Patched CreatureCmd.GainBlock [BlockVar] (block attribution guard).");

                }



                var gainBlockMethod2 = AccessTools.Method(creatureCmdType, "GainBlock", new Type[] {

                    typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature),

                    typeof(decimal),

                    typeof(MegaCrit.Sts2.Core.ValueProps.ValueProp),

                    typeof(MegaCrit.Sts2.Core.Entities.Cards.CardPlay),

                    typeof(bool)

                });

                if (gainBlockMethod2 != null)

                {

                    harmony.Patch(gainBlockMethod2, new HarmonyMethod(typeof(AttributionPatches.BlockAttributionPatch), "PrefixDecimal"), null);

                    GD.Print("[AiTeammate][Attribution] Patched CreatureCmd.GainBlock [Decimal] (block attribution guard).");

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate] INIT-FAIL: Attribution guard patches: " + ex);

            }



            // Directive #5.0.5 Task 0 (D2) & R2-1: Hook CardPileCmd.GetTweenForCardsChangingPiles with explicit parameter disambiguation

            try

            {

                MethodInfo getTweenMethod = AccessTools.Method(

                    typeof(CardPileCmd),

                    "GetTweenForCardsChangingPiles",

                    new Type[] { typeof(IEnumerable<CardPileAddResult>), typeof(bool) }

                );

                if (getTweenMethod == null)

                {

                    foreach (var m in AccessTools.GetDeclaredMethods(typeof(CardPileCmd)))

                    {

                        if (m.Name == "GetTweenForCardsChangingPiles")

                        {

                            var ps = m.GetParameters();

                            if (ps.Length == 2 && ps[1].ParameterType == typeof(bool))

                            {

                                getTweenMethod = m;

                                break;

                            }

                        }

                    }

                }

                if (getTweenMethod != null)

                {

                    harmony.Patch(getTweenMethod, new HarmonyMethod(typeof(CoordinatorPatches), "GetTweenForCardsChangingPilesPrefix"), null, null, new HarmonyMethod(typeof(CoordinatorPatches), "GetTweenForCardsChangingPilesFinalizer"));

                    GD.Print("[AiTeammate][Coord] Hooked CardPileCmd.GetTweenForCardsChangingPiles (Draw Refresh LocalContext Alignment).");

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate] INIT-FAIL: CardPileCmd.GetTweenForCardsChangingPiles: " + ex.Message);

            }



            HookedTurnCoordinator = true;

            HookedAutonomousGatekeeper = true;

            if (!HookedGroups.Contains("turn-coordinator")) HookedGroups.Add("turn-coordinator");

            if (!HookedGroups.Contains("autonomous-gatekeeper")) HookedGroups.Add("autonomous-gatekeeper");

        }

    }



    public static class CoordinatorPatches

    {

        public static bool EndPlayerTurnActionPrefix(MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction __instance, ref Task __result)

        {

            try

            {
                // #5.26: keep the solver's empty-plan EndPlayerTurnAction behind
                // the one-shot retry/fallback, but never block the same turn forever.
                if (__instance != null && __instance.OwnerId == TurnCoordinator.GetPlayer2NetId() &&
                    TurnCoordinator.P2FallbackActionEnqueuedThisTurn && !TurnCoordinator.P2FallbackActionExecutedThisTurn)
                {
                    if (!TurnCoordinator.P2FallbackEndTurnBlockUsedThisTurn)
                    {
                        TurnCoordinator.P2FallbackEndTurnBlockUsedThisTurn = true;
                        __result = Task.CompletedTask;
                        GD.Print("[AiTeammate][Coord] p2-end-turn-blocked reason=fallback-pending-once turn=" + TurnCoordinator.TurnNumber);
                        return false;
                    }
                    TurnCoordinator.P2TurnEndReason = "fallback-not-executed-fail-open";
                    TurnCoordinator.P2ZeroActionProtectionFinalizedThisTurn = true;
                    GD.PrintErr("[AiTeammate][Coord] p2-end-turn-fail-open reason=fallback-not-executed-after-one-block turn=" + TurnCoordinator.TurnNumber);
                }

                if (__instance != null && __instance.OwnerId == TurnCoordinator.GetPlayer2NetId() &&
                    !TurnCoordinator.P2ZeroActionProtectionFinalizedThisTurn && TurnCoordinator.P2PlayedCardsThisTurn == 0)
                {
                    Player p2ForGuard = FlowCoordinator.GetPlayer2();
                    int knownActionCount = TurnCoordinator.P2DeployStartActionCountThisTurn >= 0 ? TurnCoordinator.P2DeployStartActionCountThisTurn :
                        (TurnCoordinator.P2FullAutoActionCountThisTurn >= 0 ? TurnCoordinator.P2FullAutoActionCountThisTurn : TurnCoordinator.P2SolverActionCountThisTurn);
                    bool hasAttack;
                    bool hasPlayable = TurnCoordinator.P2HasUsableCard(p2ForGuard, out hasAttack);
                    int energy = p2ForGuard != null && p2ForGuard.PlayerCombatState != null ? p2ForGuard.PlayerCombatState.Energy : 0;
                    if (energy <= 0 || !hasPlayable)
                    {
                        TurnCoordinator.P2TurnEndReason = energy <= 0 ? "zero-energy" : "no-playable-card";
                        TurnCoordinator.P2ZeroActionProtectionFinalizedThisTurn = true;
                        GD.Print("[AiTeammate][Coord] p2-zero-action-legal turn=" + TurnCoordinator.TurnNumber + " reason=" + TurnCoordinator.P2TurnEndReason + " energy=" + energy);
                    }
                    else if (knownActionCount == 0)
                    {
                        if (!TurnCoordinator.P2ZeroActionRetryUsedThisTurn)
                        {
                            TurnCoordinator.P2ZeroActionRetryUsedThisTurn = true;
                            TurnCoordinator.HasRetriedEmptyPlanThisTurn = true;
                            TurnCoordinator.P2TurnEndReason = "zero-action-retry";
                            TurnCoordinator.RequestRecalculation(CombatManager.Instance != null ? CombatManager.Instance.DebugOnlyGetState() : null, "empty-plan-retry");
                            __result = Task.CompletedTask;
                            GD.Print("[AiTeammate][Coord] p2-end-turn-blocked reason=zero-action-retry turn=" + TurnCoordinator.TurnNumber + " known-action-count=0");
                            return false;
                        }
                        if (p2ForGuard != null && TurnCoordinator.TryFallbackGreedy(p2ForGuard))
                        {
                            TurnCoordinator.P2TurnEndReason = "fallback-greedy";
                            __result = Task.CompletedTask;
                            GD.Print("[AiTeammate][Coord] p2-end-turn-blocked reason=fallback-enqueued turn=" + TurnCoordinator.TurnNumber);
                            return false;
                        }
                        TurnCoordinator.P2TurnEndReason = "fallback-failed-fail-open";
                        TurnCoordinator.P2ZeroActionProtectionFinalizedThisTurn = true;
                        GD.PrintErr("[AiTeammate][Coord] p2-zero-action-protection-failed turn=" + TurnCoordinator.TurnNumber + " fail-open=true");
                    }
                    else if (knownActionCount < 0)
                    {
                        GD.PrintErr("[AiTeammate][Coord] p2-deploy-hook-missed turn=" + TurnCoordinator.TurnNumber + " reason=end-turn-with-unknown-plan-count");
                    }
                }

                // Directive #4.0: Hold switch gate - if Hold is disabled, pass through directly (直通)

                if (!TurnCoordinator.HoldEnabled) return true;

                if (!TurnCoordinator.IsInCombat()) return true;



                var cm = CombatManager.Instance;

                if (cm == null) return true;

                var state = cm.DebugOnlyGetState();

                if (state == null || state.Players == null || state.Players.Count < 2) return true;



                var player1 = state.Players[0];

                var player2 = state.Players[state.Players.Count - 1];

                if (player1 == null || player2 == null) return true;

                if (player1.Creature == null || !player1.Creature.IsAlive) return true;



                // Check if action belongs to player2

                ulong ownerId = 0;

                try { ownerId = __instance.OwnerId; } catch { }

                if (ownerId != player2.NetId)

                {

                    return true;

                }



                // If Hold is already released this turn, allow it to execute!

                if (TurnCoordinator.HasReleasedHoldThisTurn)

                {

                    return true;

                }



                // Check if condition 3 is already met: P2 has no payable playable cards AND no recalculation in flight

                bool isRecalcInFlight = (TurnCoordinator.RecalcRequestedThisTurn && !TurnCoordinator.RecalcSucceeded);

                if (!isRecalcInFlight && !TurnCoordinator.HasPlayableCards(player2))

                {

                    TurnCoordinator.HasReleasedHoldThisTurn = true;

                    TurnCoordinator.HoldActive = false;

                    GD.Print("[AiTeammate][Coord] hold-release reason=no-playable-cards");

                    return true;

                }



                // Otherwise, enter Hold!

                if (!TurnCoordinator.PendingP2EndTurn)

                {

                    TurnCoordinator.PendingP2EndTurn = true;

                    TurnCoordinator.HoldActive = true;

                    TurnCoordinator.HoldStartTime = DateTime.UtcNow;

                    GD.Print("[AiTeammate][Coord] hold-begin");

                }



                __result = Task.CompletedTask;

                return false;

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] EndPlayerTurnActionPrefix fail-open exception: " + ex);

                return true;

            }

        }

        public static void EndPlayerTurnActionPostfix(MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction __instance)
        {
            try
            {
                Player p2 = FlowCoordinator.GetPlayer2();
                if (__instance != null && p2 != null && __instance.OwnerId == p2.NetId)
                {
                    TurnCoordinator.P2EndTurnEnqueuedThisTurn = true;
                    GD.Print("[AiTeammate][Coord] p2-end-turn-seen turn=" + TurnCoordinator.TurnNumber + " owner=P2(" + p2.NetId + ")");
                }
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][Coord] p2 summary on end-turn action failed: " + ex.Message); }
        }



        public static void PlayCardActionPostfix(MegaCrit.Sts2.Core.GameActions.PlayCardAction __instance)

        {

            TurnCoordinator.LastActionExecutedTime = DateTime.UtcNow;

            TurnCoordinator.HasExecutedActionInDeployment = true;
            try
            {
                if (__instance == null) return;
                bool isP2 = false;
                try { isP2 = __instance.OwnerId == TurnCoordinator.GetPlayer2NetId(); } catch { }
                if (!isP2)
                {
                    foreach (string member in new string[] { "Player", "Owner", "ActionOwner" })
                    {
                        object value = TurnCoordinator.GetMemberValue(__instance, member);
                        Player owner = value as Player;
                        if (owner != null && TurnCoordinator.IsPlayer2(owner)) { isP2 = true; break; }
                    }
                }
                if (!isP2 && TurnCoordinator.IsDeploying)
                {
                    GD.PrintErr("[AiTeammate][Coord] p2-play-count-owner-unresolved action-owner=" + __instance.OwnerId + "; count not attributed");
                }
                if (isP2)
                {
                    TurnCoordinator.P2PlayedCardsThisTurn++;
                    int actionTurn = TurnCoordinator.P2TelemetryTurn > 0 ? TurnCoordinator.P2TelemetryTurn : TurnCoordinator.TurnNumber;
                    TurnCoordinator.P2PlayedCardsByTurn[actionTurn] = TurnCoordinator.P2PlayedCardsThisTurn;
                    if (TurnCoordinator.P2FallbackActionPendingThisTurn)
                    {
                        TurnCoordinator.P2FallbackActionExecutedThisTurn = true;
                        TurnCoordinator.P2FallbackActionPendingThisTurn = false;
                        GD.Print("[AiTeammate][Coord] fallback-greedy-executed turn=" + TurnCoordinator.TurnNumber + " count=" + TurnCoordinator.P2PlayedCardsThisTurn);
                        if (!TurnCoordinator.IsEnqueuingFallbackBatch)
                        {
                            TurnCoordinator.EnqueueP2EndTurnAfterFallback(FlowCoordinator.GetPlayer2());
                        }
                        TurnCoordinator.P2ZeroActionProtectionFinalizedThisTurn = true;
                        TurnCoordinator.LogP2TurnSummary("fallback-greedy-executed");
                    }
                    GD.Print("[AiTeammate][Coord] p2-play-executed count=" + TurnCoordinator.P2PlayedCardsThisTurn);
                }
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][Coord] p2-play-count failed: " + ex.Message); }

        }



        public static void SetUpCombatPostfix()

        {

            try

            {

                ModEntry.EnsureCombatSolverPatched();

                ModEntry.LoadSettings();

                TurnCoordinator.P2PendingChoiceCount = 0;
                TurnCoordinator.P2SummaryLoggedTurn = -1;
                TurnCoordinator.ResetP1RestoreState();
                TurnCoordinator.ResetState("SetUpCombat");
                TurnCoordinator.CurrentExecutingActionOwnerNetId = 0;

                TurnCoordinator.IsPreCombatOrLoading = true;

                TurnCoordinator.TurnStartTime = DateTime.MinValue;

                TurnCoordinator.StallRecalcTriggered = false;

                TurnCoordinator.StallRecalcTime = DateTime.MinValue;

                TurnCoordinator.StallForceEndTurnTriggered = false;

                TurnCoordinator.LeakStackDumpCount = 0;

                TurnCoordinator.FullAutoDisabledDumpCount = 0;

                TurnCoordinator.EnsureFullAutoEnabled();

                SwitchSourceAuditor.ResetForCombat();

                GD.Print("[AiTeammate][Coord] CombatManager.SetUpCombat: reset LeakStackDumpCount = 0, FullAutoDisabledDumpCount = 0, unknown switch source tracker, and enforced FullAutoEnabled=true.");

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] SetUpCombatPostfix error: " + ex);

            }

        }



        public static void OnEndedTurnLocallyPrefix()

        {

            try

            {
                ulong endedOwner = TurnCoordinator.ResolveEndedTurnOwner();
                ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                ulong p2Id = TurnCoordinator.GetPlayer2NetId();
                GD.Print("[AiTeammate][Coord] OnEndedTurnLocallyPrefix owner=" +
                    (endedOwner == p2Id && p2Id != 0 ? "P2" : (endedOwner == p1Id && p1Id != 0 ? "P1" : "unknown")) +
                    " ownerId=" + endedOwner + " scopeDepth=" + TurnCoordinator.P2ActionSenderScopeDepth);
                if (p2Id != 0 && endedOwner == p2Id)
                {
                    return;
                }

                if (TurnCoordinator.IsWaiting)

                {

                    TurnCoordinator.ReleaseWait("p1-end-turn");

                }

                if (TurnCoordinator.HoldActive && !TurnCoordinator.HasReleasedHoldThisTurn)

                {

                    TurnCoordinator.ReleaseHold("p1-end-turn");

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] OnEndedTurnLocallyPrefix error: " + ex);

            }

        }



        public static void SetupPlayerTurnPostfix()

        {

            try

            {

                ModEntry.EnsureCombatSolverPatched();

                TurnCoordinator.IsPreCombatOrLoading = false;

                TurnCoordinator.ResetTurnState("SetupPlayerTurn");

                // #5.26: capture the P2 baseline after the generic reset so the
                // previous turn's execution count remains available to its summary.
                var telemetryState = CombatManager.Instance != null ? CombatManager.Instance.DebugOnlyGetState() : null;
                if (telemetryState != null && telemetryState.Players != null && telemetryState.Players.Count > 1)
                {
                    TurnCoordinator.BeginP2TurnTelemetry(telemetryState, telemetryState.Players[telemetryState.Players.Count - 1]);
                }

                TurnCoordinator.SnapshotEnemyIntents();

                TurnCoordinator.HasSelfHealedHandThisTurn = false;



                // Directive #5.0.10 T1: Transition timing telemetry

                if (TurnCoordinator.TurnNumber == 1 && TurnCoordinator.RoomEnterTime != DateTime.MinValue)

                {

                    double totalMs = (DateTime.UtcNow - TurnCoordinator.RoomEnterTime).TotalMilliseconds;

                    GD.Print(string.Format("[AiTeammate][Transition] transition-timing: room-enter->Combat started={0:F1}ms (potion={1:F1}ms, relic={2:F1}ms, topbar={3:F1}ms)", totalMs, TurnCoordinator.PotionRebuildMs, TurnCoordinator.RelicRebuildMs, TurnCoordinator.TopBarRebuildMs));

                }



                // Directive #3.9: Init integrity check on first turn of combat

                if (!ModEntry.InitIntegrityChecked)

                {

                    ModEntry.InitIntegrityChecked = true;

                    if (ModEntry.FailedGroups.Count == 0 &&

                        ModEntry.HookedWakuu &&

                        ModEntry.HookedCombatSolver &&

                        ModEntry.HookedFullAutoGuard &&

                        ModEntry.HookedRootControl &&

                        ModEntry.HookedChoiceSuppression &&

                        ModEntry.HookedDeploymentInputLock &&

                        ModEntry.HookedTurnCoordinator)

                    {

                        GD.Print("[AiTeammate][Coord] init-integrity: ok");

                    }

                    else

                    {

                        string failedStr = ModEntry.FailedGroups.Count > 0 ? string.Join(",", ModEntry.FailedGroups.ToArray()) : "missing-critical-group";

                        GD.PrintErr("[AiTeammate][Coord] init-integrity: failed=" + failedStr);

                    }

                }

                if (EndTurnHandlerFallbackPatch.IsP1TurnActive())
                {
                    TurnCoordinator.LogFixedP1StateCheck("p1-turn-start");
                    EndTurnR1Telemetry.DumpLiveButtons("p1-turn-start");
                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] SetupPlayerTurnPostfix error: " + ex);

            }

        }



        // Directive #5.34 T0.5: ActionQueue UI null guard
        public static bool CardPlayQueueOnActionEnqueuedPrefix(MegaCrit.Sts2.Core.GameActions.GameAction action)
        {
            try
            {
                if (action == null) return false;
                if (action is MegaCrit.Sts2.Core.GameActions.PlayCardAction)
                {
                    var pca = (MegaCrit.Sts2.Core.GameActions.PlayCardAction)action;
                    Player p = pca.Player;
                    if (p != null && !LocalContext.IsMe(p))
                    {
                        var room = MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance;
                        if (room == null || p.Creature == null)
                        {
                            GD.Print(string.Format("[AiTeammate][Coord] enqueue-ui-null action={0} owner={1} reason=room-or-creature-null", action.GetType().Name, action.OwnerId));
                            return false;
                        }
                        var creatureNode = room.GetCreatureNode(p.Creature);
                        if (creatureNode == null || creatureNode.PlayerIntentHandler == null || creatureNode.PlayerIntentHandler.CardIntent == null)
                        {
                            GD.Print(string.Format("[AiTeammate][Coord] enqueue-ui-null action={0} owner={1} reason=intent-node-null", action.GetType().Name, action.OwnerId));
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr(string.Format("[AiTeammate][Coord] enqueue-ui-null action={0} owner={1} exception={2}", action != null ? action.GetType().Name : "null", action != null ? action.OwnerId : 0, ex.Message));
                return false;
            }
            return true;
        }

        public static Exception CardPlayQueueOnActionEnqueuedFinalizer(Exception __exception, MegaCrit.Sts2.Core.GameActions.GameAction action)
        {
            if (__exception != null)
            {
                GD.PrintErr(string.Format("[AiTeammate][Coord] enqueue-ui-null action={0} owner={1} finalizer-caught={2}", action != null ? action.GetType().Name : "null", action != null ? action.OwnerId : 0, __exception.Message));
                return null;
            }
            return null;
        }

        public static void BackendEnqueueConfirmedPostfix(object[] __args)
        {
            try
            {
                if (__args != null && __args.Length > 0 && __args[0] is MegaCrit.Sts2.Core.GameActions.GameAction)
                {
                    var ga = (MegaCrit.Sts2.Core.GameActions.GameAction)__args[0];
                    GD.Print(string.Format("[AiTeammate][Coord] backend-enqueue-confirmed action={0} owner={1}", ga.GetType().Name, ga.OwnerId));
                }
            }
            catch { }
        }

        // Directive #5.42 Task 3: Block Canceled P2 PlayCardAction from calling RemoveCardFromQueueForCancellation
        public static bool RemoveCardFromQueueForCancellationPrefix(object[] __args)
        {
            try
            {
                if (!TurnCoordinator.IsFixedP1CombatControlActive() || __args == null) return true;
                for (int i = 0; i < __args.Length; i++)
                {
                    var pca = __args[i] as MegaCrit.Sts2.Core.GameActions.PlayCardAction;
                    if (pca != null && TurnCoordinator.IsP2Target(pca.OwnerId))
                    {
                        GD.Print("[AiTeammate][Coord] p2-cancel-card-queue-ui-suppressed owner=" + pca.OwnerId);
                        return false;
                    }
                }
            }
            catch { }
            return true;
        }

        // Directive #5.36 T2.2: Hard gate preventing P2 cards from entering NPlayerHand
        public static bool NPlayerHandAddPrefix(object[] __args)
        {
            try
            {
                if (!TurnCoordinator.IsFixedP1CombatControlActive()) return true;
                if (__args == null) return true;

                CardModel model = null;
                Node nodeToFree = null;
                for (int i = 0; i < __args.Length; i++)
                {
                    object arg = __args[i];
                    if (arg == null) continue;
                    CardModel cm = arg as CardModel;
                    if (cm != null)
                    {
                        model = cm;
                    }
                    else
                    {
                        MegaCrit.Sts2.Core.Nodes.Cards.NCard ncard = arg as MegaCrit.Sts2.Core.Nodes.Cards.NCard;
                        if (ncard != null)
                        {
                            model = ncard.Model;
                            nodeToFree = ncard;
                        }
                        else
                        {
                            MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder holder = arg as MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder;
                            if (holder != null)
                            {
                                model = holder.CardModel;
                                nodeToFree = holder;
                            }
                        }
                    }
                }

                if (model != null && model.Owner != null && TurnCoordinator.IsP2Target(model.Owner.NetId))
                {
                    GD.Print(string.Format("[AiTeammate][Coord] p2-card-blocked-from-p1-hand card={0} owner={1}", model.Id, model.Owner.NetId));
                    // Directive #5.42 Task 3: Do not free NCardHolder node so third-party postfixes (e.g. STS2RitsuLib) do not crash
                    if (nodeToFree != null && !(nodeToFree is MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder) && GodotObject.IsInstanceValid(nodeToFree))
                    {
                        var parent = nodeToFree.GetParent();
                        if (parent != null) parent.RemoveChild(nodeToFree);
                        nodeToFree.QueueFree();
                    }
                    return false; // 彻底阻止 P2 卡牌进入 P1 的 NPlayerHand！
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Coord] NPlayerHandAddPrefix error: " + ex.Message);
            }
            return true;
        }

        // Directive #5.34 T1: P2 CardPile & Potion critical section hooks
        public static void CardPileAddPrefix(object[] __args, out ulong? __state)
        {
            __state = null;
            try
            {
                if (__args == null || __args.Length == 0) return;
                bool isP2 = false;
                CardModel firstCard = __args[0] as CardModel;
                if (firstCard != null)
                {
                    if (firstCard.Owner != null && TurnCoordinator.IsP2Target(firstCard.Owner.NetId)) isP2 = true;
                }
                else
                {
                    IEnumerable<CardModel> cards = __args[0] as IEnumerable<CardModel>;
                    if (cards != null)
                    {
                        foreach (CardModel cm in cards)
                        {
                            if (cm != null && cm.Owner != null && TurnCoordinator.IsP2Target(cm.Owner.NetId))
                            {
                                isP2 = true;
                                break;
                            }
                        }
                    }
                }
                if (isP2)
                {
                    TurnCoordinator.P2CardPileOpInFlightCount++;
                    ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                    if (TurnCoordinator.IsFixedP1CombatControlActive() && p1Id != 0 && LocalContext.NetId != p1Id)
                    {
                        __state = LocalContext.NetId;
                        LocalContext.NetId = p1Id;
                    }
                    GD.Print("[AiTeammate][Coord] p2-hand-change-ui-suppressed owner=P2 reason=cardpile-add");
                }
            }
            catch { }
        }

        public static void CardPileAddFinalizer(object[] __args, ulong? __state)
        {
            try
            {
                if (__state.HasValue)
                {
                    LocalContext.NetId = __state.Value;
                }
                if (__args == null || __args.Length == 0) return;
                bool isP2 = false;
                CardModel firstCard = __args[0] as CardModel;
                if (firstCard != null)
                {
                    if (firstCard.Owner != null && TurnCoordinator.IsP2Target(firstCard.Owner.NetId)) isP2 = true;
                }
                else
                {
                    IEnumerable<CardModel> cards = __args[0] as IEnumerable<CardModel>;
                    if (cards != null)
                    {
                        foreach (CardModel cm in cards)
                        {
                            if (cm != null && cm.Owner != null && TurnCoordinator.IsP2Target(cm.Owner.NetId))
                            {
                                isP2 = true;
                                break;
                            }
                        }
                    }
                }
                if (isP2)
                {
                    if (TurnCoordinator.P2CardPileOpInFlightCount > 0) TurnCoordinator.P2CardPileOpInFlightCount--;
                    TurnCoordinator.PendingDeferredHandCheckAfterP2 = true;
                }
            }
            catch { }
        }

        public static void DrawInternalPrefix(Player player, out ulong? __state)
        {
            __state = null;
            try
            {
                if (player != null && TurnCoordinator.IsP2Target(player.NetId))
                {
                    TurnCoordinator.P2CardPileOpInFlightCount++;
                    ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                    if (TurnCoordinator.IsFixedP1CombatControlActive() && p1Id != 0 && LocalContext.NetId != p1Id)
                    {
                        __state = LocalContext.NetId;
                        LocalContext.NetId = p1Id;
                    }
                    GD.Print("[AiTeammate][Coord] p2-hand-change-ui-suppressed owner=P2 reason=draw-internal");
                }
            }
            catch { }
        }

        public static void DrawInternalFinalizer(Player player, ulong? __state)
        {
            try
            {
                if (__state.HasValue)
                {
                    LocalContext.NetId = __state.Value;
                }
                if (player != null && TurnCoordinator.IsP2Target(player.NetId))
                {
                    if (TurnCoordinator.P2CardPileOpInFlightCount > 0) TurnCoordinator.P2CardPileOpInFlightCount--;
                    TurnCoordinator.PendingDeferredHandCheckAfterP2 = true;
                }
            }
            catch { }
        }

        public static void LiquidMemoriesOnUsePrefix(MegaCrit.Sts2.Core.Models.Potions.LiquidMemories __instance)
        {
            try
            {
                if (__instance != null && __instance.Owner != null && TurnCoordinator.IsP2Target(__instance.Owner.NetId))
                {
                    TurnCoordinator.P2PotionOpInFlightCount++;
                    GD.Print("[AiTeammate][Coord] p2-hand-change-ui-suppressed owner=P2 reason=liquid-memories");
                }
            }
            catch { }
        }

        public static void LiquidMemoriesOnUseFinalizer(MegaCrit.Sts2.Core.Models.Potions.LiquidMemories __instance)
        {
            try
            {
                if (__instance != null && __instance.Owner != null && TurnCoordinator.IsP2Target(__instance.Owner.NetId))
                {
                    if (TurnCoordinator.P2PotionOpInFlightCount > 0) TurnCoordinator.P2PotionOpInFlightCount--;
                    TurnCoordinator.PendingDeferredHandCheckAfterP2 = true;
                }
            }
            catch { }
        }

        public static void UsePotionActionPrefix(MegaCrit.Sts2.Core.GameActions.UsePotionAction __instance)
        {
            try
            {
                if (__instance != null && TurnCoordinator.IsP2Target(__instance.OwnerId))
                {
                    TurnCoordinator.P2PotionOpInFlightCount++;
                    GD.Print("[AiTeammate][Coord] p2-hand-change-ui-suppressed owner=P2 reason=use-potion");
                }
            }
            catch { }
        }

        public static void UsePotionActionFinalizer(MegaCrit.Sts2.Core.GameActions.UsePotionAction __instance)
        {
            try
            {
                if (__instance != null && TurnCoordinator.IsP2Target(__instance.OwnerId))
                {
                    if (TurnCoordinator.P2PotionOpInFlightCount > 0) TurnCoordinator.P2PotionOpInFlightCount--;
                    TurnCoordinator.PendingDeferredHandCheckAfterP2 = true;
                }
            }
            catch { }
        }

        public static void GetTweenForCardsChangingPilesPrefix(object results, out ulong? __state)
        {
            __state = null;
            try
            {
                if (!TurnCoordinator.IsInCombat()) return;

                var cm = CombatManager.Instance;
                var state = cm != null ? cm.DebugOnlyGetState() : null;
                if (state == null || state.Players == null || state.Players.Count < 1) return;

                var p1 = state.Players[0];
                if (p1 == null || p1.Creature == null || !p1.Creature.IsAlive) return;

                if (TurnCoordinator.IsFixedP1CombatControlActive())
                {
                    ulong curLocal = LocalContext.NetId.HasValue ? LocalContext.NetId.Value : 0;
                    if (curLocal != p1.NetId)
                    {
                        __state = LocalContext.NetId;
                        LocalContext.NetId = p1.NetId;
                    }
                }
            }
            catch { }
        }

        public static void GetTweenForCardsChangingPilesFinalizer(object results, ulong? __state)
        {
            try
            {
                if (__state.HasValue)
                {
                    LocalContext.NetId = __state.Value;
                }
            }
            catch { }
        }



        public static void EndCardOrPotionEffectPostfix()

        {

            try

            {

                if (!TurnCoordinator.IsDeploying)

                {

                    TurnCoordinator.CheckHandOwnershipGuard(DateTime.UtcNow);

                    if (LocalContext.NetId == TurnCoordinator.GetPlayer1NetId())

                    {

                        TurnCoordinator.RefreshPlayerHandVisuals(null);

                    }

                }

                if (TurnCoordinator.IsWaiting)

                {

                    TurnCoordinator.CheckTriggers("card-played");

                }

                if (TurnCoordinator.HoldActive && !TurnCoordinator.HasReleasedHoldThisTurn)

                {

                    TurnCoordinator.CheckMidturnReplan("p1-card-played");

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] EndCardOrPotionEffectPostfix error: " + ex);

            }

        }



        public static void EnergyCounterProcessPostfix()

        {

            try

            {

                TurnCoordinator.OnProcessTick();

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] EnergyCounterProcessPostfix error: " + ex);

            }

        }

    }



    public static class UiSuppressionPatches

    {

        private static DateTime _lastInputLockLogTime = DateTime.MinValue;



        public static bool CanPlayCardsPrefix(ref bool __result)

        {

            if (!TurnCoordinator.IsInCombat()) return true;

            // Directive #5.35 T2.1: Under fixed P1 combat control, P1 must ALWAYS retain input control!
            // P2 card deployment happens silently in background action queue without context switch.
            if (TurnCoordinator.IsFixedP1CombatControlActive())
            {
                return true;
            }




            if (TurnCoordinator.IsDeploying)

            {

                var now = DateTime.UtcNow;

                if ((now - _lastInputLockLogTime).TotalSeconds >= 1.0)

                {

                    _lastInputLockLogTime = now;

                    GD.Print("[AiTeammate][Coord] input-locked (IsDeploying=true)");

                }

                __result = false;

                return false;

            }

            return true;

        }



        public static bool RefreshCombatUiPrefix(ref bool __result, object[] __args)

        {

            if (!TurnCoordinator.IsInCombat()) return true;

            ulong targetId = ResolveTargetId(__args);
            if (targetId == 0) targetId = TurnFlowPatches.GetDraCurrentControlledPlayerId();
            if (TurnCoordinator.IsFixedP1CombatControlActive() &&
                (TurnCoordinator.IsP2Target(targetId) || (targetId == 0 && TurnCoordinator.P2ActionSenderScopeDepth > 0)))
            {
                __result = true;
                GD.Print("[AiTeammate][Coord] p2-ui-refresh-suppressed target=" + targetId + " kind=combat");
                return false;
            }



            if (ModEntry.SuppressControlSwitchUi)

            {

                __result = true;

                return false;

            }

            return true;

        }



        public static bool SuppressVoidPrefix(object[] __args)

        {

            if (!TurnCoordinator.IsInCombat()) return true;

            ulong targetId = ResolveTargetId(__args);
            if (targetId == 0) targetId = TurnFlowPatches.GetDraCurrentControlledPlayerId();
            if (TurnCoordinator.IsFixedP1CombatControlActive() &&
                (TurnCoordinator.IsP2Target(targetId) || (targetId == 0 && TurnCoordinator.P2ActionSenderScopeDepth > 0)))
            {
                GD.Print("[AiTeammate][Coord] p2-ui-refresh-suppressed target=" + targetId + " kind=refresh");
                return false;
            }



            if (ModEntry.SuppressControlSwitchUi)

            {

                return false;

            }

            return true;

        }

        private static ulong ResolveTargetId(object[] args)
        {
            try
            {
                if (args == null) return 0;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] is ulong) return (ulong)args[i];
                    Player p = args[i] as Player;
                    if (p != null) return p.NetId;
                }
            }
            catch { }
            return 0;
        }



        public static bool EnsureForegroundForCombatChoicePrefix(Player player, string source)

        {

            try

            {

                if (!TurnCoordinator.IsInCombat()) return true;



                ulong p1NetId = TurnCoordinator.GetPlayer1NetId();

                ulong localNetId = LocalContext.NetId.HasValue ? LocalContext.NetId.Value : p1NetId;



                if (player == null || player.NetId == p1NetId || player.NetId == localNetId)

                {

                    return true;

                }



                if (player.NetId != p1NetId)

                {

                    GD.Print("[AiTeammate][Coord] Suppressed choice foreground popup for P2 (" + player.NetId + ") source=" + source);

                    return false;

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] EnsureForegroundForCombatChoicePrefix error: " + ex);

            }

            return true;

        }



        public static bool AlignContextForActionOwnerPrefix(ulong playerId, string source)

        {

            return DraControlSwitchPatches.AlignContextForActionOwnerPrefix(playerId, source);

        }



        public static bool SwitchControlledPlayerToPrefix(ulong playerId, string source, out bool __state)

        {

            return DraControlSwitchPatches.SwitchControlledPlayerToPrefix(playerId, source, out __state);

        }



        public static void SwitchControlledPlayerToFinalizer(bool __state)

        {

            DraControlSwitchPatches.SwitchControlledPlayerToFinalizer(__state);

        }



        public static bool ApplyControlContextPrefix(string source, out bool __state)

        {

            return DraControlSwitchPatches.ApplyControlContextPrefix(source, out __state);

        }



        public static void ApplyControlContextPostfix(string source)
        {
            DraControlSwitchPatches.ApplyControlContextPostfix(source);
        }

        public static void ApplyControlContextFinalizer(bool __state)

        {

            DraControlSwitchPatches.ApplyControlContextFinalizer(__state);

        }

    }



    public static class SwitchSourceAuditor

    {

        public static readonly string[] InteractiveWhitelist = new string[]

        {

            "hotkey:Tab/]/R",

            "hotkey:S-Tab/[/T",

            "combat-ui-down",

            "combat-ui-up",

            "player-state-button",

            "player-state-right-click",

            "gamepad:lt+right-stick-down",

            "gamepad:lt+right-stick-up",

            "char-select-id-anchor-button",

            "switch-lobby-editing-player",

            "wakuu-subpage-manual"

        };



        public static readonly string[] KnownSuppressedExactSources = new string[]

        {

            "auto-end-turn-next-playable",

            "auto-end-turn-all-vakuu-no-cards",

            "auto-end-turn-fallback",

            "manual-end-turn",

            "wakuu-no-playable-cards-tick",

            "combat-watchdog",

            "combat-setup",

            "combat-setup-deferred",

            "combat-setup-deferred-2",

            "run-launched",

            "before-play-phase",

            "event-proceed-next-player",

            "event-auto-next",

            "event-finished-next-player",

            "event-shared-serial-execution",

            "rest-site-next-player-choice",

            "merged-rewards-offer",

            "merged-rewards-offer-from-combatroom",

            "merged-rewards-offer-from-rewardsset",

            "rewards-offer",

            "treasure-picked",

            "treasure-skip-picked-player",

            "wakuu-autoplay-loop",

            "wakuu-pending-retry",

            "wakuu-pending"

        };



        public static readonly string[] KnownSuppressedPrefixes = new string[]

        {

            "wakuu-",

            "rest-site-enter-primary-",

            "rest-site-finalizer-recover-",

            "combat-choice-",

            "event-refresh-before-",

            "event-refresh-after-"

        };



        public static readonly string[] KnownSuppressedSuffixes = new string[]

        {

            "-once-per-round"

        };



        private static readonly List<string> LoggedUnknownSourcesThisCombat = new List<string>();



        public static void ResetForCombat()

        {

            lock (LoggedUnknownSourcesThisCombat)

            {

                LoggedUnknownSourcesThisCombat.Clear();

            }

        }



        public static bool IsInteractive(string source)

        {

            if (string.IsNullOrEmpty(source)) return false;

            for (int i = 0; i < InteractiveWhitelist.Length; i++)

            {

                if (string.Equals(source, InteractiveWhitelist[i], StringComparison.OrdinalIgnoreCase))

                    return true;

            }

            return false;

        }



        public static bool IsKnownSuppressed(string source)

        {

            if (string.IsNullOrEmpty(source)) return false;

            for (int i = 0; i < KnownSuppressedExactSources.Length; i++)

            {

                if (string.Equals(source, KnownSuppressedExactSources[i], StringComparison.OrdinalIgnoreCase))

                    return true;

            }



            for (int i = 0; i < KnownSuppressedPrefixes.Length; i++)

            {

                if (source.StartsWith(KnownSuppressedPrefixes[i], StringComparison.OrdinalIgnoreCase))

                    return true;

            }



            for (int i = 0; i < KnownSuppressedSuffixes.Length; i++)

            {

                if (source.EndsWith(KnownSuppressedSuffixes[i], StringComparison.OrdinalIgnoreCase))

                    return true;

            }



            return false;

        }



        public static void AuditSource(string source)

        {

            string cleanSource = source ?? "<null>";

            if (IsInteractive(cleanSource) || IsKnownSuppressed(cleanSource))

            {

                return;

            }



            bool shouldLog = false;

            lock (LoggedUnknownSourcesThisCombat)

            {

                bool alreadyLogged = false;

                for (int i = 0; i < LoggedUnknownSourcesThisCombat.Count; i++)

                {

                    if (string.Equals(LoggedUnknownSourcesThisCombat[i], cleanSource, StringComparison.OrdinalIgnoreCase))

                    {

                        alreadyLogged = true;

                        break;

                    }

                }

                if (!alreadyLogged)

                {

                    LoggedUnknownSourcesThisCombat.Add(cleanSource);

                    shouldLog = true;

                }

            }



            if (shouldLog)

            {

                GD.Print("[AiTeammate][Coord] unknown-switch-source: " + cleanSource);

            }

        }



        public static bool ShouldSuppressControlSwitchUi(ulong targetPlayerId, string source)

        {

            // Non-combat scene: never suppress UI

            if (!TurnCoordinator.IsInCombat())

            {

                return false;

            }



            // Rule 1: Interactive Whitelist bypasses suppression

            if (IsInteractive(source))

            {

                return false;

            }



            // Rule 2: Explicit full-UI restore to Player 1 when Player 1 is alive

            ulong p1NetId = TurnCoordinator.GetPlayer1NetId();

            if (targetPlayerId != 0 && targetPlayerId == p1NetId)

            {

                var cm = CombatManager.Instance;

                var state = cm != null ? cm.DebugOnlyGetState() : null;

                if (state != null && state.Players != null && state.Players.Count > 0)

                {

                    var p1 = state.Players[0];

                    if (p1 != null && p1.Creature != null && p1.Creature.IsAlive)

                    {

                        if (string.Equals(source, "full-restore-1p", StringComparison.OrdinalIgnoreCase))

                        {

                            return false;

                        }

                    }

                }

            }



            // Default: Suppress UI for all internal, automated, or unknown switches

            return true;

        }

    }





    // Directive #5.22 R1: native end-turn button chain telemetry.
    public static class EndTurnR1Telemetry
    {
        private static bool IsButton(object instance)
        {
            try { return instance != null && instance.GetType().Name.IndexOf("NEndTurnButton", StringComparison.OrdinalIgnoreCase) >= 0; }
            catch { return false; }
        }

        private static object Read(object target, string name)
        {
            if (target == null) return null;
            try
            {
                var p = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) return p.GetValue(target, null);
                var f = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return f != null ? f.GetValue(target) : null;
            }
            catch { return null; }
        }

        private static string Describe(object value)
        {
            try { return value == null ? "null" : value.ToString(); }
            catch { return "<error>"; }
        }

        private static string NodeIdentity(object instance)
        {
            try
            {
                Node node = instance as Node;
                if (node == null) return "node=null";
                string path = "<path-error>";
                string id = "<id-error>";
                try { path = node.GetPath().ToString(); } catch { }
                try { id = node.GetInstanceId().ToString(); } catch { }
                return "path=" + path + " instance=" + id;
            }
            catch { return "node=<error>"; }
        }

        private static string DescribeConnections(object instance, string signal)
        {
            try
            {
                Node node = instance as Node;
                if (node == null) return "connections=[]";
                MethodInfo m = node.GetType().GetMethod("GetSignalConnectionList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null) m = typeof(GodotObject).GetMethod("GetSignalConnectionList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null) return "connections=method-missing";
                object result = m.Invoke(node, new object[] { new StringName(signal) });
                var rows = new List<string>();
                IEnumerable enumerable = result as IEnumerable;
                if (enumerable != null) foreach (object row in enumerable)
                {
                    string text = Describe(row);
                    try
                    {
                        IDictionary dict = row as IDictionary;
                        if (dict != null)
                        {
                            object method = dict.Contains("method") ? dict["method"] : null;
                            object callable = dict.Contains("callable") ? dict["callable"] : null;
                            object target = dict.Contains("target") ? dict["target"] : null;
                            text = "method=" + Describe(method) + " callable=" + Describe(callable) + " target=" + Describe(target);
                        }
                    }
                    catch { }
                    rows.Add(text);
                }
                return "connections=" + (rows.Count == 0 ? "[]" : string.Join(" || ", rows.ToArray()));
            }
            catch (Exception ex) { return "connections=error:" + ex.GetType().Name; }
        }

        public static void DumpButton(object instance, string source)
        {
            if (!IsButton(instance)) return;
            try
            {
                object state = Read(instance, "State") ?? Read(instance, "_state");
                object canEnd = Read(instance, "CanTurnBeEnded");
                object visible = Read(instance, "Visible");
                object enabled = Read(instance, "Enabled") ?? Read(instance, "Interactable") ?? Read(instance, "_enabled");
                GD.Print("[AiTeammate][R1] endturn-node-dump source=" + source + " " + NodeIdentity(instance) +
                    " state=" + Describe(state) + " can-turn=" + Describe(canEnd) +
                    " enabled=" + Describe(enabled) + " visible=" + Describe(visible) +
                    " " + DescribeConnections(instance, "pressed") +
                    " " + DescribeConnections(instance, "gui_input"));
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][R1] endturn-node-dump failed: " + ex.Message); }
        }

        public static void DumpLiveButtons(string source)
        {
            try
            {
                SceneTree tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null || tree.Root == null) { GD.Print("[AiTeammate][R1] endturn-node-dump source=" + source + " root=null"); return; }
                int count = 0;
                foreach (Node node in Enumerate(tree.Root)) if (IsButton(node)) { DumpButton(node, source); count++; }
                if (count == 0) GD.Print("[AiTeammate][R1] endturn-node-dump source=" + source + " buttons=0");
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][R1] endturn-node-dump scan failed: " + ex.Message); }
        }

        private static IEnumerable<Node> Enumerate(Node root)
        {
            if (root == null) yield break;
            yield return root;
            foreach (Node child in root.GetChildren()) foreach (Node nested in Enumerate(child)) yield return nested;
        }

        public static void InputLayerPrefix(object __instance, object[] __args, MethodBase __originalMethod)
        {
            if (!IsButton(__instance)) return;
            string eventName = (__args != null && __args.Length > 0 && __args[0] != null) ? __args[0].GetType().FullName : "none";
            GD.Print("[AiTeammate][R1] endturn-input-layer method=" + (__originalMethod != null ? __originalMethod.Name : "unknown") + " event=" + eventName + " " + NodeIdentity(__instance));
            DumpButton(__instance, "input-layer");
        }

        public static void ButtonMethodPrefix(object __instance, object[] __args, MethodBase __originalMethod)
        {
            if (!IsButton(__instance)) return;
            string method = __originalMethod != null ? __originalMethod.Name : "unknown";
            GD.Print("[AiTeammate][R1] endturn-signal-layer method=" + method + " " + NodeIdentity(__instance) + " " + DescribeConnections(__instance, "pressed"));
            DumpButton(__instance, "handler-entry");
            bool disabled = false;
            object state = Read(__instance, "State") ?? Read(__instance, "_state");
            object canEnd = Read(__instance, "CanTurnBeEnded");
            object visible = Read(__instance, "Visible");
            try { disabled = state != null && Convert.ToInt32(state) != 0; } catch { }
            bool turnCheck = EndTurnHandlerFallbackPatch.IsP1TurnActive();
            bool contextCheck = false;
            try { Player p1 = FlowCoordinator.GetPlayer1(); contextCheck = p1 != null && LocalContext.NetId.HasValue && LocalContext.NetId.Value == p1.NetId; } catch { }
            GD.Print("[AiTeammate][R1] endturn-handler-state method=" + method + " disabled=" + disabled + " state=" + Describe(state) + " can-turn=" + Describe(canEnd) + " visible=" + Describe(visible) + " turn-check=" + turnCheck + " context-check=" + contextCheck + " deploying=" + TurnCoordinator.IsDeploying);
        }

        public static void EnqueueLayer(GameAction action, string source)
        {
            try
            {
                if (action == null || action.GetType().Name != "EndPlayerTurnAction") return;
                GD.Print("[AiTeammate][R1] endturn-enqueue-layer source=" + source + " action=" + action.GetType().FullName + " owner=" + action.OwnerId);
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][R1] endturn-enqueue-layer failed: " + ex.Message); }
        }
    }

    // Directive #5.18 T1: ActionQueue owner hard gate.
    // Directive #5.20: independent floating end-turn control and registered F9 fallback.
    public sealed class FloatingEndTurnOverlay : Control
    {
        private static FloatingEndTurnOverlay _instance;
        private Button _button;
        private Label _hint;
        private bool _hintShown;
        private bool _lastCombatActive;
        private bool _shownLogged;
        private const string HotkeyAction = "aiteammate_end_turn_f9";
        private static bool _hotkeyRegistered;

        public static void Initialize()
        {
            try
            {
                if (_instance == null)
                {
                    SceneTree tree = Engine.GetMainLoop() as SceneTree;
                    if (tree == null || tree.Root == null) return;
                    _instance = new FloatingEndTurnOverlay();
                    _instance.Name = "AiTeammateFloatingEndTurn";
                    _instance.Visible = false;
                    _instance.MouseFilter = Control.MouseFilterEnum.Ignore;
                    _instance.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                    tree.Root.AddChild(_instance);
                    _instance.ZIndex = 2000;
                    _instance._button = new Button();
                    _instance._button.Name = "FloatingEndTurnButton";
                    _instance._button.Text = "结束回合  [F9]";
                    _instance._button.Size = new Vector2(220, 62);
                    _instance._button.AddThemeFontSizeOverride("font_size", 22);
                    _instance._button.TooltipText = "结束 P1 回合（F9）";
                    _instance._button.MouseFilter = Control.MouseFilterEnum.Stop;
                    _instance._button.Visible = false;
                    _instance._button.Pressed += _instance.OnPressed;
                    _instance.AddChild(_instance._button);
                    _instance._hint = new Label();
                    _instance._hint.Text = "结束回合失灵时，点击右下悬浮按钮或按 F9";
                    _instance._hint.AddThemeFontSizeOverride("font_size", 18);
                    _instance._hint.Visible = false;
                    _instance._hint.MouseFilter = Control.MouseFilterEnum.Ignore;
                    _instance.AddChild(_instance._hint);
                }
                RegisterHotkey();
                GD.Print("[AiTeammate][Coord] floating-endturn-ready");
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][Coord] floating-endturn initialize failed: " + ex); }
        }

        private static void RegisterHotkey()
        {
            try
            {
                if (_hotkeyRegistered) return;
                if (!InputMap.HasAction(HotkeyAction)) InputMap.AddAction(HotkeyAction);
                InputEventKey key = new InputEventKey();
                key.Keycode = Key.F9;
                InputMap.ActionAddEvent(HotkeyAction, key);
                _hotkeyRegistered = true;
                GD.Print("[AiTeammate][Coord] hotkey-registered F9=end-turn");
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][Coord] hotkey registration failed: " + ex.Message); }
        }

        private void OnPressed()
        {
            if (!EndTurnHandlerFallbackPatch.IsP1TurnActive()) return;
            GD.Print("[AiTeammate][Coord] floating-endturn-click");
            EndTurnHandlerFallbackPatch.Submit("floating-endturn-click");
        }

        public override void _Process(double delta)
        {
            bool active = FlowCoordinator.FloatingEndTurnEnabled && EndTurnHandlerFallbackPatch.IsP1TurnActive() && EndTurnHandlerFallbackPatch.IsCombatUiPresent();
            if (!TurnCoordinator.IsInCombat()) _hintShown = false;
            if (active && !_lastCombatActive && !_hintShown)
            {
                _hintShown = true;
                if (_hint != null) { _hint.Visible = true; _hint.Position = new Vector2(24, 24); }
                GD.Print("[AiTeammate][Coord] user-hint-shown: 结束回合失灵时点右下悬浮按钮或按 F9");
            }
            if (!active) _shownLogged = false;
            _lastCombatActive = active;
            Visible = active;
            if (_button != null)
            {
                Vector2 size = GetViewportRect().Size;
                Position = new Vector2(Math.Max(0, size.X - 240), Math.Max(0, size.Y - 160));
                _button.Visible = active;
                if (active && !_shownLogged) { _shownLogged = true; GD.Print("[AiTeammate][Coord] floating-endturn-shown canvas-layer=2000 mouse-filter=Stop"); }
            }
            if (Input.IsActionJustPressed(HotkeyAction))
            {
                if (active) { GD.Print("[AiTeammate][Coord] endturn-hotkey"); EndTurnHandlerFallbackPatch.Submit("hotkey-F9"); }
                else GD.Print("[AiTeammate][Coord] endturn-hotkey-ignored reason=non-p1-or-noncombat");
            }
        }
    }

    // Directive #5.19-R2: handler-level end-turn fallback and F9 hotkey.
    public static class EndTurnHandlerFallbackPatch
    {
        static DateTime LastRequest = DateTime.MinValue;
        public static bool IsP1TurnActive(){try{Player p1=FlowCoordinator.GetPlayer1();return p1!=null&&TurnCoordinator.IsInCombat()&&!TurnCoordinator.IsPlayer1Dead()&&!TurnCoordinator.IsDeploying&&LocalContext.NetId.HasValue&&LocalContext.NetId.Value==p1.NetId;}catch{return false;}}
        public static bool IsCombatUiPresent(){try{SceneTree tree=Engine.GetMainLoop() as SceneTree;return tree!=null&&FindCombatUi(tree.Root);}catch{return false;}}
        static bool FindCombatUi(Node n){if(n==null)return false;string t=n.GetType().Name;if(t.IndexOf("NCombatUi",StringComparison.OrdinalIgnoreCase)>=0||t.IndexOf("CombatUi",StringComparison.OrdinalIgnoreCase)>=0)return true;foreach(Node c in n.GetChildren())if(FindCombatUi(c))return true;return false;}
        static bool Active(){return IsP1TurnActive();}
        public static bool Submit(string source){try{if(!Active())return true;DateTime n=DateTime.UtcNow;if((n-LastRequest).TotalMilliseconds<150)return false;LastRequest=n;PlayerCmd.EndTurn(FlowCoordinator.GetPlayer1(),false,null);GD.Print("[AiTeammate][Coord] endturn-recorded source="+source);return false;}catch(Exception ex){GD.PrintErr("[AiTeammate][Coord] endturn-handler-fail-open: "+ex.Message);return true;}}
        public static bool ButtonPrefix(object __instance, object[] __args, MethodBase __originalMethod){EndTurnR1Telemetry.ButtonMethodPrefix(__instance, __args, __originalMethod); return true;}
        public static bool InputPrefix(object __instance, object[] __args){try{EndTurnR1Telemetry.InputLayerPrefix(__instance, __args, null); foreach(object a in __args??new object[0]){if(a==null)continue;string n=a.GetType().Name;if(n.IndexOf("InputEventKey",StringComparison.OrdinalIgnoreCase)<0)continue;object pressed=Get(a,"Pressed");if(pressed is bool && !(bool)pressed)continue;object kc=Get(a,"Keycode")??Get(a,"PhysicalKeycode");if(kc!=null&&kc.ToString().IndexOf("F9",StringComparison.OrdinalIgnoreCase)>=0){Submit("hotkey-F9");return false;}}}catch(Exception ex){GD.PrintErr("[AiTeammate][Coord] endturn-hotkey fail-open: "+ex.Message);}return true;}
        static object Get(object o,string n){var p=o.GetType().GetProperty(n,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);try{return p!=null?p.GetValue(o,null):null;}catch{return null;}}
    }

    public static class ActionQueueOwnerPatch
    {
        static bool Covered(MegaCrit.Sts2.Core.GameActions.GameAction a){string n=a.GetType().Name;return n=="PlayCardAction"||n=="EndPlayerTurnAction"||n.IndexOf("UsePotion",StringComparison.OrdinalIgnoreCase)>=0;}
        public static bool Prefix(MegaCrit.Sts2.Core.GameActions.GameAction action)
        {
            try
            {
                EndTurnR1Telemetry.EnqueueLayer(action, "ActionQueueSynchronizer.RequestEnqueue");
                if (action == null || !Covered(action)) return true;
                ulong owner = action.OwnerId;
                ulong local = LocalContext.NetId.HasValue ? LocalContext.NetId.Value : 0;
                if (owner == 0 || local == 0 || owner == local) return true;
                if (TurnCoordinator.FixedP1CombatControl && TurnCoordinator.IsP2Target(owner))
                {
                    GD.Print(string.Format("[AiTeammate][Coord] enqueue-owner-mismatch-corrected actionOwner={0} queueOwner={1} corrected=True action={2} fixed-p1-scope=true", owner, local, action.GetType().Name));
                    return true;
                }
                LocalContext.NetId = owner;
                GD.Print(string.Format("[AiTeammate][Coord] enqueue-owner-mismatch-corrected actionOwner={0} queueOwner={1} corrected=True action={2}", owner, local, action.GetType().Name));
                return true;
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Coord] enqueue-rejected: " + ex.Message);
                return false;
            }
        }
    }

    // Directive #5.13 T2: Effect Attribution Guard & Execution Window Nails

    public static class AttributionPatches

    {

        private sealed class P2SenderScopeState
        {
            public bool HadPrevious;
            public ulong Previous;
            public bool PreviousSuppressUi;
        }

        private static readonly Dictionary<MegaCrit.Sts2.Core.GameActions.GameAction, P2SenderScopeState> P2SenderScopes =
            new Dictionary<MegaCrit.Sts2.Core.GameActions.GameAction, P2SenderScopeState>();

        public static void ClearAllP2SenderScopes()
        {
            try
            {
                lock (P2SenderScopes)
                {
                    P2SenderScopes.Clear();
                }
            }
            catch { }
        }

        public static void ExitActionExecutionScope(MegaCrit.Sts2.Core.GameActions.GameAction action, string source)
        {
            try
            {
                P2SenderScopeState p2Scope = null;
                if (action != null)
                {
                    lock (P2SenderScopes)
                    {
                        if (P2SenderScopes.TryGetValue(action, out p2Scope))
                        {
                            P2SenderScopes.Remove(action);
                        }
                    }
                }
                if (p2Scope != null)
                {
                    if (p2Scope.HadPrevious) LocalContext.NetId = p2Scope.Previous;
                    else LocalContext.NetId = null;
                    ModEntry.SuppressControlSwitchUi = p2Scope.PreviousSuppressUi;
                    if (TurnCoordinator.P2ActionSenderScopeDepth > 0) TurnCoordinator.P2ActionSenderScopeDepth--;
                    TurnCoordinator.MarkP1RestoreAfterP2Scope("action=" + action.GetType().Name + ":" + source);
                    if (TurnCoordinator.P2ActionSenderScopeDepth == 0) TurnCoordinator.QueueP1RestoreAfterP2Scope();
                    GD.Print("[AiTeammate][Coord] p2-action-sender-scope end owner=P2 action=" + action.GetType().Name + " source=" + source);
                    GD.Print("[AiTeammate][Coord] p2-action-executed owner=P2 action=" + action.GetType().Name);
                    TurnCoordinator.LogFixedP1StateCheck("p2-action-scope-end", action.OwnerId);
                    TurnCoordinator.CheckNativeChoiceContinuation("action-exit");
                }

                if (action != null && object.ReferenceEquals(TurnCoordinator.P1ActionExecutingInstance, action))
                {
                    TurnCoordinator.P1ActionExecutingInstance = null;
                    TurnCoordinator.IsP1ActionExecuting = false;
                }

                if (action != null && TurnCoordinator.CurrentExecutingActionOwnerNetId == action.OwnerId)
                {
                    TurnCoordinator.CurrentExecutingActionOwnerNetId = 0;
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Attribution] ExitActionExecutionScope error: " + ex);
            }
        }

        public static class ActionExecutionWindowPatch
        {
            public static void Prefix(MegaCrit.Sts2.Core.GameActions.GameAction __instance)
            {
                try
                {
                    if (!TurnCoordinator.IsInCombat() || __instance == null) return;

                    // Directive #5.34 T0.5: Backend action executed output
                    GD.Print(string.Format("[AiTeammate][Coord] backend-action-executed action={0} owner={1}", __instance.GetType().Name, __instance.OwnerId));

                    TurnCoordinator.CurrentExecutingActionOwnerNetId = __instance.OwnerId;

                    ulong p2Id = TurnCoordinator.GetPlayer2NetId();
                    if (TurnCoordinator.FixedP1CombatControl && p2Id != 0 && __instance.OwnerId == p2Id)
                    {
                        var scope = new P2SenderScopeState
                        {
                            HadPrevious = LocalContext.NetId.HasValue,
                            Previous = LocalContext.NetId.HasValue ? LocalContext.NetId.Value : 0,
                            PreviousSuppressUi = ModEntry.SuppressControlSwitchUi
                        };
                        lock (P2SenderScopes) { P2SenderScopes[__instance] = scope; }
                        TurnCoordinator.P2ActionSenderScopeDepth++;
                        ModEntry.SuppressControlSwitchUi = true;
                        LocalContext.NetId = p2Id;
                        GD.Print("[AiTeammate][Coord] p2-action-sender-scope begin owner=P2 action=" + __instance.GetType().Name);
                    }

                    ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                    if (p1Id != 0 && __instance.OwnerId == p1Id)
                    {
                        TurnCoordinator.IsP1ActionExecuting = true;
                        TurnCoordinator.P1ActionExecutingInstance = __instance;
                        MegaCrit.Sts2.Core.Context.LocalContext.NetId = p1Id;
                    }
                }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Attribution] ActionExecutionWindowPatch Prefix error: " + ex);
                }
            }

            public static void Postfix(MegaCrit.Sts2.Core.GameActions.GameAction __instance, Task __result)
            {
                try
                {
                    if (__result != null && __instance != null)
                    {
                        var captured = __instance;
                        __result.ContinueWith(delegate(Task t)
                        {
                            AttributionPatches.ExitActionExecutionScope(captured, "execute-task-continue");
                        }, TaskScheduler.Default);
                    }
                }
                catch (Exception ex)
                {
                    GD.PrintErr("[AiTeammate][Attribution] ActionExecutionWindowPatch Postfix error: " + ex);
                }
            }
        }

        public static class ActionExecutionWindowExitPatch
        {
            public static void Postfix(MegaCrit.Sts2.Core.GameActions.GameAction action)
            {
                AttributionPatches.ExitActionExecutionScope(action, "after-action-finished");
            }
        }



        public static class PlayCardActionAttributionPatch

        {

            private static readonly System.Reflection.FieldInfo TargetIdField = typeof(MegaCrit.Sts2.Core.GameActions.PlayCardAction).GetField("<TargetId>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);



            public static void Prefix(MegaCrit.Sts2.Core.GameActions.PlayCardAction __instance)

            {

                try

                {

                    if (!TurnCoordinator.IsInCombat() || __instance == null) return;

                    var player = __instance.Player;

                    var target = __instance.Target;

                    MegaCrit.Sts2.Core.Models.CardModel card = null;

                    if (__instance.NetCombatCard != null)

                    {

                        card = __instance.NetCombatCard.ToCardModelOrNull();

                    }



                    if (player != null && player.Creature != null && card != null)

                    {

                        bool isSelfCard = (card.TargetType == MegaCrit.Sts2.Core.Entities.Cards.TargetType.Self || card.TargetType == MegaCrit.Sts2.Core.Entities.Cards.TargetType.None);

                        if (isSelfCard && target != null && target.Side == MegaCrit.Sts2.Core.Combat.CombatSide.Player)

                        {

                            if (target != player.Creature)

                            {

                                ulong fromId = (target.Player != null) ? target.Player.NetId : 0;

                                ulong toId = player.NetId;

                                GD.PrintErr(string.Format("[AiTeammate][Attribution] attribution-block: redirected PlayCardAction self-target from player {0} to action owner {1} (card={2})",

                                    fromId,

                                    toId,

                                    card.Id.ToString()));

                                if (TargetIdField != null)

                                {

                                    TargetIdField.SetValue(__instance, (uint?)player.Creature.CombatId);

                                }

                            }

                        }

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Attribution] PlayCardActionAttributionPatch error: " + ex);

                }

            }

        }



        public static class PowerAttributionPatch

        {

            public static void Prefix(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext choiceContext, MegaCrit.Sts2.Core.Models.PowerModel power, ref MegaCrit.Sts2.Core.Entities.Creatures.Creature target, decimal amount, MegaCrit.Sts2.Core.Entities.Creatures.Creature applier, MegaCrit.Sts2.Core.Models.CardModel cardSource, bool silent)

            {

                try

                {

                    if (!TurnCoordinator.IsInCombat()) return;



                    MegaCrit.Sts2.Core.Entities.Players.Player expectedOwner = null;

                    if (cardSource != null && cardSource.Owner != null)

                    {

                        expectedOwner = cardSource.Owner;

                    }

                    else if (TurnCoordinator.IsP1ActionExecuting)

                    {

                        var pca = TurnCoordinator.P1ActionExecutingInstance as MegaCrit.Sts2.Core.GameActions.PlayCardAction;

                        if (pca != null && pca.Player != null)

                        {

                            expectedOwner = pca.Player;

                        }

                    }



                    if (expectedOwner != null && expectedOwner.Creature != null)

                    {

                        bool isSelfBuffCard = (cardSource == null || cardSource.TargetType == MegaCrit.Sts2.Core.Entities.Cards.TargetType.Self || cardSource.TargetType == MegaCrit.Sts2.Core.Entities.Cards.TargetType.None);

                        if (isSelfBuffCard && target != null && target.Side == MegaCrit.Sts2.Core.Combat.CombatSide.Player)

                        {

                            if (target != expectedOwner.Creature)

                            {

                                ulong fromId = (target.Player != null) ? target.Player.NetId : 0;

                                ulong toId = expectedOwner.NetId;

                                GD.PrintErr(string.Format("[AiTeammate][Attribution] attribution-block: redirected self-buff {0} from player {1} to action owner {2} (card={3})",

                                    power != null ? power.GetType().Name : "unknown",

                                    fromId,

                                    toId,

                                    cardSource != null ? cardSource.Id.ToString() : "unknown"));

                                target = expectedOwner.Creature;

                            }

                        }

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Attribution] PowerAttributionPatch error: " + ex);

                }

            }

        }



        public static class BlockAttributionPatch

        {

            public static void PrefixBlockVar(ref MegaCrit.Sts2.Core.Entities.Creatures.Creature creature, MegaCrit.Sts2.Core.Localization.DynamicVars.BlockVar blockVar, MegaCrit.Sts2.Core.Entities.Cards.CardPlay cardPlay, bool fast)

            {

                CheckBlockTarget(ref creature, cardPlay);

            }



            public static void PrefixDecimal(ref MegaCrit.Sts2.Core.Entities.Creatures.Creature creature, decimal amount, MegaCrit.Sts2.Core.ValueProps.ValueProp props, MegaCrit.Sts2.Core.Entities.Cards.CardPlay cardPlay, bool fast)

            {

                CheckBlockTarget(ref creature, cardPlay);

            }



            private static void CheckBlockTarget(ref MegaCrit.Sts2.Core.Entities.Creatures.Creature creature, MegaCrit.Sts2.Core.Entities.Cards.CardPlay cardPlay)

            {

                try

                {

                    if (!TurnCoordinator.IsInCombat()) return;



                    MegaCrit.Sts2.Core.Entities.Players.Player expectedOwner = null;

                    MegaCrit.Sts2.Core.Models.CardModel card = (cardPlay != null) ? cardPlay.get_Card() : null;

                    if (card != null && card.Owner != null)

                    {

                        expectedOwner = card.Owner;

                    }

                    else if (TurnCoordinator.IsP1ActionExecuting)

                    {

                        var pca = TurnCoordinator.P1ActionExecutingInstance as MegaCrit.Sts2.Core.GameActions.PlayCardAction;

                        if (pca != null && pca.Player != null)

                        {

                            expectedOwner = pca.Player;

                        }

                    }



                    if (expectedOwner != null && expectedOwner.Creature != null)

                    {

                        bool isSelfCard = (card == null || card.TargetType == MegaCrit.Sts2.Core.Entities.Cards.TargetType.Self || card.TargetType == MegaCrit.Sts2.Core.Entities.Cards.TargetType.None);

                        if (isSelfCard && creature != null && creature.Side == MegaCrit.Sts2.Core.Combat.CombatSide.Player)

                        {

                            if (creature != expectedOwner.Creature)

                            {

                                ulong fromId = (creature.Player != null) ? creature.Player.NetId : 0;

                                ulong toId = expectedOwner.NetId;

                                GD.PrintErr(string.Format("[AiTeammate][Attribution] attribution-block: redirected self-block from player {0} to action owner {1} (card={2})",

                                    fromId,

                                    toId,

                                    card != null ? card.Id.ToString() : "unknown"));

                                creature = expectedOwner.Creature;

                            }

                        }

                    }

                }

                catch (Exception ex)

                {

                    GD.PrintErr("[AiTeammate][Attribution] BlockAttributionPatch error: " + ex);

                }

            }

        }

    }



    public static class DraControlSwitchPatches

    {

        // Directive #5.13 T1 & T2: Intercept AlignContextForActionOwner

        public static bool AlignContextForActionOwnerPrefix(ulong playerId, string source)

        {

            try

            {

                if (TurnCoordinator.IsInCombat())

                {

                    if (TurnCoordinator.IsP1ActionExecuting)

                    {

                        GD.PrintErr(string.Format("[AiTeammate][Attribution] attribution-block: blocked AlignContextForActionOwner during P1 action execution window (target={0}, source={1})", playerId, source ?? "null"));

                        return false;

                    }



                    ulong p2Id = TurnCoordinator.GetPlayer2NetId();

                    if (playerId == TurnCoordinator.GetPlayer1NetId() && TurnCoordinator.IsP1RecoverySource(source))
                    {
                        return true;
                    }

                    if ((p2Id != 0 && playerId == p2Id) || !SwitchSourceAuditor.IsInteractive(source))

                    {

                        return false; // Skip AlignContextForActionOwner completely: zero sender switch, zero drift warning!

                    }

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] AlignContextForActionOwnerPrefix error: " + ex);

            }

            return true;

        }



        public static bool SwitchControlledPlayerToPrefix(ulong playerId, string source, out bool __state)

        {

            __state = ModEntry.SuppressControlSwitchUi;

            try

            {

                // Directive #5.17 T3: forced resolve before user-driven room/control leave.
                if (SwitchSourceAuditor.IsInteractive(source) && playerId != TurnCoordinator.GetPlayer1NetId()) TurnCoordinator.GuardPendingChoicesBeforeLeave("control-switch:" + (source ?? "unknown"));
                // Directive #5.14 T1: During room enter window, all non-interactive switches are deferred/blocked

                if (TurnCoordinator.IsEnteringRoom)

                {

                    if (!SwitchSourceAuditor.IsInteractive(source))

                    {

                        GD.PrintErr(string.Format("[AiTeammate][Coord] control-switch-deferred-during-room-enter source={0} target={1}", source ?? "null", playerId));

                        return false;

                    }

                }



                // Directive #5.31: EventRoom P1 Foreground Lock for Auto Event Sources (BEFORE non-combat return!)
                if (TurnCoordinator.IsInEventRoom())
                {
                    bool isAuto = TurnCoordinator.IsAutoEventSource(source);
                    bool isP2 = TurnCoordinator.IsP2Target(playerId);
                    if (isAuto && isP2)
                    {
                        GD.Print(string.Format("[AiTeammate][Event] event-auto-switch-suppressed source={0} target=P2", source ?? "unknown"));

                        if (RunManager.Instance != null && RunManager.Instance.EventSynchronizer != null)
                        {
                            Player p2 = FlowCoordinator.GetPlayer2();
                            if (p2 != null)
                            {
                                FlowCoordinator.ResolveP2PendingEvent(RunManager.Instance.EventSynchronizer, p2);
                            }
                        }

                        ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                        ulong actual = TurnFlowPatches.GetDraCurrentControlledPlayerId();
                        if (p1Id != 0 && actual != p1Id)
                        {
                            TurnFlowPatches.SwitchControlToPlayer(p1Id, "wakuu-p1-restore:event-auto-suppressed", false);
                        }
                        string finalCtrl = (actual == p1Id || (p1Id != 0 && TurnFlowPatches.GetDraCurrentControlledPlayerId() == p1Id)) ? "P1" : actual.ToString();
                        GD.Print(string.Format("[AiTeammate][Event] event-room-final-controller expected=P1 actual={0}", finalCtrl));

                        return false;
                    }
                }



                if (!TurnCoordinator.IsInCombat())
                {
                    if (TurnCoordinator.IsFullRunP1ControlLockActive() && TurnCoordinator.IsP2Target(playerId))
                    {
                        GD.Print(string.Format("[AiTeammate][Coord] out-of-combat-p2-switch-blocked source={0} target={1}", source ?? "unknown", playerId));
                        return false;
                    }
                    ModEntry.SuppressControlSwitchUi = false;
                    return true;
                }

                if (TurnCoordinator.IsFixedP1CombatControlActive() && TurnCoordinator.IsP2Target(playerId))
                {
                    if (TurnCoordinator.IsTurnBootstrapSource(source))
                    {
                        ModEntry.SuppressControlSwitchUi = true;
                        TurnCoordinator.IsP2TurnBootstrapping = true;
                    }
                    else
                    {
                        GD.Print(string.Format("[AiTeammate][Coord] p2-foreground-switch-blocked source={0} target={1}", source ?? "unknown", playerId));
                        TurnCoordinator.LogFixedP1StateCheck("switch-blocked", playerId);
                        return false;
                    }
                }



                if (TurnCoordinator.IsP1ActionExecuting)

                {

                    GD.PrintErr(string.Format("[AiTeammate][Attribution] attribution-block: blocked SwitchControlledPlayerTo during P1 action execution window (target={0}, source={1})", playerId, source ?? "null"));

                    return false;

                }



                SwitchSourceAuditor.AuditSource(source);



                // Directive #5.13 T1: Root suppression: in combat, all non-interactive switches are blocked!

                if (!SwitchSourceAuditor.IsInteractive(source) &&
                    !(playerId == TurnCoordinator.GetPlayer1NetId() && TurnCoordinator.IsP1RecoverySource(source)))

                {

                    if (TurnCoordinator.IsPlayer1Dead() && playerId == TurnCoordinator.GetPlayer2NetId())

                    {

                        return true;

                    }

                    return false;

                }



                if (SwitchSourceAuditor.ShouldSuppressControlSwitchUi(playerId, source))

                {

                    ModEntry.SuppressControlSwitchUi = true;

                }

                else

                {

                    ModEntry.SuppressControlSwitchUi = false;

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] SwitchControlledPlayerToPrefix error: " + ex);

            }

            return true;

        }



        public static void SwitchControlledPlayerToFinalizer(bool __state)

        {

            ModEntry.SuppressControlSwitchUi = __state;

        }



        public static bool ApplyControlContextPrefix(string source, out bool __state)

        {

            __state = ModEntry.SuppressControlSwitchUi;

            try

            {

                // Directive #5.14 T1: During room enter window, all non-interactive switches are deferred/blocked

                if (TurnCoordinator.IsEnteringRoom)

                {

                    if (!SwitchSourceAuditor.IsInteractive(source))

                    {

                        GD.PrintErr(string.Format("[AiTeammate][Coord] control-switch-deferred-during-room-enter source={0} target={1}", source ?? "null", 0));

                        return false;

                    }

                }



                if (!TurnCoordinator.IsInCombat())
                {
                    if (TurnCoordinator.IsFullRunP1ControlLockActive())
                    {
                        ulong draCurrent = TurnFlowPatches.GetDraCurrentControlledPlayerId();
                        if (TurnCoordinator.IsP2Target(draCurrent) || (!SwitchSourceAuditor.IsInteractive(source) && !TurnCoordinator.IsP1RecoverySource(source)))
                        {
                            ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                            if (draCurrent != 0 && draCurrent != p1Id && p1Id != 0)
                            {
                                TurnFlowPatches.SwitchControlToPlayer(p1Id, "wakuu-p1-restore:out-of-combat-apply", false);
                            }
                            GD.Print(string.Format("[AiTeammate][Coord] out-of-combat-p2-apply-blocked source={0} draCurrent={1}", source ?? "unknown", draCurrent));
                            return false;
                        }
                    }
                    ModEntry.SuppressControlSwitchUi = false;
                    return true;
                }

                bool p1Recovery = TurnCoordinator.IsP1RecoverySource(source);
                bool isBootstrap = TurnCoordinator.IsTurnBootstrapSource(source);
                if (TurnCoordinator.IsFixedP1CombatControlActive() && !p1Recovery)
                {
                    if (isBootstrap)
                    {
                        TurnCoordinator.IsP2TurnBootstrapping = true;
                        ModEntry.SuppressControlSwitchUi = true;
                        int round = TurnCoordinator.TurnNumber;
                        ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                        ulong p2Id = TurnCoordinator.GetPlayer2NetId();
                        ulong currentControlledId = TurnFlowPatches.GetDraCurrentControlledPlayerId();
                        GD.Print(string.Format("[AiTeammate][Coord] p2-turn-bootstrap begin round={0} p1={1} p2={2} controller={3}", round, p1Id, p2Id, currentControlledId));
                    }
                    else
                    {
                        ulong currentControlledId = TurnFlowPatches.GetDraCurrentControlledPlayerId();
                        if (currentControlledId != 0 && TurnCoordinator.IsP2Target(currentControlledId))
                        {
                            GD.Print("[AiTeammate][Coord] p2-foreground-switch-blocked source=" + (source ?? "unknown") + " target=" + currentControlledId);
                            TurnCoordinator.LogFixedP1StateCheck("apply-blocked", currentControlledId);
                            return false;
                        }
                        if (!SwitchSourceAuditor.IsInteractive(source))
                        {
                            GD.Print("[AiTeammate][Coord] p2-foreground-switch-blocked source=" + (source ?? "unknown") + " target=implicit-P2");
                            TurnCoordinator.LogFixedP1StateCheck("apply-noninteractive-blocked", TurnCoordinator.GetPlayer2NetId());
                            return false;
                        }
                    }
                }



                if (TurnCoordinator.IsP1ActionExecuting)

                {

                    GD.PrintErr(string.Format("[AiTeammate][Attribution] attribution-block: blocked ApplyControlContext during P1 action execution window (source={0})", source ?? "null"));

                    return false;

                }



                SwitchSourceAuditor.AuditSource(source);



                // Directive #5.13 T1: In combat, non-interactive ApplyControlContext is blocked if P1 alive

                if (!SwitchSourceAuditor.IsInteractive(source) && !p1Recovery && !isBootstrap)

                {

                    if (!TurnCoordinator.IsPlayer1Dead())

                    {

                        return false;

                    }

                }



                if (!__state)

                {

                    ulong currentControlledId = 0;

                    try

                    {

                        var multiRuntimeType = AccessTools.TypeByName("LocalMultiControl.Scripts.Runtime.LocalMultiControlRuntime");

                        if (multiRuntimeType != null)

                        {

                            var sessionField = AccessTools.Field(multiRuntimeType, "Session");

                            var sessionObj = sessionField != null ? sessionField.GetValue(null) : null;

                            if (sessionObj != null)

                            {

                                var prop = AccessTools.Property(sessionObj.GetType(), "CurrentControlledPlayerId");

                                if (prop != null)

                                {

                                    var val = prop.GetValue(sessionObj, null);

                                    if (val is ulong) currentControlledId = (ulong)val;

                                }

                            }

                        }

                    }

                    catch { }



                    if (SwitchSourceAuditor.ShouldSuppressControlSwitchUi(currentControlledId, source))

                    {

                        ModEntry.SuppressControlSwitchUi = true;

                    }

                }

                else if (SwitchSourceAuditor.IsInteractive(source) && !TurnCoordinator.IsDeploying)

                {

                    ModEntry.SuppressControlSwitchUi = false;

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] ApplyControlContextPrefix error: " + ex);

            }

            return true;

        }



        // Directive #5.30: P2 Turn Bootstrap Postfix & Completion
        public static void ApplyControlContextPostfix(string source)
        {
            try
            {
                if (TurnCoordinator.IsP2TurnBootstrapping)
                {
                    int round = TurnCoordinator.TurnNumber;
                    ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                    ulong p2Id = TurnCoordinator.GetPlayer2NetId();

                    TurnFlowPatches.RestorePlayer1ControlIfActive("wakuu-p1-restore:p2-turn-bootstrap", false);
                    GD.Print(string.Format("[AiTeammate][Coord] p2-turn-bootstrap end round={0} p1={1} p2={2} result=success", round, p1Id, p2Id));
                    TurnCoordinator.IsP2TurnBootstrapping = false;

                    Player p2 = FlowCoordinator.GetPlayer2();
                    bool hasAttack;
                    bool hasPlayable = p2 != null && TurnCoordinator.P2HasUsableCard(p2, out hasAttack);
                    if (p2 != null && !hasPlayable)
                    {
                        int handCount = (p2.PlayerCombatState != null && p2.PlayerCombatState.Hand != null && p2.PlayerCombatState.Hand.Cards != null) ? p2.PlayerCombatState.Hand.Cards.Count : 0;
                        int energy = p2.PlayerCombatState != null ? p2.PlayerCombatState.Energy : 0;
                        GD.Print(string.Format("[AiTeammate][Coord] p2-no-action turn={0} reason={1} hand={2} energy={3}", round, energy <= 0 ? "zero-energy" : "no-playable-cards", handCount, energy));
                        TurnCoordinator.EnqueueP2EndTurn(p2);
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Coord] ApplyControlContextPostfix error: " + ex);
            }
        }

        public static void ApplyControlContextFinalizer(bool __state)

        {

            ModEntry.SuppressControlSwitchUi = __state;

        }

    }



    public static class FullAutoPatches

    {

        public static void SetFullAutoSessionPrefix(bool value)

        {

            try

            {

                if (!value && TurnCoordinator.FullAutoDisabledDumpCount < 2)

                {

                    TurnCoordinator.FullAutoDisabledDumpCount++;

                    GD.PrintErr("[AiTeammate][Coord] full-auto-disabled-by: SolverCombatSession.set_FullAutoEnabled(false)\n" + System.Environment.StackTrace);

                }

            }

            catch { }

        }



        public static void SetFullAutoControllerPrefix(object[] __args)

        {

            try

            {

                if (__args != null && __args.Length > 0)

                {

                    object lastArg = __args[__args.Length - 1];

                    if (lastArg is bool && !(bool)lastArg)

                    {

                        if (TurnCoordinator.FullAutoDisabledDumpCount < 2)

                        {

                            TurnCoordinator.FullAutoDisabledDumpCount++;

                            GD.PrintErr("[AiTeammate][Coord] full-auto-disabled-by: SolverController.SetFullAuto(false)\n" + System.Environment.StackTrace);

                        }

                    }

                }

            }

            catch { }

        }

    }



    public static class ChoiceSuppressionPatches

    {

        [HarmonyPriority(Priority.Last)]

        public static void ShouldSelectLocalCardPostfix(Player player, ref bool __result)

        {

            try

            {

                if (!TurnCoordinator.IsInCombat()) return;



                ulong p1NetId = TurnCoordinator.GetPlayer1NetId();

                ulong localNetId = LocalContext.NetId.HasValue ? LocalContext.NetId.Value : p1NetId;



                // Insurance A: Never intercept if choice owner is local player or P1 (shop card removal, events, upgrades)

                if (player == null || player.NetId == p1NetId || player.NetId == localNetId)

                {

                    return;

                }



                __result = false;

            }

            catch { }

        }



        public static bool PlayerHandSelectCardsPrefix(

            CardSelectorPrefs prefs,

            Func<CardModel, bool> filter,

            AbstractModel source,

            ref Task<IEnumerable<CardModel>> __result)

        {

            try

            {

                if (!TurnCoordinator.IsInCombat()) return true;



                ulong p1NetId = TurnCoordinator.GetPlayer1NetId();

                ulong localNetId = LocalContext.NetId.HasValue ? LocalContext.NetId.Value : p1NetId;



                bool isP2Source = false;

                Player owner = null;



                if (source is CardModel)

                {

                    var card = (CardModel)source;

                    owner = card.Owner;

                    if (owner != null && owner.NetId != p1NetId && owner.NetId != localNetId)

                    {

                        isP2Source = true;

                    }

                }



                // If choice owner is local player or P1, NEVER intercept!

                if (owner != null && (owner.NetId == p1NetId || owner.NetId == localNetId))

                {

                    return true;

                }



                if (isP2Source || TurnCoordinator.IsDeploying)

                {

                    var cm = CombatManager.Instance;

                    var state = cm != null ? cm.DebugOnlyGetState() : null;

                    if (owner == null && state != null && state.Players != null && state.Players.Count >= 2)

                    {

                        owner = state.Players[state.Players.Count - 1];

                    }



                    if (owner != null && (owner.NetId == p1NetId || owner.NetId == localNetId))

                    {

                        return true;

                    }



                    List<CardModel> selected = new List<CardModel>();

                    if (owner != null)

                    {

                        var handPile = PileType.Hand.GetPile(owner);

                        if (handPile != null && handPile.Cards != null)

                        {

                            foreach (var c in handPile.Cards)

                            {

                                if (c != null && (filter == null || filter(c)))

                                {

                                    selected.Add(c);

                                    break;

                                }

                            }

                        }

                    }



                    string cardName = selected.Count > 0 ? (selected[0].Title ?? selected[0].Id.ToString()) : "none";

                    GD.Print("[AiTeammate][Coord] Suppressed NPlayerHand.SelectCards UI for P2, returned candidate: " + cardName);

                    var tcs = new TaskCompletionSource<IEnumerable<CardModel>>();

                    tcs.SetResult(selected);

                    __result = tcs.Task;

                    return false;

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] PlayerHandSelectCardsPrefix error: " + ex);

            }

            return true;

        }



        // Directive #3.9.5: Overlay Master Gate (NOverlayStack.Push)

        public static DateTime LastOverlaySuppressedTime = DateTime.MinValue;
        public static DateTime LastP1ChoicePassThroughTime = DateTime.MinValue;



        public static Player ExtractScreenOwner(object screen)

        {

            if (screen == null) return null;

            try

            {

                var screenType = screen.GetType();

                var prop = AccessTools.Property(screenType, "Owner")

                    ?? AccessTools.Property(screenType, "Player")

                    ?? AccessTools.Property(screenType, "PlayerOwner");

                if (prop != null)

                {

                    var val = prop.GetValue(screen);

                    var p = val as Player;

                    if (p != null) return p;

                }



                var field = AccessTools.Field(screenType, "_owner")

                    ?? AccessTools.Field(screenType, "_player")

                    ?? AccessTools.Field(screenType, "player")

                    ?? AccessTools.Field(screenType, "owner");

                if (field != null)

                {

                    var val = field.GetValue(screen);

                    var p = val as Player;

                    if (p != null) return p;

                }

            }

            catch { }

            return null;

        }



        public static bool OverlayStackPushPrefix(object __instance, object screen)
        {
            try
            {
                if (RewardBackendFlow.AllowNativeRewardOverlay(screen)) return true;

                // Directive #3.9.2: Never suppress outside combat
                if (!TurnCoordinator.IsInCombat()) return true;

                ulong p1NetId = TurnCoordinator.GetPlayer1NetId();
                ulong p2NetId = TurnCoordinator.GetPlayer2NetId();

                Player owner = ExtractScreenOwner(screen);
                var aqs = MegaCrit.Sts2.Core.Runs.RunManager.Instance != null
                    ? MegaCrit.Sts2.Core.Runs.RunManager.Instance.ActionQueueSet
                    : null;
                bool p1QueuePausedForChoice = aqs != null && p1NetId != 0 && aqs.ActionQueueIsPaused(p1NetId);
                bool p2QueuePausedForChoice = aqs != null && p2NetId != 0 && aqs.ActionQueueIsPaused(p2NetId);
                bool recentP1PassThrough = (DateTime.UtcNow - LastP1ChoicePassThroughTime).TotalSeconds < 2.5;

                bool isExplicitlyP1 =
                    (owner != null && owner.NetId == p1NetId) ||
                    TurnCoordinator.IsP1ActionExecuting ||
                    (p1NetId != 0 && TurnCoordinator.CurrentExecutingActionOwnerNetId == p1NetId) ||
                    recentP1PassThrough ||
                    (p1QueuePausedForChoice && !p2QueuePausedForChoice && TurnCoordinator.P2ActionSenderScopeDepth == 0);

                if (isExplicitlyP1 && (owner == null || owner.NetId != p2NetId))
                {
                    GD.Print("[AiTeammate][Coord] overlay-allowed-p1 screen=" + (screen != null ? screen.GetType().Name : "null"));
                    return true;
                }

                bool isP2 = (owner != null && owner.NetId == p2NetId);
                bool isP2ActivelyChoosing =
                    TurnCoordinator.P2ActionSenderScopeDepth > 0 ||
                    (p2NetId != 0 && TurnCoordinator.CurrentExecutingActionOwnerNetId == p2NetId) ||
                    p2QueuePausedForChoice;

                bool shouldSuppress = isP2 || (owner == null && isP2ActivelyChoosing);

                if (shouldSuppress)
                {
                    var now = DateTime.UtcNow;
                    if (LastOverlaySuppressedTime != DateTime.MinValue && (now - LastOverlaySuppressedTime).TotalSeconds > 5.0)
                    {
                        GD.Print("[AiTeammate][Coord] overlay-failsafe-shown screen=" + (screen != null ? screen.GetType().Name : "null"));
                        return true;
                    }

                    LastOverlaySuppressedTime = now;
                    GD.Print("[AiTeammate][Coord] overlay-suppressed screen=" + (screen != null ? screen.GetType().Name : "null"));
                    return false;
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Coord] OverlayStackPushPrefix fail-open exception: " + ex);
            }
            return true;
        }
    }

    public static class WakuuPatches

    {

        private static object _emptyHoverTips;



        public static bool SkipGrantWakuuRelicsPrefix(ref Task __result)

        {

            __result = Task.CompletedTask;

            return false;

        }



        public static bool DisableModifyMaxEnergyPrefix(Player player, decimal amount, ref decimal __result)

        {

            __result = amount;

            return false;

        }



        public static bool SafeExtraHoverTipsPrefix(ref object __result)

        {

            if (_emptyHoverTips == null)

            {

                var tipType = AccessTools.TypeByName("MegaCrit.Sts2.Core.HoverTips.IHoverTip");

                if (tipType != null)

                {

                    _emptyHoverTips = Array.CreateInstance(tipType, 0);

                }

            }

            __result = _emptyHoverTips;

            return false;

        }



        public static bool DisableWakuuAutoPlayPrefix(object relic, HookPlayerChoiceContext choiceContext, Player player, ref Task __result)

        {

            __result = Task.CompletedTask;

            return false;

        }



        public static bool DisableWatchdogPrefix(ref bool __result)

        {

            __result = false;

            return false;

        }

    }



    public static class TurnFlowPatches

    {
        public static void PauseAfterNativeChoiceFailurePostfix(object host, object deployment, int turn, Exception failure)
        {
            try
            {
                TurnCoordinator.EnsureFullAutoEnabled();
                TurnCoordinator.IsDeploying = false;
                TurnCoordinator.MidDeployStartTime = DateTime.MinValue;
                TurnCoordinator.PendingNativeChoiceContinuation = true;
                TurnCoordinator.NativeChoiceFailureTime = DateTime.UtcNow;
                GD.Print(string.Format("[AiTeammate][Coord] solver-native-choice-recovery: caught PauseAfterNativeChoiceFailure on turn={0}, scheduling post-action continuation", turn));
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Coord] PauseAfterNativeChoiceFailurePostfix error: " + ex);
            }
        }


        public static bool SuppressControlSwitchUi

        {

            get { return ModEntry.SuppressControlSwitchUi; }

            set { ModEntry.SuppressControlSwitchUi = value; }

        }



        // Directive #3.9.5: Triple-hooking deployment pipeline entry points

        private static CombatState ResolveStateFromArgs(object[] args)
        {
            try { if (args != null) foreach (object arg in args) if (arg is CombatState) return (CombatState)arg; } catch { }
            try { return CombatManager.Instance != null ? CombatManager.Instance.DebugOnlyGetState() : null; } catch { return null; }
        }

        private static object ResolveResultFromArgs(object[] args)
        {
            if (args == null) return null;
            for (int i = args.Length - 1; i >= 0; i--)
            {
                object arg = args[i];
                if (arg == null || arg is CombatState) continue;
                try { if (TurnCoordinator.GetMemberValue(arg, "BestNode") != null || TurnCoordinator.GetMemberValue(arg, "Actions") != null || TurnCoordinator.GetMemberValue(arg, "ActionCount") != null) return arg; } catch { }
            }
            return null;
        }

        public static bool StartFullAutoDeploymentPrefix(object __instance, object[] __args)

        {

            try

            {

                CombatState state = ResolveStateFromArgs(__args);
                object result = ResolveResultFromArgs(__args);
                TurnCoordinator.RecordDeploymentEntryTelemetry("full-auto", __args, result, state, __instance);
                TurnCoordinator.ArmIndependentP2Lifeline("StartFullAutoDeployment");

                TurnCoordinator.DeploySeenThisTurn = true;

                GD.Print("[AiTeammate][Coord] full-auto-deploy-enter turn=" + TurnCoordinator.TurnNumber + " method=" + (__instance != null ? __instance.GetType().FullName : "unknown"));

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] StartFullAutoDeploymentPrefix fail-open exception: " + ex);

            }

            return true;

        }



        public static bool StartDeploymentPrefix(object __instance, object[] __args)
        {
            try
            {
                // Directive #5.35 T1.2: Cache solver controller and args immediately
                if (__instance != null)
                {
                    TurnCoordinator.CachedSolverController = __instance;
                }
                if (__args != null)
                {
                    TurnCoordinator.CachedDeployArgs = __args;
                }

                CombatState state = ResolveStateFromArgs(__args);
                object result = ResolveResultFromArgs(__args);
                TurnCoordinator.RecordDeploymentEntryTelemetry("start-deploy", __args, result, state, __instance);
                TurnCoordinator.ArmIndependentP2Lifeline("StartDeployment");

                TurnCoordinator.DeploySeenThisTurn = true;
                TurnCoordinator.MidDeployStartTime = DateTime.UtcNow;
                TurnCoordinator.MidDeployHangTimeoutTriggered = false;
                TurnCoordinator.MidDeployHangCriticalTriggered = false;

                GD.Print("[AiTeammate][Coord] start-deploy-enter turn=" + TurnCoordinator.TurnNumber);

                if (!TurnCoordinator.IsInCombat())
                {
                    TurnCoordinator.IsDeploying = true;
                    TurnCoordinator.DeployStartTime = DateTime.UtcNow;
                    return true;
                }

                // Directive #5.35 T1.1: CoordinationEnabled / WaitEnabled gate
                if (!TurnCoordinator.CoordinationEnabled || !TurnCoordinator.WaitEnabled)
                {
                    TurnCoordinator.IsDeploying = true;
                    TurnCoordinator.DeployStartTime = DateTime.UtcNow;
                    return true;
                }

                if (!TurnCoordinator.HasEvaluatedThisTurn)
                {
                    TurnCoordinator.EvaluatedPath = "prefix";
                    bool proceed = ProcessDeploymentEvaluation(state, result, __args);
                    if (!proceed)
                    {
                        TurnCoordinator.IsDeploying = false;
                        return false;
                    }
                }

                TurnCoordinator.IsDeploying = true;
                TurnCoordinator.DeployStartTime = DateTime.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AiTeammate][Coord] StartDeploymentPrefix fail-open exception: " + ex);
                TurnCoordinator.IsDeploying = true;
                TurnCoordinator.DeployStartTime = DateTime.UtcNow;
            }
            return true;
        }

        public static bool ProcessDeploymentEvaluation(CombatState state, object result, object[] args)
        {
            // Directive #5.35 T1.1: Direct pass-through if coordination or wait disabled
            if (!TurnCoordinator.CoordinationEnabled || !TurnCoordinator.WaitEnabled) return true;

            var cm = CombatManager.Instance;
            if (cm == null) return true;
            if (state == null) state = cm.DebugOnlyGetState();
            if (state == null || state.Players == null || state.Players.Count < 2) return true;

            var player1 = state.Players[0];
            var player2 = state.Players[state.Players.Count - 1];
            if (player1 == null || player2 == null) return true;
            if (player2.Creature == null || !player2.Creature.IsAlive) return true;
            if (player1.Creature == null || !player1.Creature.IsAlive) return true;

            if (TurnCoordinator.HasReleasedThisTurn)
            {
                TurnCoordinator.RecalcSucceeded = true;
                return true;
            }

            TurnCoordinator.HasEvaluatedThisTurn = true;
            string reason;
            var decision = TurnCoordinator.EvaluateDecision(cm, state, player1, player2, out reason);

            if (decision == CoordinatorDecision.Wait)
            {
                if (TurnCoordinator.HasEnemyHarmfulStatus(state) || TurnCoordinator.HasP2ReceivedBuffOrResources(state, player2))
                {
                    if (!TurnCoordinator.RecalcRequestedThisTurn)
                    {
                        // Directive #5.35 T1.2: DO NOT set RecalcRequestedThisTurn = true before RequestRecalculation!
                        TurnCoordinator.IsWaiting = false;
                        TurnCoordinator.HasReleasedThisTurn = true;
                        TurnCoordinator.LastReleaseTrigger = "enemy-already-debuffed";
                        GD.Print("[AiTeammate][Coord] eval-path=prefix decision=wait (enemy already debuffed during search window, requesting recalculation)");
                        TurnCoordinator.RequestRecalculation(state, "enemy-already-debuffed");
                        TurnCoordinator.IsDeploying = false; // Directive #5.35 T1.3: ensure IsDeploying is not hung
                        return false;
                    }
                    else
                    {
                        TurnCoordinator.IsWaiting = false;
                        TurnCoordinator.HasReleasedThisTurn = true;
                        TurnCoordinator.LastReleaseTrigger = "enemy-debuffed-present";
                        TurnCoordinator.RecalcSucceeded = true;
                        GD.Print("[AiTeammate][Coord] eval-path=prefix decision=immediate (enemy-debuffed-present, deploy cached)");
                        return true;
                    }
                }

                if (!TurnCoordinator.IsWaiting)
                {
                    TurnCoordinator.WaitStartTime = DateTime.UtcNow;
                }
                TurnCoordinator.IsWaiting = true;
                TurnCoordinator.HasReleasedThisTurn = false;
                GD.Print("[AiTeammate][Coord] eval-path=prefix decision=wait (" + reason + ")");
                TurnCoordinator.CleanHangingDeployment();
                TurnCoordinator.IsDeploying = false;
                return false;
            }
            else if (decision == CoordinatorDecision.ImmediateSetup)
            {
                TurnCoordinator.IsWaiting = false;
                TurnCoordinator.HasReleasedThisTurn = true;
                TurnCoordinator.RecalcSucceeded = true;
                GD.Print("[AiTeammate][Coord] eval-path=prefix decision=immediate reason=ai-setup (" + reason + ")");
                return true;
            }
            else // CoordinatorDecision.Immediate
            {
                TurnCoordinator.IsWaiting = false;
                TurnCoordinator.HasReleasedThisTurn = true;
                TurnCoordinator.RecalcSucceeded = true;
                GD.Print("[AiTeammate][Coord] eval-path=prefix decision=immediate (" + reason + ")");
                return true;
            }
        }

        public static bool DeployCurrentTurnPrefix(object __instance, object[] __args, ref Task __result)

        {

            try

            {

                // Directive #3.9.5: Hard requirement - first line unconditional prefix-enter log

                GD.Print("[AiTeammate][Coord] prefix-enter turn=" + TurnCoordinator.TurnNumber);
                TurnCoordinator.RecordDeploymentEntryTelemetry("deploy-current-turn", __args, ResolveResultFromArgs(__args), null, __instance);



                TurnCoordinator.DeploySeenThisTurn = true;

                TurnCoordinator.HasEnteredDeployPrefixThisTurn = true;

                TurnCoordinator.ArmIndependentP2Lifeline("DeployCurrentTurn");



                // Directive #4.0: Coordination OFF -> Fallback to #1/#1.5 baseline:

                // DeployCurrentTurnPrefix 仅做切 P2（UI 压制）+放行，不评估、不压制部署、不 hold（EndPlayerTurnAction 钩直通）、门卫与 midturn 全停。

                if (!TurnCoordinator.CoordinationEnabled)

                {

                    var cmBaseline = CombatManager.Instance;

                    if (cmBaseline != null)

                    {

                        var stateBaseline = cmBaseline.DebugOnlyGetState();

                        if (stateBaseline != null && stateBaseline.Players != null && stateBaseline.Players.Count >= 2)

                        {

                            var p1Baseline = stateBaseline.Players[0];

                            var p2Baseline = stateBaseline.Players[stateBaseline.Players.Count - 1];

                            if (p2Baseline != null && p2Baseline.Creature != null && p2Baseline.Creature.IsAlive)

                            {

                                TurnCoordinator.IsDeploying = true;

                                TurnCoordinator.DeployStartTime = DateTime.UtcNow;

                                if (p1Baseline == null || p1Baseline.Creature == null || !p1Baseline.Creature.IsAlive)

                                {

                                    SwitchControlToPlayer(p2Baseline.NetId, "wakuu-aiteammate-deploy2p");

                                }

                                SetPlayerActionsDisabled(cmBaseline, true);

                                GD.Print("[AiTeammate] Deploying cards for Player 2 (" + p2Baseline.NetId + ") with zero context touch (coordination=OFF baseline).");

                            }

                        }

                    }

                    return true;

                }



                // 1. Cache __instance, __args, and method

                if (__instance != null)

                {

                    TurnCoordinator.CachedSolverController = __instance;

                }

                if (__args != null)

                {

                    TurnCoordinator.CachedDeployArgs = __args;

                    if (__args.Length > 2)

                    {

                        if (TurnCoordinator.CachedDeployResult == null)

                        {

                            TurnCoordinator.CachedDeployResult = __args[2];

                        }

                    }

                    else if (__args.Length > 1 && TurnCoordinator.CachedDeployResult == null)

                    {

                        TurnCoordinator.CachedDeployResult = __args[1];

                    }

                }

                if (TurnCoordinator.DeployCurrentTurnMethod == null)

                {

                    var solverType = AccessTools.TypeByName("CombatSolver.SolverController");

                    if (solverType != null)

                    {

                        TurnCoordinator.DeployCurrentTurnMethod = AccessTools.Method(solverType, "DeployCurrentTurn");

                    }

                    else if (__instance != null)

                    {

                        TurnCoordinator.DeployCurrentTurnMethod = AccessTools.Method(__instance.GetType(), "DeployCurrentTurn");

                    }

                }



                var cm = CombatManager.Instance;

                if (cm == null) return true;

                var state = cm.DebugOnlyGetState();

                if (state == null || state.Players == null || state.Players.Count < 2)

                    return true;



                var player1 = state.Players[0];

                var player2 = state.Players[state.Players.Count - 1];

                if (player2 == null || player2.Creature == null || !player2.Creature.IsAlive)

                    return true;

                TurnCoordinator.RecordPlanTelemetry(__args, "DeployCurrentTurnPrefix");



                if (player1 == null || player1.Creature == null || !player1.Creature.IsAlive)

                {

                    // Directive #3.9.1: Check for empty plan with physical cards before deploying

                    if (!TurnCoordinator.CheckEmptyPlanRecovery(state, player2, __args, ref __result))

                    {

                        TurnCoordinator.IsDeploying = false;

                        return false;

                    }



                    // Player 1 is dead: deploy immediately

                    TurnCoordinator.HasExecutedActionInDeployment = false;

                    TurnCoordinator.IsDeploying = true;

                    TurnCoordinator.DeployStartTime = DateTime.UtcNow;

                    SwitchControlToPlayer(player2.NetId, "wakuu-aiteammate-deploy2p");

                    SetPlayerActionsDisabled(cm, true);

                    GD.Print("[AiTeammate] Player 1 dead; switched context to Player 2 (" + player2.NetId + ") for CombatSolver deployment.");

                    return true;

                }



                // If not evaluated yet this turn, evaluate now!

                if (!TurnCoordinator.HasEvaluatedThisTurn)

                {

                    TurnCoordinator.EvaluatedPath = "prefix";

                    object currentResult = (__args != null && __args.Length > 2) ? __args[2] : null;

                    bool proceed = ProcessDeploymentEvaluation(state, currentResult, __args);

                    if (!proceed)

                    {

                        TurnCoordinator.IsDeploying = false;

                        __result = Task.CompletedTask;

                        return false;

                    }

                }



                // If waiting and not released, suppress deployment

                if (TurnCoordinator.IsWaiting && !TurnCoordinator.HasReleasedThisTurn)

                {

                    TurnCoordinator.IsDeploying = false;

                    __result = Task.CompletedTask;

                    return false;

                }



                // Directive #3.9.1: Check for empty plan with physical cards before deploying

                if (!TurnCoordinator.CheckEmptyPlanRecovery(state, player2, __args, ref __result))

                {

                    TurnCoordinator.IsDeploying = false;

                    return false;

                }



                TurnCoordinator.RecalcSucceeded = true;

                TurnCoordinator.HasExecutedActionInDeployment = false;

                TurnCoordinator.IsDeploying = true;

                TurnCoordinator.DeployStartTime = DateTime.UtcNow;



                if (TurnCoordinator.HasReleasedThisTurn)

                {

                    object currentResult = (__args != null && __args.Length > 2) ? __args[2] : ((__args != null && __args.Length > 1) ? __args[1] : null);

                    bool isFresh = currentResult != null && !object.ReferenceEquals(currentResult, TurnCoordinator.CachedDeployResult);

                    if (isFresh)

                    {

                        GD.Print("[AiTeammate][Coord] fresh-deploy generation=" + TurnCoordinator.CurrentRecalcGeneration + " timestamp=" + DateTime.UtcNow.ToString("HH:mm:ss.fff"));

                    }

                    else

                    {

                        GD.Print("[AiTeammate][Coord] Deploying after release trigger=" + (TurnCoordinator.LastReleaseTrigger ?? "cached") + " (cached plan)");

                    }

                }



                // Directive #5.13 T1: Zero context touch during P2 automated deployment

                SetPlayerActionsDisabled(cm, true);

                GD.Print("[AiTeammate] Deploying cards for Player 2 (" + player2.NetId + ") with zero context touch (P1 retains control).");

                return true;

            }

            catch (Exception ex)

            {

                // Fail-Open Guarantee: on any exception, deploy immediately!

                GD.PrintErr("[AiTeammate][Coord] DeployCurrentTurnPrefix fail-open exception: " + ex);

                TurnCoordinator.IsDeploying = true;

                TurnCoordinator.DeployStartTime = DateTime.UtcNow;

                try

                {

                    if (TurnCoordinator.IsPlayer1Dead())

                    {

                        var cm = CombatManager.Instance;

                        var state = cm != null ? cm.DebugOnlyGetState() : null;

                        if (state != null && state.Players != null && state.Players.Count >= 2)

                        {

                            var player2 = state.Players[state.Players.Count - 1];

                            if (player2 != null)

                            {

                                SwitchControlToPlayer(player2.NetId, "wakuu-aiteammate-deploy2p");

                                SetPlayerActionsDisabled(cm, true);

                            }

                        }

                    }

                }

                catch { }

                return true;

            }

        }



        // Directive #5.16 T1/T2: RequestSearchPrefix reentrancy guard

        public static void RequestSearchPrefix()

        {

            try

            {

                TurnCoordinator.ArmIndependentSearchHeartbeat("RequestSearchPrefix");

                if (TurnCoordinator.IsSolverDeploying() || TurnCoordinator.IsDeploying)

                {

                    GD.Print("[AiTeammate][Coord] state_mismatch/search reentrancy guard: cancelling stale deployment session");

                    if (TurnCoordinator.CancelDeploymentMethod != null)

                    {

                        TurnCoordinator.CancelDeploymentMethod.Invoke(null, null);

                    }

                    TurnCoordinator.IsDeploying = false;

                    TurnCoordinator.CleanHangingDeployment();

                    TurnCoordinator.MidDeployStartTime = DateTime.MinValue;

                    TurnCoordinator.MidDeployHangTimeoutTriggered = false;

                    TurnCoordinator.MidDeployHangCriticalTriggered = false;

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate][Coord] RequestSearchPrefix error: " + ex);

            }

        }



        public static void CompleteDeploymentPostfix()

        {
            TurnCoordinator.DisarmIndependentSearchHeartbeat("deployment-complete");

            if (TurnCoordinator.IsSuppressingCancelHook)

            {

                return;

            }

            Player p2Check = FlowCoordinator.GetPlayer2();
            if (TurnCoordinator.IsPreCombatOrLoading || (TurnCoordinator.P2TurnStartHandCount <= 0 && TurnCoordinator.GetPlayerHandCardCount(p2Check) == 0))
            {
                TurnCoordinator.IsDeploying = false;
                TurnCoordinator.MidDeployStartTime = DateTime.MinValue;
                GD.Print("[AiTeammate][Coord] complete-deployment-precombat-bypassed");
                return;
            }

            // Directive #3.6.5: Empty turn telemetry

            // Final turn summaries are emitted after deployment/recovery state is known.

            TurnCoordinator.IsDeploying = false;

            TurnCoordinator.HasCompletedDeploymentThisTurn = true;

            TurnCoordinator.MidDeployStartTime = DateTime.MinValue;

            TurnCoordinator.MidDeployHangTimeoutTriggered = false;

            TurnCoordinator.MidDeployHangCriticalTriggered = false;

            TurnCoordinator.DeploymentEndTime = DateTime.UtcNow;
            try
            {
                var cmNow = CombatManager.Instance;
                var stNow = cmNow != null ? cmNow.DebugOnlyGetState() : null;
                var p2Now = stNow != null && stNow.Players != null && stNow.Players.Count > 1 ? stNow.Players[stNow.Players.Count - 1] : null;
                string reason = TurnCoordinator.P2TurnEndReason != "normal" ? TurnCoordinator.P2TurnEndReason : (TurnCoordinator.P2FallbackGreedyUsedThisTurn ? "fallback-greedy" : (TurnCoordinator.HasRetriedEmptyPlanThisTurn ? "plan-empty" : "normal"));
                bool hasPlayable;
                bool hasAttack;
                string earlyReason = TurnCoordinator.DetectSolverEarlyEnd(p2Now, out hasPlayable, out hasAttack);
                if (!string.IsNullOrEmpty(earlyReason))
                {
                    TurnCoordinator.UpdateSearchTelemetry("solver-early-end");
                    GD.Print(string.Format("[AiTeammate][Coord] solver-early-end reason={0} search-status={1} search-nodes={2} plan-len={3} playable={4} attack={5}", earlyReason, TurnCoordinator.P2SearchStatusThisTurn, TurnCoordinator.P2SearchNodesThisTurn, TurnCoordinator.P2PlanLengthThisTurn, hasPlayable, hasAttack));
                    TurnCoordinator.SolverEarlyEndReportedThisTurn = true;
                    if (earlyReason == "plan-empty" && !TurnCoordinator.HasRetriedEmptyPlanThisTurn)
                    {
                        TurnCoordinator.HasRetriedEmptyPlanThisTurn = true;
                        GD.Print("[AiTeammate][Coord] plan-empty-recalc-once");
                        TurnCoordinator.RequestRecalculation(stNow, "plan-empty-retry");
                        reason = "plan-empty";
                    }
                    else if (!TurnCoordinator.HasRetriedSolverEarlyEndThisTurn)
                    {
                        GD.Print("[AiTeammate][Coord] solver-early-end-recalc-once reason=" + earlyReason);
                        TurnCoordinator.RequestRecalculation(stNow, "solver-early-end-" + earlyReason);
                        reason = "cancel";
                    }
                    else
                    {
                        GD.Print("[AiTeammate][Coord] guard-override reason=" + earlyReason + " action=fallback-greedy");
                        if (!TurnCoordinator.P2FallbackGreedyUsedThisTurn) TurnCoordinator.TryFallbackGreedy(p2Now);
                        reason = TurnCoordinator.P2FallbackGreedyUsedThisTurn ? "fallback-greedy" : "cancel";
                    }
                }
                bool summaryReady = TurnCoordinator.P2PlayedCardsThisTurn > 0 || TurnCoordinator.P2ZeroActionProtectionFinalizedThisTurn || !hasPlayable || (p2Now != null && p2Now.PlayerCombatState != null && p2Now.PlayerCombatState.Energy <= 0);
                if (summaryReady)
                {
                    TurnCoordinator.P2ZeroActionProtectionFinalizedThisTurn = true;
                    TurnCoordinator.LogP2TurnSummary(reason);
                }
                else
                {
                    GD.Print("[AiTeammate][Coord] p2-summary-deferred turn=" + TurnCoordinator.TurnNumber + " reason=deployment-awaiting-action-execution");
                }
            }
            catch (Exception exSummary) { GD.PrintErr("[AiTeammate][Coord] p2 summary/fallback error: " + exSummary.Message); }



            // Directive #3.9.4: Arm Hold state if combat active and P2 has finished deployment

            if (TurnCoordinator.HoldEnabled && !TurnCoordinator.HasReleasedHoldThisTurn)

            {

                TurnCoordinator.HoldActive = true;

                TurnCoordinator.HoldStartTime = DateTime.UtcNow;

            }



            TurnCoordinator.MarkP1RestoreAfterP2Scope("complete-deployment");

        }



        public static void OnEndedTurnLocallyPostfix()

        {

            ulong endedOwner = TurnCoordinator.ResolveEndedTurnOwner();
            ulong p2Id = TurnCoordinator.GetPlayer2NetId();
            if (p2Id != 0 && endedOwner == p2Id)
            {
                TurnCoordinator.MarkP1RestoreAfterP2Scope("on-ended-turn-p2");
                GD.Print("[AiTeammate][Coord] OnEndedTurnLocallyPostfix owner=P2 restore-deferred scopeDepth=" + TurnCoordinator.P2ActionSenderScopeDepth);
                if (TurnCoordinator.P2ActionSenderScopeDepth > 0 && !TurnCoordinator.IsPlayer2ActionInFlight(false))
                {
                    TurnCoordinator.ResetState("OnEndedTurnLocally:P2End");
                }
                return;
            }

            if (TurnCoordinator.IsDeploying || (TurnCoordinator.HasReleasedThisTurn && !TurnCoordinator.RecalcSucceeded) || (TurnCoordinator.HoldEnabled && TurnCoordinator.HoldActive))

            {

                var cm = CombatManager.Instance;

                var state = cm != null ? cm.DebugOnlyGetState() : null;

                if (state != null && state.Players != null && state.Players.Count >= 2)

                {

                    var p1 = state.Players[0];

                    if (p1 != null && cm.IsPlayerReadyToEndTurn(p1))

                    {

                        GD.Print("[AiTeammate][Coord] OnEndedTurnLocallyPostfix: P1 ended turn while P2 is deploying or on hold, releasing hold.");

                        TurnCoordinator.ReleaseHold("p1-end-turn");

                    }

                }

                GD.Print("[AiTeammate][Coord] OnEndedTurnLocallyPostfix: P2 is actively deploying, recalculating or on hold, deferring control restore.");

                return;

            }

            TurnCoordinator.ResetTurnState("OnEndedTurnLocally");

            RestorePlayer1ControlIfActive("wakuu-p1-restore-after-p1-end", true);

        }



        public static void RestorePlayer1ControlIfActive(string source, bool internalP1Recovery = true)

        {

            try

            {
                if (internalP1Recovery && TurnCoordinator.P2ActionSenderScopeDepth > 0)
                {
                    TurnCoordinator.MarkP1RestoreAfterP2Scope(source ?? "restore-request");
                    GD.Print("[AiTeammate][Coord] p1-restore-deferred scopeDepth=" + TurnCoordinator.P2ActionSenderScopeDepth + " source=" + (source ?? "unknown"));
                    return;
                }

                var cm = CombatManager.Instance;
                var state = cm != null ? cm.DebugOnlyGetState() : null;

                if (cm == null || state == null || !TurnCoordinator.IsInCombat())
                {
                    ulong p1IdOut = TurnCoordinator.GetPlayer1NetId();
                    if (p1IdOut != 0)
                    {
                        ulong draId = GetDraCurrentControlledPlayerId();
                        if ((draId != 0 && draId != p1IdOut) || (TurnCoordinator.P2ActionSenderScopeDepth == 0 && LocalContext.NetId.HasValue && LocalContext.NetId.Value != p1IdOut))
                        {
                            LocalContext.NetId = p1IdOut;
                            SwitchControlToPlayer(p1IdOut, "wakuu-p1-restore:" + (source ?? "out-of-combat"), true, true);
                        }
                    }
                    return;
                }

                if (state.Players == null || state.Players.Count < 2)
                    return;

                var player1 = state.Players[0];

                if (player1 == null || player1.Creature == null || !player1.Creature.IsAlive)

                {

                    // Player 1 is dead. Switch logical control to Player 2 with UI suppressed (death spectator: empty hand, corpse state)

                    var player2 = state.Players[state.Players.Count - 1];

                    if (player2 != null && player2.Creature != null && player2.Creature.IsAlive)

                    {

                        SwitchControlToPlayer(player2.NetId, "wakuu-player1-dead", true);

                        if (!cm.IsPlayerReadyToEndTurn(player2))

                        {

                            SetPlayerActionsDisabled(cm, false);

                        }

                        if (player1 != null)

                        {

                            TurnCoordinator.RebindHandContainerToPlayer1(cm, state, player1);

                        }

                        GD.Print("[AiTeammate] Player 1 is dead. Switched logical control to Player 2 (" + player2.NetId + ") with UI suppressed (corpse spectator view).");

                    }

                    return;

                }



                ulong p1Id = TurnCoordinator.GetPlayer1NetId();
                ulong draCurrentId = TurnFlowPatches.GetDraCurrentControlledPlayerId();

                if (LocalContext.NetId != p1Id || (TurnCoordinator.FixedP1CombatControl && draCurrentId != 0 && draCurrentId != p1Id))

                {

                    SwitchControlToPlayer(player1.NetId, source, true, internalP1Recovery);

                }

                if (!TurnCoordinator.FixedP1CombatControl && !cm.IsPlayerReadyToEndTurn(player1))

                {

                    SetPlayerActionsDisabled(cm, false);

                }

                RefreshPlayer1CombatUiMinimal(cm, state, player1);

                TurnCoordinator.RefreshPlayerHandVisuals(state);

                if (TurnCoordinator.FixedP1CombatControl) TurnCoordinator.LogFixedP1StateCheck("restore-p1");
                LogP1RestoreResult(cm, state, player1, source);

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate] RestorePlayer1ControlIfActive error: " + ex);

            }

        }



        private static void LogP1RestoreResult(CombatManager cm, CombatState state, Player player1, string source)
        {
            try
            {
                object phase = TurnCoordinator.GetMemberValue(state, "Phase") ?? TurnCoordinator.GetMemberValue(cm, "Phase");
                object disabled = TurnCoordinator.GetMemberValue(cm, "PlayerActionsDisabled");
                bool ready = player1 != null && cm != null && cm.IsPlayerReadyToEndTurn(player1);
                string buttonState = "unknown";
                var room = NCombatRoom.Instance;
                var ui = room != null ? room.Ui : null;
                var button = ui != null ? ui.EndTurnButton : null;
                if (button != null)
                {
                    object value = TurnCoordinator.GetMemberValue(button, "State");
                    if (value != null) buttonState = value.ToString();
                }
                GD.Print(string.Format("[AiTeammate][Coord] p1-restore-result source={0} action-owner={1} scopeDepth={2} LocalContext={3} DRA={4} phase={5} p1-ready={6} PlayerActionsDisabled={7} button-state={8}",
                    source ?? "unknown", TurnCoordinator.CurrentExecutingActionOwnerNetId, TurnCoordinator.P2ActionSenderScopeDepth,
                    LocalContext.NetId.HasValue ? LocalContext.NetId.Value.ToString() : "none",
                    GetDraCurrentControlledPlayerId(), phase ?? "unknown", ready, disabled ?? "unknown", buttonState));
            }
            catch (Exception ex) { GD.PrintErr("[AiTeammate][Coord] p1-restore-result failed: " + ex.Message); }
        }

        public static void RefreshPlayer1CombatUiMinimal()

        {

            try

            {
                if (TurnCoordinator.P2ActionSenderScopeDepth > 0)
                {
                    TurnCoordinator.MarkP1RestoreAfterP2Scope("refresh-request");
                    GD.Print("[AiTeammate][Coord] p1-refresh-deferred scopeDepth=" + TurnCoordinator.P2ActionSenderScopeDepth);
                    return;
                }

                var cm = CombatManager.Instance;

                var state = cm != null ? cm.DebugOnlyGetState() : null;

                var p1 = (state != null && state.Players != null && state.Players.Count > 0) ? state.Players[0] : null;

                RefreshPlayer1CombatUiMinimal(cm, state, p1);

            }

            catch { }

        }



        private static void RefreshPlayer1CombatUiMinimal(CombatManager cm, CombatState state, Player player1)

        {

            try

            {

                if (player1 == null || player1.Creature == null || !player1.Creature.IsAlive)

                {

                    return;

                }



                var room = NCombatRoom.Instance;

                var combatUi = room != null ? room.Ui : null;



                var multiRuntimeType = AccessTools.TypeByName("LocalMultiControl.Scripts.Runtime.LocalMultiControlRuntime");

                bool energyRefreshed = false;



                if (combatUi != null)

                {

                    // 1. Refresh pile counts to Player 1

                    try

                    {

                        if (combatUi.DrawPile != null)

                        {

                            combatUi.DrawPile.Initialize(player1);

                        }

                        if (combatUi.DiscardPile != null)

                        {

                            combatUi.DiscardPile.Initialize(player1);

                        }

                        if (combatUi.ExhaustPile != null)

                        {

                            combatUi.ExhaustPile.Initialize(player1);

                        }

                    }

                    catch (Exception exPiles)

                    {

                        GD.PrintErr("[AiTeammate] RefreshPlayer1CombatUiMinimal pile init error: " + exPiles.Message);

                    }



                    // 2. Ensure EndTurnButton references Player 1's hand pile

                    try

                    {

                        var handPile = PileType.Hand.GetPile(player1);

                        if (combatUi.EndTurnButton != null && handPile != null)

                        {

                            var playerHandField = AccessTools.Field(combatUi.EndTurnButton.GetType(), "_playerHand");

                            if (playerHandField != null)

                            {

                                playerHandField.SetValue(combatUi.EndTurnButton, handPile);

                            }

                        }

                    }

                    catch (Exception exBtn)

                    {

                        GD.PrintErr("[AiTeammate] RefreshPlayer1CombatUiMinimal EndTurnButton hand error: " + exBtn.Message);

                    }



                    // 3. Minimal energy refresh entrypoint: RefreshCombatEnergyUi

                    if (multiRuntimeType != null)

                    {

                        try

                        {

                            var refreshEnergyUiMethod = AccessTools.Method(multiRuntimeType, "RefreshCombatEnergyUi", new Type[] { typeof(NCombatUi), typeof(Player) });

                            if (refreshEnergyUiMethod != null)

                            {

                                refreshEnergyUiMethod.Invoke(null, new object[] { combatUi, player1 });

                                energyRefreshed = true;

                                GD.Print("[AiTeammate] RefreshCombatEnergyUi executed for Player 1 (" + player1.NetId + ").");

                            }

                        }

                        catch (Exception exEnergy)

                        {

                            GD.PrintErr("[AiTeammate] RefreshCombatEnergyUi error: " + exEnergy.Message);

                        }



                        // 4. Re-evaluate End Turn button state

                        if (combatUi.EndTurnButton != null)

                        {

                            try

                            {

                                if (state == null && cm != null)

                                {

                                    state = cm.DebugOnlyGetState();

                                }

                                if (state != null && TurnCoordinator.IsPlayPhase())

                                {

                                    var reevalMethod = AccessTools.Method(multiRuntimeType, "ReevaluateEndTurnButtonState", new Type[] { typeof(NCombatUi), typeof(CombatState), typeof(Player) });

                                    if (reevalMethod != null)

                                    {

                                        reevalMethod.Invoke(null, new object[] { combatUi, state, player1 });

                                    }

                                }

                            }

                            catch (Exception exReeval)

                            {

                                GD.PrintErr("[AiTeammate] ReevaluateEndTurnButtonState error: " + exReeval.Message);

                            }

                        }

                    }



                    // 5. Fallback minimal energy refresh via public entrypoint RefreshCombatEnergyForCurrentPlayer (only if Step 3 failed, combatUi is present, and LocalContext is player1)

                    if (multiRuntimeType != null && !energyRefreshed && LocalContext.NetId == player1.NetId)

                    {

                        try

                        {

                            var refreshForCurrentMethod = AccessTools.Method(multiRuntimeType, "RefreshCombatEnergyForCurrentPlayer", new Type[] { typeof(string) });

                            if (refreshForCurrentMethod != null)

                            {

                                refreshForCurrentMethod.Invoke(null, new object[] { "wakuu-restore1p" });

                                GD.Print("[AiTeammate] Fallback RefreshCombatEnergyForCurrentPlayer executed for Player 1.");

                            }

                        }

                        catch (Exception exCur)

                        {

                            GD.PrintErr("[AiTeammate] RefreshCombatEnergyForCurrentPlayer error: " + exCur.Message);

                        }

                    }

                }



                // 6. Refresh top bar (HP/gold/deck count) for Player 1

                if (multiRuntimeType != null)

                {

                    try

                    {

                        var refreshTopBarMethod = AccessTools.Method(multiRuntimeType, "RefreshTopBarForControlledPlayer", new Type[] { typeof(ulong) });

                        if (refreshTopBarMethod != null)

                        {

                            refreshTopBarMethod.Invoke(null, new object[] { player1.NetId });

                        }

                    }

                    catch (Exception exTop)

                    {

                        GD.PrintErr("[AiTeammate] RefreshTopBarForControlledPlayer error: " + exTop.Message);

                    }

                }



                // 7. Refresh Player 1 hand card energy cost colors

                TurnCoordinator.RefreshPlayerHandVisuals(state);

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate] RefreshPlayer1CombatUiMinimal general error: " + ex);

            }

        }



        public static void SwitchControlToPlayer(ulong targetNetId, string source, bool suppressUi = true, bool allowP1Recovery = false)

        {

            if (string.IsNullOrEmpty(source) || !source.StartsWith("wakuu-", StringComparison.OrdinalIgnoreCase))

            {

                source = "wakuu-" + (source ?? "aiteammate");

            }
            if (allowP1Recovery && targetNetId == TurnCoordinator.GetPlayer1NetId() && !TurnCoordinator.IsP1RecoverySource(source))
            {
                source = "wakuu-p1-restore:" + source;
            }

            if (TurnCoordinator.IsInEventRoom() && TurnCoordinator.IsP2Target(targetNetId) && TurnCoordinator.IsAutoEventSource(source))
            {
                GD.Print(string.Format("[AiTeammate][Event] event-auto-switch-suppressed source={0} target=P2", source ?? "unknown"));
                return;
            }

            if (TurnCoordinator.IsFullRunP1ControlLockActive() && TurnCoordinator.IsP2Target(targetNetId) && !(TurnCoordinator.IsInCombat() && TurnCoordinator.IsTurnBootstrapSource(source)))
            {
                GD.Print("[AiTeammate][Coord] p2-foreground-switch-blocked source=" + (source ?? "unknown") + " target=" + targetNetId);
                TurnCoordinator.LogFixedP1StateCheck("direct-switch-blocked", targetNetId);
                return;
            }



            // Directive #5.14 T1: Room enter window control switch ban

            if (TurnCoordinator.IsEnteringRoom)

            {

                bool isModSource = !SwitchSourceAuditor.IsInteractive(source) || (source != null && source.StartsWith("wakuu-", StringComparison.OrdinalIgnoreCase));

                if (isModSource)

                {

                    GD.PrintErr(string.Format("[AiTeammate][Coord] control-switch-deferred-during-room-enter source={0} target={1}", source ?? "null", targetNetId));

                    TurnCoordinator.QueueDeferredModSwitch(targetNetId, source, suppressUi);

                    return;

                }

            }



            bool previousSuppressUi = ModEntry.SuppressControlSwitchUi;
            if (suppressUi)

            {

                ModEntry.SuppressControlSwitchUi = true;

            }

            try

            {

                var multiRuntimeType = AccessTools.TypeByName("LocalMultiControl.Scripts.Runtime.LocalMultiControlRuntime");

                if (multiRuntimeType == null) return;



                var switchMethod = AccessTools.Method(multiRuntimeType, "SwitchControlledPlayerTo", new Type[] { typeof(ulong), typeof(string) });

                if (switchMethod != null)

                {

                    switchMethod.Invoke(null, new object[] { targetNetId, source });

                }

                else

                {

                    var applyMethod = AccessTools.Method(multiRuntimeType, "ApplyControlContext", new Type[] { typeof(string) });

                    if (applyMethod != null)

                    {

                        applyMethod.Invoke(null, new object[] { source });

                    }

                }

            }

            catch (Exception ex)

            {

                GD.PrintErr("[AiTeammate] SwitchControlToPlayer error: " + ex);

            }

            finally

            {

                ModEntry.SuppressControlSwitchUi = previousSuppressUi;

            }



            // Directive #3.9: Automated assertion of control switch logic

            AssertSwitchLogic(targetNetId, source);

        }



        public static ulong GetDraCurrentControlledPlayerId()

        {

            try

            {

                var multiRuntimeType = AccessTools.TypeByName("LocalMultiControl.Scripts.Runtime.LocalMultiControlRuntime");

                if (multiRuntimeType != null)

                {

                    var sessionField = AccessTools.Field(multiRuntimeType, "Session");

                    var sessionObj = sessionField != null ? sessionField.GetValue(null) : null;

                    if (sessionObj != null)

                    {

                        var prop = AccessTools.Property(sessionObj.GetType(), "CurrentControlledPlayerId");

                        if (prop != null)

                        {

                            var val = prop.GetValue(sessionObj, null);

                            if (val is ulong) return (ulong)val;

                        }

                    }

                }

            }

            catch { }

            return 0;

        }



        public static void AssertSwitchLogic(ulong targetNetId, string source)

        {

            try

            {

                ulong localNetId = LocalContext.NetId.HasValue ? LocalContext.NetId.Value : 0;

                ulong draSessionId = GetDraCurrentControlledPlayerId();

                if (localNetId != targetNetId || (draSessionId != 0 && draSessionId != targetNetId))

                {

                    GD.PrintErr("[AiTeammate][Coord] switch-logic-fail: expected=" + targetNetId + " localContext=" + localNetId + " draSession=" + draSessionId + " source=" + source);

                }

            }

            catch { }

        }



        internal static void SetPlayerActionsDisabled(CombatManager cm, bool disabled)

        {

            // Directive #5.35 T2.1: Never disable player actions during fixed P1 combat control while P1 alive
            if (disabled && TurnCoordinator.IsFixedP1CombatControlActive() && !TurnCoordinator.IsPlayer1Dead())
            {
                return;
            }

            var setter = AccessTools.PropertySetter(typeof(CombatManager), "PlayerActionsDisabled");

            if (setter != null)

            {

                setter.Invoke(cm, new object[] { disabled });

            }

        }

    }

}







