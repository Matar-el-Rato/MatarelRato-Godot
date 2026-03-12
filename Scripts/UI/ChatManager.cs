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
		_chatHistory.ScrollFollowing = true;
		_chatHistory.FitContent = true;
		_chatInput.Visible = false;
		
		// Initial closed state: Input hidden, Spacer takes the 28px (24 + 4 separation) to reserve position
		_inputWrapper.Visible = false;
		_bottomSpacer.CustomMinimumSize = new Vector2(0, 28);
		
		_chatInput.TextSubmitted += OnTextSubmitted;
		
		// Initial state: Invisible and transparent
		Modulate = new Color(1, 1, 1, 0);
		Visible = false;
		
		CallDeferred(MethodName.FindPlayer);
		
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

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventKey key && key.Pressed && !key.Echo)
		{
			if (key.Keycode == Key.T && !_isChatOpen)
			{
				if (FocusController.Instance != null && FocusController.Instance.IsFocused) return;
				
				GetViewport().SetInputAsHandled();
				OpenChat();
			}
			else if (key.Keycode == Key.Escape && _isChatOpen)
			{
				GetViewport().SetInputAsHandled();
				CloseChat(false);
			}
			
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
		_chatInput.Visible = false;
		_inputWrapper.Visible = false;
		_bottomSpacer.CustomMinimumSize = new Vector2(0, 28);
		
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
			_instance._chatHistory.AppendText(message + "\n");
			_instance.StartFadeTimer();
			_instance.HandleNewMessage();
		}
	}
	
	private async void HandleNewMessage()
	{
		// Force layout update
		_chatHistory.QueueRedraw();
		
		// Wait multiple frames for the first message to avoid "stale large height" issue
		// This ensures GetContentHeight() returns a stabilized value.
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		
		UpdateLayout();
		
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		ScrollToBottom();
	}

	private void UpdateLayout()
	{
		if (_chatHistory == null || _scrollContainer == null) return;
		
		float historyHeight = _chatHistory.GetContentHeight();
		
		// Sanity check: if height is 0, we don't want to hide it completely if there's text
		// But usually GetContentHeight returns at least one line height if it's ready.
		if (historyHeight <= 0) return;

		// Cap it at 40% of screen
		float screenHeight = GetViewportRect().Size.Y;
		float maxHeight = screenHeight * 0.4f - 20; 
		
		float targetHeight = Mathf.Min(historyHeight, maxHeight);
		_scrollContainer.CustomMinimumSize = new Vector2(0, targetHeight);
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
