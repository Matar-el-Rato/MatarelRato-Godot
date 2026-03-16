// ═══════════════════════════════════════════════════
// ClipboardUI.cs
// 2D Control rendered inside a SubViewport on a 3D clipboard prop.
// Handles username/password input, calls ServerProtocol on a background
// thread, and notifies AuthManager + NPC on completion.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

/// <summary>
/// UI Control embedded in a clipboard's SubViewport.
/// Handles form input, fires off async network requests via <see cref="ServerProtocol"/>,
/// and routes results back to the main thread through a concurrent queue.
/// </summary>
public partial class ClipboardUI : Control
{
	[Export] public LineEdit            UsernameInput;
	[Export] public LineEdit            PasswordInput;
	[Export] public Button              SignButton;
	[Export] public AudioStreamPlayer   ScribbleAudio;

	/// <summary>
	/// Set by <see cref="ClipboardController"/> after instantiation.
	/// True = Registration clipboard, False = Sign-In clipboard.
	/// </summary>
	public bool IsRegistration = false;

	// Thread-safe queue drained on the main thread in _Process.
	// Guarantees all Godot API calls happen on the main thread after Task.Run.
	private readonly ConcurrentQueue<Action> _mainThreadQueue = new();

	private Random      _random          = new Random();
	private OmniLight3D _ambientFireLight;
	private Tween       _firePulseTween;

	// Cached styles for the hover-glow effect on LineEdit fields.
	private StyleBoxFlat _normalStyle;
	private StyleBoxFlat _hoverStyle;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		UsernameInput ??= GetNode<LineEdit>("Field1/LineEdit");
		PasswordInput ??= GetNode<LineEdit>("Field2/LineEdit");
		SignButton    ??= GetNode<Button>("SignButton");
		ScribbleAudio ??= GetNode<AudioStreamPlayer>("ScribbleAudio");

		if (SignButton != null)
			SignButton.Pressed += () => _ = OnSignButtonPressed();

		if (UsernameInput != null)
		{
			UsernameInput.TextChanged += _ => PlayScribble();
			SetupHover(UsernameInput);
		}
		if (PasswordInput != null)
		{
			PasswordInput.TextChanged += _ => PlayScribble();
			SetupHover(PasswordInput);
		}
	}

	/// <summary>
	/// Drains the main-thread action queue, executing any callbacks posted from
	/// background Task.Run calls (network results, UI updates).
	/// </summary>
	public override void _Process(double delta)
	{
		while (_mainThreadQueue.TryDequeue(out var action))
			action();
	}

	// ── Hover style ───────────────────────────────────────────────────────────

	/// <summary>
	/// Wires mouse-enter / mouse-exit to a subtle warm highlight on a LineEdit field.
	/// The hover style is created once and shared between both fields.
	/// </summary>
	private void SetupHover(LineEdit field)
	{
		if (_normalStyle == null)
			_normalStyle = field.GetThemeStylebox("normal") as StyleBoxFlat;

		if (_hoverStyle == null)
		{
			_hoverStyle = _normalStyle?.Duplicate() as StyleBoxFlat;
			if (_hoverStyle != null)
			{
				_hoverStyle.BgColor     = new Color(1f, 0.95f, 0.9f, 0.08f);
				_hoverStyle.BorderColor = new Color(0.6f, 0.2f, 0.1f, 0.35f);
			}
		}

		field.MouseEntered += () =>
		{
			if (_hoverStyle != null && !field.HasFocus())
				field.AddThemeStyleboxOverride("normal", _hoverStyle);
		};
		field.MouseExited += () =>
		{
			if (_normalStyle != null)
				field.AddThemeStyleboxOverride("normal", _normalStyle);
		};
	}

	// ── Public API ────────────────────────────────────────────────────────────

	/// <summary>
	/// Clears both input fields and re-enables the sign button.
	/// Called by <see cref="ClipboardController"/> before showing the clipboard.
	/// </summary>
	public void Reset()
	{
		if (UsernameInput != null) UsernameInput.Text = "";
		if (PasswordInput != null) PasswordInput.Text = "";
		if (SignButton    != null) SignButton.Disabled = false;
	}

	// ── Ambient fire light ────────────────────────────────────────────────────

	/// <summary>
	/// Spawns a flickering OmniLight3D near the clipboard to simulate a hellfire glow.
	/// Idempotent — safe to call multiple times.
	/// </summary>
	public void StartAmbientFire()
	{
		if (_ambientFireLight != null) return;

		// Walk up the hierarchy to find the Node3D that owns this Control's SubViewport.
		Node3D clipboardRoot = null;
		Node current = this;
		while (current != null)
		{
			if (current is Node3D n3d && current.GetParent() is not SubViewport)
			{
				clipboardRoot = n3d;
				break;
			}
			current = current.GetParent();
		}
		if (clipboardRoot == null) return;

		_ambientFireLight = new OmniLight3D
		{
			LightColor    = new Color(1.0f, 0.4f, 0.1f),
			LightEnergy   = 0.0f,
			OmniRange     = 2.0f,
			ShadowEnabled = false
		};
		clipboardRoot.GetParent().AddChild(_ambientFireLight);
		_ambientFireLight.GlobalPosition = clipboardRoot.GlobalPosition + new Vector3(0, 0.1f, 0);

		Tween fadeIn = CreateTween();
		fadeIn.TweenProperty(_ambientFireLight, "light_energy", 0.25f, 0.4f);
		fadeIn.Finished += StartFirePulse;
	}

	/// <summary>
	/// Starts an infinite looping energy-pulse tween that mimics a dancing flame.
	/// </summary>
	private void StartFirePulse()
	{
		if (_ambientFireLight == null || !IsInstanceValid(_ambientFireLight)) return;
		_firePulseTween = CreateTween().SetLoops();
		_firePulseTween.TweenProperty(_ambientFireLight, "light_energy", 0.35f, 0.30f).SetTrans(Tween.TransitionType.Sine);
		_firePulseTween.TweenProperty(_ambientFireLight, "light_energy", 0.15f, 0.20f).SetTrans(Tween.TransitionType.Sine);
		_firePulseTween.TweenProperty(_ambientFireLight, "light_energy", 0.28f, 0.15f).SetTrans(Tween.TransitionType.Sine);
		_firePulseTween.TweenProperty(_ambientFireLight, "light_energy", 0.18f, 0.35f).SetTrans(Tween.TransitionType.Sine);
	}

	/// <summary>
	/// Fades the ambient fire light to zero and then frees it.
	/// </summary>
	public void StopAmbientFire()
	{
		_firePulseTween?.Kill();
		_firePulseTween = null;

		if (_ambientFireLight != null && IsInstanceValid(_ambientFireLight))
		{
			Tween fadeOut = CreateTween();
			var light = _ambientFireLight;
			_ambientFireLight = null; // clear ref before async fade completes
			fadeOut.TweenProperty(light, "light_energy", 0.0f, 0.3f);
			fadeOut.Finished += () => { if (IsInstanceValid(light)) light.QueueFree(); };
		}
	}

	// ── Sign button ───────────────────────────────────────────────────────────

	/// <summary>
	/// Validates input, then dispatches a network request on a background thread.
	/// Results are enqueued and processed on the main thread via <see cref="_Process"/>.
	/// </summary>
	private async Task OnSignButtonPressed()
	{
		string user = UsernameInput?.Text?.Trim() ?? "";
		string pass = PasswordInput?.Text?.Trim() ?? "";

		PlayScribble();

		if (user.Length == 0 || pass.Length == 0)
		{
			AuthManager.NotifyFailure(IsRegistration, "Fill in both fields, fool.");
			return;
		}
		if (user.Length > 12 || pass.Length > 12)
		{
			AuthManager.NotifyFailure(IsRegistration, "12 characters max. The ledger has limits.");
			return;
		}
		foreach (char c in user)
		{
			if (c < 32 || c > 126)
			{
				AuthManager.NotifyFailure(IsRegistration, "No weird characters in the username, fool.");
				return;
			}
		}

		if (SignButton != null) SignButton.Disabled = true;

		bool isReg = IsRegistration;

		if (isReg)
		{
			// Registration: register first, then auto-login to get the user ID.
			var result = await Task.Run(() =>
				ServerProtocol.RegisterUser(ServerProtocol.DefaultHost, ServerProtocol.DefaultPort, user, pass));

			if (!result.IsSuccess)
			{
				string msg = result.Message;
				_mainThreadQueue.Enqueue(() => {
					if (SignButton != null) SignButton.Disabled = false;
					NPC.Instance?.ReactToAuthFailed(true);
					ChatManager.AddLog($"[color=#888888]► Registration failed: {msg}[/color]");
				});
				return;
			}

			var loginResult = await Task.Run(() =>
				ServerProtocol.LoginUser(ServerProtocol.DefaultHost, ServerProtocol.DefaultPort, user, pass));

			int userId = loginResult.IsSuccess ? loginResult.UserId : -1;
			_mainThreadQueue.Enqueue(() => OnAuthSuccess(user, userId, true));
		}
		else
		{
			var result = await Task.Run(() =>
				ServerProtocol.LoginUser(ServerProtocol.DefaultHost, ServerProtocol.DefaultPort, user, pass));

			if (!result.IsSuccess)
			{
				string msg = result.Message;
				_mainThreadQueue.Enqueue(() => {
					if (SignButton != null) SignButton.Disabled = false;
					NPC.Instance?.ReactToAuthFailed(false);
					ChatManager.AddLog($"[color=#888888]► Login failed: {msg}[/color]");
				});
				return;
			}

			int userId = result.UserId;
			_mainThreadQueue.Enqueue(() => OnAuthSuccess(user, userId, false));
		}
	}

	/// <summary>
	/// Runs on the main thread after a successful auth round-trip.
	/// Hides clipboards, exits focus, notifies AuthManager, and updates the NPC + chat.
	/// </summary>
	private void OnAuthSuccess(string username, int userId, bool isNewUser)
	{
		StopAmbientFire();
		ClipboardController.Instance?.HideClipboards();
		FocusController.Instance?.ExitFocus();

		AuthManager.NotifySuccess(username, userId, isNewUser);

		NPC.Instance?.ReactToAuth(username, isNewUser);
		NPC.Instance?.OnAuthComplete(username);

		if (isNewUser)
			ChatManager.AddLog($"[color=#c04040]{username} has joined the game for the first time, wont be his last...[/color]");
		else
			ChatManager.AddLog($"[color=#c04040]{username} has returned for more...[/color]");
	}

	// ── Audio ─────────────────────────────────────────────────────────────────

	/// <summary>
	/// Plays the scribble sound with randomised volume and pitch to simulate handwriting.
	/// </summary>
	private void PlayScribble()
	{
		if (ScribbleAudio == null) return;
		ScribbleAudio.VolumeDb   = (float)(_random.NextDouble() * -10.0);
		ScribbleAudio.PitchScale = (float)(0.9 + _random.NextDouble() * 0.2);
		if (!ScribbleAudio.Playing) ScribbleAudio.Play();
	}
}
