shader_type spatial;
render_mode cull_front, depth_prepass_alpha, depth_draw_opaque, unshaded;

uniform vec4 outline_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform float thickness : hint_range(0, 10) = 2.0;
uniform float smoothing_cutoff : hint_range(0, 1) = 0.1;
uniform float smoothing_max : hint_range(0, 1) = 0.1;
uniform float transparency_threshold : hint_range(0, 1) = 0.1;
uniform float edge_sensitivity : hint_range(0, 1) = 0.05;
uniform float occlusion_bias : hint_range(0, 1) = 0.005;

uniform sampler2D depth_tex : hint_depth_texture;
uniform sampler2D screen_tex : hint_screen_texture;

void vertex() {
}

float linear_depth(sampler2D depth_texture, vec2 uv, mat4 inv_projection_matrix) {
	float depth = texture(depth_texture, uv).r;
	vec4 up = inv_projection_matrix * vec4(uv * 2.0 - 1.0, depth, 1.0);
	return -(up.xyz / up.w).z;
}

float get_diff(float depth_center, float depth_neighbor, float sensitivity){
	// Calculate a threshold based on depth to ignore slopes.
	// As depth increases, natural perspective jumps over pixels get larger.
	float threshold = depth_center * sensitivity;
	float diff = depth_neighbor - depth_center;
	
	// Only return value if it exceeds the slope threshold (magnitude check)
	return max(0.0, diff - threshold);
}

void fragment() {
	// 1. Reconstruct current surface depth
	float d_frag = FRAGCOORD.z;
	vec4 up_frag = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, d_frag, 1.0);
	float z0 = -(up_frag.xyz / up_frag.w).z;

	// 2. Opaque scene depth at center
	float d = linear_depth(depth_tex, SCREEN_UV, INV_PROJECTION_MATRIX);

	// 3. Occlusion/Transparency Mask:
	if (d < z0 - occlusion_bias) {
		discard;
	}
	
	if (abs(d - z0) > transparency_threshold) {
		discard;
	}

	ALBEDO = outline_color.rgb;
	
	// 4. Calculate pixel size for sampling (consistent screen-space)
	vec2 screen_size = vec2(textureSize(screen_tex, 0));
	vec2 pixel_size = thickness / screen_size;

	float du = linear_depth(depth_tex, SCREEN_UV + vec2(0.0, pixel_size.y), INV_PROJECTION_MATRIX);
	float dd = linear_depth(depth_tex, SCREEN_UV + vec2(0.0, -pixel_size.y), INV_PROJECTION_MATRIX);
	float dr = linear_depth(depth_tex, SCREEN_UV + vec2(pixel_size.x, 0.0), INV_PROJECTION_MATRIX);
	float dl = linear_depth(depth_tex, SCREEN_UV + vec2(-pixel_size.x, 0.0), INV_PROJECTION_MATRIX);

	// 5. Edge mask: Only jump to background, ignoring natural slopes
	ALPHA = 
		get_diff(d, du, edge_sensitivity) +
		get_diff(d, dl, edge_sensitivity) +
		get_diff(d, dd, edge_sensitivity) +
		get_diff(d, dr, edge_sensitivity) 
	;
	
	ALPHA = smoothstep(smoothing_cutoff, max(smoothing_cutoff, smoothing_max), ALPHA);
	ALPHA *= outline_color.a;
	ALPHA = clamp(ALPHA, 0.0, 1.0);
}
