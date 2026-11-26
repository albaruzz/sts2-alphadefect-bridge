using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using Sts2Headless;

namespace Sts2Bridge;

/// <summary>
/// STS2 Mod entry point discovered automatically by MegaCrit.Sts2.Core.Modding.ModManager
/// via [ModInitializer(nameof(Init))] when Sts2Bridge.json + Sts2Bridge.dll are placed in mods/Sts2Bridge/.
///
/// IMPORTANT (Godot 4 + Microsoft.NET.Sdk):
/// Without Godot.SourceGenerators, custom C# subclasses of Godot.Node / CanvasLayer do NOT get
/// generated HasGodotClassMethod / InvokeGodotClassMethod tables, so Godot C++ never invokes
/// overridden _Ready / _Process / _Input methods on custom subclasses. Therefore Sts2Bridge uses
/// 100% pure composition of built-in Godot engine types (CanvasLayer, PanelContainer, Button, HSlider, Label)
/// driven by SceneTree.ProcessFrame + Callable.From(...).CallDeferred() + Harmony Postfix on NGame._Input.
/// </summary>
[ModInitializer(nameof(Init))]
public static class Entry
{
    static bool _initialized;
    static Harmony? _harmony;
    static BridgeLink? _link;
    static NativeModelManager? _nativeModels;
    static BridgeController? _controller;

    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;

        var cfg = BridgeConfig.Load();
        BridgeLog.Init(BridgeConfig.ModDir);
        BridgeLog.Info($"Initializing Sts2Bridge v1.1.1 in {BridgeConfig.ModDir} (log={BridgeLog.ActiveLogPath}, backend={cfg.Backend}, top_k={cfg.TopK}, auto_play={cfg.AutoPlay})");

        string vocabPath = Path.Combine(BridgeConfig.ModDir, "vocab.txt");
        Vocab.Load(vocabPath);
        BridgeLog.Info($"Loaded Vocab ({Vocab.Count} entries, {Vocab.AliasIds.Count} aliases) from {vocabPath}");

        string freeRelicsPath = Path.Combine(BridgeConfig.ModDir, "no_downside_relics.txt");
        RewardsFlow.LoadNoDownside(freeRelicsPath);

        _harmony = new Harmony("alphadefect.sts2bridge");
        BridgePatches.Install(_harmony, cfg);

        _nativeModels = new NativeModelManager(BridgeConfig.ModDir);
        _link = new BridgeLink(cfg);
        _controller = new BridgeController(cfg, _link, _nativeModels);

        AttachToSceneTree(_controller);
    }

    internal static void OnGameInput(InputEvent inputEvent)
    {
        _controller?.HandleInput(inputEvent);
    }

    static void AttachToSceneTree(BridgeController controller)
    {
        if (Engine.GetMainLoop() is SceneTree)
        {
            Callable.From(controller.AttachOnMainThread).CallDeferred();
            BridgeLog.Info("Scheduled BridgeController.AttachOnMainThread via Callable.CallDeferred().");
        }
        else
        {
            BridgeLog.Warn("SceneTree not ready during Init; polling in background task.");
            Task.Run(async () =>
            {
                for (int i = 0; i < 200; i++)
                {
                    await Task.Delay(50);
                    if (Engine.GetMainLoop() is SceneTree)
                    {
                        Callable.From(controller.AttachOnMainThread).CallDeferred();
                        BridgeLog.Info("Scheduled BridgeController.AttachOnMainThread after SceneTree became ready.");
                        return;
                    }
                }
                BridgeLog.Error("Timed out waiting for Engine.GetMainLoop() SceneTree.");
            });
        }
    }
}

/// <summary>
/// Pure-composition main-thread controller driving LiveSessionDriver and BridgeOverlay via SceneTree.ProcessFrame.
/// Does NOT subclass Godot.Node so it works reliably without Godot.SourceGenerators on macOS/Windows/Linux.
/// </summary>
public sealed class BridgeController
{
    readonly BridgeConfig _cfg;
    readonly BridgeLink _link;
    readonly LiveSessionDriver _driver;
    readonly BridgeOverlay _overlay;
    SceneTree? _tree;
    bool _attached;
    bool _loggedFirstTick;

    public BridgeController(BridgeConfig cfg, BridgeLink link, NativeModelManager nativeModels)
    {
        _cfg = cfg;
        _link = link;
        _driver = new LiveSessionDriver(cfg, link, nativeModels);
        _overlay = new BridgeOverlay(cfg);

        _overlay.OnToggleMode += () =>
        {
            _driver.AutoPlay = !_driver.AutoPlay;
            BridgeLog.Info($"Switched mode to {(_driver.AutoPlay ? "AUTO-PLAY" : "ADVISORY")}");
        };
        _overlay.OnStepOnce += () =>
        {
            BridgeLog.Info("Manual single-step requested.");
            _driver.RequestStepOnce();
        };
        _overlay.OnReconnect += () =>
        {
            BridgeLog.Info("Manual TCP reconnect requested (F10).");
            _link.Reconnect();
        };

        string lastBackend = cfg.Backend;
        cfg.Changed += () =>
        {
            if (cfg.Backend != lastBackend)
            {
                lastBackend = cfg.Backend;
                if (cfg.Backend != "builtin") _link.Reconnect();
                _driver.ForceReevaluate();
            }
        };
    }

    public void AttachOnMainThread()
    {
        if (_attached) return;
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
            {
                BridgeLog.Warn("AttachOnMainThread called before SceneTree.Root was ready; retrying next frame.");
                Callable.From(AttachOnMainThread).CallDeferred();
                return;
            }

            _attached = true;
            _tree = tree;
            _overlay.Mount(tree.Root);
            tree.ProcessFrame += OnProcessFrame;
            BridgeLog.Info("BridgeController mounted HUD CanvasLayer onto SceneTree.Root and subscribed to SceneTree.ProcessFrame.");
        }
        catch (Exception e)
        {
            BridgeLog.Error($"AttachOnMainThread failed: {e}");
        }
    }

    public void HandleInput(InputEvent inputEvent)
    {
        if (!_attached) return;
        try
        {
            _overlay.HandleInput(inputEvent, _tree?.Root);
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"HandleInput error: {e.Message}");
        }
    }

    void OnProcessFrame()
    {
        try
        {
            if (!_loggedFirstTick)
            {
                _loggedFirstTick = true;
                BridgeLog.Info("First SceneTree.ProcessFrame tick received; HUD & LiveSessionDriver active.");
            }
            _driver.Tick();
            _overlay.UpdateHud(
                tcpConnected: _link.IsConnected,
                nativeStatus: _driver.NativeStatus,
                autoPlay: _driver.AutoPlay,
                decisionKind: _driver.ActiveDecisionKind,
                seq: _driver.ActiveSeq,
                waitingReply: _driver.WaitingReply,
                reply: _driver.LatestReply,
                statusNote: _driver.StatusNote);
        }
        catch (Exception e)
        {
            BridgeLog.Error($"OnProcessFrame exception: {e}");
        }
    }
}
