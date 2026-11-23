// Mountain symbol colour (LayerPalette.MountainSymbolColor, passed as a uniform).

uniform vec3 uColour;

out vec4 fragColour;

void main()
{
    fragColour = vec4(uColour, 1.0);
}
