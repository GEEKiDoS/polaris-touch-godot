shader_type canvas_item;
render_mode blend_add;

uniform vec3 FaderColor = vec3(1.0, 1.0, 1.0);

void vertex() {
	// Called for every vertex the material is visible on.
}

void fragment() {
	vec4 col = texture(TEXTURE, UV);
	col.rgb *= FaderColor;
	
	COLOR = col;
}
