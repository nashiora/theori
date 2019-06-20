#version 330
#extension GL_ARB_separate_shader_objects : enable

layout (location = 1) in vec2 frag_TexCoord;
layout (location = 0) out vec4 target;

uniform sampler2D MainTexture;

uniform vec3 LaserColor;
uniform vec3 HiliteColor;

uniform float Glow;
uniform int GlowState;

void main()
{	
	float x = float(GlowState) * 0.25;

	vec3 s = texture(MainTexture, vec2(frag_TexCoord.x * 0.25 + x, frag_TexCoord.y)).rgb;
	vec3 color = mix(s.g * LaserColor, vec3(1), s.r);

	target = vec4(color, 1);
}