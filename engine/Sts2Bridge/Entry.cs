using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using Sts2Headless;

namespace Sts2Bridge;

/// <summary>
/// STS2 Mod entry point discovered automatically by MegaCrit.Sts2.Core.Modding.ModManager
/// via [ModInitializer(nameof(Init))] when Sts2Bridge.json + Sts2Bridge.dll are placed in mods/Sts2Bridge/.
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
        BridgeLog.Info($"Initializing Sts2Bridge v1.1.0 in {BridgeConfig.ModDir} (backend={cfg.Backend}, top_k={cfg.TopK}, auto_play={cfg.AutoPlay})");

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

    static void AttachToSceneTree(BridgeController controller)
    {
        if (Engine.GetMainLoop() is SceneTree tree && tree.Root != null)
        {
            tree.Root.CallDeferred(Node.MethodName.AddChild, controller);
            BridgeLog.Info("Scheduled BridgeController attachment onto SceneTree.Root.");
        }
        else
        {
            BridgeLog.Warn("SceneTree.Root not ready during Init; scheduling background deferred attach.");
            Task.Run(async () =>
            {
                for (int i = 0; i < 200; i++)
                {
                    await Task.Delay(50);
                    if (Engine.GetMainLoop() is SceneTree st && st.Root != null)
                    {
                        st.Root.CallDeferred(Node.MethodName.AddChild, controller);
                        BridgeLog.Info("Attached BridgeController onto SceneTree.Root.");
                        return;
                    }
                }
            });
        }
    }
}

/// <summary>
/// Main-thread Godot Node driving LiveSessionDriver and BridgeOverlay every _Process frame.
/// </summary>
public partial class BridgeController : Node
{
    readonly BridgeConfig _cfg;
    readonly BridgeLink _link;
    readonly LiveSessionDriver _driver;
    readonly BridgeOverlay _overlay;

    public BridgeController(BridgeConfig cfg, BridgeLink link, NativeModelManager nativeModels)
    {
        _cfg = cfg;
        _link = link;
        Name = "AlphaDefectBridgeController";
        ProcessMode = ProcessModeEnum.Always;

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

        // Re-evaluate current decision if user switches inference backend live
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

    public override void _Ready()
    {
        AddChild(_overlay);
        BridgeLog.Info("BridgeController _Ready on Godot main thread (F8=Auto/Advisory, F9=Step, F10=Reconnect, F7=Hide HUD).");
    }

    public override void _Process(double delta)
    {
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

    public override void _ExitTree()
    {
        _link.Dispose();
    }
}
