// ═══════════════════════════════════════════════════
// DialogBubble.cs
// Animated 3D speech bubble used by the NPC.
// Supports typewriter reveal, popup/shrink tweens,
// and a gentle sine-wave bobbing idle loop.
// ═══════════════════════════════════════════════════
using Godot;
using System;

/// <summary>
/// Animated 3D dialog bubble rendered via a <see cref="SubViewport"/> + <see cref="Sprite3D"/>.
/// Displays text with a typewriter effect and bobs up and down when idle.
/// Call <see cref="ShowDialog"/> to display text and <see cref="HideDialog"/> to dismiss it.
/// </summary>
public partial class DialogBubble : Node3D
{
	[Export] public RichTextLabel       TextLabel;
	[Export] public SubViewport         Viewport;
	[Export] public Sprite3D            DisplaySprite;
	[Export] public float               TypingSpeed = 0.03f;
	[Export] public AudioStreamPlayer   TypingAudio;
	[Export] public Vector3             BubbleOffset = new Vector3(0, 0.25f, 0);

	private Tween               _typingTween;
	private Tween               _bobTween;
	private bool                _isTyping = false;
	private AudioStreamPlayer3D _oinkAudio;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_oinkAudio = GetNodeOrNull<AudioStreamPlayer3D>("OinkAudio");

		Visible = false;
		if (TextLabel != null)
			TextLabel.VisibleRatio = 0;

		if (DisplaySprite != null)
		{
			DisplaySprite.Position = BubbleOffset;
			StartBobbing();
		}
	}

	// ── Bobbing ───────────────────────────────────────────────────────────────

	/// <summary>
	/// Starts an infinite sine-wave vertical bob on the display sprite.
	/// </summary>
	private void StartBobbing()
	{
		if (DisplaySprite == null) return;

		if (_bobTween != null) _bobTween.Kill();
		_bobTween = CreateTween().SetLoops();

		float bobAmount = 0.03f; // 3 cm vertical travel
		Vector3 targetPos = BubbleOffset + new Vector3(0, bobAmount, 0);

		_bobTween.TweenProperty(DisplaySprite, "position", targetPos, 2.0f)
			.SetTrans(Tween.TransitionType.Sine)
			.SetEase(Tween.EaseType.InOut);
		_bobTween.TweenProperty(DisplaySprite, "position", BubbleOffset, 2.0f)
			.SetTrans(Tween.TransitionType.Sine)
			.SetEase(Tween.EaseType.InOut);
	}

	// ── Public API ────────────────────────────────────────────────────────────

	/// <summary>
	/// Pops the bubble into view and types out <paramref name="text"/> character by character.
	/// The sprite also grows from a small scale to full size as text is revealed.
	/// </summary>
	/// <param name="text">The text to display (BBCode supported via RichTextLabel).</param>
	/// <param name="force">If true, interrupts any currently-typing dialog.</param>
	public async void ShowDialog(string text, bool force = false)
	{
		if (_isTyping && !force) return;
		if (_isTyping && force)
		{
			_typingTween?.Kill();
			_isTyping = false;
		}
		_isTyping = true;
		_oinkAudio?.Play();

		Visible = true;
		Scale   = Vector3.Zero;

		if (TextLabel != null)
		{
			TextLabel.Text         = text;
			TextLabel.VisibleRatio = 0;
		}

		if (DisplaySprite != null)
			DisplaySprite.Scale = new Vector3(0.4f, 0.4f, 0.4f);

		// Pop-up animation: scale the whole node from 0 to 1.
		Tween popupTween = CreateTween();
		popupTween.TweenProperty(this, "scale", Vector3.One, 0.3f)
			.SetTrans(Tween.TransitionType.Cubic)
			.SetEase(Tween.EaseType.Out);

		if (TextLabel != null)
		{
			int   totalChars = text.Length;
			float duration   = totalChars * TypingSpeed;

			// Simultaneously reveal text and grow the sprite to match content.
			_typingTween = CreateTween().SetParallel(true);
			_typingTween.TweenProperty(TextLabel, "visible_ratio", 1.0f, duration);

			if (DisplaySprite != null)
			{
				DisplaySprite.Scale = new Vector3(0.4f, 0.4f, 0.4f);
				_typingTween.TweenProperty(DisplaySprite, "scale", Vector3.One, duration)
					.SetTrans(Tween.TransitionType.Cubic)
					.SetEase(Tween.EaseType.Out);
			}

			if (TypingAudio != null)
			{
				TypingAudio.Play();
				_typingTween.Finished += () => TypingAudio.Stop();
			}

			await ToSignal(_typingTween, Tween.SignalName.Finished);
		}

		_isTyping = false;
	}

	/// <summary>
	/// Shrinks the bubble out of view using a Back ease-in, then hides it.
	/// </summary>
	public void HideDialog()
	{
		Tween hideTween = CreateTween();
		hideTween.TweenProperty(this, "scale", Vector3.Zero, 0.2f)
			.SetTrans(Tween.TransitionType.Back)
			.SetEase(Tween.EaseType.In);
		hideTween.Finished += () => Visible = false;
	}
}
