using Godot;
using System;

public partial class ChatManager : Control
{
	private static ChatManager _instance;
	
	private RichTextLabel _chatHistory;
	private ScrollContainer _scrollContainer;
	private PanelContainer _panelContainer;
	private Control _inputWrapper;
	private Control _bottomSpacer;
	private LineEdit _chatInput;
	private PlayerCameraController _player;
	
	private bool _isChatOpen = false;
	private Tween _fadeTween;
	private double _pingTimer = 0;

	public override void _Ready()
	{
		_instance = this;
		_chatHistory = GetNode<RichTextLabel>("%ChatHistory");
		_scrollContainer = GetNode<ScrollContainer>("%ScrollContainer");
		_panelContainer = GetNode<PanelContainer>("%PanelContainer");
		_inputWrapper = GetNode<Control>("%InputWrapper");
		_bottomSpacer = GetNode<Control>("%BottomSpacer");
		_chatInput = GetNode<LineEdit>("%ChatInput");
		
		_chatHistory.Text = "";
		_chatInput.Visible = false;
		
		// Initial closed state: Input hidden, Spacer takes the 16px to reserve position
		_inputWrapper.Visible = false;
		_bottomSpacer.CustomMinimumSize = new Vector2(0, 16);
		
		_chatInput.TextSubmitted += OnTextSubmitted;
		
		// Initial state: Invisible and transparent
		Modulate = new Color(1, 1, 1, 0);
		Visible = false;
		
		CallDeferred(MethodName.FindPlayer);
		
		// Reset ping timer to start firing after 10s
		_pingTimer = 0;
	}

	public override void _Process(double delta)
	{
		_pingTimer += delta;
		if (_pingTimer >= 30.0)
		{
			_pingTimer = 0;
			AddLog("[color=#aaaaaa]Ping...[/color]");
		}
	}

	private void FindPlayer()
	{
		_player = GetTree().Root.FindChild("Player", true, false) as PlayerCameraController;
	}

	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventKey key && key.Pressed && !key.Echo)
		{
			if (key.Keycode == Key.T && !_isChatOpen)
			{
				GetViewport().SetInputAsHandled();
				OpenChat();
			}
			else if (key.Keycode == Key.Escape && _isChatOpen)
			{
				GetViewport().SetInputAsHandled();
				CloseChat(false);
			}
			
			// Scrolling with Arrows
			if (_isChatOpen && _scrollContainer != null)
			{
				var scrollBar = _scrollContainer.GetVScrollBar();
				if (key.Keycode == Key.Up)
				{
					scrollBar.Value -= 24;
					GetViewport().SetInputAsHandled();
				}
				else if (key.Keycode == Key.Down)
				{
					scrollBar.Value += 24;
					GetViewport().SetInputAsHandled();
				}
			}
		}
	}

	public void OpenChat()
	{
		ResetFade();
		_isChatOpen = true;
		
		// Swap spacer for input zone
		_inputWrapper.Visible = true;
		_bottomSpacer.CustomMinimumSize = new Vector2(0, 0);
		
		_chatInput.Visible = true;
		_chatInput.GrabFocus();
		_chatInput.Clear();
		
		if (_player != null) _player.MovementEnabled = false;
		Input.MouseMode = Input.MouseModeEnum.Visible;
		
		UpdateLayout();
	}

	public void CloseChat(bool sendMessage)
	{
		string text = _chatInput.Text.Trim();
		if (sendMessage && !string.IsNullOrEmpty(text))
		{
			AddLog($"[color=#ffffff][LOG][/color] {text}");
		}
		
		_isChatOpen = false;
		
		// Swap input zone back for spacer to maintain history position
		_chatInput.Visible = false;
		_inputWrapper.Visible = false;
		_bottomSpacer.CustomMinimumSize = new Vector2(0, 16);
		
		_chatInput.ReleaseFocus();
		_chatInput.Clear();
		
		if (_player != null) _player.MovementEnabled = true;
		Input.MouseMode = Input.MouseModeEnum.Captured;
		
		UpdateLayout();
		StartFadeTimer();
	}

	private void OnTextSubmitted(string text)
	{
		CloseChat(true);
	}

	private void ResetFade()
	{
		if (_fadeTween != null) _fadeTween.Kill();
		Modulate = new Color(1, 1, 1, 1);
		Visible = true;
	}

	private void StartFadeTimer()
	{
		if (_fadeTween != null) _fadeTween.Kill();
		if (_isChatOpen) return;

		_fadeTween = CreateTween();
		_fadeTween.TweenInterval(3.0f);
		_fadeTween.TweenProperty(this, "modulate:a", 0.0f, 1.0f);
	}

	public static void AddLog(string message)
	{
		if (_instance != null && IsInstanceValid(_instance))
		{
			_instance.ResetFade();
			
			// ALWAYS append a newline AFTER the message to ensure vertical stacking.
			// This is more robust than checking for existing content because AppendText 
			// might not immediately update the Text property.
			_instance._chatHistory.AppendText(message + "\n");
			
			_instance.StartFadeTimer();
			
			// Defer height calculation so RichTextLabel can process the new text
			_instance.CallDeferred(MethodName.UpdateLayout);
			_instance.CallDeferred(MethodName.ScrollToBottom);
		}
		else
		{
			GD.Print("[ChatLog] " + message);
		}
	}

	private void UpdateLayout()
	{
		if (_chatHistory == null || _scrollContainer == null) return;
		
		// Force label to update before measuring height
		float historyHeight = _chatHistory.GetContentHeight();
		
		// Cap it at 40% of screen
		float screenHeight = GetViewportRect().Size.Y;
		float maxHeight = screenHeight * 0.4f - 20; 
		
		float targetHeight = Mathf.Min(historyHeight, maxHeight);
		_scrollContainer.CustomMinimumSize = new Vector2(0, targetHeight);
		
		GD.Print($"Chat: Layout Updated. Height: {targetHeight}, Open: {_isChatOpen}");
	}

	private void ScrollToBottom()
	{
		if (_scrollContainer != null)
		{
			var scrollBar = _scrollContainer.GetVScrollBar();
			scrollBar.Value = scrollBar.MaxValue;
		}
	}
}
