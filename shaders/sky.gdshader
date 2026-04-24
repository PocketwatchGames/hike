shader_type sky;

#include "sky_common.gdshaderinc"

void sky()
{
	COLOR = sample_sky(POSITION, EYEDIR, TIME);
}
