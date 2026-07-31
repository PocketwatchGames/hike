using Godot;

// CPU port of shaders/roof_broken.gdshaderinc.
//
// MUST STAY IN SYNC WITH THAT FILE. The shader decides which fragments of a
// derelict roof are missing; this decides which voxel columns the sun-occlusion
// pass leaves open. Those two answers have to agree or the god ray comes down
// somewhere there is no visible hole (or worse, a hole shows daylight through a
// roof that is still shadowing the floor beneath it).
//
// The hash is integer arithmetic on purpose — a fract(sin(...)) hash does not
// reproduce bit-for-bit between GLSL and C#, and this pair has to.
public static class RoofBrokenNoise
{
    private static float Hash(int x, int y)
    {
        unchecked
        {
            int n = x * 374761393 + y * 668265263;
            n = (n ^ (n >> 13)) * 1274126177;
            return ((n ^ (n >> 16)) & 0xFFFF) / 65535f;
        }
    }

    // Smooth value noise in [0,1].
    public static float Noise(float px, float pz)
    {
        float flx = Mathf.Floor(px);
        float flz = Mathf.Floor(pz);
        int ix = (int)flx;
        int iz = (int)flz;
        float fx = px - flx;
        float fz = pz - flz;
        float ux = fx * fx * (3f - 2f * fx);
        float uz = fz * fz * (3f - 2f * fz);
        float a = Hash(ix, iz);
        float b = Hash(ix + 1, iz);
        float c = Hash(ix, iz + 1);
        float d = Hash(ix + 1, iz + 1);
        return Mathf.Lerp(Mathf.Lerp(a, b, ux), Mathf.Lerp(c, d, ux), uz);
    }

    // Two octaves. The COARSE one decides where holes are; the FINE one is a
    // signed perturbation, so it only bites near the threshold crossing — the
    // hole's edge — giving ragged rims rather than a rash of pinholes.
    public static float Field(float worldX, float worldZ, float scale, float edgeScale, float jagged)
    {
        float coarse = Noise(worldX * scale, worldZ * scale);
        if (jagged <= 0f)
        {
            return coarse;
        }
        float fine = Noise(worldX * edgeScale, worldZ * edgeScale);
        return coarse + (fine - 0.5f) * jagged;
    }

    // Turns an authored "fraction of the surface gone" into the threshold to
    // compare the noise against.
    //
    // Needed because smooth value noise is NOT uniformly distributed — it is the
    // bilinear blend of four uniform hashes, so it clusters hard around 0.5 and
    // its tails are thin. Comparing directly meant broken = 0.2 opened about 1%
    // of the roof, and the knob lied by an order of magnitude at the low end.
    // The noise's CDF is very close to smoothstep, so inverting smoothstep maps
    // the authored fraction onto the threshold that actually yields it.
    //
    // Evaluated on the CPU only: the result is per-roof, so it is baked into the
    // instance uniform the shader receives and costs the GPU nothing.
    public static float ThresholdFor(float broken)
    {
        if (broken <= 0f) { return 0f; }
        if (broken >= 1f) { return 1f; }
        // Inverse of y = 3t^2 - 2t^3.
        return 0.5f - Mathf.Sin(Mathf.Asin(1f - 2f * broken) / 3f);
    }

    // True where the roof is missing. Takes a WORLD XZ position, matching the
    // shader — a hole is a vertical opening, so the column beneath it is the
    // thing that has to stay lit.
    public static bool IsHole(float worldX, float worldZ, float broken, float scale, float edgeScale, float jagged)
    {
        if (broken <= 0f)
        {
            return false;
        }
        return Field(worldX, worldZ, scale, edgeScale, jagged) < broken;
    }
}
