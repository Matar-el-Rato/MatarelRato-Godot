// ═══════════════════════════════════════════════════
// ChatManager.cs
// In-game chat overlay: displays scrollable BBCode log entries,
// handles T to open / Escape to close, and fades out when idle.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Collections.Concurrent;

/// <summary>
/// HUD chat panel displayed in the corner of the screen.
/// Press T to open, Enter to submit, Escape to cancel.
/// Messages are sent to the server (REQ_SEND_CHAT) and displayed only
/// when the server echoes them back as MSG_CHAT — so all players see the
/// same messages in the same order.
/// Auto-fades after 3 seconds of inactivity.
/// </summary>
public partial class ChatManager : Control
{
	// Static reference so any script can call AddLog without a node reference.
	private static ChatManager _instance;

	private RichTextLabel    _chatHistory;
	private ScrollContainer  _scrollContainer;
	private PanelContainer   _panelContainer;
	private Control          _inputWrapper;
	private Control          _bottomSpacer;
	private LineEdit         _chatInput;
	private PlayerCameraController _player;

	private bool   _isChatOpen = false;
	private Tween  _fadeTween;
	private double _pingTimer  = 0;

	// Incoming chat messages from the background listener thread are enqueued here
	// and drained on the main thread in _Process — the same pattern as ConnectedPlayersBoard.
	private readonly ConcurrentQueue<(string sender, string message)> _pendingMessages = new();

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _EnterTree()
	{
		// Subscribe here (not in _Ready) so the subscription is guaranteed to be
		// in place even if _Ready throws before reaching the subscription line,
		// and so it is properly restored if the node ever re-enters the tree.
		LiveConnectionManager.OnChatMessageReceived += OnChatMessageReceivedBackground;
	}

	public override void _Ready()
	{
		_instance        = this;
		_chatHistory     = GetNode<RichTextLabel>("%ChatHistory");
		_scrollContainer = GetNode<ScrollContainer>("%ScrollContainer");
		_panelContainer  = GetNode<PanelContainer>("%PanelContainer");
		_inputWrapper    = GetNode<Control>("%InputWrapper");
		_bottomSpacer    = GetNode<Control>("%BottomSpacer");
		_chatInput       = GetNode<LineEdit>("%ChatInput");

		_chatHistory.Text           = "";
		_chatHistory.ScrollFollowing = true;
		_chatHistory.FitContent     = true;
		_chatInput.Visible          = false;
		_chatInput.MaxLength        = 100;

		// Reserve space at the bottom so the log doesn't jump when the input opens.
		_inputWrapper.Visible                    = false;
		_bottomSpacer.CustomMinimumSize          = new Vector2(0, 28);

		_chatInput.TextSubmitted += OnTextSubmitted;

		// Start invisible — the chat fades in when a message arrives.
		Modulate = new Color(1, 1, 1, 0);
		Visible  = false;

		_pingTimer = 0;

		// Explicitly enable _Process. Godot does not automatically enable it for
		// nodes that did not previously override it in a prior compiled version.
		SetProcess(true);

		CallDeferred(MethodName.FindPlayer);
	}

	public override void _ExitTree()
	{
		LiveConnectionManager.OnChatMessageReceived -= OnChatMessageReceivedBackground;
	}

	public override void _Process(double delta)
	{
		while (_pendingMessages.TryDequeue(out var item))
			AddLog($"[color=#e8e0a0]{item.sender}[/color]: {item.message}");
	}

	private void FindPlayer()
	{
		_player = GetTree().Root.FindChild("Player", true, false) as PlayerCameraController;
	}

	// ── Network chat ──────────────────────────────────────────────────────────

	/// <summary>
	/// Called on the background listener thread — must NOT touch Godot nodes.
	/// Enqueues to be drained on the main thread in _Process.
	/// </summary>
	private void OnChatMessageReceivedBackground(string sender, string message)
	{
		_pendingMessages.Enqueue((sender, message));
	}

	// ── Input ─────────────────────────────────────────────────────────────────

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventKey key && key.Pressed && !key.Echo)
		{
			if (key.Keycode == Key.T && !_isChatOpen)
			{
				// Don't steal T from the focus UI (e.g. clipboard text fields).
				if (FocusController.Instance != null && FocusController.Instance.IsFocused) return;

				GetViewport().SetInputAsHandled();
				OpenChat();
			}
			else if (key.Keycode == Key.Escape && _isChatOpen)
			{
				GetViewport().SetInputAsHandled();
				CloseChat(false);
			}

			// Scroll the history with arrow keys while chat is open.
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

	// ── Open / Close ──────────────────────────────────────────────────────────

	/// <summary>Shows the input field, pauses player movement, and switches to visible mouse.</summary>
	public void OpenChat()
	{
		ResetFade();
		_isChatOpen = true;

		_inputWrapper.Visible          = true;
		_bottomSpacer.CustomMinimumSize = new Vector2(0, 0);

		_chatInput.Visible = true;
		_chatInput.GrabFocus();
		_chatInput.Clear();

		if (_player != null) _player.MovementEnabled = false;
		Input.MouseMode = Input.MouseModeEnum.Visible;

		UpdateLayout();
	}

	/// <summary>
	/// Hides the input field and optionally submits the current text.
	/// Restores player movement and recaptures the mouse.
	/// </summary>
	/// <param name="sendMessage">If true, sends the input text to the server before closing.</param>
	public void CloseChat(bool sendMessage)
	{
		string text = _chatInput.Text.Trim();
		if (sendMessage && !string.IsNullOrEmpty(text))
		{
			// '/' is reserved for commands — route to HandleCommand instead of sending.
			if (text.StartsWith("/"))
				HandleCommand(text);
			else
				SendMessage(text);
		}

		_isChatOpen                    = false;
		_chatInput.Visible             = false;
		_inputWrapper.Visible          = false;
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

	/// <summary>Sends a message to the server. The server echoes it back to all clients.</summary>
	private void SendMessage(string text)
	{
		if (!AuthManager.IsLoggedIn || !LiveConnectionManager.IsConnected)
		{
			AddLog("[color=#888888]You must be connected to send messages.[/color]");
			return;
		}
		LiveConnectionManager.SendChatMessage(text);
	}

	// ── Command handling ──────────────────────────────────────────────────────

	/// <summary>
	/// Processes a "/" command. Returns true if the input was a recognised
	/// command (suppresses the normal send), false if unrecognised.
	/// </summary>
	private bool HandleCommand(string input)
	{
		int    spaceIdx = input.IndexOf(' ');
		string cmd      = (spaceIdx < 0 ? input : input.Substring(0, spaceIdx)).ToLower();

		switch (cmd)
		{
			case "/players":
				if (!AuthManager.IsLoggedIn)
				{
					AddLog("[color=#888888]You must be logged in to see connected players.[/color]");
				}
				else
				{
					var players = LiveConnectionManager.LastKnownPlayers;
					if (players.Count == 0)
						AddLog("[color=#888888]No players currently connected.[/color]");
					else
					{
						AddLog($"[color=#c04040]Connected ({players.Count}):[/color]");
						foreach (var p in players)
							AddLog($"[color=#e89060]  {p}[/color]");
					}
				}
				return true;

			case "/tell":
				// TODO: Direct message to a specific player: "/tell <playername> <message>"
				// Requires a new REQ_SEND_TELL packet type and server routing by username.
				AddLog("[color=#888888]/tell is not yet implemented.[/color]");
				return true;

			default:
				AddLog($"[color=#888888]Unknown command: {cmd}[/color]");
				return true; // suppress the send echo for any "/" input
		}
	}

	// ── Static log API ────────────────────────────────────────────────────────

	/// <summary>
	/// Appends <paramref name="message"/> (BBCode supported) to the chat history
	/// from any script without needing a node reference.
	/// </summary>
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

	// ── Layout ────────────────────────────────────────────────────────────────

	/// <summary>
	/// Waits two frames for <see cref="RichTextLabel.GetContentHeight"/> to stabilise,
	/// then resizes the scroll container and scrolls to the bottom.
	/// </summary>
	private async void HandleNewMessage()
	{
		_chatHistory.QueueRedraw();

		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		UpdateLayout();

		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		ScrollToBottom();
	}

	/// <summary>
	/// Clamps the scroll container height to at most 40% of the viewport,
	/// growing naturally with content until that limit is reached.
	/// </summary>
	private void UpdateLayout()
	{
		if (_chatHistory == null || _scrollContainer == null) return;

		float historyHeight = _chatHistory.GetContentHeight();
		if (historyHeight <= 0) return;

		float screenHeight = GetViewportRect().Size.Y;
		float maxHeight    = screenHeight * 0.4f - 20;
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

	// ── Fade ─────────────────────────────────────────────────────────────────

	private void ResetFade()
	{
		if (_fadeTween != null) _fadeTween.Kill();
		Modulate = new Color(1, 1, 1, 1);
		Visible  = true;
	}

	/// <summary>
	/// Starts a 3-second hold followed by a 1-second fade-out.
	/// Cancelled if the chat is opened or a new message arrives.
	/// </summary>
	private void StartFadeTimer()
	{
		if (_fadeTween != null) _fadeTween.Kill();
		if (_isChatOpen) return;

		_fadeTween = CreateTween();
		_fadeTween.TweenInterval(3.0f);
		_fadeTween.TweenProperty(this, "modulate:a", 0.0f, 1.0f);
	}
}
