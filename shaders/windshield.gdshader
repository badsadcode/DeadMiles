shader_type canvas_item;

// Rain on the windscreen, seen from the driver's seat. Drops build up between wiper passes.

uniform float intensity = 0.0;
uniform float clear_amount = 1.0; // 0 right after the wiper passed, 1 when fully wet again

float hash(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

void fragment() {
	vec2 g = UV * vec2(46.0, 26.0);
	vec2 id = floor(g);
	vec2 f = fract(g) - 0.5;
	float h = hash(id);
	float t = fract(TIME * 0.22 * (0.5 + h) + h * 10.0);
	vec2 offs = (vec2(hash(id + 3.1), hash(id + 7.7)) - 0.5) * 0.55;
	offs.y += t * 0.35;
	float d = length((f - offs) * vec2(1.0, 0.7));
	float r = 0.06 + 0.11 * hash(id + 1.3);
	float present = step(1.0 - intensity * 0.85, hash(id + 5.5));
	float drop = smoothstep(r, r * 0.45, d) * present * (1.0 - t * 0.6);
	float rim = smoothstep(r * 0.45, r, d) * smoothstep(r * 1.15, r, d) * present;
	// keep the lower dashboard area clear
	float glass = smoothstep(0.78, 0.6, UV.y);
	float a = (drop * 0.22 + rim * 0.2) * clear_amount * glass * smoothstep(0.0, 0.15, intensity);
	COLOR = vec4(vec3(0.78, 0.83, 0.9), a);
}
