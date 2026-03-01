using Godot;
using System;
using System.Collections.Generic;

[GlobalClass]
public partial class Interactable : Node3D, IInteractable
{
	[Signal] public delegate void InteractedEventHandler();
	[Signal] public delegate void FocusedEventHandler();
	[Signal] public delegate void UnfocusedEventHandler();

	[Export] public string PromptText = "Interact";
	[Export] public string InteractionAction = "interact";
	[Export] public bool UseLeftClick = false;
	
	[ExportGroup("Visuals")]
	[Export] public bool ShowExclamation = false;
	[Export] public Vector3 ExclamationOffset = new Vector3(0, 2.5f, 0);
	[Export] public float ExclamationScale = 4.0f;
	[Export] public Font CustomFont;
	
	[ExportGroup("Automation")]
	[Export] public bool AutoGenerateCollision = true;
	[Export] public bool HandleHighlight = true;
	[Export] public NodePath HighlightTargetMesh;
	[Export] public Color HighlightColor = Colors.Yellow;
	[Export] public float HighlightThickness = 2.0f;
	[Export] public float HighlightSmoothingCutoff = 0.1f;
	[Export] public float HighlightSmoothingMax = 0.1f;
	[Export] public float HighlightTransparencyThreshold = 0.1f;
	[Export] public float HighlightEdgeSensitivity = 0.01f;
	[Export] public float HighlightOcclusionBias = 0.02f;

	[Export] public bool UseShellHighlight = false;
	[Export] public bool UseOverlayHighlight = false;
	[Export] public Color OverlayColor = new Color(1, 1, 0, 0.4f);
	[Export] public float HighlightOverlayInflation = 0.005f;
	private ShaderMaterial _shellMaterial;
	private static readonly string SHELL_SHADER_PATH = "res://Shaders/outline_vertex.gdshader";

	private Label3D _exclamationLabel;
	private Tween _floatTween;
	private List<MeshInstance3D> _highlightMeshes = new List<MeshInstance3D>();
	private List<MeshInstance3D> _shellMeshes = new List<MeshInstance3D>();
	private ShaderMaterial _highlightMaterial;
	private ShaderMaterial _overlayMaterial;
	private static readonly string SHADER_PATH = "res://Shaders/highlight.gdshader";
	private static readonly string OVERLAY_SHADER_PATH = "res://Shaders/overlay_highlight.gdshader";

	public override void _Ready()
	{
		CallDeferred(MethodName.InitializeInteractable);
	}

	private void InitializeInteractable()
	{
		SetupExclamation();
		
		if (AutoGenerateCollision)
		{
			GenerateCollisions(GetParent());
		}

		if (HandleHighlight)
		{
			SetupHighlightMaterial();
			if (UseShellHighlight)
			{
				SetupShellMeshes();
			}
		}
	}

	private void SetupShellMeshes()
	{
		_shellMaterial = new ShaderMaterial();
		_shellMaterial.Shader = GD.Load<Shader>(SHELL_SHADER_PATH);
		_shellMaterial.SetShaderParameter("outline_color", HighlightColor);
		
		// Map pixel-style thickness to meters (e.g. 4.0 -> 0.008m)
		_shellMaterial.SetShaderParameter("thickness", HighlightThickness * 0.002f);

		foreach (var mesh in _highlightMeshes)
		{
			var shell = new MeshInstance3D();
			shell.Mesh = mesh.Mesh;
			shell.MaterialOverride = _shellMaterial;
			shell.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
			shell.Visible = false;
			shell.Name = "HighlightShell_" + mesh.Name;
			
			// Attach to the mesh so it follows transforms/skeletons
			mesh.AddChild(shell);
			_shellMeshes.Add(shell);
		}
	}

	private void SetupHighlightMaterial()
	{
		_highlightMaterial = new ShaderMaterial();
		_highlightMaterial.Shader = GD.Load<Shader>(SHADER_PATH);
		_highlightMaterial.RenderPriority = 10;
		_highlightMaterial.SetShaderParameter("outline_color", HighlightColor);
		_highlightMaterial.SetShaderParameter("thickness", HighlightThickness);
		_highlightMaterial.SetShaderParameter("smoothing_cutoff", HighlightSmoothingCutoff);
		_highlightMaterial.SetShaderParameter("smoothing_max", HighlightSmoothingMax);
		_highlightMaterial.SetShaderParameter("transparency_threshold", HighlightTransparencyThreshold);
		_highlightMaterial.SetShaderParameter("edge_sensitivity", HighlightEdgeSensitivity);
		_highlightMaterial.SetShaderParameter("occlusion_bias", HighlightOcclusionBias);

		if (UseOverlayHighlight)
		{
			_overlayMaterial = new ShaderMaterial();
			_overlayMaterial.Shader = GD.Load<Shader>(OVERLAY_SHADER_PATH);
			_overlayMaterial.SetShaderParameter("overlay_color", OverlayColor);
			_overlayMaterial.SetShaderParameter("inflation", HighlightOverlayInflation);
			_overlayMaterial.RenderPriority = 100;
			GD.Print($"[Interactable] Initialized overlay highlight for {Name}. Priority: 100");
		}

		// Cache meshes
		_highlightMeshes.Clear();
		if (HighlightTargetMesh != null && !HighlightTargetMesh.IsEmpty)
		{
			var targetNode = GetNodeOrNull<Node>(HighlightTargetMesh);
			if (targetNode is MeshInstance3D mesh)
			{
				_highlightMeshes.Add(mesh);
			}
			else if (targetNode != null)
			{
				// If it's a container (like a GLB root), find all meshes inside
				FindMeshesRecursive(targetNode);
			}
		}
		else
		{
			FindMeshesRecursive(GetParent());
		}
	}

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

	private void FindMeshesRecursive(Node node)
	{
		if (node is MeshInstance3D mesh)
		{
			_highlightMeshes.Add(mesh);
			GD.Print($"[Interactable] Cached mesh: {mesh.Name} for {Name}");
		}
		
		foreach (Node child in node.GetChildren(true))
		{
			FindMeshesRecursive(child);
		}
	}

	private void SetupExclamation()
	{
		if (!ShowExclamation) return;

		_exclamationLabel = new Label3D();
		_exclamationLabel.Text = "!";
		_exclamationLabel.FontSize = (int)(32 * ExclamationScale);
		_exclamationLabel.OutlineSize = 12;
		_exclamationLabel.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
		_exclamationLabel.Position = ExclamationOffset;
		_exclamationLabel.Modulate = Colors.Yellow;
		
		if (CustomFont != null)
		{
			_exclamationLabel.Font = CustomFont;
		}
		
		AddChild(_exclamationLabel);
		AnimateExclamation();
	}

	private void AnimateExclamation()
	{
		if (_exclamationLabel == null || !IsInsideTree()) return;

		if (_floatTween != null)
		{
			_floatTween.Kill();
		}

		Vector3 startPos = ExclamationOffset;
		Vector3 endPos = startPos + new Vector3(0, 0.3f, 0);

		_floatTween = CreateTween();
		_floatTween.TweenProperty(_exclamationLabel, "position", endPos, 1.0f)
			.SetTrans(Tween.TransitionType.Sine)
			.SetEase(Tween.EaseType.InOut);
		_floatTween.TweenProperty(_exclamationLabel, "position", startPos, 1.0f)
			.SetTrans(Tween.TransitionType.Sine)
			.SetEase(Tween.EaseType.InOut);
		_floatTween.SetLoops();
	}

	private void GenerateCollisions(Node node)
	{
		if (node is MeshInstance3D meshInstance)
		{
			// Check if it already has a StaticBody3D child
			bool hasCollision = false;
			foreach (Node child in meshInstance.GetChildren(true))
			{
				if (child is StaticBody3D) { hasCollision = true; break; }
			}

			if (!hasCollision)
			{
				meshInstance.CreateTrimeshCollision();
			}
		}

		foreach (Node child in node.GetChildren(true))
		{
			GenerateCollisions(child);
		}
	}

	public void Interact()
	{
		EmitSignal(SignalName.Interacted);
	}

	public void OnFocus()
	{
		EmitSignal(SignalName.Focused);
		ApplyHighlight(true);
	}

	public void OnBlur()
	{
		EmitSignal(SignalName.Unfocused);
		ApplyHighlight(false);
	}

	private void ApplyHighlight(bool active)
	{
		if (!HandleHighlight) return;

		// Use Shell highlight for specific objects (like the clipboard)
		if (UseShellHighlight)
		{
			foreach (var shell in _shellMeshes)
			{
				if (IsInstanceValid(shell))
				{
					shell.Visible = active;
				}
			}
			return;
		}

		// Use Overlay highlight if enabled
		if (UseOverlayHighlight)
		{
			if (_overlayMaterial == null)
			{
				GD.PrintErr($"[Interactable] Overlay material null for {Name} but UseOverlayHighlight is true!");
				return;
			}
			
			GD.Print($"[Interactable] Applying overlay highlight ({active}) to {_highlightMeshes.Count} meshes on {Name}");
			foreach (var mesh in _highlightMeshes)
			{
				if (IsInstanceValid(mesh))
				{
					mesh.MaterialOverlay = active ? _overlayMaterial : null;
					GD.Print($"[Interactable] Set MaterialOverlay on {mesh.Name} to {(active ? "material" : "null")}");
				}
			}
			return;
		}

		// Fallback to Screen-Space highlight for solid objects
		if (_highlightMaterial == null) return;
		foreach (var mesh in _highlightMeshes)
		{
			if (IsInstanceValid(mesh))
			{
				mesh.MaterialOverlay = active ? _highlightMaterial : null;
			}
		}
	}
}
