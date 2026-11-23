// Background: the outer colour beyond the visible cap, the disc (gap) colour inside it (as MapRenderer.DrawBackground).

uniform vec2 uViewport;     // device-independent pixels
uniform float uPixelRatio;  // framebuffer pixels per device-independent pixel
uniform float uMaxRadius;   // pixels
uniform vec3 uDiscColour;
uniform vec3 uOuterColour;

out vec4 fragColour;

void main()
{
    vec2 p = vec2(gl_FragCoord.x, float(uViewport.y) * uPixelRatio - gl_FragCoord.y) / uPixelRatio;
    float d = length(p - uViewport * 0.5);
    fragColour = vec4(d <= uMaxRadius ? uDiscColour : uOuterColour, 1.0);
}
