shader_type spatial;
render_mode cull_back, specular_disabled, depth_draw_opaque;

// Dungeon surfaces: mode 0 = stone blocks (walls), mode 1 = cobblestones (floor, projected from above).
uniform vec4 tint : source_color = vec4(0.5, 0.48, 0.45, 1.0);
uniform int mode = 0;
uniform float grime = 0.7;
uniform bool use_bricks = true;
uniform float emission_energy = 0.0;
uniform vec4 emission_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);

varying vec3 wpos;
varying vec3 wnorm;

float hash(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

vec2 hash22(vec2 p) {
	vec3 q = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
	q += dot(q, q.yzx + 33.33);
	return fract((q.xx + q.yz) * q.zy);
}

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	wnorm = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	vec3 p = wpos;
	vec3 col = tint.rgb;
	float speck = hash(floor(p * 9.0));

	if (mode == 1) {
		// Voronoi cobbles on the XZ plane.
		vec2 q = p.xz * 1.8;
		vec2 ip = floor(q);
		vec2 fp = fract(q);
		float f1 = 8.0;
		float f2 = 8.0;
		vec2 id = vec2(0.0);
		for (int j = -1; j <= 1; j++) {
			for (int i = -1; i <= 1; i++) {
				vec2 g = vec2(float(i), float(j));
				vec2 o = hash22(ip + g);
				vec2 r = g + 0.15 + 0.7 * o - fp;
				float d = length(r);
				if (d < f1) {
					f2 = f1;
					f1 = d;
					id = ip + g;
				} else if (d < f2) {
					f2 = d;
				}
			}
		}
		float mortar = 1.0 - smoothstep(0.02, 0.13, f2 - f1);
		float n = hash(vec3(id, 3.0));
		col *= 0.62 + 0.55 * n;
		col = mix(col, tint.rgb * 0.18, mortar);
		col *= 1.0 - 0.3 * speck * grime;
	} else {
		vec2 uv;
		if (abs(wnorm.y) > 0.5) {
			uv = p.xz;
		} else if (abs(wnorm.x) > 0.5) {
			uv = p.zy;
		} else {
			uv = p.xy;
		}
		if (use_bricks) {
			float row = floor(uv.y * 2.2);
			vec2 g = vec2(uv.x * 1.2 + mod(row, 2.0) * 0.5, uv.y * 2.2);
			vec2 cell = floor(g);
			vec2 f = fract(g);
			float mortar = clamp(step(f.x, 0.06) + step(f.y, 0.1), 0.0, 1.0);
			float n = hash(vec3(cell, 1.0));
			col *= 0.62 + 0.6 * n;
			col *= 1.0 - 0.6 * mortar;
		}
		col *= 1.0 - 0.4 * speck * grime;
	}
	ALBEDO = col;
	ROUGHNESS = 1.0;
	EMISSION = emission_color.rgb * emission_energy;
}
