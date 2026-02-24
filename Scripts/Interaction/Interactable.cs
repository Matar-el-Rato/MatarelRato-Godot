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
	[Export] public float HighlightThreshold = 0.1f;
	[Export] public float HighlightOpacity = 0.5f;

	private Label3D _exclamationLabel;
	private Tween _floatTween;
	private List<MeshInstance3D> _highlightMeshes = new List<MeshInstance3D>();
	private ShaderMaterial _highlightMaterial;
	private static readonly string SHADER_PATH = "res://Shaders/highlight.gdshader";

	public override void _Ready()
	{
		SetupExclamation();
		
		if (AutoGenerateCollision)
		{
			GenerateCollisions(GetParent());
		}

		if (HandleHighlight)
		{
			SetupHighlightMaterial();
		}
	}

	private void SetupHighlightMaterial()
	{
		_highlightMaterial = new ShaderMaterial();
		_highlightMaterial.Shader = GD.Load<Shader>(SHADER_PATH);
		_highlightMaterial.RenderPriority = 10;
		_highlightMaterial.SetShaderParameter("edge_color", HighlightColor);
		_highlightMaterial.SetShaderParameter("thickness", HighlightThickness);
		_highlightMaterial.SetShaderParameter("discard_threshold", HighlightThreshold);
		_highlightMaterial.SetShaderParameter("opacity", HighlightOpacity);

		// Cache meshes
		_highlightMeshes.Clear();
		if (HighlightTargetMesh != null && !HighlightTargetMesh.IsEmpty)
		{
			var mesh = GetNodeOrNull<MeshInstance3D>(HighlightTargetMesh);
			if (mesh != null) _highlightMeshes.Add(mesh);
		}
		else
		{
			FindMeshesRecursive(GetParent());
		}
	}

	private void FindMeshesRecursive(Node node)
	{
		if (node is MeshInstance3D mesh)
		{
			_highlightMeshes.Add(mesh);
		}
		foreach (Node child in node.GetChildren())
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

		Vector3 startPos = ExclamationOffset;
		Vector3 endPos = startPos + new Vector3(0, 0.3f, 0);

		_floatTween = GetTree().CreateTween();
		_floatTween.SetLoops();
		_floatTween.TweenProperty(_exclamationLabel, "position", endPos, 1.0f)
			.SetTrans(Tween.TransitionType.Sine)
			.SetEase(Tween.EaseType.InOut);
		_floatTween.TweenProperty(_exclamationLabel, "position", startPos, 1.0f)
			.SetTrans(Tween.TransitionType.Sine)
			.SetEase(Tween.EaseType.InOut);
	}

	private void GenerateCollisions(Node node)
	{
		if (node is MeshInstance3D meshInstance)
		{
			meshInstance.CreateTrimeshCollision();
		}

		foreach (Node child in node.GetChildren())
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
		if (!HandleHighlight || _highlightMaterial == null) return;

		foreach (var mesh in _highlightMeshes)
		{
			if (IsInstanceValid(mesh))
			{
				mesh.MaterialOverlay = active ? _highlightMaterial : null;
			}
		}
	}
}
