shader_type spatial;
render_mode unshaded, blend_mix, depth_test_disabled;

uniform vec4 overlay_color : source_color = vec4(1.0, 1.0, 0.0, 0.4);

void fragment() {
    ALBEDO = overlay_color.rgb;
    ALPHA = overlay_color.a;
}
