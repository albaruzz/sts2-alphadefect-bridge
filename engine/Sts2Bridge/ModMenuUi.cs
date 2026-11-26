using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;

namespace Sts2Bridge;

/// <summary>
/// Resolves STS2's built-in Chinese (CJK) font via FontManager.GetFontForLanguage("zhs", FontType.Regular)
/// so Chinese UI labels render crisply regardless of the player's current game language or OS font fallback.
/// </summary>
internal static class BridgeFonts
{
    static bool _queried;
    static Font? _cjkFont;

    public static Font? GetCjkFont()
    {
        if (_queried) return _cjkFont;
        _queried = true;
        try
        {
            var fmType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Localization.Fonts.FontManager");
            var ftType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Localization.Fonts.FontType");
            if (fmType != null && ftType != null)
            {
                var method = AccessTools.Method(fmType, "GetFontForLanguage", new[] { typeof(string), ftType });
                object regularEnum = Enum.ToObject(ftType, 0); // FontType.Regular = 0
                _cjkFont = method?.Invoke(null, new[] { "zhs", regularEnum }) as Font;
            }
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"CJK font lookup fallback: {e.Message}");
        }
        return _cjkFont;
    }

    public static void ApplyFont(Control ctrl, int fontSize = 14)
    {
        var font = GetCjkFont();
        if (font != null)
            ctrl.AddThemeFontOverride("font", font);
        ctrl.AddThemeFontSizeOverride("font_size", fontSize);
    }
}

/// <summary>
/// Injects an interactive Mod Options panel directly into Slay the Spire 2's native Modding Screen
/// (NModInfoContainer) when "Sts2Bridge" is selected, allowing players to adjust Top-K candidate count,
/// Auto-Play mode, action delay, inference backend, and HUD visibility right from the game's Mod menu.
/// </summary>
internal static class ModMenuUi
{
    static BridgeConfig? _cfg;
    static readonly ConditionalWeakTable<Control, OptionsPanelRefs> Panels = new();

    sealed class OptionsPanelRefs
    {
        public required PanelContainer Root;
        public required HSlider TopKSlider;
        public required Label TopKValue;
        public required Button AutoBtn;
        public required HSlider DelaySlider;
        public required Label DelayValue;
        public required Button BackendBtn;
        public required Button OverlayBtn;
        public bool Updating;
    }

    public static void Install(Harmony h, BridgeConfig cfg)
    {
        _cfg = cfg;
        var containerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen.NModInfoContainer");
        if (containerType == null)
        {
            BridgeLog.Warn("NModInfoContainer not found; skipping native ModdingScreen UI hook.");
            return;
        }

        if (AccessTools.Method(containerType, "Fill") is { } fillMethod)
            h.Patch(fillMethod, postfix: new HarmonyMethod(typeof(ModMenuUi), nameof(AfterFill)));

        if (AccessTools.Method(containerType, "Clear") is { } clearMethod)
            h.Patch(clearMethod, postfix: new HarmonyMethod(typeof(ModMenuUi), nameof(AfterClear)));

        BridgeLog.Info("Installed native ModdingScreen options panel hooks on NModInfoContainer.");
    }

    static void AfterFill(object __instance, object? __0)
    {
        if (__instance is not Control container || _cfg == null) return;
        try
        {
            string? modId = ExtractModId(__0);
            if (!string.Equals(modId, "Sts2Bridge", StringComparison.OrdinalIgnoreCase))
            {
                if (Panels.TryGetValue(container, out var existing))
                    existing.Root.Visible = false;
                return;
            }

            if (!Panels.TryGetValue(container, out var refs))
            {
                refs = BuildPanel(container, _cfg);
                Panels.Add(container, refs);
            }

            RefreshPanel(refs, _cfg);
            refs.Root.Visible = true;
            refs.Root.MoveToFront();
        }
        catch (Exception e)
        {
            BridgeLog.Warn($"ModMenuUi.AfterFill error: {e.Message}");
        }
    }

    static void AfterClear(object __instance)
    {
        if (__instance is Control container && Panels.TryGetValue(container, out var refs))
            refs.Root.Visible = false;
    }

    static string? ExtractModId(object? modObj)
    {
        if (modObj == null) return null;
        var manifestField = AccessTools.Field(modObj.GetType(), "manifest");
        var manifest = manifestField?.GetValue(modObj);
        if (manifest == null) return null;
        var idField = AccessTools.Field(manifest.GetType(), "id");
        return idField?.GetValue(manifest) as string;
    }

    static OptionsPanelRefs BuildPanel(Control container, BridgeConfig cfg)
    {
        var boxStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.10f, 0.15f, 0.96f),
            BorderColor = new Color(0.35f, 0.65f, 0.95f, 0.92f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 16,
            ContentMarginRight = 16,
            ContentMarginTop = 12,
            ContentMarginBottom = 12,
        };

        var panel = new PanelContainer
        {
            Name = "Sts2BridgeModOptionsPanel",
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        panel.AddThemeStyleboxOverride("panel", boxStyle);
        panel.AnchorLeft = 0.02f;
        panel.AnchorRight = 0.98f;
        panel.AnchorTop = 0.98f;
        panel.AnchorBottom = 0.98f;
        panel.GrowVertical = Control.GrowDirection.Begin;
        container.AddChild(panel);

        var vbox = new VBoxContainer { Name = "OptionsVBox" };
        vbox.AddThemeConstantOverride("separation", 8);
        panel.AddChild(vbox);

        var title = new Label { Text = "⚙ 模组选项 · AI 预测与自动模式" };
        BridgeFonts.ApplyFont(title, 16);
        title.AddThemeColorOverride("font_color", new Color(0.98f, 0.84f, 0.38f, 1f));
        vbox.AddChild(title);

        // 1. Top-K row
        var topKRow = MakeRow(vbox, "预测候选数量 (Top-K):");
        var topKMinus = MakeSmallBtn(" - ");
        var topKSlider = new HSlider
        {
            MinValue = 1,
            MaxValue = 10,
            Step = 1,
            Value = cfg.TopK,
            CustomMinimumSize = new Vector2(140f, 24f),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        var topKPlus = MakeSmallBtn(" + ");
        var topKLabel = MakeValueLabel($"Top-{cfg.TopK}", 64f);
        topKRow.AddChild(topKMinus);
        topKRow.AddChild(topKSlider);
        topKRow.AddChild(topKPlus);
        topKRow.AddChild(topKLabel);

        // 2. Auto-Play row
        var autoRow = MakeRow(vbox, "自动模式 (Auto-Play):");
        var autoBtn = MakeActionBtn("", 220f);
        autoRow.AddChild(autoBtn);

        // 3. Action Delay row
        var delayRow = MakeRow(vbox, "自动出牌间隔:");
        var delayMinus = MakeSmallBtn(" - ");
        var delaySlider = new HSlider
        {
            MinValue = 50,
            MaxValue = 1500,
            Step = 50,
            Value = cfg.ActionDelayMs,
            CustomMinimumSize = new Vector2(140f, 24f),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        var delayPlus = MakeSmallBtn(" + ");
        var delayLabel = MakeValueLabel($"{cfg.ActionDelayMs} ms", 72f);
        delayRow.AddChild(delayMinus);
        delayRow.AddChild(delaySlider);
        delayRow.AddChild(delayPlus);
        delayRow.AddChild(delayLabel);

        // 4. Backend row
        var backendRow = MakeRow(vbox, "推理引擎:");
        var backendBtn = MakeActionBtn("", 240f);
        backendRow.AddChild(backendBtn);

        // 5. Overlay Visibility row
        var ovRow = MakeRow(vbox, "局内右上角预测面板:");
        var ovBtn = MakeActionBtn("", 220f);
        ovRow.AddChild(ovBtn);

        var hint = new Label
        {
            Text = "提示: 局内也可随时点击右上角 [⚙ 选项] 或按 F8(切换自动) / F9(单步) / F7(显隐面板)",
        };
        BridgeFonts.ApplyFont(hint, 12);
        hint.AddThemeColorOverride("font_color", new Color(0.70f, 0.76f, 0.84f, 0.95f));
        vbox.AddChild(hint);

        var refs = new OptionsPanelRefs
        {
            Root = panel,
            TopKSlider = topKSlider,
            TopKValue = topKLabel,
            AutoBtn = autoBtn,
            DelaySlider = delaySlider,
            DelayValue = delayLabel,
            BackendBtn = backendBtn,
            OverlayBtn = ovBtn,
        };

        topKMinus.Pressed += () => { cfg.TopK = Math.Max(1, cfg.TopK - 1); cfg.SaveAndNotify(); };
        topKPlus.Pressed += () => { cfg.TopK = Math.Min(10, cfg.TopK + 1); cfg.SaveAndNotify(); };
        topKSlider.ValueChanged += val =>
        {
            if (refs.Updating) return;
            cfg.TopK = (int)Math.Round(val);
            cfg.SaveAndNotify();
        };

        autoBtn.Pressed += () => { cfg.AutoPlay = !cfg.AutoPlay; cfg.SaveAndNotify(); };

        delayMinus.Pressed += () => { cfg.ActionDelayMs = Math.Max(50, cfg.ActionDelayMs - 50); cfg.SaveAndNotify(); };
        delayPlus.Pressed += () => { cfg.ActionDelayMs = Math.Min(1500, cfg.ActionDelayMs + 50); cfg.SaveAndNotify(); };
        delaySlider.ValueChanged += val =>
        {
            if (refs.Updating) return;
            cfg.ActionDelayMs = (int)(Math.Round(val / 50.0) * 50);
            cfg.SaveAndNotify();
        };

        backendBtn.Pressed += () => cfg.CycleBackend();
        ovBtn.Pressed += () => { cfg.ShowOverlay = !cfg.ShowOverlay; cfg.SaveAndNotify(); };

        cfg.Changed += () =>
        {
            if (GodotObject.IsInstanceValid(panel))
                RefreshPanel(refs, cfg);
        };

        return refs;
    }

    static void RefreshPanel(OptionsPanelRefs refs, BridgeConfig cfg)
    {
        refs.Updating = true;
        try
        {
            refs.TopKSlider.Value = cfg.TopK;
            refs.TopKValue.Text = $"Top-{cfg.TopK}";

            refs.AutoBtn.Text = cfg.AutoPlay ? "● 已开启 (自动执行最优动作)" : "○ 已关闭 (仅显示预测建议)";
            refs.AutoBtn.Modulate = cfg.AutoPlay ? new Color(0.55f, 1.0f, 0.68f) : new Color(0.92f, 0.95f, 1.0f);

            refs.DelaySlider.Value = cfg.ActionDelayMs;
            refs.DelayValue.Text = $"{cfg.ActionDelayMs} ms";

            refs.BackendBtn.Text = cfg.BackendDisplayLabel();

            refs.OverlayBtn.Text = cfg.ShowOverlay ? "● 显示面板 (快捷键 F7)" : "○ 隐藏面板 (快捷键 F7)";
        }
        finally
        {
            refs.Updating = false;
        }
    }

    static HBoxContainer MakeRow(VBoxContainer parent, string labelText)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var lbl = new Label
        {
            Text = labelText,
            CustomMinimumSize = new Vector2(180f, 0f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        BridgeFonts.ApplyFont(lbl, 14);
        lbl.AddThemeColorOverride("font_color", new Color(0.90f, 0.93f, 0.98f, 1f));
        row.AddChild(lbl);
        parent.AddChild(row);
        return row;
    }

    static Button MakeSmallBtn(string text)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(32f, 28f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        BridgeFonts.ApplyFont(btn, 14);
        return btn;
    }

    static Button MakeActionBtn(string text, float minWidth)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(minWidth, 28f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        BridgeFonts.ApplyFont(btn, 13);
        return btn;
    }

    static Label MakeValueLabel(string text, float minWidth)
    {
        var lbl = new Label
        {
            Text = text,
            CustomMinimumSize = new Vector2(minWidth, 0f),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        BridgeFonts.ApplyFont(lbl, 14);
        lbl.AddThemeColorOverride("font_color", new Color(0.48f, 0.85f, 1.0f, 1f));
        return lbl;
    }
}
