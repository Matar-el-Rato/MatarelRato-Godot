// Glow variant of outline_vertex.gdshader.
// Same screen-space silhouette expansion technique but with alpha blending
// and a separate glow_alpha uniform so C# can drive the pulse from _Process.
shader_type spatial;
render_mode unshaded, cull_front, blend_mix, depth_draw_never, shadows_disabled;

uniform vec4  outline_color : source_color = vec4(1.0, 0.1, 0.04, 1.0);
uniform float thickness     : hint_range(0.001, 0.2) = 0.04;
uniform float glow_alpha    : hint_range(0.0, 1.0)   = 0.0;

void vertex() {
	vec4 clip_pos    = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
	vec4 clip_normal = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX + NORMAL, 1.0));
	vec2 screen_normal = normalize(
		clip_normal.xy / clip_normal.w - clip_pos.xy / clip_pos.w
	);
	// No * clip_pos.w: offset stays in NDC, so the glow scales with the
	// cup's apparent screen size instead of blowing up at long distances.
	clip_pos.xy += screen_normal * thickness;
	POSITION = clip_pos;
}

void fragment() {
	ALBEDO = outline_color.rgb;
	ALPHA  = outline_color.a * glow_alpha;
}
