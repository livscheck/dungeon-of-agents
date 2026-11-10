shader_type canvas_item;

// Screen post: ordered dither, colour quantization, vignette.
uniform sampler2D screen_tex : hint_screen_texture, filter_nearest;
uniform float levels = 255.0;
uniform float dither_strength = 0.012;
uniform float vignette = 0.65;
uniform float pixel_size = 1.0;

float bayer4(vec2 p) {
	ivec2 i = ivec2(mod(p, 4.0));
	int idx = i.x + i.y * 4;
	float m[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
	return m[idx] / 16.0 - 0.5;
}

void fragment() {
	vec3 c = texture(screen_tex, SCREEN_UV).rgb;
	c += bayer4(floor(FRAGCOORD.xy / pixel_size)) * dither_strength;
	c = floor(c * levels + 0.5) / levels;
	// Pull the colours toward grey: a cold, grey stone dungeon.
	float luma = dot(c, vec3(0.299, 0.587, 0.114));
	c = mix(vec3(luma), c, 0.6);
	vec2 d = UV - 0.5;
	float v = smoothstep(0.95, 0.2, length(d) * 1.5);
	c *= mix(1.0 - vignette, 1.0, v);
	COLOR = vec4(c, 1.0);
}
