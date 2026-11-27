using System.Text;
using Godot;

namespace Sts2Bridge;

/// <summary>
/// Non-intrusive Godot HUD overlay anchored in the top-right corner, built via 100% pure composition
/// of built-in Godot engine controls (CanvasLayer + Control + PanelContainer + VBoxContainer + Button + HSlider + Label)
/// so it works without Godot.SourceGenerators across macOS (arm64/x86_64), Windows, and Linux.
/// </summary>
public sealed class BridgeOverlay
{
    readonly BridgeConfig _cfg;
    CanvasLayer? _canvas;
    PanelContainer? _panel;
    StyleBoxFlat? _style;
    Button? _autoBtn;
    Button? _stepBtn;
    Button? _optToggleBtn;
    Button? _collapseBtn;
    VBoxContainer? _optDrawer;
    HSlider? _topKSlider;
    Label? _topKValLabel;
    HSlider? _delaySlider;
    Label? _delayValLabel;
    Button? _backendBtn;
    Label? _label;

    bool _drawerOpen;
    bool _collapsed;
    bool _syncingControls;

    public event Action? OnToggleMode;
    public event Action? OnStepOnce;
    public event Action? OnReconnect;

    public BridgeOverlay(BridgeConfig cfg)
    {
        _cfg = cfg;
    }

    public void Mount(Window rootWindow)
    {
        if (_canvas != null) return;

        _canvas = new CanvasLayer
        {
            Name = "AlphaDefectBridgeOverlay",
            Layer = 120,
            ProcessMode = Node.ProcessModeEnum.Always,
        };

        var root = new Control
        {
            Name = "OverlayRoot",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _canvas.AddChild(root);

        _style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.07f, 0.11f, 0.88f),
            BorderColor = new Color(0.28f, 0.56f, 0.90f, 0.92f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 7,
            CornerRadiusTopRight = 7,
            CornerRadiusBottomLeft = 7,
            CornerRadiusBottomRight = 7,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
        };

        _panel = new PanelContainer
        {
            Name = "HudPanel",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = _cfg.ShowOverlay,
        };
        _panel.AddThemeStyleboxOverride("panel", _style);
        _panel.AnchorLeft = 1.0f;
        _panel.AnchorRight = 1.0f;
        _panel.AnchorTop = 0.0f;
        _panel.AnchorBottom = 0.0f;
        _panel.GrowHorizontal = Control.GrowDirection.Begin;
        _panel.GrowVertical = Control.GrowDirection.End;
        _panel.OffsetLeft = -486f;
        _panel.OffsetRight = -16f;
        _panel.OffsetTop = 68f;
        _panel.CustomMinimumSize = new Vector2(470f, 0f);
        root.AddChild(_panel);

        var mainVBox = new VBoxContainer
        {
            Name = "HudMainVBox",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        mainVBox.AddThemeConstantOverride("separation", 6);
        _panel.AddChild(mainVBox);

        // Interactive mini-toolbar (100% mouse-operable for macOS TouchBar/Fn-less keyboards too!)
        var toolbar = new HBoxContainer
        {
            Name = "HudToolbar",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        toolbar.AddThemeConstantOverride("separation", 6);
        mainVBox.AddChild(toolbar);

        var titleLbl = new Label
        {
            Text = "AlphaDefect AI",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        BridgeFonts.ApplyFont(titleLbl, 14);
        titleLbl.AddThemeColorOverride("font_color", new Color(0.98f, 0.85f, 0.42f, 1f));
        toolbar.AddChild(titleLbl);

        _autoBtn = MakeToolbarBtn("Auto: OFF");
        _autoBtn.Pressed += () => OnToggleMode?.Invoke();
        toolbar.AddChild(_autoBtn);

        _stepBtn = MakeToolbarBtn("Step");
        _stepBtn.Pressed += () => OnStepOnce?.Invoke();
        toolbar.AddChild(_stepBtn);

        _optToggleBtn = MakeToolbarBtn("⚙ Options");
        _optToggleBtn.Pressed += () =>
        {
            _drawerOpen = !_drawerOpen;
            if (_drawerOpen && _collapsed)
            {
                _collapsed = false;
                if (_label != null) _label.Visible = true;
                if (_collapseBtn != null) _collapseBtn.Text = "—";
            }
            if (_optDrawer != null) _optDrawer.Visible = _drawerOpen;
            _optToggleBtn.Text = _drawerOpen ? "⚙ Close" : "⚙ Options";
        };
        toolbar.AddChild(_optToggleBtn);

        _collapseBtn = MakeToolbarBtn("—");
        _collapseBtn.Pressed += () =>
        {
            _collapsed = !_collapsed;
            if (_label != null) _label.Visible = !_collapsed;
            if (_collapsed && _optDrawer != null)
            {
                _drawerOpen = false;
                _optDrawer.Visible = false;
                if (_optToggleBtn != null) _optToggleBtn.Text = "⚙ Options";
            }
            _collapseBtn.Text = _collapsed ? "Expand" : "—";
        };
        toolbar.AddChild(_collapseBtn);

        // Collapsible inline options drawer (adjust Top-K, Delay, Backend right inside combat!)
        _optDrawer = BuildInlineDrawer();
        _optDrawer.Visible = false;
        mainVBox.AddChild(_optDrawer);

        // Prediction & telemetry readout label (MouseFilter = Ignore so clicks pass through; WordSmart wrap prevents UI truncation)
        _label = new Label
        {
            Name = "HudLabel",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(442f, 0f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Text = "Initializing AlphaDefect AI assistant...",
        };
        BridgeFonts.ApplyFont(_label, 13);
        _label.AddThemeColorOverride("font_color", new Color(0.93f, 0.95f, 0.98f, 1.0f));
        _label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
        _label.AddThemeConstantOverride("outline_size", 3);
        mainVBox.AddChild(_label);

        rootWindow.AddChild(_canvas);

        _cfg.Changed += SyncControlsFromConfig;
        SyncControlsFromConfig();
    }

    VBoxContainer BuildInlineDrawer()
    {
        var drawer = new VBoxContainer
        {
            Name = "InlineOptionsDrawer",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        drawer.AddThemeConstantOverride("separation", 5);

        // Top-K slider row
        var tkRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        tkRow.AddThemeConstantOverride("separation", 5);
        var tkTitle = new Label
        {
            Text = "Candidates (Top-K):",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        BridgeFonts.ApplyFont(tkTitle, 13);
        var tkMinus = MakeToolbarBtn("-");
        _topKSlider = new HSlider
        {
            MinValue = 1,
            MaxValue = 10,
            Step = 1,
            Value = _cfg.TopK,
            CustomMinimumSize = new Vector2(105f, 20f),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        var tkPlus = MakeToolbarBtn("+");
        _topKValLabel = new Label
        {
            Text = $"Top-{_cfg.TopK}",
            CustomMinimumSize = new Vector2(48f, 0f),
            HorizontalAlignment = HorizontalAlignment.Right,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        BridgeFonts.ApplyFont(_topKValLabel, 13);
        _topKValLabel.AddThemeColorOverride("font_color", new Color(0.50f, 0.86f, 1.0f, 1f));

        tkMinus.Pressed += () => { _cfg.TopK = Math.Max(1, _cfg.TopK - 1); _cfg.SaveAndNotify(); };
        tkPlus.Pressed += () => { _cfg.TopK = Math.Min(10, _cfg.TopK + 1); _cfg.SaveAndNotify(); };
        _topKSlider.ValueChanged += v =>
        {
            if (_syncingControls) return;
            _cfg.TopK = (int)Math.Round(v);
            _cfg.SaveAndNotify();
        };

        tkRow.AddChild(tkTitle);
        tkRow.AddChild(tkMinus);
        tkRow.AddChild(_topKSlider);
        tkRow.AddChild(tkPlus);
        tkRow.AddChild(_topKValLabel);
        drawer.AddChild(tkRow);

        // Delay slider row
        var dlRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        dlRow.AddThemeConstantOverride("separation", 5);
        var dlTitle = new Label
        {
            Text = "Auto delay:",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        BridgeFonts.ApplyFont(dlTitle, 13);
        var dlMinus = MakeToolbarBtn("-");
        _delaySlider = new HSlider
        {
            MinValue = 50,
            MaxValue = 1500,
            Step = 50,
            Value = _cfg.ActionDelayMs,
            CustomMinimumSize = new Vector2(105f, 20f),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        var dlPlus = MakeToolbarBtn("+");
        _delayValLabel = new Label
        {
            Text = $"{_cfg.ActionDelayMs}ms",
            CustomMinimumSize = new Vector2(48f, 0f),
            HorizontalAlignment = HorizontalAlignment.Right,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        BridgeFonts.ApplyFont(_delayValLabel, 13);
        _delayValLabel.AddThemeColorOverride("font_color", new Color(0.50f, 0.86f, 1.0f, 1f));

        dlMinus.Pressed += () => { _cfg.ActionDelayMs = Math.Max(50, _cfg.ActionDelayMs - 50); _cfg.SaveAndNotify(); };
        dlPlus.Pressed += () => { _cfg.ActionDelayMs = Math.Min(1500, _cfg.ActionDelayMs + 50); _cfg.SaveAndNotify(); };
        _delaySlider.ValueChanged += v =>
        {
            if (_syncingControls) return;
            _cfg.ActionDelayMs = (int)(Math.Round(v / 50.0) * 50);
            _cfg.SaveAndNotify();
        };

        dlRow.AddChild(dlTitle);
        dlRow.AddChild(dlMinus);
        dlRow.AddChild(_delaySlider);
        dlRow.AddChild(dlPlus);
        dlRow.AddChild(_delayValLabel);
        drawer.AddChild(dlRow);

        // Backend cycle row
        var bkRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        bkRow.AddThemeConstantOverride("separation", 5);
        var bkTitle = new Label
        {
            Text = "Inference engine:",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        BridgeFonts.ApplyFont(bkTitle, 13);
        _backendBtn = MakeToolbarBtn(_cfg.BackendDisplayLabel());
        _backendBtn.Pressed += () => _cfg.CycleBackend();
        bkRow.AddChild(bkTitle);
        bkRow.AddChild(_backendBtn);
        drawer.AddChild(bkRow);

        return drawer;
    }

    void SyncControlsFromConfig()
    {
        if (_canvas == null || !_canvas.IsInsideTree()) return;
        _syncingControls = true;
        try
        {
            if (_panel != null) _panel.Visible = _cfg.ShowOverlay;
            if (_autoBtn != null)
            {
                _autoBtn.Text = _cfg.AutoPlay ? "Auto: ON" : "Auto: OFF";
                _autoBtn.Modulate = _cfg.AutoPlay ? new Color(0.50f, 1.0f, 0.65f) : new Color(0.95f, 0.97f, 1.0f);
            }
            if (_topKSlider != null) _topKSlider.Value = _cfg.TopK;
            if (_topKValLabel != null) _topKValLabel.Text = $"Top-{_cfg.TopK}";
            if (_delaySlider != null) _delaySlider.Value = _cfg.ActionDelayMs;
            if (_delayValLabel != null) _delayValLabel.Text = $"{_cfg.ActionDelayMs}ms";
            if (_backendBtn != null) _backendBtn.Text = _cfg.BackendDisplayLabel();
        }
        finally
        {
            _syncingControls = false;
        }
    }

    static Button MakeToolbarBtn(string text)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(28f, 24f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        BridgeFonts.ApplyFont(btn, 12);
        return btn;
    }

    public void HandleInput(InputEvent @event, Viewport? viewport)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        switch (key.Keycode)
        {
            case Key.F8:
                OnToggleMode?.Invoke();
                viewport?.SetInputAsHandled();
                break;
            case Key.F9:
                OnStepOnce?.Invoke();
                viewport?.SetInputAsHandled();
                break;
            case Key.F10:
                OnReconnect?.Invoke();
                viewport?.SetInputAsHandled();
                break;
            case Key.F7:
                _cfg.ShowOverlay = !_cfg.ShowOverlay;
                _cfg.SaveAndNotify();
                viewport?.SetInputAsHandled();
                break;
        }
    }

    public void UpdateHud(
        bool tcpConnected,
        string nativeStatus,
        bool autoPlay,
        string decisionKind,
        int seq,
        bool waitingReply,
        PolicyReply? reply,
        string? statusNote)
    {
        if (_panel == null || _label == null || _style == null) return;
        _panel.Visible = _cfg.ShowOverlay;
        if (!_cfg.ShowOverlay || _collapsed) return;

        bool activeReady = tcpConnected || (_cfg.Backend != "tcp" && !string.IsNullOrEmpty(nativeStatus));

        // Border color: Gold = Tree Search, Green = Auto-Play, Blue = Advisory Ready, Red = Waiting for TCP
        if (!activeReady)
            _style.BorderColor = new Color(0.75f, 0.28f, 0.28f, 0.88f);
        else if (reply?.Searched == true)
            _style.BorderColor = new Color(0.95f, 0.74f, 0.20f, 0.95f);
        else if (autoPlay)
            _style.BorderColor = new Color(0.22f, 0.82f, 0.48f, 0.92f);
        else
            _style.BorderColor = new Color(0.28f, 0.56f, 0.90f, 0.92f);

        var sb = new StringBuilder(512);
        string modeTag = autoPlay ? "AUTO" : "ADVISORY";
        string engineTag;
        if (tcpConnected && _cfg.Backend != "builtin")
            engineTag = reply?.CkptVersion is { } tv ? $"external service v{tv}" : "external service connected";
        else if (_cfg.Backend == "tcp")
            engineTag = $"waiting for external service ({_cfg.Host}:{_cfg.Port})";
        else if (reply is { Source: "builtin" })
            engineTag = $"built-in model: {reply.ModelCharacter} v{reply.CkptVersion}";
        else if (!string.IsNullOrEmpty(nativeStatus))
            engineTag = nativeStatus;
        else
            engineTag = "built-in engine idle";

        sb.AppendLine($"[{modeTag}] · {engineTag}");

        if (!string.IsNullOrEmpty(decisionKind))
        {
            string noteSuffix = !string.IsNullOrEmpty(statusNote) ? $" · {statusNote}" : "";
            sb.AppendLine($"Node #{seq}: {LocalizeDecision(decisionKind)}{(waitingReply ? " (computing...)" : "")}{noteSuffix}");
        }
        else if (!string.IsNullOrEmpty(statusNote))
        {
            sb.AppendLine($"State: {statusNote}");
        }
        else
        {
            sb.AppendLine("State: waiting for a decision point...");
        }

        if (reply != null && reply.Seq == seq)
        {
            sb.AppendLine($"Next: ▶ [{reply.Action + 1}] {reply.Label} ({reply.Prob * 100f:0.1}%)");
            string winStr = reply.WinProb is { } wp ? $"Win prob {wp * 100f:0.1}%  |  " : "";
            sb.AppendLine($"Eval: {winStr}value {reply.Value:+0.000;-0.000;0.000} ({reply.InferMs:0.1}ms)");

            if (reply.Searched)
            {
                int sims = reply.SearchInfo?.Sims ?? 0;
                float sms = reply.SearchInfo?.ElapsedMs ?? 0f;
                string overrideTag = reply.Changed ? " [tree search override!]" : " [tree search confirmed]";
                sb.AppendLine($"Tree search: {sims} sims ({sms:0}ms){overrideTag}");
            }

            if (reply.TopK.Count > 0)
            {
                int take = Math.Clamp(_cfg.TopK, 1, reply.TopK.Count);
                sb.AppendLine($"Candidates (Top-{take} of {reply.TopK.Count}):");
                for (int i = 0; i < take; i++)
                {
                    var c = reply.TopK[i];
                    string mark = c.Index == reply.Action ? "▶" : " ";
                    string bar = MakeProbBar(c.Prob, 6);
                    sb.AppendLine($"{mark}[{c.Index + 1}] {bar} {c.Prob * 100f,5:0.1}%  {c.Label}");
                }
            }
        }

        sb.Append("Keys: [F8] auto  [F9] step  [F7] hide (Fn on Mac)");
        _label.Text = sb.ToString();
    }

    static string MakeProbBar(float prob, int width)
    {
        int filled = Math.Clamp((int)MathF.Round(prob * width), 0, width);
        return new string('█', filled) + new string('░', width - filled);
    }

    static string LocalizeDecision(string kind) => kind switch
    {
        "Combat" => "Combat",
        "CardSelect" => "Card select",
        "CardReward" => "Card reward",
        "Bundle" => "Bundle",
        "Rewards" => "Rewards",
        "Event" => "Event / opening",
        "RestSite" => "Rest Site",
        "Shop" => "Shop",
        "Treasure" => "Chest relic",
        "Minigame" => "Divination",
        "Map" => "Map route",
        _ => kind,
    };
}
