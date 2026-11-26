using System.Text;
using Godot;

namespace Sts2Bridge;

/// <summary>
/// Non-intrusive Godot CanvasLayer HUD overlay anchored in the top-right corner.
/// MouseFilter is set to Ignore on every Control so clicks pass straight through to the game UI.
/// Hotkeys:
///   F8  - Toggle Auto-Play vs Advisory Mode
///   F9  - Execute single recommended action (in Advisory Mode)
///   F10 - Reconnect TCP link to play_live.py
///   F7  - Show / Hide HUD overlay
/// </summary>
public partial class BridgeOverlay : CanvasLayer
{
    readonly BridgeConfig _cfg;
    PanelContainer? _panel;
    StyleBoxFlat? _style;
    Label? _label;
    bool _visible = true;

    public event Action? OnToggleMode;
    public event Action? OnStepOnce;
    public event Action? OnReconnect;

    public BridgeOverlay(BridgeConfig cfg)
    {
        _cfg = cfg;
        _visible = cfg.ShowOverlay;
        Name = "AlphaDefectBridgeOverlay";
        Layer = 120;
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Ready()
    {
        var root = new Control
        {
            Name = "OverlayRoot",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        _style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.07f, 0.11f, 0.84f),
            BorderColor = new Color(0.28f, 0.56f, 0.90f, 0.90f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
        };

        _panel = new PanelContainer
        {
            Name = "HudPanel",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = _visible,
        };
        _panel.AddThemeStyleboxOverride("panel", _style);
        _panel.AnchorLeft = 1.0f;
        _panel.AnchorRight = 1.0f;
        _panel.AnchorTop = 0.0f;
        _panel.AnchorBottom = 0.0f;
        _panel.GrowHorizontal = Control.GrowDirection.Begin;
        _panel.GrowVertical = Control.GrowDirection.End;
        _panel.OffsetRight = -16f;
        _panel.OffsetTop = 74f;     // Below STS2 top bar (relics/potions/seed)
        _panel.CustomMinimumSize = new Vector2(310f, 0f);
        root.AddChild(_panel);

        _label = new Label
        {
            Name = "HudLabel",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = "AlphaDefect Bridge Initializing...",
        };
        _label.AddThemeFontSizeOverride("font_size", 14);
        _label.AddThemeColorOverride("font_color", new Color(0.93f, 0.95f, 0.98f, 1.0f));
        _label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
        _label.AddThemeConstantOverride("outline_size", 3);
        _panel.AddChild(_label);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        switch (key.Keycode)
        {
            case Key.F8:
                OnToggleMode?.Invoke();
                GetViewport()?.SetInputAsHandled();
                break;
            case Key.F9:
                OnStepOnce?.Invoke();
                GetViewport()?.SetInputAsHandled();
                break;
            case Key.F10:
                OnReconnect?.Invoke();
                GetViewport()?.SetInputAsHandled();
                break;
            case Key.F7:
                _visible = !_visible;
                if (_panel != null) _panel.Visible = _visible;
                GetViewport()?.SetInputAsHandled();
                break;
        }
    }

    public void UpdateHud(
        bool connected,
        bool autoPlay,
        string decisionKind,
        int seq,
        bool waitingReply,
        PolicyReply? reply,
        string? statusNote)
    {
        if (_panel == null || _label == null || _style == null) return;
        _panel.Visible = _visible;
        if (!_visible) return;

        // Border color indicates state: Gold = Tree Search active, Green = Auto-Play, Blue = Advisory, Gray/Red = Offline
        if (!connected)
            _style.BorderColor = new Color(0.75f, 0.28f, 0.28f, 0.88f);
        else if (reply?.Searched == true)
            _style.BorderColor = new Color(0.95f, 0.74f, 0.20f, 0.95f);
        else if (autoPlay)
            _style.BorderColor = new Color(0.22f, 0.82f, 0.48f, 0.92f);
        else
            _style.BorderColor = new Color(0.28f, 0.56f, 0.90f, 0.90f);

        var sb = new StringBuilder(384);
        string modeTag = autoPlay ? "AUTO-PLAY" : "ADVISORY";
        string connTag = connected
            ? (reply?.CkptVersion is { } v ? $"Connected (v{v})" : "Connected")
            : $"Offline ({_cfg.Host}:{_cfg.Port})";
        sb.AppendLine($"AlphaDefect [{modeTag}] · {connTag}");

        if (!string.IsNullOrEmpty(statusNote))
            sb.AppendLine($"State: {statusNote}");
        else if (!string.IsNullOrEmpty(decisionKind))
            sb.AppendLine($"Decision #{seq}: {decisionKind}{(waitingReply ? " (thinking...)" : "")}");
        else
            sb.AppendLine("State: Waiting for player decision...");

        if (reply != null && reply.Seq == seq)
        {
            string winStr = reply.WinProb is { } wp ? $" | Win≈{wp * 100f:0.0}%" : "";
            sb.AppendLine($"Recommend: [{reply.Action}] {reply.Label}");
            sb.AppendLine($"Value: {reply.Value:+0.000;-0.000;0.000}{winStr} ({reply.InferMs:0.0}ms)");

            if (reply.Searched)
            {
                int sims = reply.SearchInfo?.Sims ?? 0;
                float sms = reply.SearchInfo?.ElapsedMs ?? 0f;
                string overrideTag = reply.Changed ? " [TREE OVERRIDE]" : " [TREE CONFIRMED]";
                sb.AppendLine($"Search: {sims} sims in {sms:0}ms{overrideTag}");
            }
            else if (reply.SearchInfo is { Backend: not null } si && !string.IsNullOrEmpty(si.Reason) && si.Reason != "disabled")
            {
                string syncTag = si.Synced ? "mirror:ok" : "mirror:off";
                sb.AppendLine($"Search: greedy ({si.Reason}, {syncTag})");
            }

            if (reply.TopK.Count > 0)
            {
                sb.AppendLine("Top Candidates:");
                int take = Math.Min(3, reply.TopK.Count);
                for (int i = 0; i < take; i++)
                {
                    var c = reply.TopK[i];
                    string mark = c.Index == reply.Action ? "▶" : " ";
                    sb.AppendLine($" {mark} {c.Prob * 100f,5:0.1}%  {Truncate(c.Label, 34)}");
                }
            }
        }

        sb.Append("[F8] Auto/Adv  [F9] Step  [F10] Reconnect  [F7] Hide");
        _label.Text = sb.ToString();
    }

    static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..(maxLen - 1)] + "…";
}
