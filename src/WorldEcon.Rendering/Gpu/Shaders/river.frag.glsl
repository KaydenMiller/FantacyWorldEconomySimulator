// Round-capped, anti-aliased river stroke; colour by width level from the palette (GpuPalette.RowRiverStroke).
// Output is premultiplied alpha (blend: ONE, ONE_MINUS_SRC_ALPHA).

uniform sampler2D uPalette;
uniform float uPixelRatio;

in vec2 vLocal;
flat in float vLen;
flat in float vHalf;
flat in float vLevel;

out vec4 fragColour;

void main()
{
    float along = clamp(vLocal.x, 0.0, vLen);
    float d = length(vec2(vLocal.x - along, vLocal.y));
    float alpha = clamp((vHalf - d) * uPixelRatio + 0.5, 0.0, 1.0);
    if (alpha <= 0.0) discard;
    vec3 col = texelFetch(uPalette, ivec2(int(vLevel), 8), 0).rgb;
    fragColour = vec4(col * alpha, alpha);
}
