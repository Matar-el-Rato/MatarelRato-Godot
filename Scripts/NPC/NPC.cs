// ═══════════════════════════════════════════════════
// NPC.cs
// "Pigga" — the macabre pizzeria host NPC.
// Handles appear/disappear VFX, dialog bubble, pointing animation,
// and orchestrates the login/register flow with AuthChoiceUI.
// ═══════════════════════════════════════════════════
using Godot;
using System;

/// <summary>
/// CharacterBody3D for the pizzeria host NPC (Pigga).
/// Manages appearance via scale tween + ember VFX, dialog bubble text,
/// animation state (idle / point), and drives the auth flow when interacted with.
/// </summary>
public partial class NPC : CharacterBody3D
{
	/// <summary>Global singleton — set in _Ready.</summary>
	public static NPC Instance { get; private set; }

	[Export] public string IdleAnimation        = "Armature|mixamo_com|Layer0_007";
	[Export] public string PointAnimation       = "Armature_002|mixamo_com|Layer0_001";
	[Export] public string BoardPointAnimation  = "Armature_001|mixamo.com|Layer0";
	[Export] public float  TransitionDuration = 0.8f;
	[Export] public Color  TransitionColor    = new Color(1.0f, 0.5f, 0.2f);
	[Export] public string DialogText         = "Good to see you again...\nWhat can I get for you today?";
	[Export] public NodePath DialogBubblePath;
	/// <summary>FOV to zoom down to when showing the board.</summary>
	[Export] public float  BoardZoomFov = 10f;

	private AnimationPlayer _animPlayer;
	private Node3D          _model;
	private AudioStreamPlayer3D _burnAudio;
	private DialogBubble    _dialogBubble;
	private Interactable    _interactable;

	private bool _hasAppeared   = false;
	private bool _hasInteracted = false;
	private bool _isLoggedIn    = false;

	// ── Macabre phrase pools ──────────────────────────────────────────────────

	private static readonly string[] LogoutPhrases =
	{
		"Leaving so soon?\nThe house never forgets.",
		"Running away?\nThe ledger stays open...",
		"Until next time, fool.\nWe'll be waiting.",
	};

	private static readonly string[] LoginPhrases =
	{
		"You crawled back...\n[i]The house always wins, {0}.[/i]",
		"Still breathing?\nWelcome back to the table,\n[i]{0}.[/i]",
		"Ah... {0} returns.\nThe game was getting bored\nwithout you.",
		"The ledger remembers you, {0}.\nSit down...\nThe game is just starting.",
	};

	private static readonly string[] RegisterPhrases =
	{
		"Fresh blood...\nThe ink is dry, [i]{0}[/i].\nYou belong to us now.",
		"A new soul signs the ledger.\nWelcome to perdition,\n[i]{0}.[/i]",
		"How delightful...\nAnother one.\nDon't worry, {0}...\nIt won't hurt. [i]Much.[/i]",
		"The pact is sealed, {0}.\nThere is no leaving\nthis table.",
	};

	private static readonly string[] LoginFailPhrases =
	{
		"Wrong credentials, fool.\n[i]The dead don't forget.[/i]",
		"The door is locked.\nSomeone is lying...",
		"Incorrect. Try again.\nThe rats are watching.",
	};

	private static readonly string[] RegisterFailPhrases =
	{
		"That name is... taken.\nSomeone got here before you.",
		"The ledger already has\nthat name. Try another.",
		"Registration denied.\nThe house has\nits reasons.",
	};

	private static readonly string[] BoardPointPhrases =
	{
		"Look behind you.\nYou have other penitent souls\ncurrently in...",
		"Turn around, fool.\nYou are not alone\nin this wretched place.",
		"See that board?\nOthers have already\nsigned the ledger.",
		"You think you're special?\nCheck the board.\nYou're just another name.",
		"The house has other guests.\nThey didn't run.\nSee for yourself.",
	};

	private static readonly string[] AfterBoardPhrases =
	{
		"Did you count them?\nGood.\nNow wonder how many\nwill make it out.",
		"Each name on that board\nis another debt\nowed to the house.",
		"They came.\nThey played.\nMost of them regretted it.\n[i]Most.[/i]",
		"The more the merrier...\nthat's what they [i]all[/i] say,\nbefore the losses pile up.",
		"Don't get attached\nto those names.\nThings change quickly\naround here.",
		"Every soul in here\npaid their price.\nDon't forget yours.",
	};

	private static readonly string[] CancelPhrases =
	{
		"...Thought so.\nCowards always hesitate.",
		"Running away already?\nThe door was right there.",
		"No courage?\nThe ledger can wait...\n[i]for now.[/i]",
		"Hm. Perhaps next time\nyou'll find your spine.",
		"Wise choice.\nFor you.\nNot for us.",
	};

	private static readonly string[] DismissalPhrases =
	{
		"Ring whenever you\nneed to annoy me...",
		"You know where the bell is.\nDon't be shy...",
		"I'll be around.\nJust ring that bell,\npest.",
		"Need me? Ring the bell.\nI dare you.",
	};

	private Random _rng = new Random();

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		Instance = this;

		_model      = GetNodeOrNull<Node3D>("OrientationFix");
		_animPlayer = GetNodeOrNull<AnimationPlayer>("OrientationFix/pigga/AnimationPlayer");
		if (_animPlayer == null) _animPlayer = FindAnimationPlayer(this);
		_burnAudio  = GetNodeOrNull<AudioStreamPlayer3D>("BurnAudio");

		if (_animPlayer != null && _animPlayer.HasAnimation(IdleAnimation))
		{
			// Force loop on the idle clip — it may not be set in the imported asset.
			var anim = _animPlayer.GetAnimation(IdleAnimation);
			anim.LoopMode = Animation.LoopModeEnum.Linear;
			_animPlayer.Play(IdleAnimation);
		}

		// Start hidden and near-zero scale; Appear() tweens to full size.
		Visible = false;
		Scale   = new Vector3(0.001f, 0.001f, 0.001f);

		_dialogBubble = (DialogBubblePath != null && !DialogBubblePath.IsEmpty)
			? GetNodeOrNull<DialogBubble>(DialogBubblePath)
			: GetNodeOrNull<DialogBubble>("DialogBubble");

		_interactable = GetNodeOrNull<Interactable>("Interactable");
		if (_interactable != null)
			_interactable.Interacted += OnInteracted;
	}

	// ── Auth callbacks ────────────────────────────────────────────────────────

	/// <summary>
	/// Called by <see cref="ClipboardUI"/> after a successful login or registration.
	/// Switches the interactable prompt to "Log Out" and marks the session active.
	/// </summary>
	public async void OnAuthComplete(string username)
	{
		_isLoggedIn = true;
		if (_interactable != null)
			_interactable.PromptText = "Log Out";

		// Wait for the auth greeting to be read
		await ToSignal(GetTree().CreateTimer(5.0f), SceneTreeTimer.SignalName.Timeout);

		// ── Point to the connected players board ──────────────────────────────
		ForceShowDialog(BoardPointPhrases[_rng.Next(BoardPointPhrases.Length)]);

		if (_animPlayer != null && _animPlayer.HasAnimation(BoardPointAnimation))
		{
			_animPlayer.Play(BoardPointAnimation, 0.3f);
			await ToSignal(_animPlayer, AnimationPlayer.SignalName.AnimationFinished);
			_animPlayer.Play(IdleAnimation, 0.3f);
		}
		else
		{
			await ToSignal(GetTree().CreateTimer(3.5f), SceneTreeTimer.SignalName.Timeout);
		}

		// Quirky follow-up about the other souls on the board
		ForceShowDialog(AfterBoardPhrases[_rng.Next(AfterBoardPhrases.Length)]);
		await ToSignal(GetTree().CreateTimer(4.0f), SceneTreeTimer.SignalName.Timeout);

		// Quirky dismissal before vanishing
		ForceShowDialog(DismissalPhrases[_rng.Next(DismissalPhrases.Length)]);

		await ToSignal(GetTree().CreateTimer(4.0f), SceneTreeTimer.SignalName.Timeout);

		// Disappear — player must ring the bell again to logout
		Disappear();
		BellController.Instance?.ShowBellExclamation();
		// Disappear resets prompt to "Talk", restore it since we're still logged in
		if (_interactable != null)
			_interactable.PromptText = "Log Out";
	}

	/// <summary>
	/// Called when the player rings the bell while a clipboard is open.
	/// Hides the clipboard, dismisses the NPC with a snarky farewell.
	/// </summary>
	public async void CancelAuth()
	{
		_hasInteracted = false;

		AuthChoiceUI.Instance?.Hide();
		ClipboardController.Instance?.HideClipboards();
		FocusController.Instance?.ExitFocus();

		ForceShowDialog(CancelPhrases[_rng.Next(CancelPhrases.Length)]);

		await ToSignal(GetTree().CreateTimer(3.0f), SceneTreeTimer.SignalName.Timeout);

		Disappear();
		BellController.Instance?.ShowBellExclamation();
	}

	// ── Auth reactions ────────────────────────────────────────────────────────

	/// <summary>
	/// Displays a random macabre greeting in the dialog bubble after auth success.
	/// </summary>
	/// <param name="username">The authenticated player name to embed in the phrase.</param>
	/// <param name="isNewUser">Selects the register pool when true, login pool otherwise.</param>
	public void ReactToAuth(string username, bool isNewUser)
	{
		var pool   = isNewUser ? RegisterPhrases : LoginPhrases;
		string phrase = string.Format(pool[_rng.Next(pool.Length)], username);
		ForceShowDialog(phrase);
	}

	/// <summary>
	/// Displays a random failure phrase after a failed login or registration attempt.
	/// </summary>
	/// <param name="isRegistration">Selects register-fail pool when true.</param>
	public void ReactToAuthFailed(bool isRegistration)
	{
		var pool = isRegistration ? RegisterFailPhrases : LoginFailPhrases;
		ForceShowDialog(pool[_rng.Next(pool.Length)]);
	}

	/// <summary>
	/// Shows the given text in the dialog bubble, interrupting any in-progress typing.
	/// </summary>
	private void ForceShowDialog(string text)
	{
		if (_dialogBubble == null) return;
		_dialogBubble.ShowDialog(text, force: true);
	}

	// ── Appear / Disappear ────────────────────────────────────────────────────

	/// <summary>
	/// Scales the NPC from near-zero to full size with a burn flash + ember burst.
	/// Idempotent — subsequent calls while already visible are ignored.
	/// </summary>
	public void Appear()
	{
		if (_hasAppeared) return;
		_hasAppeared = true;

		// Sync with AuthManager in case login happened outside the NPC flow (e.g. debug mode).
		if (AuthManager.IsLoggedIn && !_isLoggedIn)
		{
			_isLoggedIn = true;
			if (_interactable != null) _interactable.PromptText = "Log Out";
		}

		Visible = true;
		Tween tween = CreateTween();
		tween.TweenProperty(this, "scale", Vector3.One, TransitionDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.InOut);

		AddBurnFlash();
		AddEmbers();
		_burnAudio?.Play();
	}

	/// <summary>
	/// Shrinks the NPC back to near-zero, plays VFX, then hides it.
	/// </summary>
	private void Disappear()
	{
		_hasAppeared   = false;
		_hasInteracted = false;
		if (_interactable != null) _interactable.PromptText = "Talk";

		_dialogBubble?.HideDialog();
		AddBurnFlash();
		AddEmbers();
		_burnAudio?.Play();

		Tween tween = CreateTween();
		tween.TweenProperty(this, "scale", new Vector3(0.001f, 0.001f, 0.001f), TransitionDuration)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.InOut);
		tween.Finished += () => Visible = false;
	}

	// ── Interaction ───────────────────────────────────────────────────────────

	private async void OnInteracted()
	{
		if (!_hasAppeared || _hasInteracted) return;
		_hasInteracted = true;

		// ── Logout flow ───────────────────────────────────────────────────────
		if (_isLoggedIn)
		{
			_isLoggedIn = false;
			string loggedOutUser = AuthManager.Username;
			AuthManager.NotifyLogout();
			ForceShowDialog(LogoutPhrases[_rng.Next(LogoutPhrases.Length)]);
			ChatManager.AddLog($"[color=#888888]{loggedOutUser} has left the fun...[/color]");

			await ToSignal(GetTree().CreateTimer(4.0f), SceneTreeTimer.SignalName.Timeout);
			Disappear();
			BellController.Instance?.ShowBellExclamation();
			return;
		}

		// ── Step 1: greeting ──────────────────────────────────────────────────
		_dialogBubble?.ShowDialog(DialogText);
		await ToSignal(GetTree().CreateTimer(4.0f), SceneTreeTimer.SignalName.Timeout);

		// ── Step 2: show choice panel and play pointing animation ─────────────
		_dialogBubble?.ShowDialog("So what is it then?", force: true);

		AuthChoiceUI.Instance?.Show(
			onLogin: () => {
				ForceShowDialog("Huh...");
				ClipboardController.Instance?.ShowLoginClipboard();
			},
			onRegister: () => {
				ForceShowDialog("Huh...");
				ClipboardController.Instance?.ShowRegistrationClipboard();
			}
		);

		if (_animPlayer != null && _animPlayer.HasAnimation(PointAnimation))
		{
			_animPlayer.Play(PointAnimation, 0.3f);
			await ToSignal(_animPlayer, AnimationPlayer.SignalName.AnimationFinished);
			_animPlayer.Play(IdleAnimation, 0.3f);
		}
	}

	// ── VFX helpers ───────────────────────────────────────────────────────────

	/// <summary>
	/// Spawns a short-lived OmniLight3D that flashes orange on appear/disappear.
	/// </summary>
	private void AddBurnFlash()
	{
		OmniLight3D flash = new OmniLight3D
		{
			TopLevel    = true,
			LightColor  = TransitionColor,
			LightEnergy = 0.0f,
			OmniRange   = 6.0f
		};
		GetParent().AddChild(flash);
		flash.GlobalPosition = GlobalPosition + Vector3.Up * 1.0f;

		Tween t = CreateTween();
		t.TweenProperty(flash, "light_energy", 5.0f, TransitionDuration * 0.15f);
		t.TweenProperty(flash, "light_energy", 0.0f, TransitionDuration * 0.85f);
		t.Finished += () => flash.QueueFree();
	}

	/// <summary>
	/// Spawns a one-shot CPU particle burst that mimics flying embers/sparks.
	/// </summary>
	private void AddEmbers()
	{
		Vector3 spawnPos = GlobalPosition + Vector3.Up * 0.5f;

		CpuParticles3D particles = new CpuParticles3D { TopLevel = true };
		GetParent().AddChild(particles);
		particles.GlobalPosition     = spawnPos;
		particles.Amount             = 120;
		particles.Lifetime           = TransitionDuration * 1.5f;
		particles.OneShot            = true;
		particles.Explosiveness      = 0.85f;
		particles.EmissionShape      = CpuParticles3D.EmissionShapeEnum.Box;
		particles.EmissionBoxExtents = new Vector3(0.5f, 0.8f, 0.5f);
		particles.Direction          = new Vector3(0, 1, 0);
		particles.Spread             = 55.0f;
		particles.Gravity            = new Vector3(0, 2.0f, 0);
		particles.InitialVelocityMin = 1.0f;
		particles.InitialVelocityMax = 2.5f;
		particles.ScaleAmountMin     = 0.8f;
		particles.ScaleAmountMax     = 1.5f;

		// Yellow-white at birth → dark red at death
		Gradient gradient = new Gradient();
		gradient.SetColor(0, new Color(1, 1, 0.5f, 1));
		gradient.AddPoint(0.3f, new Color(1, 0.5f, 0.1f, 0.9f));
		gradient.SetColor(gradient.GetPointCount() - 1, new Color(0.8f, 0.1f, 0, 0));
		particles.ColorRamp = gradient;

		QuadMesh qm = new QuadMesh { Size = new Vector2(0.018f, 0.018f) };
		particles.Mesh = qm;

		StandardMaterial3D mat = new StandardMaterial3D
		{
			ShadingMode            = StandardMaterial3D.ShadingModeEnum.Unshaded,
			VertexColorUseAsAlbedo = true,
			BillboardMode          = StandardMaterial3D.BillboardModeEnum.Enabled,
			Transparency           = StandardMaterial3D.TransparencyEnum.Alpha
		};
		particles.MaterialOverride = mat;
		particles.Emitting = true;

		GetTree().CreateTimer(particles.Lifetime + 0.5f).Timeout += () => particles.QueueFree();
	}

	/// <summary>
	/// Depth-first recursive search for the first AnimationPlayer in a subtree.
	/// Used as a fallback when the node path doesn't resolve directly.
	/// </summary>
	private AnimationPlayer FindAnimationPlayer(Node node)
	{
		if (node is AnimationPlayer ap) return ap;
		foreach (Node child in node.GetChildren())
		{
			var found = FindAnimationPlayer(child);
			if (found != null) return found;
		}
		return null;
	}
}
