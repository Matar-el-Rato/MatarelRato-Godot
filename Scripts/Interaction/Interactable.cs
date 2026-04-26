// ═══════════════════════════════════════════════════
// Interactable.cs
// Reusable Node3D component that makes any prop interactable.
// Auto-generates trimesh collision, manages highlight/outline shaders,
// and emits signals when focused or interacted with.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Drop-in interactable component. Place as a child of any prop Node3D.
/// Emits <see cref="Interacted"/>, <see cref="Focused"/>, and <see cref="Unfocused"/>
/// signals. Optionally auto-generates trimesh collision and applies a highlight
/// shader (screen-space, shell, or overlay) when the player looks at it.
/// </summary>
[GlobalClass]
public partial class Interactable : Node3D, IInteractable
{
	/// <summary>Emitted when the player triggers an interaction (key or click).</summary>
	[Signal] public delegate void InteractedEventHandler();
	/// <summary>Emitted when the player's raycast first enters this interactable.</summary>
	[Signal] public delegate void FocusedEventHandler();
	/// <summary>Emitted when the player's raycast leaves this interactable.</summary>
	[Signal] public delegate void UnfocusedEventHandler();

	// ── Core settings ─────────────────────────────────────────────────────────

	[Export] public string PromptText        = "Interact";
	[Export] public string InteractionAction = "interact";
	/// <summary>When true, the interactable responds to left-click instead of (or in addition to) the key.</summary>
	[Export] public bool   UseLeftClick      = false;

	// ── Visuals ───────────────────────────────────────────────────────────────

	[ExportGroup("Visuals")]
	/// <summary>When true, a floating "!" label is spawned above the interactable.</summary>
	[Export] public bool    ShowExclamation   = false;
	[Export] public Vector3 ExclamationOffset = new Vector3(0, 2.5f, 0);
	[Export] public float   ExclamationScale  = 4.0f;
	[Export] public Font    CustomFont;

	// ── Automation ────────────────────────────────────────────────────────────

	[ExportGroup("Automation")]
	/// <summary>When true, trimesh collision is generated for every MeshInstance3D in the parent hierarchy.</summary>
	/// <summary>Color used to tint the prompt text when this interactable is focused.</summary>
	[Export] public Color    PromptColor                  = Colors.White;
	[Export] public bool     AutoGenerateCollision        = true;
	[Export] public bool     HandleHighlight              = true;
	[Export] public NodePath HighlightTargetMesh;
	[Export] public Color    HighlightColor               = Colors.Yellow;
	[Export] public float    HighlightThickness           = 2.0f;
	[Export] public float    HighlightSmoothingCutoff     = 0.1f;
	[Export] public float    HighlightSmoothingMax        = 0.1f;
	[Export] public float    HighlightTransparencyThreshold = 0.1f;
	[Export] public float    HighlightEdgeSensitivity     = 0.01f;
	[Export] public float    HighlightOcclusionBias       = 0.02f;

	/// <summary>Uses a vertex-expanded shell mesh for the outline (good for convex props).</summary>
	[Export] public bool  UseShellHighlight       = false;
	/// <summary>Uses a full-screen overlay material instead of an outline pass.</summary>
	[Export] public bool  UseOverlayHighlight      = false;
	[Export] public Color OverlayColor             = new Color(1, 1, 0, 0.4f);
	[Export] public float HighlightOverlayInflation = 0.005f;

	private static readonly string SHADER_PATH         = "res://Shaders/highlight.gdshader";
	private static readonly string OVERLAY_SHADER_PATH = "res://Shaders/overlay_highlight.gdshader";
	private static readonly string SHELL_SHADER_PATH   = "res://Shaders/outline_vertex.gdshader";

	private Label3D             _exclamationLabel;
	private Tween               _floatTween;
	private List<MeshInstance3D> _highlightMeshes = new List<MeshInstance3D>();
	private List<MeshInstance3D> _shellMeshes     = new List<MeshInstance3D>();
	private ShaderMaterial      _highlightMaterial;
	private ShaderMaterial      _overlayMaterial;
	private ShaderMaterial      _shellMaterial;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		// Defer so the parent hierarchy is fully in the tree before we traverse it.
		CallDeferred(MethodName.InitializeInteractable);
	}

	private void InitializeInteractable()
	{
		SetupExclamation();

		if (AutoGenerateCollision)
			GenerateCollisions(GetParent());

		if (HandleHighlight)
		{
			SetupHighlightMaterial();
			if (UseShellHighlight)
				SetupShellMeshes();
		}
	}

	// ── Highlight setup ───────────────────────────────────────────────────────

	/// <summary>
	/// Creates per-mesh shell duplicates that are toggled visible/invisible on focus.
	/// The shell shader inflates vertex normals to produce a flat-colour outline.
	/// </summary>
	private void SetupShellMeshes()
	{
		_shellMaterial = new ShaderMaterial();
		_shellMaterial.Shader = GD.Load<Shader>(SHELL_SHADER_PATH);
		_shellMaterial.SetShaderParameter("outline_color", HighlightColor);
		// Map pixel-style thickness to world-space meters (e.g. 4.0 px → 0.008 m).
		_shellMaterial.SetShaderParameter("thickness", HighlightThickness * 0.002f);

		foreach (var mesh in _highlightMeshes)
		{
			var shell = new MeshInstance3D();
			shell.Mesh             = mesh.Mesh;
			shell.MaterialOverride = _shellMaterial;
			shell.CastShadow       = GeometryInstance3D.ShadowCastingSetting.Off;
			shell.Visible          = false;
			shell.Name             = "HighlightShell_" + mesh.Name;

			// Attach to the original mesh so it follows skeleton animations.
			mesh.AddChild(shell);
			_shellMeshes.Add(shell);
		}
	}

	/// <summary>
	/// Loads the highlight (or overlay) shader and collects all target mesh instances
	/// from the parent hierarchy.
	/// </summary>
	private void SetupHighlightMaterial()
	{
		_highlightMaterial = new ShaderMaterial();
		_highlightMaterial.Shader          = GD.Load<Shader>(SHADER_PATH);
		_highlightMaterial.RenderPriority  = 10;
		_highlightMaterial.SetShaderParameter("outline_color",           HighlightColor);
		_highlightMaterial.SetShaderParameter("thickness",               HighlightThickness);
		_highlightMaterial.SetShaderParameter("smoothing_cutoff",        HighlightSmoothingCutoff);
		_highlightMaterial.SetShaderParameter("smoothing_max",           HighlightSmoothingMax);
		_highlightMaterial.SetShaderParameter("transparency_threshold",  HighlightTransparencyThreshold);
		_highlightMaterial.SetShaderParameter("edge_sensitivity",        HighlightEdgeSensitivity);
		_highlightMaterial.SetShaderParameter("occlusion_bias",          HighlightOcclusionBias);

		if (UseOverlayHighlight)
		{
			_overlayMaterial = new ShaderMaterial();
			_overlayMaterial.Shader         = GD.Load<Shader>(OVERLAY_SHADER_PATH);
			_overlayMaterial.RenderPriority = 100;
			_overlayMaterial.SetShaderParameter("overlay_color", OverlayColor);
			_overlayMaterial.SetShaderParameter("inflation",     HighlightOverlayInflation);
		}

		// Populate _highlightMeshes from the explicit target or the whole parent tree.
		_highlightMeshes.Clear();
		if (HighlightTargetMesh != null && !HighlightTargetMesh.IsEmpty)
		{
			var targetNode = GetNodeOrNull<Node>(HighlightTargetMesh);
			if (targetNode is MeshInstance3D mesh)
				_highlightMeshes.Add(mesh);
			else if (targetNode != null)
				FindMeshesRecursive(targetNode); // GLB root: collect all meshes inside
		}
		else
		{
			FindMeshesRecursive(GetParent());
		}
	}

	/// <summary>
	/// Returns the first <see cref="MeshInstance3D"/> found in the subtree (depth-first).
	/// </summary>
	private MeshInstance3D FindMeshRecursive(Node node)
	{
		if (node is MeshInstance3D mesh) return mesh;
		foreach (Node child in node.GetChildren(true))
		{
			var found = FindMeshRecursive(child);
			if (found != null) return found;
		}
		return null;
	}

	/// <summary>
	/// Collects every <see cref="MeshInstance3D"/> in the subtree into <see cref="_highlightMeshes"/>.
	/// </summary>
	private void FindMeshesRecursive(Node node)
	{
		if (node is MeshInstance3D mesh)
			_highlightMeshes.Add(mesh);

		foreach (Node child in node.GetChildren(true))
			FindMeshesRecursive(child);
	}

	// ── Exclamation mark ──────────────────────────────────────────────────────

	private void SetupExclamation()
	{
		if (!ShowExclamation) return;

		_exclamationLabel = new Label3D();
		_exclamationLabel.Text        = "!";
		_exclamationLabel.FontSize    = (int)(32 * ExclamationScale);
		_exclamationLabel.OutlineSize = 12;
		_exclamationLabel.Billboard   = BaseMaterial3D.BillboardModeEnum.Enabled;
		_exclamationLabel.Position    = ExclamationOffset;
		_exclamationLabel.Modulate    = Colors.Yellow;

		if (CustomFont != null)
			_exclamationLabel.Font = CustomFont;

		AddChild(_exclamationLabel);
		AnimateExclamation();
	}

	/// <summary>
	/// Programmatically shows or hides the floating exclamation mark.
	/// </summary>
	public void SetExclamationVisible(bool visible)
	{
		if (_exclamationLabel != null)
			_exclamationLabel.Visible = visible;
	}

	private void AnimateExclamation()
	{
		if (_exclamationLabel == null || !IsInsideTree()) return;

		if (_floatTween != null) _floatTween.Kill();

		Vector3 startPos = ExclamationOffset;
		// Bob height scales with symbol size: default scale 4.0 → 0.3 m travel.
		float bobHeight = 0.075f * ExclamationScale;
		Vector3 endPos  = startPos + new Vector3(0, bobHeight, 0);

		_floatTween = CreateTween();
		_floatTween.TweenProperty(_exclamationLabel, "position", endPos, 1.0f)
			.SetTrans(Tween.TransitionType.Sine)
			.SetEase(Tween.EaseType.InOut);
		_floatTween.TweenProperty(_exclamationLabel, "position", startPos, 1.0f)
			.SetTrans(Tween.TransitionType.Sine)
			.SetEase(Tween.EaseType.InOut);
		_floatTween.SetLoops();
	}

	// ── Collision generation ──────────────────────────────────────────────────

	/// <summary>
	/// Recursively creates trimesh collision siblings for every MeshInstance3D
	/// that doesn't already have a StaticBody3D child.
	/// </summary>
	private void GenerateCollisions(Node node)
	{
		if (node is MeshInstance3D meshInstance)
		{
			bool hasCollision = false;
			foreach (Node child in meshInstance.GetChildren(true))
			{
				if (child is StaticBody3D) { hasCollision = true; break; }
			}

			if (!hasCollision)
				meshInstance.CreateTrimeshCollision();
		}

		foreach (Node child in node.GetChildren(true))
			GenerateCollisions(child);
	}

	// ── IInteractable ─────────────────────────────────────────────────────────

	/// <summary>Changes the highlight color at runtime (e.g., to show a locked state).</summary>
	public void SetHighlightColor(Color color)
	{
		_highlightMaterial?.SetShaderParameter("outline_color", color);
		_shellMaterial?.SetShaderParameter("outline_color", color);
		if (_overlayMaterial != null)
			_overlayMaterial.SetShaderParameter("overlay_color", new Color(color.R, color.G, color.B, OverlayColor.A));
	}

	/// <summary>Fires the <see cref="Interacted"/> signal.</summary>
	public void Interact() => EmitSignal(SignalName.Interacted);

	/// <summary>Fires <see cref="Focused"/> and enables the highlight effect.</summary>
	public void OnFocus()
	{
		EmitSignal(SignalName.Focused);
		ApplyHighlight(true);
	}

	/// <summary>Fires <see cref="Unfocused"/> and disables the highlight effect.</summary>
	public void OnBlur()
	{
		EmitSignal(SignalName.Unfocused);
		ApplyHighlight(false);
	}

	// ── Highlight application ─────────────────────────────────────────────────

	private void ApplyHighlight(bool active)
	{
		if (!HandleHighlight) return;

		if (UseShellHighlight)
		{
			foreach (var shell in _shellMeshes)
			{
				if (IsInstanceValid(shell))
					shell.Visible = active;
			}
			return;
		}

		if (UseOverlayHighlight)
		{
			if (_overlayMaterial == null)
			{
				GD.PrintErr($"[Interactable] Overlay material null for {Name} but UseOverlayHighlight is true!");
				return;
			}
			foreach (var mesh in _highlightMeshes)
			{
				if (IsInstanceValid(mesh))
					mesh.MaterialOverlay = active ? _overlayMaterial : null;
			}
			return;
		}

		// Default: screen-space outline via MaterialOverlay.
		if (_highlightMaterial == null) return;
		foreach (var mesh in _highlightMeshes)
		{
			if (IsInstanceValid(mesh))
				mesh.MaterialOverlay = active ? _highlightMaterial : null;
		}
	}
}
