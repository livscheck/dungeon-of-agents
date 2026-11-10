shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix;

// Pulsing red summoning circle drawn on the floor under an agent that is waiting on the user.
uniform vec4 color : source_color = vec4(0.85, 0.05, 0.06, 1.0);

float seg(vec2 p, vec2 a, vec2 b) {
	vec2 pa = p - a;
	vec2 ba = b - a;
	float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
	return length(pa - ba * h);
}

void fragment() {
	vec2 uv = UV * 2.0 - 1.0;
	float r = length(uv);
	float a = 0.0;
	a += smoothstep(0.035, 0.0, abs(r - 0.93));
	a += smoothstep(0.03, 0.0, abs(r - 0.80)) * 0.8;
	for (int k = 0; k < 5; k++) {
		float a0 = 1.5708 + float(k) * 1.25664;
		float a1 = 1.5708 + float((k + 2) % 5) * 1.25664;
		a += smoothstep(0.025, 0.0, seg(uv, 0.8 * vec2(cos(a0), sin(a0)), 0.8 * vec2(cos(a1), sin(a1))));
	}
	// tick marks around the outer ring
	float ang = atan(uv.y, uv.x);
	float ticks = step(0.8, fract(ang * 3.8197)) * smoothstep(0.05, 0.0, abs(r - 0.87));
	a += ticks * 0.7;
	float beat = 0.5 + 0.5 * sin(TIME * 1.2);
	ALBEDO = color.rgb * (1.0 + 0.25 * beat);
	ALPHA = clamp(a, 0.0, 1.0) * (0.72 + 0.28 * beat) * smoothstep(1.0, 0.9, r);
}
