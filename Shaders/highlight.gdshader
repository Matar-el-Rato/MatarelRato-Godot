shader_type spatial;
render_mode blend_premul_alpha, unshaded, ambient_light_disabled, depth_test_disabled;

uniform sampler2D depth_texture : hint_depth_texture;
uniform vec3 edge_color : source_color = vec3(1.0, 1.0, 0.0);
uniform float thickness : hint_range(0.0, 10.0) = 2.0;
uniform float discard_threshold : hint_range(0.01, 1.0) = 0.1;
uniform float opacity : hint_range(0.0, 1.0) = 1.0;

void fragment() {
	// 1. Get current mesh depth (z0)
	float d0 = FRAGCOORD.z;
	vec4 up0 = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, d0, 1.0);
	float z0 = -(up0.xyz / up0.w).z;

	// 2. Get opaque scene depth at this pixel (zs)
	float ds = texture(depth_texture, SCREEN_UV).r;
	vec4 ups = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, ds, 1.0);
	float zs = -(ups.xyz / ups.w).z;

	// 3. Occlusion Check: If something major is in front of the mesh, don't draw highlight
	if (zs < z0 - 0.05) {
		discard;
	}

	// 4. Edge Detection (Silhouette based)
	float edge = 0.0;
	vec2 pixel_size = 1.0 / VIEWPORT_SIZE;
	
	// Sample neighbors in a circle
	for (float a = 0.0; a < TAU; a += TAU / 8.0) {
		vec2 offset = vec2(cos(a), sin(a)) * thickness * pixel_size;
		float dn = texture(depth_texture, SCREEN_UV + offset).r;
		vec4 upn = INV_PROJECTION_MATRIX * vec4((SCREEN_UV + offset) * 2.0 - 1.0, dn, 1.0);
		float zn = -(upn.xyz / upn.w).z;
		
		// If neighbor is significantly deeper than our surface, we are at a silhouette edge
		if (zn > z0 + 0.1) {
			edge = 1.0;
			break;
		}
		
		// Internal depth discontinuity (useful for table edges)
		if (abs(zn - z0) > 0.05 && abs(zn - z0) < 1.0) {
			edge = max(edge, 0.5);
		}
	}

	ALBEDO = edge_color;
	ALPHA = edge * opacity;
}
