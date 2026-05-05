using Godot;

/// <summary>
/// Self-contained 3D tooltip bubble (SubViewport → Sprite3D billboard).
/// Yellow border, yellow text, black background, no audio.
/// After two frames the sprite is cropped to the actual text height via
/// RegionRect so no empty space is shown at the bottom.
/// </summary>
public partial class ItemTooltipBubble : Node3D
{
	public string Text { get; set; } = "";

	// Narrow width → more frequent line-breaks.
	// Height is generous; RegionRect crops it to content after layout.
	private const int   ViewportW   = 240;
	private const int   ViewportH   = 200;
	private const float PixelSize   = 0.0024f;
	private const int   FontSize    = 18;
	private const float TypingSpeed = 0.007f;

	private SubViewport   _viewport;
	private RichTextLabel _label;
	private Sprite3D      _sprite;

	public override void _Ready()
	{
		// ── SubViewport ──────────────────────────────────────────────────────────
		_viewport = new SubViewport();
		_viewport.Size                   = new Vector2I(ViewportW, ViewportH);
		_viewport.TransparentBg          = true;
		_viewport.Disable3D              = true;
		_viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
		AddChild(_viewport);

		// ── Panel ────────────────────────────────────────────────────────────────
		var style = new StyleBoxFlat();
		style.BgColor = Colors.Black;
		style.SetBorderWidthAll(2);
		style.BorderColor = Colors.Yellow;
		style.SetCornerRadiusAll(4);

		var panel = new PanelContainer();
		panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		panel.GrowHorizontal = Control.GrowDirection.Both;
		panel.GrowVertical   = Control.GrowDirection.Both;
		panel.AddThemeStyleboxOverride("panel", style);
		_viewport.AddChild(panel);

		var margin = new MarginContainer();
		margin.AddThemeConstantOverride("margin_left",   10);
		margin.AddThemeConstantOverride("margin_right",  10);
		margin.AddThemeConstantOverride("margin_top",     8);
		margin.AddThemeConstantOverride("margin_bottom",  8);
		panel.AddChild(margin);

		// ── Label ────────────────────────────────────────────────────────────────
		_label = new RichTextLabel();
		_label.BbcodeEnabled = true;
		_label.AutowrapMode  = TextServer.AutowrapMode.Word;
		_label.FitContent    = false;
		_label.ScrollActive  = false;
		_label.Text          = Text;
		_label.VisibleRatio  = 0f;
		_label.AddThemeColorOverride("default_color", Colors.Yellow);
		_label.AddThemeFontSizeOverride("normal_font_size", FontSize);

		var font = GD.Load<FontFile>("res://Assets/Fonts/Jersey10-Regular.ttf");
		if (font != null)
			_label.AddThemeFontOverride("normal_font", font);

		margin.AddChild(_label);

		// ── Sprite3D ─────────────────────────────────────────────────────────────
		_sprite = new Sprite3D();
		_sprite.Texture        = _viewport.GetTexture();
		_sprite.Billboard      = BaseMaterial3D.BillboardModeEnum.Enabled;
		_sprite.PixelSize      = PixelSize;
		_sprite.DoubleSided    = false;
		_sprite.TextureFilter  = BaseMaterial3D.TextureFilterEnum.Nearest;
		_sprite.RenderPriority = 10;
		AddChild(_sprite);

		// Two deferred calls so the SubViewport has rendered at least once
		// before we query the content height and crop the sprite.
		CallDeferred(MethodName.ScheduleFit);

		// ── Pop-in then typewrite ─────────────────────────────────────────────────
		Scale = Vector3.Zero;
		var popTween = CreateTween();
		popTween.TweenProperty(this, "scale", Vector3.One, 0.2f)
			.SetTrans(Tween.TransitionType.Back)
			.SetEase(Tween.EaseType.Out);
		popTween.Finished += () =>
		{
			if (!IsInsideTree()) return;
			CreateTween().TweenProperty(_label, "visible_ratio", 1.0f, Text.Length * TypingSpeed);
		};
	}

	// Called at end of first frame → schedules FitToContent for end of second frame.
	private void ScheduleFit() => CallDeferred(MethodName.FitToContent);

	private void FitToContent()
	{
		if (!IsInsideTree() || _label == null) return;

		int contentH = (int)_label.GetContentHeight();
		if (contentH <= 0)
		{
			// Layout not settled yet — retry next frame.
			CallDeferred(MethodName.FitToContent);
			return;
		}

		// Margins (top 8 + bottom 8) + border (2 + 2) + small breathing room.
		int totalH = contentH + 22;

		_sprite.RegionEnabled = true;
		_sprite.RegionRect    = new Rect2(0, 0, ViewportW, totalH);
	}

	public void DismissAndFree()
	{
		if (!IsInsideTree()) { QueueFree(); return; }
		var tween = CreateTween();
		tween.TweenProperty(this, "scale", Vector3.Zero, 0.14f)
			.SetTrans(Tween.TransitionType.Back)
			.SetEase(Tween.EaseType.In);
		tween.Finished += QueueFree;
	}
}
