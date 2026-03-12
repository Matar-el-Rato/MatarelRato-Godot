using Godot;
using System;

public partial class DialogBubble : Node3D
{
	[Export] public RichTextLabel TextLabel;
	[Export] public SubViewport Viewport;
	[Export] public Sprite3D DisplaySprite;
	[Export] public float TypingSpeed = 0.03f;
	[Export] public AudioStreamPlayer TypingAudio;
	[Export] public Vector3 BubbleOffset = new Vector3(0, 0.25f, 0);

	private Tween _typingTween;
	private Tween _bobTween;
	private bool _isTyping = false;

	public override void _Ready()
	{
		Visible = false;
		if (TextLabel != null)
		{
			TextLabel.VisibleRatio = 0;
		}
		
		if (DisplaySprite != null)
		{
			DisplaySprite.Position = BubbleOffset;
			StartBobbing();
		}
	}

	private void StartBobbing()
	{
		if (DisplaySprite == null) return;
		
		if (_bobTween != null) _bobTween.Kill();
		_bobTween = CreateTween().SetLoops();
		
		float bobAmount = 0.03f; // 3cm bob
		Vector3 targetPos = BubbleOffset + new Vector3(0, bobAmount, 0);
		
		_bobTween.TweenProperty(DisplaySprite, "position", targetPos, 2.0f)
			.SetTrans(Tween.TransitionType.Sine)
			.SetEase(Tween.EaseType.InOut);
		_bobTween.TweenProperty(DisplaySprite, "position", BubbleOffset, 2.0f)
			.SetTrans(Tween.TransitionType.Sine)
			.SetEase(Tween.EaseType.InOut);
	}

	public async void ShowDialog(string text)
	{
		if (_isTyping) return;
		_isTyping = true;
		
		Visible = true;
		Scale = Vector3.Zero;
		
		if (TextLabel != null)
		{
			TextLabel.Text = text;
			TextLabel.VisibleRatio = 0;
		}

		if (DisplaySprite != null)
		{
			DisplaySprite.Scale = new Vector3(0.4f, 0.4f, 0.4f);
		}

		// Pop up animation
		Tween popupTween = CreateTween();
		popupTween.TweenProperty(this, "scale", Vector3.One, 0.3f)
			.SetTrans(Tween.TransitionType.Cubic)
			.SetEase(Tween.EaseType.Out);

		// Typing and Growth animation
		if (TextLabel != null)
		{
			int totalChars = text.Length;
			float duration = totalChars * TypingSpeed;
			
			_typingTween = CreateTween().SetParallel(true);
			_typingTween.TweenProperty(TextLabel, "visible_ratio", 1.0f, duration);
			
			// Growth animation: Start from a smaller scale and reach full size smoothly as text types
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

	public void HideDialog()
	{
		Tween hideTween = CreateTween();
		hideTween.TweenProperty(this, "scale", Vector3.Zero, 0.2f)
			.SetTrans(Tween.TransitionType.Back)
			.SetEase(Tween.EaseType.In);
		hideTween.Finished += () => Visible = false;
	}
}
