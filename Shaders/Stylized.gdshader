shader_type canvas_item;

uniform float scanline_count : hint_range(0, 1080) = 20.0;
uniform float scanline_opacity : hint_range(0, 1) = 0.1;
uniform float vignette_intensity : hint_range(0, 1) = 0.2;

void fragment() {
    // In Godot 4 canvas drawing, COLOR contains the result of DrawRect calls
    vec4 color = COLOR;
    
    // Simple scanline effect based on screen UV
    float s = sin(SCREEN_UV.y * scanline_count * 3.14159 * 2.0) * scanline_opacity;
    color.rgb -= s;
    
    // Subtle vignette
    float dist = distance(SCREEN_UV, vec2(0.5, 0.5));
    color.rgb *= (1.0 - dist * vignette_intensity);
    
    COLOR = color;
}
