using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Nodes.TreasureRooms;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using Sts2Headless;

namespace Sts2Bridge;

/// <summary>
/// Main-thread live game state poller, canonical Decision builder, observation encoder,
/// and UI-safe action dispatcher. Produces bit-compatible frames with Sts2Headless.RunSession.
/// </summary>
internal sealed class LiveSessionDriver
{
    static readonly HashSet<string> ExcludedEventOptions = new() { "TRIAL.pages.REJECT.options.DOUBLE_DOWN" };

    // Cached reflection handles for UI screen internals (read-only inspection + button click helpers)
    static readonly FieldInfo? RewardsSetField = AccessTools.Field(typeof(NRewardsScreen), "_rewardsSet");
    static readonly FieldInfo? RewardsButtonsField = AccessTools.Field(typeof(NRewardsScreen), "_rewardButtons");
    static readonly FieldInfo? RewardsSkippedField = AccessTools.Field(typeof(NRewardsScreen), "_skippedRewardButtons");
    static readonly FieldInfo? RewardsProceedField = AccessTools.Field(typeof(NRewardsScreen), "_proceedButton");

    static readonly FieldInfo? CardRewardOptsField = AccessTools.Field(typeof(NCardRewardSelectionScreen), "_options");
    static readonly FieldInfo? CardRewardExtraField = AccessTools.Field(typeof(NCardRewardSelectionScreen), "_extraOptions");
    static readonly MethodInfo? CardRewardSelectMethod = AccessTools.Method(typeof(NCardRewardSelectionScreen), "SelectCard");
    static readonly MethodInfo? CardRewardAltMethod = AccessTools.Method(typeof(NCardRewardSelectionScreen), "OnAlternateRewardSelected");

    static readonly FieldInfo? ChooseCardListField = AccessTools.Field(typeof(NChooseACardSelectionScreen), "_cards");
    static readonly FieldInfo? ChooseCardCanSkipField = AccessTools.Field(typeof(NChooseACardSelectionScreen), "_canSkip");
    static readonly FieldInfo? ChooseCardCompleteField = AccessTools.Field(typeof(NChooseACardSelectionScreen), "_screenComplete");
    static readonly FieldInfo? ChooseCardRowField = AccessTools.Field(typeof(NChooseACardSelectionScreen), "_cardRow");
    static readonly FieldInfo? ChooseCardSkipBtnField = AccessTools.Field(typeof(NChooseACardSelectionScreen), "_skipButton");
    static readonly MethodInfo? ChooseCardSelectHolderMethod = AccessTools.Method(typeof(NChooseACardSelectionScreen), "SelectHolder");

    static readonly FieldInfo? GridCardsField = AccessTools.Field(typeof(NCardGridSelectionScreen), "_cards");
    static readonly MethodInfo? GridCardClickedMethod = AccessTools.Method(typeof(NCardGridSelectionScreen), "OnCardClicked");

    static readonly FieldInfo? HandSelectedCardsField = AccessTools.Field(typeof(NPlayerHand), "_selectedCards");
    static readonly FieldInfo? HandPrefsField = AccessTools.Field(typeof(NPlayerHand), "_prefs");
    static readonly FieldInfo? HandFilterField = AccessTools.Field(typeof(NPlayerHand), "_currentSelectionFilter");
    static readonly FieldInfo? HandConfirmBtnField = AccessTools.Field(typeof(NPlayerHand), "_selectModeConfirmButton");
    static readonly MethodInfo? HandHolderPressedMethod = AccessTools.Method(typeof(NPlayerHand), "OnHolderPressed");

    static readonly FieldInfo? BundleListField = AccessTools.Field(typeof(NChooseABundleSelectionScreen), "_bundles");
    static readonly FieldInfo? BundleRowField = AccessTools.Field(typeof(NChooseABundleSelectionScreen), "_bundleRow");
    static readonly FieldInfo? BundleConfirmBtnField = AccessTools.Field(typeof(NChooseABundleSelectionScreen), "_previewConfirmButton");
    static readonly MethodInfo? BundleClickedMethod = AccessTools.Method(typeof(NChooseABundleSelectionScreen), "OnBundleClicked");
    static readonly MethodInfo? BundleConfirmMethod = AccessTools.Method(typeof(NChooseABundleSelectionScreen), "ConfirmSelection");

    static readonly FieldInfo? SphereEntityField = AccessTools.Field(typeof(NCrystalSphereScreen), "_entity");
    static readonly FieldInfo? SphereCellContainerField = AccessTools.Field(typeof(NCrystalSphereScreen), "_cellContainer");
    static readonly FieldInfo? SphereSmallBtnField = AccessTools.Field(typeof(NCrystalSphereScreen), "_smallDivinationButton");
    static readonly FieldInfo? SphereBigBtnField = AccessTools.Field(typeof(NCrystalSphereScreen), "_bigDivinationButton");
    static readonly FieldInfo? SphereProceedBtnField = AccessTools.Field(typeof(NCrystalSphereScreen), "_proceedButton");
    static readonly MethodInfo? SphereCellClickedMethod = AccessTools.Method(typeof(NCrystalSphereScreen), "OnCellClicked");

    static readonly FieldInfo? FakeMerchProceedField = AccessTools.Field(typeof(NFakeMerchant), "_proceedButton");
    static readonly FieldInfo? TreasureOpenedField = AccessTools.Field(typeof(NTreasureRoom), "_hasChestBeenOpened");
    static readonly FieldInfo? TreasureChestBtnField = AccessTools.Field(typeof(NTreasureRoom), "_chestButton");
    static readonly FieldInfo? TreasureRelicOpenField = AccessTools.Field(typeof(NTreasureRoom), "_isRelicCollectionOpen");
    static readonly FieldInfo? TreasureCollectionField = AccessTools.Field(typeof(NTreasureRoom), "_relicCollection");
    static readonly PropertyInfo? TreasureSingleHolderProp = AccessTools.Property(typeof(NTreasureRoomRelicCollection), "SingleplayerRelicHolder");
    static readonly FieldInfo? TreasureHoldersField = AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_holdersInUse");
    static readonly MethodInfo? TreasurePickMethod = AccessTools.Method(typeof(NTreasureRoomRelicCollection), "PickRelic");

    static readonly FieldInfo? MapDictField = AccessTools.Field(typeof(NMapScreen), "_mapPointDictionary");
    static readonly FieldInfo? OverlayListField = AccessTools.Field(typeof(NOverlayStack), "_overlays");

    readonly BridgeConfig _cfg;
    readonly BridgeLink _link;
    readonly RunSession _shimSession = new();

    RunState? _trackedRun;
    int _lastReconnectEpoch = -1;
    bool _runEndSent;

    AbstractRoom? _room;
    bool _roomLeft;
    bool _restChosen;
    bool _relicDecided;
    bool _shopInitialized;
    AbstractRoom? _cancelRoom;
    readonly HashSet<object> _failed = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<Reward> _attemptedRewards = new(ReferenceEqualityComparer.Instance);
    RewardsSet? _lastRewardsSet;
    RestSiteSynchronizer? _subscribedRestSync;

    int _seq;
    string? _currentSignature;
    ulong _stableSinceMs;
    ulong _cooldownUntilMs;
    Decision? _activeDecision;
    int _activeSeq;
    bool _waitingReply;
    PolicyReply? _latestReply;
    bool _stepOnceRequested;
    string? _statusNote;

    public bool AutoPlay { get; set; }
    public string ActiveDecisionKind => _activeDecision?.Kind.ToString() ?? "";
    public int ActiveSeq => _activeSeq;
    public bool WaitingReply => _waitingReply;
    public PolicyReply? LatestReply => _latestReply;
    public string? StatusNote => _statusNote;

    public LiveSessionDriver(BridgeConfig cfg, BridgeLink link)
    {
        _cfg = cfg;
        _link = link;
        AutoPlay = cfg.AutoPlay;
    }

    public void RequestStepOnce() => _stepOnceRequested = true;

    public void Tick()
    {
        // Drain incoming replies from play_live.py
        while (_link.TryDequeueReply(out var reply))
        {
            if (reply != null && reply.Seq == _activeSeq)
            {
                _latestReply = reply;
                _waitingReply = false;
            }
        }

        ulong now = Time.GetTicksMsec();
        if (!RunManager.Instance.IsInProgress || RunManager.Instance.IsCleaningUp || RunManager.Instance.DebugOnlyGetState() is not { } state)
        {
            HandleNoActiveRun();
            return;
        }

        EnsureRunSubscriptions(state);

        if (state.IsGameOver || RunManager.Instance.IsGameOver || NOverlayStack.Instance?.Peek() is NGameOverScreen)
        {
            if (!_runEndSent)
            {
                bool victory = BridgePatches.LastRunVictory ?? (!state.Players[0].Creature.IsDead);
                _shimSession.IsVictory = victory;
                _link.SendRunEnd(victory, state.CurrentActIndex, state.TotalFloor);
                _runEndSent = true;
                BridgeLog.Info($"Run ended (victory={victory}, act={state.CurrentActIndex + 1}, floor={state.TotalFloor}).");
            }
            _activeDecision = null;
            _statusNote = (_shimSession.IsVictory ? "Run Victory!" : "Run Defeated");
            return;
        }

        if (now < _cooldownUntilMs)
            return;

        // Poll whether the game is at a stable player decision point (or needs a deterministic non-decision UI step)
        RewardsFlow.Reset();
        FakeScreens.CrystalSphere = null;
        _shimSession.Selecting = null;
        _shimSession.SelectedIndices = Array.Empty<int>();

        var poll = PollDecisionOrAutoStep(state, out var autoStep, out string? autoDesc);
        if (autoStep != null)
        {
            if (AutoPlay || _stepOnceRequested)
            {
                _stepOnceRequested = false;
                ExecuteSafely(autoDesc ?? "auto-step", autoStep, incrementStep: false);
                _cooldownUntilMs = now + (ulong)Math.Max(120, _cfg.ActionDelayMs / 2);
            }
            else
            {
                _statusNote = $"Ready: {autoDesc} (press F9 or click)";
            }
            return;
        }

        if (poll == null || poll.Choices.Count == 0)
        {
            _currentSignature = null;
            _activeDecision = null;
            _waitingReply = false;
            return;
        }

        _statusNote = null;
        string sig = ComputeSignature(state, poll);
        if (sig != _currentSignature)
        {
            _currentSignature = sig;
            _stableSinceMs = now;
            _activeDecision = null;
            _latestReply = null;
            _waitingReply = false;
            return;
        }

        // Wait for animations / transitions to settle for StabilizeMs before encoding & sending
        if (_activeDecision == null)
        {
            if (now - _stableSinceMs < (ulong)Math.Max(30, _cfg.StabilizeMs))
                return;

            _activeDecision = poll;
            _activeSeq = ++_seq;
            _latestReply = null;
            _waitingReply = true;
            SendDecisionFrame(state, poll, _activeSeq);
            return;
        }

        // Refresh active decision closures while keeping _activeSeq if state signature hasn't changed
        _activeDecision = poll;

        if (_latestReply is { } rep && rep.Seq == _activeSeq && (AutoPlay || _stepOnceRequested))
        {
            _stepOnceRequested = false;
            int idx = rep.Action;
            if (idx >= 0 && idx < poll.Choices.Count)
            {
                var choice = poll.Choices[idx];
                BridgeLog.Info($"Executing #{_activeSeq} [{idx}/{poll.Choices.Count}] {choice.Label} (V={rep.Value:+0.00;-0.00}, searched={rep.Searched})");
                _latestReply = null;
                _activeDecision = null;
                _currentSignature = null;
                ExecuteSafely(choice.Label, choice.Run, incrementStep: true);
                _cooldownUntilMs = now + (ulong)Math.Max(80, _cfg.ActionDelayMs);
            }
        }
    }

    void HandleNoActiveRun()
    {
        if (_trackedRun != null && !_runEndSent)
        {
            _link.SendRunEnd(BridgePatches.LastRunVictory == true, 0, 0);
            _runEndSent = true;
        }
        _trackedRun = null;
        _activeDecision = null;
        _currentSignature = null;
        _waitingReply = false;
        _statusNote = "Main Menu (start a run)";
    }

    void EnsureRunSubscriptions(RunState state)
    {
        var restSync = RunManager.Instance.RestSiteSynchronizer;
        if (!ReferenceEquals(restSync, _subscribedRestSync))
        {
            if (_subscribedRestSync != null)
            {
                try { _subscribedRestSync.AfterPlayerOptionChosen -= OnRestOptionChosen; } catch { }
            }
            _subscribedRestSync = restSync;
            if (restSync != null)
                restSync.AfterPlayerOptionChosen += OnRestOptionChosen;
        }

        int epoch = _link.ReconnectEpoch;
        if (!ReferenceEquals(state, _trackedRun) || epoch != _lastReconnectEpoch)
        {
            bool isNewRunObj = !ReferenceEquals(state, _trackedRun);
            _trackedRun = state;
            _lastReconnectEpoch = epoch;
            if (isNewRunObj)
            {
                _runEndSent = false;
                _shimSession.Steps = 0;
                _shimSession.IsVictory = false;
                _room = null;
                _cancelRoom = null;
                _failed.Clear();
                _attemptedRewards.Clear();
                _currentSignature = null;
                _activeDecision = null;
            }
            var me = state.Players[0];
            string ch = me.Character.Id.Entry;
            string seed = state.Rng?.StringSeed ?? "";
            string gm = state.GameMode.ToString();
            int asc = state.AscensionLevel;
            int lf = BridgePatches.LoadedFloor;
            _link.SendRunInfo(ch, asc, seed, gm, lf);
            BridgeLog.Info($"Sent run_info: character={ch}, ascension={asc}, seed={seed}, mode={gm}, loaded_floor={lf}");
        }
    }

    void OnRestOptionChosen(RestSiteOption option, bool success, ulong _)
    {
        if (success) _restChosen = true;
        else _failed.Add(option);
    }

    void SendDecisionFrame(RunState state, Decision d, int seq)
    {
        try
        {
            Observation.Privileged = false;
            Observation.FutureContent = false;
            Observation.Oracle = false;
            Observation.RouteSummary = _cfg.RouteSummary;
            Observation.Stage = 0;
            Observation.BossPrev = 0;
            Observation.BossNext = 0;

            byte[] raw;
            using (BridgePatches.StripMergedEventVars(d))
            {
                var obs = Observation.Build(_shimSession, d, labels: true);
                raw = obs.Serialize();
            }

            var me = state.Players[0];
            var labels = d.Choices.Select(c => c.Label).ToList();
            _link.SendDecision(seq, d.Kind.ToString(), me.Character.Id.Entry, state.CurrentActIndex, state.TotalFloor, labels, raw);
        }
        catch (Exception e)
        {
            BridgeLog.Error($"Failed to encode/send observation frame #{seq}: {e}");
            _waitingReply = false;
        }
    }

    void ExecuteSafely(string label, Func<Task> action, bool incrementStep)
    {
        try
        {
            if (incrementStep) _shimSession.Steps++;
            var t = action();
            if (t is { IsFaulted: true })
                BridgeLog.Warn($"Action '{label}' faulted: {t.Exception?.GetBaseException().Message}");
        }
        catch (Exception e)
        {
            BridgeLog.Error($"Exception executing '{label}': {e}");
        }
    }

    // -------------------------------------------------------------------------
    // Stabilization & Canonical Decision Enumeration (matching RunSession.cs)
    // -------------------------------------------------------------------------

    Decision? PollDecisionOrAutoStep(RunState s, out Func<Task>? autoStep, out string? autoDesc)
    {
        autoStep = null;
        autoDesc = null;

        if (RunManager.Instance.ActionExecutor is { IsRunning: true })
            return null;

        var me = s.Players[0];
        var topOverlay = NOverlayStack.Instance?.Peek();

        // 1. Overlay screens take highest priority (CardSelect, CardReward, Bundle, Rewards, Minigame)
        if (topOverlay != null)
        {
            switch (topOverlay)
            {
                case NChooseACardSelectionScreen chooseScreen:
                    return BuildChooseACardDecision(s, chooseScreen);
                case NCardGridSelectionScreen gridScreen:
                    return BuildGridCardSelectDecision(s, gridScreen);
                case NCardRewardSelectionScreen cardRewardScreen:
                    return BuildCardRewardDecision(cardRewardScreen);
                case NChooseABundleSelectionScreen bundleScreen:
                    return BuildBundleDecision(bundleScreen);
                case NRewardsScreen rewardsScreen:
                    return BuildRewardsDecision(me, rewardsScreen, out autoStep, out autoDesc);
                case NCrystalSphereScreen sphereScreen:
                    return BuildCrystalSphereDecision(sphereScreen, out autoStep, out autoDesc);
                default:
                    // Inspect screen, pause menu, settings, etc. — wait while player is viewing
                    return null;
            }
        }

        // 2. In-combat hand card selection (NPlayerHand.IsInCardSelection)
        if (CombatManager.Instance.IsInProgress && NPlayerHand.Instance is { IsInCardSelection: true } handSel)
            return BuildHandCardSelectDecision(s, handSel);

        // 3. Active Combat Play Phase
        if (CombatManager.Instance.IsInProgress)
            return BuildCombatDecision(me);

        if (CombatManager.Instance.IsStarting || CombatManager.Instance.IsOverOrEnding)
            return null;

        // Track room changes
        if (!ReferenceEquals(s.CurrentRoom, _room))
        {
            _room = s.CurrentRoom;
            _roomLeft = _restChosen = _relicDecided = _shopInitialized = false;
            _failed.Clear();
        }

        // 4. Open Map Screen
        if (NMapScreen.Instance is { IsOpen: true } mapScreen)
        {
            if (!mapScreen.IsTravelEnabled || mapScreen.IsTraveling) return null;
            return BuildMapDecision(s, me, mapScreen);
        }

        // 5. Out-of-combat Rooms
        switch (s.CurrentRoom)
        {
            case EventRoom { CanonicalEvent: FakeMerchant } when !_roomLeft
                && RunManager.Instance.EventSynchronizer.GetLocalEvent() is FakeMerchant { StartedFight: false } fm:
                return BuildFakeMerchantDecision(me, fm);

            case EventRoom:
            {
                var ev = RunManager.Instance.EventSynchronizer.GetLocalEvent();
                if (ev == null) return null;
                if (ev.IsFinished)
                {
                    if (NEventRoom.Instance != null)
                    {
                        autoDesc = "Proceed from Event";
                        autoStep = () => NEventRoom.Proceed();
                    }
                    return null;
                }
                return BuildEventDecision(me, ev);
            }

            case RestSiteRoom:
                return BuildRestDecision(me, out autoStep, out autoDesc);

            case MerchantRoom shopRoom when !_roomLeft:
                return BuildShopDecision(me, shopRoom);

            case TreasureRoom treasureRoom:
                return BuildTreasureDecision(me, treasureRoom, out autoStep, out autoDesc);

            default:
                if (NMapScreen.Instance is { IsTravelEnabled: true, IsTraveling: false } ms)
                    return BuildMapDecision(s, me, ms);
                return null;
        }
    }

    Decision? BuildCombatDecision(Player me)
    {
        var pcs = me.PlayerCombatState;
        if (pcs == null || pcs.Phase != PlayerTurnPhase.Play) return null;
        if (CombatManager.Instance.IsEnemyTurnStarted || CombatManager.Instance.PlayerActionsDisabled || CombatManager.Instance.IsExecutingCardOrPotionEffect(me))
            return null;
        if (NPlayerHand.Instance is { InCardPlay: true })
            return null;

        var cs = me.Creature.CombatState;
        if (cs == null) return null;

        var d = new Decision(DecisionKind.Combat) { Context = $"Turn {pcs.TurnNumber}" };
        var targets = new List<Creature?> { null };
        targets.AddRange(cs.Creatures.Where(c => c.IsAlive));

        foreach (CardModel c in PileType.Hand.GetPile(me).Cards)
        {
            if (!c.CanPlay(out _, out _)) continue;
            foreach (var t in targets)
            {
                if (c.CanPlayTargeting(t))
                {
                    var card = c;
                    var target = t;
                    d.Add(ActionKind.PlayCard, $"Play {c.Id.Entry}{TargetLabel(t)}", () =>
                    {
                        card.TryManualPlay(target);
                        return Task.CompletedTask;
                    }, a: c, b: t);
                }
            }
        }

        foreach (var p in UsablePotions(me, inCombat: true))
        {
            foreach (var t in targets)
            {
                if (p.IsValidTarget(t))
                {
                    var pot = p;
                    var target = t;
                    d.Add(ActionKind.UsePotion, $"Use {p.Id.Entry}{TargetLabel(t)}", () =>
                    {
                        pot.EnqueueManualUse(target);
                        return Task.CompletedTask;
                    }, a: p, b: t);
                }
            }
        }

        int turn = pcs.TurnNumber;
        d.Add(ActionKind.EndTurn, "End Turn", () =>
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(me, turn));
            return Task.CompletedTask;
        });

        return d;
    }

    Decision? BuildChooseACardDecision(RunState s, NChooseACardSelectionScreen screen)
    {
        if (ChooseCardCompleteField?.GetValue(screen) is true) return null;
        if (ChooseCardListField?.GetValue(screen) is not IReadOnlyList<CardModel> cards || cards.Count == 0) return null;
        bool canSkip = ChooseCardCanSkipField?.GetValue(screen) is true;

        int min = canSkip ? 0 : 1;
        int max = 1;
        var info = BridgePatches.LastSelectInfo ?? new SelectInfo(null, null, "FromChooseACardScreen", null);
        var pc = new PendingCardChoice(cards, min, max, false, info);
        _shimSession.Selecting = pc;
        _shimSession.SelectedIndices = Array.Empty<int>();

        var d = new Decision(DecisionKind.CardSelect) { Context = $"Choose {min}..{max}", Selected = 0 };
        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            d.Add(ActionKind.SelectCard, "Pick " + card.Id.Entry, () =>
            {
                if (ChooseCardRowField?.GetValue(screen) is Node row)
                {
                    var holder = FindCardHolder(row, card);
                    if (holder != null && ChooseCardSelectHolderMethod != null)
                    {
                        ChooseCardSelectHolderMethod.Invoke(screen, new object[] { holder });
                        return Task.CompletedTask;
                    }
                }
                return Task.CompletedTask;
            }, a: card);
        }

        if (min == 0)
        {
            d.Add(ActionKind.ConfirmSelection, "Skip", () =>
            {
                if (ChooseCardSkipBtnField?.GetValue(screen) is NClickableControl skipBtn)
                    skipBtn.ForceClick();
                return Task.CompletedTask;
            });
        }

        return d;
    }

    Decision? BuildGridCardSelectDecision(RunState s, NCardGridSelectionScreen screen)
    {
        if (GridCardsField?.GetValue(screen) is not IReadOnlyList<CardModel> cards) return null;
        var st = screen.GetType();
        var prefs = AccessTools.Field(st, "_prefs")?.GetValue(screen) is CardSelectorPrefs pr ? pr : default;
        var selectedSet = AccessTools.Field(st, "_selectedCards")?.GetValue(screen) as IEnumerable<CardModel> ?? Enumerable.Empty<CardModel>();
        var selectedRef = new HashSet<CardModel>(selectedSet, ReferenceEqualityComparer.Instance);

        var selectedIndices = new List<int>();
        for (int i = 0; i < cards.Count; i++)
            if (selectedRef.Contains(cards[i])) selectedIndices.Add(i);

        int max = Math.Min(prefs.MaxSelect, cards.Count);
        int min = Math.Min(prefs.MinSelect, max);
        var info = BridgePatches.LastSelectInfo;
        bool upgrade = (screen is NDeckUpgradeSelectScreen || info?.Method == "FromDeckForUpgrade") && max > 0;
        if (upgrade) min = Math.Max(min, 1);
        bool cancel = prefs.Cancelable && !upgrade && selectedIndices.Count == 0 && !ReferenceEquals(_cancelRoom, s.CurrentRoom);

        var pc = new PendingCardChoice(cards, min, max, prefs.Cancelable, info);
        _shimSession.Selecting = pc;
        _shimSession.SelectedIndices = selectedIndices;

        var d = new Decision(DecisionKind.CardSelect)
        {
            Context = $"Select {min}..{max} ({selectedIndices.Count} chosen)",
            Selected = selectedIndices.Count,
        };

        bool upgradeAny = screen is NDeckUpgradeSelectScreen || (info?.Method?.Contains("ForUpgrade") == true);
        for (int i = 0; i < cards.Count; i++)
        {
            if (selectedIndices.Contains(i)) continue;
            var card = cards[i];
            bool completesMax = selectedIndices.Count + 1 >= max;
            d.Add(ActionKind.SelectCard, "Select " + card.Id.Entry, () =>
            {
                GridCardClickedMethod?.Invoke(screen, new object[] { card });
                if (completesMax)
                    TryClickGridPreviewOrConfirm(screen, previewOnly: true);
                return Task.CompletedTask;
            }, a: card, b: upgradeAny ? Observation.UpgradedCopy(card) : null);
        }

        if (selectedIndices.Count >= min)
        {
            d.Add(ActionKind.ConfirmSelection, "Confirm", () =>
            {
                TryClickGridPreviewOrConfirm(screen, previewOnly: false);
                return Task.CompletedTask;
            });
        }

        if (cancel)
        {
            d.Add(ActionKind.Leave, "Cancel", () =>
            {
                _cancelRoom = s.CurrentRoom;
                if (AccessTools.Field(st, "_closeButton")?.GetValue(screen) is NClickableControl closeBtn)
                    closeBtn.ForceClick();
                return Task.CompletedTask;
            });
        }

        return d;
    }

    static void TryClickGridPreviewOrConfirm(NCardGridSelectionScreen screen, bool previewOnly)
    {
        var st = screen.GetType();
        foreach (var fieldName in new[] { "_previewConfirmButton", "_singlePreviewConfirmButton", "_multiPreviewConfirmButton" })
        {
            if (AccessTools.Field(st, fieldName)?.GetValue(screen) is NClickableControl btn && btn.IsVisibleInTree())
            {
                btn.ForceClick();
                return;
            }
        }
        if (previewOnly) return;
        if (AccessTools.Field(st, "_confirmButton")?.GetValue(screen) is NClickableControl mainConfirm)
        {
            mainConfirm.ForceClick();
            // Clicking _confirmButton on NDeckCardSelectScreen / NDeckTransformSelectScreen may open a preview confirmation
            foreach (var fieldName in new[] { "_previewConfirmButton", "_singlePreviewConfirmButton", "_multiPreviewConfirmButton" })
            {
                if (AccessTools.Field(st, fieldName)?.GetValue(screen) is NClickableControl pbtn && pbtn.IsVisibleInTree())
                {
                    pbtn.ForceClick();
                    return;
                }
            }
        }
    }

    Decision? BuildHandCardSelectDecision(RunState s, NPlayerHand hand)
    {
        var prefs = HandPrefsField?.GetValue(hand) is CardSelectorPrefs pr ? pr : default;
        var filter = HandFilterField?.GetValue(hand) as Func<CardModel, bool>;
        var selectedCards = HandSelectedCardsField?.GetValue(hand) as List<CardModel> ?? new List<CardModel>();
        var selectedRef = new HashSet<CardModel>(selectedCards, ReferenceEqualityComparer.Instance);

        var options = BridgePatches.HandSelectionInitialOptions;
        var activeCards = hand.ActiveHolders.Select(h => h.CardModel).Where(c => c != null && (filter == null || filter(c))).Cast<CardModel>().ToList();
        if (options == null || options.Count == 0 || !activeCards.All(c => options.Contains(c, ReferenceEqualityComparer.Instance)))
        {
            options = new List<CardModel>(selectedCards);
            foreach (var c in activeCards)
                if (!options.Contains(c, ReferenceEqualityComparer.Instance))
                    options.Add(c);
        }

        var selectedIndices = new List<int>();
        for (int i = 0; i < options.Count; i++)
            if (selectedRef.Contains(options[i])) selectedIndices.Add(i);

        int max = Math.Min(prefs.MaxSelect, options.Count);
        int min = Math.Min(prefs.MinSelect, max);
        var info = BridgePatches.LastSelectInfo;
        bool upgradeAny = hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect || (info?.Method?.Contains("ForUpgrade") == true);

        var pc = new PendingCardChoice(options, min, max, false, info);
        _shimSession.Selecting = pc;
        _shimSession.SelectedIndices = selectedIndices;

        var d = new Decision(DecisionKind.CardSelect)
        {
            Context = $"Hand Select {min}..{max} ({selectedIndices.Count} chosen)",
            Selected = selectedIndices.Count,
        };

        for (int i = 0; i < options.Count; i++)
        {
            if (selectedIndices.Contains(i)) continue;
            var card = options[i];
            bool completesMax = selectedIndices.Count + 1 >= max;
            d.Add(ActionKind.SelectCard, "Select " + card.Id.Entry, () =>
            {
                var holder = hand.GetCardHolder(card);
                if (holder != null)
                    HandHolderPressedMethod?.Invoke(hand, new object[] { holder });
                if (completesMax && HandConfirmBtnField?.GetValue(hand) is NClickableControl cbtn && cbtn.IsVisibleInTree() && cbtn.IsEnabled)
                    cbtn.ForceClick();
                return Task.CompletedTask;
            }, a: card, b: upgradeAny ? Observation.UpgradedCopy(card) : null);
        }

        if (selectedIndices.Count >= min)
        {
            d.Add(ActionKind.ConfirmSelection, "Confirm", () =>
            {
                if (HandConfirmBtnField?.GetValue(hand) is NClickableControl cbtn)
                    cbtn.ForceClick();
                return Task.CompletedTask;
            });
        }

        return d;
    }

    Decision? BuildCardRewardDecision(NCardRewardSelectionScreen screen)
    {
        if (CardRewardOptsField?.GetValue(screen) is not IReadOnlyList<CardCreationResult> cards || cards.Count == 0)
            return null;
        var alts = CardRewardExtraField?.GetValue(screen) as IReadOnlyList<CardRewardAlternative> ?? Array.Empty<CardRewardAlternative>();

        var d = new Decision(DecisionKind.CardReward);

        // Populate RewardsFlow.ActiveSet if opened from an underlying NRewardsScreen (format 18+ unclaimed reward context)
        if (FindUnderlyingRewardsScreen() is { } rwScreen && RewardsSetField?.GetValue(rwScreen) is RewardsSet set)
        {
            Reward? activeCr = set.Rewards.OfType<CardReward>().FirstOrDefault(cr =>
                !cr.SuccessfullySelected && cr.Cards.Count() == cards.Count && cr.Cards.Zip(cards).All(p => ReferenceEquals(p.First, p.Second.Card)));
            RewardsFlow.ActiveSet = set;
            RewardsFlow.ActiveChoice = activeCr;
            RewardsFlow.ActiveAttempted = _attemptedRewards;
            foreach (var r in RewardsFlow.Unclaimed(set, _attemptedRewards, activeCr))
            {
                d.Shown.Add((r, Zone.Offer, 0));
                if (r is CardReward other) ShowRewardCards(d, set, other);
            }
        }

        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i].Card;
            d.Add(ActionKind.PickCardReward, "Pick " + card.Id.Entry, () =>
            {
                var holder = screen.GetCardHolder(card);
                if (holder != null)
                    CardRewardSelectMethod?.Invoke(screen, new object[] { holder });
                return Task.CompletedTask;
            }, a: card);
        }

        for (int i = 0; i < alts.Count; i++)
        {
            int altIdx = i;
            var alt = alts[i];
            d.Add(ActionKind.CardRewardAlt, alt.OptionId, () =>
            {
                if (RewardsFlow.ActiveChoice is CardReward cr)
                    _attemptedRewards.Add(cr);
                CardRewardAltMethod?.Invoke(screen, new object[] { altIdx });
                return Task.CompletedTask;
            }, key: Vocab.Alt(alt.OptionId));
        }

        return d;
    }

    Decision? BuildBundleDecision(NChooseABundleSelectionScreen screen)
    {
        if (BundleListField?.GetValue(screen) is not IReadOnlyList<IReadOnlyList<CardModel>> bundles || bundles.Count == 0)
            return null;

        var d = new Decision(DecisionKind.Bundle);
        for (int i = 0; i < bundles.Count; i++)
        {
            int idx = i;
            foreach (var c in bundles[i]) d.Shown.Add((c, i == 0 ? Zone.Pack1 : Zone.Pack2, 0));
            d.Add(ActionKind.PickBundle, "Bundle " + string.Join("+", bundles[i].Select(c => c.Id.Entry)), () =>
            {
                if (BundleRowField?.GetValue(screen) is Node row)
                {
                    var bundleNodes = row.GetChildren().OfType<NCardBundle>().ToList();
                    if (idx < bundleNodes.Count)
                    {
                        BundleClickedMethod?.Invoke(screen, new object[] { bundleNodes[idx] });
                        var confirmBtn = BundleConfirmBtnField?.GetValue(screen) as NButton;
                        BundleConfirmMethod?.Invoke(screen, new object?[] { confirmBtn });
                    }
                }
                return Task.CompletedTask;
            }, a: bundles[i].FirstOrDefault(), f0: i);
        }
        return d;
    }

    Decision? BuildRewardsDecision(Player me, NRewardsScreen screen, out Func<Task>? autoStep, out string? autoDesc)
    {
        autoStep = null;
        autoDesc = null;
        if (RewardsSetField?.GetValue(screen) is not RewardsSet set) return null;
        if (!ReferenceEquals(set, _lastRewardsSet))
        {
            _lastRewardsSet = set;
            _attemptedRewards.Clear();
        }

        if (RewardsSkippedField?.GetValue(screen) is IEnumerable<Control> skippedCtrls)
        {
            foreach (var sc in skippedCtrls.OfType<NRewardButton>())
                if (sc.Reward != null) _attemptedRewards.Add(sc.Reward);
        }

        var buttons = (RewardsButtonsField?.GetValue(screen) as IEnumerable<Control>)?.OfType<NRewardButton>().ToList() ?? new List<NRewardButton>();

        // Auto-claim GoldReward and standalone no-downside RelicReward (matching Sts2Headless RewardsFlow)
        if (_cfg.AutoGold)
        {
            var goldBtn = buttons.FirstOrDefault(b => b.Reward is GoldReward { SuccessfullySelected: false } && b.IsEnabled);
            if (goldBtn != null)
            {
                autoDesc = "Claim Gold";
                autoStep = () => { goldBtn.ForceClick(); return Task.CompletedTask; };
                return null;
            }
        }
        if (_cfg.AutoFreeRelics)
        {
            var freeRelicBtn = buttons.FirstOrDefault(b =>
                b.Reward is RelicReward { SuccessfullySelected: false, ParentRewardSet: null } rr
                && RewardsFlow.IsNoDownside(rr.Relic) && b.IsEnabled);
            if (freeRelicBtn != null)
            {
                autoDesc = "Claim Relic " + ((RelicReward)freeRelicBtn.Reward!).Relic?.Id.Entry;
                autoStep = () => { freeRelicBtn.ForceClick(); return Task.CompletedTask; };
                return null;
            }
        }

        var pr = new PendingRewards(set, _attemptedRewards);
        RewardsFlow.Pending = pr;
        var available = pr.Available;

        // Non-terminal rewards screen with nothing left -> auto-proceed
        if (!pr.IsTerminal && available.Count == 0 && RewardsProceedField?.GetValue(screen) is NClickableControl pbtn0 && pbtn0.IsEnabled)
        {
            autoDesc = "Close Extra Rewards";
            autoStep = () => { pbtn0.ForceClick(); return Task.CompletedTask; };
            return null;
        }

        var d = new Decision(DecisionKind.Rewards) { Context = pr.IsTerminal ? "Combat Rewards" : "Extra Rewards" };
        foreach (var r in available)
        {
            var reward = r;
            d.Add(ActionKind.TakeReward, "Claim " + RewardLabel(r), () =>
            {
                var btn = buttons.FirstOrDefault(b => ReferenceEquals(b.Reward, reward));
                if (btn != null) btn.ForceClick();
                else return RunManager.Instance.RewardsSetSynchronizer.SelectLocalReward(reward);
                return Task.CompletedTask;
            }, a: r);
        }

        foreach (var r in available)
            if (r is CardReward cr) ShowRewardCards(d, set, cr);

        if (!set.DisallowSkipping || available.Count == 0)
        {
            d.Add(ActionKind.Leave, "Proceed", () =>
            {
                if (RewardsProceedField?.GetValue(screen) is NClickableControl pbtn)
                    pbtn.ForceClick();
                return Task.CompletedTask;
            });
        }

        AddPotionActions(me, d);
        if (!me.HasOpenPotionSlots && set.Rewards.Any(r => r is PotionReward && !r.SuccessfullySelected))
        {
            AddPotionDiscards(me, d);
            foreach (var r in set.Rewards)
                if (r is PotionReward { Potion: not null } p && !p.SuccessfullySelected)
                    d.Shown.Add((p, Zone.Offer, 0));
        }

        return d;
    }

    Decision? BuildCrystalSphereDecision(NCrystalSphereScreen screen, out Func<Task>? autoStep, out string? autoDesc)
    {
        autoStep = null;
        autoDesc = null;
        if (SphereEntityField?.GetValue(screen) is not CrystalSphereMinigame m) return null;
        FakeScreens.CrystalSphere = m;

        if (m.IsFinished)
        {
            if (SphereProceedBtnField?.GetValue(screen) is NClickableControl pbtn && pbtn.IsVisibleInTree() && pbtn.IsEnabled)
            {
                autoDesc = "Proceed from Crystal Sphere";
                autoStep = () => { pbtn.ForceClick(); return Task.CompletedTask; };
            }
            return null;
        }

        var d = new Decision(DecisionKind.Minigame) { Context = $"Crystal Sphere ({m.DivinationCount} left)" };
        int w = m.GridSize.X, h = m.GridSize.Y;
        for (int x = 0; x < w; x++)
        for (int y = 0; y < h; y++)
        {
            var cell = m.cells[x, y];
            if (!cell.IsHidden) continue;
            int cx = x, cy = y;
            d.Add(ActionKind.MinigameCell, $"Small ({cx},{cy})", () => ClickSphereCell(screen, m, cell, big: false), f0: cx, f1: cy, f2: 0);
            d.Add(ActionKind.MinigameCell, $"Big ({cx},{cy})", () => ClickSphereCell(screen, m, cell, big: true), f0: cx, f1: cy, f2: 1);
        }
        return d;
    }

    static Task ClickSphereCell(NCrystalSphereScreen screen, CrystalSphereMinigame m, CrystalSphereCell targetCell, bool big)
    {
        var toolBtn = (big ? SphereBigBtnField : SphereSmallBtnField)?.GetValue(screen) as NClickableControl;
        toolBtn?.ForceClick();
        m.SetTool(big ? CrystalSphereMinigame.CrystalSphereToolType.Big : CrystalSphereMinigame.CrystalSphereToolType.Small);
        if (SphereCellContainerField?.GetValue(screen) is Node container)
        {
            foreach (var uiCell in container.GetChildren().OfType<NCrystalSphereCell>())
            {
                var entityProp = AccessTools.Property(typeof(NCrystalSphereCell), "Entity") ?? AccessTools.Property(typeof(NCrystalSphereCell), "Cell");
                if (ReferenceEquals(entityProp?.GetValue(uiCell), targetCell) && SphereCellClickedMethod != null)
                {
                    if (SphereCellClickedMethod.Invoke(screen, new object[] { uiCell }) is Task t) return t;
                }
            }
        }
        return m.CellClicked(targetCell);
    }

    Decision? BuildEventDecision(Player me, EventModel ev)
    {
        if (BridgePatches.PendingEventOptionTask is { IsCompleted: false }) return null;
        if (NEventRoom.Instance == null) return null;

        var d = new Decision(DecisionKind.Event) { Context = ev.Id.Entry };
        var opts = ev.CurrentOptions;
        for (int i = 0; i < opts.Count; i++)
        {
            if (opts[i].IsLocked || ExcludedEventOptions.Contains(opts[i].TextKey)) continue;
            int k = i;
            var opt = opts[i];
            var tip = HoverObject(opt);
            d.Add(ActionKind.EventOption, opt.TextKey, () =>
            {
                if (NEventRoom.Instance is { } er)
                {
                    er.OptionButtonClicked(opt, k);
                    return Task.CompletedTask;
                }
                if (opt.IsProceed) return opt.Chosen();
                RunManager.Instance.EventSynchronizer.ChooseLocalOption(k);
                return Task.CompletedTask;
            }, a: opt, b: tip);
        }

        if (d.Choices.Count == 0) return null;
        AddPotionActions(me, d);
        return d;
    }

    Decision? BuildRestDecision(Player me, out Func<Task>? autoStep, out string? autoDesc)
    {
        autoStep = null;
        autoDesc = null;
        if (NRestSiteRoom.Instance is not { } restUi) return null;
        if (_roomLeft)
        {
            if (restUi.ProceedButton is { IsEnabled: true } pb && pb.IsVisibleInTree())
            {
                autoDesc = "Proceed from Rest Site";
                autoStep = () => { pb.ForceClick(); return Task.CompletedTask; };
            }
            return null;
        }

        var sync = RunManager.Instance.RestSiteSynchronizer;
        var opts = sync.GetLocalOptions();
        var d = new Decision(DecisionKind.RestSite);
        bool canSmith = opts.Any(o => o is SmithRestSiteOption && o.IsEnabled && !_failed.Contains(o));

        for (int i = 0; i < opts.Count; i++)
        {
            if (!opts[i].IsEnabled || _failed.Contains(opts[i])) continue;
            if (opts[i] is HealRestSiteOption && canSmith && RunSession.RestHealInfo(me) is { actual: 0, extra: 0 }) continue;
            int k = i;
            var opt = opts[i];
            d.Add(ActionKind.RestOption, opt.OptionId, () =>
            {
                if (restUi.GetButtonForOption(opt) is { IsEnabled: true } btn)
                    btn.ForceClick();
                else
                    sync.ChooseLocalOption(k);
                return Task.CompletedTask;
            }, a: opt);
        }

        if (d.Choices.Count == 0)
        {
            if (restUi.ProceedButton is { IsEnabled: true } pb && pb.IsVisibleInTree())
            {
                autoDesc = "Proceed from Rest Site";
                autoStep = () => { _roomLeft = true; pb.ForceClick(); return Task.CompletedTask; };
            }
            return null;
        }

        if (_restChosen)
        {
            d.Add(ActionKind.Leave, "Leave", () =>
            {
                _roomLeft = true;
                if (restUi.ProceedButton is { IsEnabled: true } pb) pb.ForceClick();
                return Task.CompletedTask;
            });
        }
        if (_restChosen && d.Choices.Count == 1)
        {
            if (restUi.ProceedButton is { IsEnabled: true } pb && pb.IsVisibleInTree())
            {
                autoDesc = "Proceed from Rest Site";
                autoStep = () => { _roomLeft = true; pb.ForceClick(); return Task.CompletedTask; };
            }
            return null;
        }

        AddPotionActions(me, d);
        return d;
    }

    Decision? BuildShopDecision(Player me, MerchantRoom shopRoom)
    {
        if (NMerchantRoom.Instance is not { } shopUi) return null;
#if STS2_V0111
        var inv = shopRoom.GetLocalInventory();
#else
        var inv = RunManager.Instance.MerchantSynchronizer.GetLocalInventory();
#endif
        if (inv == null) return null;

        if (!_shopInitialized)
        {
            _shopInitialized = true;
            if (inv.CardRemovalEntry is { } cre && !Hook.ShouldAllowMerchantCardRemoval(me.RunState, me))
                cre.SetUsed();
        }

        var d = new Decision(DecisionKind.Shop) { Context = $"Gold {me.Gold}" };
        AddShopEntries(d, inv, shopUi.Inventory);

        d.Add(ActionKind.Leave, "Leave Shop", () =>
        {
            _roomLeft = true;
            if (shopUi.ProceedButton is { IsEnabled: true } pb) pb.ForceClick();
            return Task.CompletedTask;
        });

        AddPotionActions(me, d, shopInventoryUi: shopUi.Inventory);
        if (!me.HasOpenPotionSlots && inv.AllEntries.Any(e => e is MerchantPotionEntry && e.IsStocked && e.EnoughGold && !_failed.Contains(e)))
            AddPotionDiscards(me, d);
        if (!me.HasOpenPotionSlots)
            foreach (var e in inv.AllEntries)
                if (e is MerchantPotionEntry { Model: { } p } && e.IsStocked)
                    d.Shown.Add((p, Zone.Offer, e.Cost));

        return d;
    }

    Decision? BuildFakeMerchantDecision(Player me, FakeMerchant fm)
    {
        var fmUi = NEventRoom.Instance?.CustomEventNode as NFakeMerchant;
        var inv = fm.Inventory;
        if (inv == null) return null;

        var d = new Decision(DecisionKind.Shop) { Context = $"Fake Merchant Gold {me.Gold}" };
        AddShopEntries(d, inv, fmUi?.Inventory);
        d.Add(ActionKind.Leave, "Leave", () =>
        {
            _roomLeft = true;
            if (fmUi != null && FakeMerchProceedField?.GetValue(fmUi) is NClickableControl pb)
                pb.ForceClick();
            return Task.CompletedTask;
        });
        AddPotionActions(me, d, shopInventoryUi: fmUi?.Inventory);
        return d;
    }

    void AddShopEntries(Decision d, MerchantInventory inv, NMerchantInventory? uiInv)
    {
        foreach (MerchantEntry e in inv.AllEntries)
        {
            if (e.IsStocked && !e.EnoughGold && !_failed.Contains(e) && e is not MerchantCardRemovalEntry { Used: true })
                d.Locked.Add((EntryObject(e), e.Cost));
            if (!e.IsStocked || !e.EnoughGold || _failed.Contains(e)) continue;
            if (e is MerchantPotionEntry && !inv.Player.HasOpenPotionSlots) continue;
            if (e is MerchantCardRemovalEntry { Used: true }) continue;

            var entry = e;
            string label = e is MerchantCardRemovalEntry ? $"Buy Card Removal ({e.Cost})" : $"Buy {EntryLabel(e)} ({e.Cost})";
            d.Add(ActionKind.Buy, label, async () =>
            {
                if (uiInv != null)
                {
                    if (!uiInv.IsOpen) uiInv.Open();
                    var slot = uiInv.GetAllSlots().FirstOrDefault(s => ReferenceEquals(s.Entry, entry));
                    if (slot != null)
                    {
                        slot.Hitbox.ForceClick();
                        _failed.Clear();
                        return;
                    }
                }
#if STS2_V0111
                bool ok = await entry.OnTryPurchaseWrapper(inv);
                if (ok && entry is MerchantCardRemovalEntry) uiInv?.OnCardRemovalUsed();
#else
                bool ok = await entry.OnTryPurchaseUnsynchronized(inv);
#endif
                if (ok) _failed.Clear();
                else _failed.Add(entry);
            }, a: EntryObject(e), f0: e.Cost);
        }
    }

    Decision? BuildTreasureDecision(Player me, TreasureRoom room, out Func<Task>? autoStep, out string? autoDesc)
    {
        autoStep = null;
        autoDesc = null;
        if (NRun.Instance?.TreasureRoom is not { } trUi) return null;

        var sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
        bool opened = TreasureOpenedField != null
            ? TreasureOpenedField.GetValue(trUi) is true
            : (TreasureRelicOpenField?.GetValue(trUi) is true
               || sync.CurrentRelics != null
               || trUi.ProceedButton is { IsEnabled: true });
        if (!opened)
        {
            if (TreasureChestBtnField?.GetValue(trUi) is NClickableControl chestBtn && chestBtn.IsEnabled)
            {
                autoDesc = "Open Treasure Chest";
                autoStep = () => { chestBtn.ForceClick(); return Task.CompletedTask; };
            }
            return null;
        }

        if (_relicDecided)
        {
            if (trUi.ProceedButton is { IsEnabled: true } pb && pb.IsVisibleInTree())
            {
                autoDesc = "Proceed from Treasure Room";
                autoStep = () => { pb.ForceClick(); return Task.CompletedTask; };
            }
            return null;
        }

        var relics = sync.CurrentRelics;
        if (relics == null || relics.Count == 0)
        {
            if (trUi.ProceedButton is { IsEnabled: true } pbEmpty && pbEmpty.IsVisibleInTree())
            {
                _relicDecided = true;
                autoDesc = "Proceed from Treasure Room";
                autoStep = () => { pbEmpty.ForceClick(); return Task.CompletedTask; };
            }
            return null;
        }

        if (_cfg.AutoFreeRelics && relics.Count == 1 && RewardsFlow.IsNoDownside(relics[0]))
        {
            autoDesc = "Claim Chest Relic " + relics[0].Id.Entry;
            autoStep = () =>
            {
                _relicDecided = true;
                ClickTreasureRelic(trUi, sync, 0);
                return Task.CompletedTask;
            };
            return null;
        }

        var d = new Decision(DecisionKind.Treasure);
        for (int i = 0; i < relics.Count; i++)
        {
            int k = i;
            d.Add(ActionKind.PickRelic, "Take " + relics[i].Id.Entry, () =>
            {
                _relicDecided = true;
                ClickTreasureRelic(trUi, sync, k);
                return Task.CompletedTask;
            }, a: relics[i]);
        }

        d.Add(ActionKind.SkipRelic, "Skip Relic", () =>
        {
            _relicDecided = true;
            if (trUi.ProceedButton is { IsEnabled: true } pb) pb.ForceClick();
            else sync.SkipRelicLocally();
            return Task.CompletedTask;
        });

        AddPotionActions(me, d);
        return d;
    }

    static void ClickTreasureRelic(NTreasureRoom trUi, TreasureRoomRelicSynchronizer sync, int index)
    {
        if (TreasureCollectionField?.GetValue(trUi) is NTreasureRoomRelicCollection col)
        {
            if (index == 0 && TreasureSingleHolderProp?.GetValue(col) is NClickableControl { IsEnabled: true } sp && sp.IsVisibleInTree())
            {
                sp.ForceClick();
                return;
            }
            if (TreasureHoldersField?.GetValue(col) is IList<NTreasureRoomRelicHolder> holders && index < holders.Count)
            {
                TreasurePickMethod?.Invoke(col, new object[] { holders[index] });
                return;
            }
        }
        sync.PickRelicLocally(index);
    }

    Decision? BuildMapDecision(RunState s, Player me, NMapScreen mapScreen)
    {
        var cur = s.CurrentMapPoint;
        var next = RunSessionMap.Travelable(s).OrderBy(p => p.coord.col).ToList();
        if (next.Count == 0) return null;

        var d = new Decision(DecisionKind.Map)
        {
            Context = $"Act {s.CurrentActIndex + 1} {(cur == null ? "Start" : $"({cur.coord.col},{cur.coord.row})")}",
        };

        foreach (var p in next)
        {
            var dest = p;
            d.Add(ActionKind.MapMove, $"→ ({p.coord.col},{p.coord.row}) {p.PointType}", () =>
            {
                if (MapDictField?.GetValue(mapScreen) is Dictionary<MapCoord, NMapPoint> dict
                    && dict.TryGetValue(dest.coord, out var node))
                {
                    mapScreen.OnMapPointSelectedLocally(node);
                    return Task.CompletedTask;
                }
                var vote = new MapVote
                {
                    coord = dest.coord,
                    mapGenerationCount = RunManager.Instance.MapSelectionSynchronizer.MapGenerationCount,
                };
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new VoteForMapCoordAction(me, s.MapLocation, vote));
                return Task.CompletedTask;
            }, a: p);
        }

        AddPotionActions(me, d);
        return d;
    }

    // -------------------------------------------------------------------------
    // Helpers (matching RunSession.cs)
    // -------------------------------------------------------------------------

    static void ShowRewardCards(Decision d, RewardsSet set, CardReward cr)
    {
        var z = Observation.RewardZone(set, cr);
        foreach (var c in cr.Cards) d.Shown.Add((c, z, 0));
    }

    static NRewardsScreen? FindUnderlyingRewardsScreen()
    {
        if (NOverlayStack.Instance is not { } stack) return null;
        if (OverlayListField?.GetValue(stack) is System.Collections.IEnumerable list)
            return list.OfType<NRewardsScreen>().LastOrDefault();
        return null;
    }

    static NCardHolder? FindCardHolder(Node parent, CardModel card)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is NCardHolder h && ReferenceEquals(h.CardModel, card))
                return h;
            if (FindCardHolder(child, card) is { } sub)
                return sub;
        }
        return null;
    }

    static IEnumerable<PotionModel> UsablePotions(Player me, bool inCombat) =>
        me.CanUseOrRemovePotions && !me.Creature.IsDead
            ? me.Potions.Where(p => !p.IsQueued && p.PassesCustomUsabilityCheck &&
                                   (p.Usage == PotionUsage.AnyTime || (inCombat && p.Usage == PotionUsage.CombatOnly)))
            : Enumerable.Empty<PotionModel>();

    static void AddPotionActions(Player me, Decision d, NMerchantInventory? shopInventoryUi = null)
    {
        foreach (var p in UsablePotions(me, inCombat: false))
        {
            if (p.IsValidTarget(me.Creature) || p.IsValidTarget(null))
            {
                var pot = p;
                d.Add(ActionKind.UsePotion, "Use " + p.Id.Entry, () =>
                {
                    // Close shop rug before throwing FoulPotion at merchant (matching 决策记录 #227)
                    if (shopInventoryUi is { IsOpen: true } && pot.Id.Entry == "FOUL_POTION")
                        AccessTools.Method(typeof(NMerchantInventory), "Close")?.Invoke(shopInventoryUi, null);
                    pot.EnqueueManualUse(null);
                    return Task.CompletedTask;
                }, a: p);
            }
        }
    }

    static void AddPotionDiscards(Player me, Decision d)
    {
        if (!me.CanUseOrRemovePotions) return;
        var slots = me.PotionSlots;
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i] is { IsQueued: false } p)
            {
                uint slot = (uint)i;
                d.Add(ActionKind.DiscardPotion, "Discard " + p.Id.Entry, () =>
                {
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                        new DiscardPotionGameAction(me, slot, CombatManager.Instance.IsInProgress));
                    return Task.CompletedTask;
                }, a: p);
            }
        }
    }

    static object? HoverObject(EventOption opt)
    {
        if (opt.HoverTips == null) return null;
        foreach (var t in opt.HoverTips)
        {
            object? m = t is CardHoverTip ct ? ct.Card
                : t is HoverTip ht ? ht.CanonicalModel : null;
            if (m is CardModel or PotionModel || (m is RelicModel r && r.Id != opt.Relic?.Id)) return m;
        }
        return null;
    }

    static object EntryObject(MerchantEntry e) => e switch
    {
        MerchantCardEntry c => c.CreationResult!.Card,
        MerchantRelicEntry { Model: { } r } => r,
        MerchantPotionEntry { Model: { } p } => p,
        _ => e,
    };

    static string EntryLabel(MerchantEntry e) => e switch
    {
        MerchantCardEntry c => c.CreationResult!.Card.Id.Entry,
        MerchantRelicEntry r => r.Model?.Id.Entry ?? "Relic",
        MerchantPotionEntry p => p.Model?.Id.Entry ?? "Potion",
        MerchantCardRemovalEntry => "CardRemoval",
        _ => e.GetType().Name,
    };

    static string RewardLabel(Reward r) => r switch
    {
        GoldReward g => $"Gold ({g.Amount})",
        PotionReward p => "Potion " + p.Potion?.Id.Entry,
        RelicReward rr => "Relic " + rr.Relic?.Id.Entry,
        CardReward => "Card Reward",
        _ => r.GetType().Name,
    };

    static string TargetLabel(Creature? t) =>
        t == null ? "" : t.IsPlayer ? " → Self" : $" → {t.Monster?.Id.Entry}";

    static string ComputeSignature(RunState s, Decision d)
    {
        var me = s.Players[0];
        var pcs = me.PlayerCombatState;
        string combatPart = pcs == null
            ? "out"
            : $"{pcs.TurnNumber}:{pcs.Energy}:{pcs.Stars}:{me.Creature.CurrentHp}:{me.Creature.Block}:{pcs.Hand.Cards.Count}:{pcs.DrawPile.Cards.Count}:{pcs.DiscardPile.Cards.Count}:{pcs.PlayPile.Cards.Count}:"
              + string.Join(",", me.Creature.CombatState?.Creatures.Select(c => $"{c.CurrentHp}/{c.Block}/{c.IsAlive}") ?? Array.Empty<string>());
        string choicesPart = string.Join("|", d.Choices.Select(c => $"{(int)c.Kind}:{c.Label}:{c.F0}:{c.F1}"));
        return $"{d.Kind}|{s.CurrentActIndex}|{s.TotalFloor}|{me.Gold}|{me.Creature.CurrentHp}/{me.Creature.MaxHp}|{d.Selected}|{combatPart}||{choicesPart}";
    }
}
