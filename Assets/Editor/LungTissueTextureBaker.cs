using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Bakes a realistic lung-tissue material (albedo + normal) and assigns it to the lung
/// renderer.
///
/// Why baked rather than generated at runtime: the mesh is a CT segmentation with no
/// authored UVs and no source texture, so the material was a flat colour. Generating
/// this procedurally every launch would cost seconds of CPU on a Quest, so it is
/// produced once here and saved as assets.
///
/// Why it does not disturb the smoking system: LungMeshDeformer.InitializeSmokingTexture
/// samples whatever readable texture sits on _BaseMap into its "healthyPixels" buffer
/// and blends the tar stain over it at runtime. Handing it a real tissue albedo simply
/// means the healthy end of that blend is now real tissue. The normal map lives on a
/// separate property the runtime never touches.
///
/// THE STRIPING FIX (previous version's biggest failure):
///  1. Frequencies are now capped below Nyquist. The old version sampled noise at
///     frequency 150 and 320 on a 512px map, which works out to 0.57 and 0.27 pixels
///     per lattice cell - roughly 7x past the sampling limit. Undersampled noise does
///     not read as "fine detail", it aliases into regular moire, which is exactly the
///     parallel-line / fabric appearance. Anything finer than the texture can resolve
///     has to be dropped, not turned up.
///  2. Gradient (Perlin) noise replaces value noise. Value noise interpolates a scalar
///     lattice and leaves visible axis-aligned blockiness; gradient noise does not.
///  3. Each octave is rotated by an orthonormal matrix so successive octaves cannot
///     stack their lattices into an apparent grid.
///  4. Domain warping displaces the sample point by another noise field, which breaks
///     up the regularity that makes procedural texture look procedural.
///  5. The tiled detail-normal map is gone. Tiling 9x across the surface is repetition
///     by construction; fine relief now comes from a higher-resolution non-tiled normal.
///
/// Seams: UVs are a cylindrical unwrap (see LungMeshDeformer.EnsureStainUVs), so u wraps
/// around the organ. All noise is sampled on a *cylinder* in 3D rather than on the flat
/// uv plane, which makes u = 0 and u = 1 agree exactly. Octave rotation and domain
/// warping preserve this, because both are pure functions of that 3D point.
/// </summary>
public static class LungTissueTextureBaker
{
    // Must stay at 512: LungMeshDeformer clamps its runtime stain buffer to the base
    // map's size, and that buffer is repainted on the CPU while the smoking slider is
    // dragged. Raising this would multiply that per-frame cost on Quest.
    const int AlbedoSize = 512;

    // Normal map is GPU-only - never read back on the CPU - so it can afford more
    // resolution, and that headroom is what carries fine tissue relief now that the
    // tiled detail map is gone.
    const int NormalSize = 1024;

    // Highest noise frequency each map can represent without aliasing, at >=4 px per
    // lattice cell. The cylinder mapping traverses (6 * freq) cells down v.
    const float MaxAlbedoFreq = 21f;   // 512 / 4 / 6
    const float MaxNormalFreq = 42f;   // 1024 / 4 / 6

    const string Folder = "Assets/Settings/LungTissue";
    // Small, seamlessly tileable, projected triplanar in object space by the custom
    // shader. Its resolution is independent of the cylindrical unwrap, which is the
    // whole point: this is what finally carries sub-millimetre detail.
    const int DetailSize = 256;

    const string AlbedoPath = Folder + "/LungTissue_Albedo.png";
    const string DetailPath = Folder + "/LungTissue_DetailNormal.png";
    const string NormalPath = Folder + "/LungTissue_Normal.png";
    const string MaskPath = Folder + "/LungTissue_Mask.png";
    const string DamagePath = Folder + "/LungTissue_Damage.png";
    const string MaterialPath = Folder + "/LungTissue.mat";

    // Natural salmon-pink parenchyma with restrained variation. Nothing here is far
    // from anything else - the references show subtle tonal drift, not colour patches.
    static readonly Color TissueBase = new Color(0.764f, 0.502f, 0.470f);
    static readonly Color TissuePale = new Color(0.824f, 0.624f, 0.573f);
    static readonly Color TissueDeep = new Color(0.659f, 0.384f, 0.361f);
    static readonly Color VesselTint = new Color(0.620f, 0.341f, 0.329f);
    // Deliberately a muted warm grey-brown, never black: fissures should read as
    // anatomical separations that still contain tissue, not as cracks.
    static readonly Color FissureTint = new Color(0.576f, 0.400f, 0.376f);

    // Moderate-to-high roughness. Soft biological tissue under a thin moist membrane
    // gives a broad, gentle specular response - not a tight plastic highlight.
    const float Smoothness = 0.27f;
    const float BumpScale = 0.55f;

    [MenuItem("BioGears/Bake Realistic Lung Tissue")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.Log("[LungTissueTextureBaker] Play mode is active - exiting Play mode, then baking automatically.");
            EditorApplication.playModeStateChanged += ResumeAfterPlayMode;
            EditorApplication.isPlaying = false;
            return;
        }

        Bake();
    }

    static void ResumeAfterPlayMode(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode)
            return;

        EditorApplication.playModeStateChanged -= ResumeAfterPlayMode;
        EditorApplication.delayCall += Bake;
    }

    static void Bake()
    {
        Directory.CreateDirectory(Folder);

        try
        {
            Color32[] albedo = BuildAlbedo(AlbedoSize);
            float[] height = BuildHeight(NormalSize);

            EditorUtility.DisplayProgressBar("Baking lung tissue", "Surface normals...", 0.85f);
            Color32[] normal = HeightToNormal(height, NormalSize, NormalSize, strength: 1.6f, wrapX: true);

            Color32[] damage = BuildDamageMask(AlbedoSize);

            EditorUtility.DisplayProgressBar("Baking lung tissue", "Gloss and cavity mask...", 0.88f);
            Color32[] mask = BuildMaskFromHeight(height, NormalSize);

            EditorUtility.DisplayProgressBar("Baking lung tissue", "Detail normal...", 0.90f);
            Color32[] detail = BuildTileableDetailNormal(DetailSize);

            EditorUtility.DisplayProgressBar("Baking lung tissue", "Writing assets...", 0.93f);
            // Albedo must be readable - the smoking system samples it on the CPU.
            Texture2D albedoTex = WritePng(albedo, AlbedoSize, AlbedoSize, AlbedoPath, isNormalMap: false, readable: true);
            Texture2D normalTex = WritePng(normal, NormalSize, NormalSize, NormalPath, isNormalMap: true, readable: false);

            Texture2D detailTex = WritePng(detail, DetailSize, DetailSize, DetailPath, isNormalMap: true, readable: false);

            Texture2D maskTex = WritePng(mask, NormalSize, NormalSize, MaskPath, isNormalMap: false, readable: false, linear: true);

            Texture2D damageTex = WritePng(damage, AlbedoSize, AlbedoSize, DamagePath, isNormalMap: false, readable: false, linear: true);

            Material mat = BuildMaterial(albedoTex, normalTex, detailTex, maskTex, damageTex);
            AssignToLung(mat);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    // ------------------------------------------------------------------ albedo ----

    static Color32[] BuildAlbedo(int size)
    {
        Color32[] pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            if ((y & 63) == 0)
                EditorUtility.DisplayProgressBar("Baking lung tissue", "Tissue colour...", 0.05f + 0.35f * y / size);

            float v = y / (float)(size - 1);

            for (int x = 0; x < size; x++)
            {
                float u = x / (float)(size - 1);

                // Warp the domain first. Every layer below samples the warped point, so
                // the whole texture inherits an organic, non-repeating flow instead of
                // the even blobbiness that makes plain fbm look synthetic.
                Vector3 p = Cyl(u, v, 1f);
                Vector3 warped = Warp(p, 2.2f, 0.35f);

                // Broad tonal drift - pale in places, deeper in others. Kept very low
                // frequency so it reads as one organ, not as patches.
                float tone = Fbm(warped * 2.0f, 4);
                Color c = Color.Lerp(TissueBase, TissuePale, Mathf.SmoothStep(0.38f, 0.72f, tone));
                c = Color.Lerp(c, TissueDeep, Mathf.SmoothStep(0.55f, 0.90f, 1f - tone) * 0.30f);

                // Soft parenchymal mottling.
                float mottle = Fbm(warped * 5.0f, 3);
                c = Color.Lerp(c, TissueDeep, Mathf.SmoothStep(0.52f, 0.95f, mottle) * 0.16f);

                // The finest colour grain the map can actually resolve. Capped at
                // MaxAlbedoFreq: pushing past it produced the moire that looked like
                // scratches, and no amount of strength tuning could hide that.
                float grain = Fbm(p * 10.0f, 2);
                c = Color.Lerp(c, c * 0.955f, Mathf.SmoothStep(0.45f, 0.95f, grain) * 0.55f);

                // Subpleural vasculature: branching, but at very low contrast so it is
                // sensed rather than seen. The references show almost no hard vessels.
                float vessels = Ridged(warped * 10f, 2);
                c = Color.Lerp(c, VesselTint, Mathf.Pow(vessels, 4f) * 0.13f);

                // Lobar fissures. Soft and wide rather than thin and dark - a low
                // exponent keeps them from collapsing into a drawn line.
                float fissure = Ridged(warped * 1.7f, 2);
                c = Color.Lerp(c, FissureTint, Mathf.Pow(fissure, 5f) * 0.34f);

                pixels[y * size + x] = c;
            }
        }

        return pixels;
    }

    // ------------------------------------------------------------------ height ----

    /// <summary>
    /// Height field for the normal map, at its own higher resolution so it can carry
    /// finer relief than the albedo without aliasing.
    /// </summary>
    static float[] BuildHeight(int size)
    {
        float[] h = new float[size * size];

        for (int y = 0; y < size; y++)
        {
            if ((y & 63) == 0)
                EditorUtility.DisplayProgressBar("Baking lung tissue", "Tissue relief...", 0.4f + 0.45f * y / size);

            float v = y / (float)(size - 1);

            for (int x = 0; x < size; x++)
            {
                float u = x / (float)(size - 1);

                Vector3 p = Cyl(u, v, 1f);
                Vector3 warped = Warp(p, 2.2f, 0.35f);

                // Gentle lobulation - the soft, rounded unevenness of a real organ.
                float lobule = Fbm(warped * 3.2f, 3);

                // Fine porous tissue grain, capped at what 1024px can resolve.
                float pores = Fbm(p * 16.0f, 2);

                // Vessels sit very slightly proud of the pleural surface.
                float vessels = Mathf.Pow(Ridged(warped * 10f, 2), 4f);

                // Fissures cut in - but shallowly. Deep displacement here is what made
                // the previous bake look like carved rock.
                float fissure = Mathf.Pow(Ridged(warped * 1.7f, 2), 5f);

                h[y * size + x] = lobule * 0.42f
                                + pores * 0.30f
                                + vessels * 0.16f
                                - fissure * 0.38f;
            }
        }

        return h;
    }

    /// <summary>
    /// Where each kind of smoking damage accumulates. Four independent noise fields, so
    /// pigment, scarring, inflammation and emphysema appear in DIFFERENT places rather
    /// than all darkening the same pixels together - which is what makes a uniform
    /// colour filter look fake.
    ///
    ///   R - anthracotic pigment. Large irregular patches, matching the coarse black
    ///       mottling in the smoker reference. Deliberately low frequency: real carbon
    ///       deposition pools in regions, it is not evenly sprinkled.
    ///   G - fibrosis / scarring. Streakier (ridged noise), appears later.
    ///   B - inflammation. Finer and concentrated toward the airways.
    ///   A - emphysematous destruction. Few large blobs, advanced stages only.
    ///
    /// The shader thresholds each channel against cumulative exposure, so damage grows
    /// outward from its own seed points instead of fading in globally.
    /// </summary>
    static Color32[] BuildDamageMask(int size)
    {
        Color32[] pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            if ((y & 63) == 0)
                EditorUtility.DisplayProgressBar("Baking lung tissue", "Damage susceptibility...", 0.78f + 0.06f * y / size);

            float v = y / (float)(size - 1);

            for (int x = 0; x < size; x++)
            {
                float u = x / (float)(size - 1);
                Vector3 p = Cyl(u, v, 1f);
                // Each field gets its own warp so they do not share structure.
                Vector3 wPig = Warp(p + new Vector3(17.3f, 5.1f, 23.9f), 1.8f, 0.45f);
                Vector3 wScar = Warp(p + new Vector3(41.7f, 29.3f, 8.6f), 2.6f, 0.40f);
                Vector3 wInf = Warp(p + new Vector3(7.9f, 37.1f, 15.2f), 3.1f, 0.30f);
                Vector3 wEmph = Warp(p + new Vector3(31.5f, 11.8f, 44.4f), 1.3f, 0.50f);

                // Pigment: broad blotches with a finer break-up so edges are ragged.
                float pigment = Fbm(wPig * 2.6f, 4) * 0.75f + Fbm(wPig * 7.5f, 2) * 0.25f;

                // Scarring: ridged, so it forms streaks and bands rather than blobs.
                float scar = Ridged(wScar * 4.0f, 3);

                // Inflammation: finer, biased toward the airway corridor (centre of u).
                float airway = Mathf.Exp(-Mathf.Pow((u - 0.5f) / 0.22f, 2f));
                float inflammation = Mathf.Clamp01(Fbm(wInf * 6.5f, 3) * 0.7f + airway * 0.4f);

                // Emphysema: a handful of large regions only.
                float emphysema = Mathf.SmoothStep(0.45f, 0.95f, Fbm(wEmph * 1.7f, 3));

                pixels[y * size + x] = new Color32(
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(pigment) * 255f),
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(scar) * 255f),
                    (byte)Mathf.RoundToInt(inflammation * 255f),
                    (byte)Mathf.RoundToInt(emphysema * 255f));
            }
        }

        return pixels;
    }

    /// <summary>
    /// Packs gloss and cavity shading into one texture: R = smoothness, G = occlusion.
    /// Both are derived from the same height field the normal map uses, so a fissure is
    /// simultaneously duller and darker, and the three maps agree with each other rather
    /// than fighting.
    ///
    /// Occlusion comes from comparing each point against a blurred copy of the height
    /// field - anywhere sitting below its local average is a cavity and gets darkened.
    /// That is a cheap approximation of ambient occlusion, and unlike real AO it costs
    /// nothing at runtime.
    /// </summary>
    static Color32[] BuildMaskFromHeight(float[] height, int size)
    {
        // Normalise, since the height field is an arbitrary sum of noise layers.
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < height.Length; i++)
        {
            if (height[i] < min) min = height[i];
            if (height[i] > max) max = height[i];
        }
        float invRange = 1f / Mathf.Max(1e-5f, max - min);

        // Separable box blur -> local average height.
        const int Radius = 5;
        float[] tmp = new float[height.Length];
        float[] blur = new float[height.Length];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float sum = 0f;
                for (int k = -Radius; k <= Radius; k++)
                    sum += height[y * size + ((x + k + size) % size)];   // wrap in u
                tmp[y * size + x] = sum / (Radius * 2 + 1);
            }
        }

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float sum = 0f;
                for (int k = -Radius; k <= Radius; k++)
                    sum += tmp[Mathf.Clamp(y + k, 0, size - 1) * size + x];
                blur[y * size + x] = sum / (Radius * 2 + 1);
            }
        }

        Color32[] pixels = new Color32[height.Length];
        for (int i = 0; i < height.Length; i++)
        {
            float h = (height[i] - min) * invRange;

            // Cavity: how far below the local average this point sits.
            float cavity = Mathf.Clamp01((blur[i] - height[i]) * invRange * 6f);
            float occlusion = Mathf.Clamp01(1f - cavity);

            // Recessed tissue is duller; raised, exposed pleura holds more moisture and
            // catches more of a highlight.
            float smoothness = Mathf.Clamp01(Mathf.SmoothStep(0.15f, 0.85f, h) * (1f - cavity * 0.6f));

            byte r = (byte)Mathf.RoundToInt(smoothness * 255f);
            byte g = (byte)Mathf.RoundToInt(occlusion * 255f);
            pixels[i] = new Color32(r, g, 0, 255);
        }

        return pixels;
    }

    /// <summary>
    /// Fine porous tissue relief, tiled by the shader in object space.
    ///
    /// Must wrap EXACTLY in both directions or triplanar projection shows grid lines
    /// wherever a tile boundary falls. Gradient noise on a lattice that wraps modulo
    /// `period` guarantees that: opposite edges read the same lattice cells, so the
    /// values and their derivatives match across the join.
    ///
    /// Unlike the UV-mapped maps above this has no Nyquist ceiling tied to the organ -
    /// the shader controls how much tissue each repeat covers (_DetailScale), so the
    /// only limit is the tile's own resolution.
    /// </summary>
    static Color32[] BuildTileableDetailNormal(int size)
    {
        const int Period = 12;      // lattice cells per repeat
        float[] h = new float[size * size];

        for (int y = 0; y < size; y++)
        {
            float v = y / (float)size;
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size;

                // Two octaves of wrapped gradient noise: broad pore structure plus a
                // finer grain over it.
                float coarse = PerlinTiled(u * Period, v * Period, Period);
                float fine = PerlinTiled(u * Period * 2f, v * Period * 2f, Period * 2);

                h[y * size + x] = coarse * 0.62f + fine * 0.38f;
            }
        }

        // Gentle: this is a micro-detail layer, and the shader scales it again via
        // _DetailStrength. Overdriving here is what made earlier bakes look crusted.
        return HeightToNormal(h, size, size, strength: 0.85f, wrapX: true, wrapY: true);
    }

    /// <summary>2D gradient noise whose lattice wraps at `period`, so the map tiles.</summary>
    static float PerlinTiled(float x, float y, int period)
    {
        int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
        float xf = x - xi, yf = y - yi;

        float u = xf * xf * xf * (xf * (xf * 6f - 15f) + 10f);
        float v = yf * yf * yf * (yf * (yf * 6f - 15f) + 10f);

        float n00 = GradTiled(xi, yi, period, xf, yf);
        float n10 = GradTiled(xi + 1, yi, period, xf - 1f, yf);
        float n01 = GradTiled(xi, yi + 1, period, xf, yf - 1f);
        float n11 = GradTiled(xi + 1, yi + 1, period, xf - 1f, yf - 1f);

        float a = Mathf.Lerp(n00, n10, u);
        float b = Mathf.Lerp(n01, n11, u);
        return Mathf.Clamp01(Mathf.Lerp(a, b, v) * 0.5f + 0.5f);
    }

    static float GradTiled(int xi, int yi, int period, float dx, float dy)
    {
        // Wrapping the LATTICE indices - not the sample position - is what makes the
        // result tile without distorting the noise.
        xi = ((xi % period) + period) % period;
        yi = ((yi % period) + period) % period;

        int hash = Hash(xi, yi, 0);
        switch (hash & 7)
        {
            case 0: return dx + dy;
            case 1: return -dx + dy;
            case 2: return dx - dy;
            case 3: return -dx - dy;
            case 4: return dx;
            case 5: return -dx;
            case 6: return dy;
            default: return -dy;
        }
    }

    static Color32[] HeightToNormal(float[] h, int w, int hgt, float strength, bool wrapX, bool wrapY = false)
    {
        Color32[] outPx = new Color32[w * hgt];

        for (int y = 0; y < hgt; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int xl = wrapX ? (x - 1 + w) % w : Mathf.Max(x - 1, 0);
                int xr = wrapX ? (x + 1) % w : Mathf.Min(x + 1, w - 1);
                int yd = wrapY ? (y - 1 + hgt) % hgt : Mathf.Max(y - 1, 0);
                int yu = wrapY ? (y + 1) % hgt : Mathf.Min(y + 1, hgt - 1);

                float dx = (h[y * w + xr] - h[y * w + xl]) * strength;
                float dy = (h[yu * w + x] - h[yd * w + x]) * strength;

                Vector3 n = new Vector3(-dx, -dy, 1f).normalized;
                outPx[y * w + x] = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt((n.x * 0.5f + 0.5f) * 255f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt((n.y * 0.5f + 0.5f) * 255f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt((n.z * 0.5f + 0.5f) * 255f), 0, 255),
                    255);
            }
        }

        return outPx;
    }

    // -------------------------------------------------------------------- noise ---

    /// <summary>
    /// Maps (u,v) onto a cylinder so noise is continuous where u wraps from 1 back to
    /// 0 - the reason the texture has no vertical seam. v is scaled by 6 so features
    /// stay roughly square (a unit circle's circumference is ~6.28).
    /// </summary>
    static Vector3 Cyl(float u, float v, float freq)
    {
        float a = u * Mathf.PI * 2f;
        return new Vector3(Mathf.Cos(a), Mathf.Sin(a), v * 6.0f) * freq;
    }

    /// <summary>
    /// Displaces the sample point by a second noise field. This is what stops the
    /// result reading as "a noise texture" - features stretch and curl organically
    /// instead of sitting in the even lumps plain fbm produces.
    /// </summary>
    static Vector3 Warp(Vector3 p, float freq, float amount)
    {
        Vector3 q = new Vector3(
            Fbm(p * freq + new Vector3(11.3f, 4.7f, 19.1f), 3),
            Fbm(p * freq + new Vector3(27.9f, 13.2f, 5.4f), 3),
            Fbm(p * freq + new Vector3(3.6f, 22.8f, 31.7f), 3));

        return p + (q - Vector3.one * 0.5f) * amount;
    }

    // Orthonormal rotation applied between octaves. Without it successive octaves share
    // an axis alignment and reinforce each other into an apparent grid.
    static Vector3 RotateOctave(Vector3 p)
    {
        return new Vector3(
            0.00f * p.x + 0.80f * p.y + 0.60f * p.z,
            -0.80f * p.x + 0.36f * p.y - 0.48f * p.z,
            -0.60f * p.x - 0.48f * p.y + 0.64f * p.z);
    }

    static float Fbm(Vector3 p, int octaves)
    {
        float sum = 0f, amp = 0.5f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += amp * Perlin(p);
            norm += amp;
            p = RotateOctave(p) * 2.03f;
            amp *= 0.5f;
        }
        return norm > 0f ? Mathf.Clamp01(sum / norm) : 0f;
    }

    static float Ridged(Vector3 p, int octaves)
    {
        float sum = 0f, amp = 0.5f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += amp * (1f - Mathf.Abs(Perlin(p) * 2f - 1f));
            norm += amp;
            p = RotateOctave(p) * 2.11f;
            amp *= 0.5f;
        }
        return norm > 0f ? Mathf.Clamp01(sum / norm) : 0f;
    }

    // Integer hash with proper avalanche. The previous multiply-xor left neighbouring
    // lattice points correlated, which shows up as faint axis-aligned banding.
    static int Hash(int x, int y, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 0x27d4eb2d) ^ (uint)(y * 0x165667b1) ^ (uint)(z * 0x9e3779b1);
            h ^= h >> 15; h *= 0x2c1b3c6d;
            h ^= h >> 12; h *= 0x297a2d39;
            h ^= h >> 15;
            return (int)h;
        }
    }

    // Standard Perlin gradient set: the 12 edge-midpoints of a cube.
    static float Grad(int hash, float x, float y, float z)
    {
        switch (hash & 15)
        {
            case 0: return x + y;
            case 1: return -x + y;
            case 2: return x - y;
            case 3: return -x - y;
            case 4: return x + z;
            case 5: return -x + z;
            case 6: return x - z;
            case 7: return -x - z;
            case 8: return y + z;
            case 9: return -y + z;
            case 10: return y - z;
            case 11: return -y - z;
            case 12: return x + y;
            case 13: return -y + z;
            case 14: return -x + y;
            default: return -y - z;
        }
    }

    /// <summary>Gradient (Perlin) noise, returned in 0-1.</summary>
    static float Perlin(Vector3 p)
    {
        int xi = Mathf.FloorToInt(p.x), yi = Mathf.FloorToInt(p.y), zi = Mathf.FloorToInt(p.z);
        float xf = p.x - xi, yf = p.y - yi, zf = p.z - zi;

        // Quintic fade - C2 continuous, so no creases along cell boundaries.
        float u = xf * xf * xf * (xf * (xf * 6f - 15f) + 10f);
        float v = yf * yf * yf * (yf * (yf * 6f - 15f) + 10f);
        float w = zf * zf * zf * (zf * (zf * 6f - 15f) + 10f);

        float n000 = Grad(Hash(xi, yi, zi), xf, yf, zf);
        float n100 = Grad(Hash(xi + 1, yi, zi), xf - 1f, yf, zf);
        float n010 = Grad(Hash(xi, yi + 1, zi), xf, yf - 1f, zf);
        float n110 = Grad(Hash(xi + 1, yi + 1, zi), xf - 1f, yf - 1f, zf);
        float n001 = Grad(Hash(xi, yi, zi + 1), xf, yf, zf - 1f);
        float n101 = Grad(Hash(xi + 1, yi, zi + 1), xf - 1f, yf, zf - 1f);
        float n011 = Grad(Hash(xi, yi + 1, zi + 1), xf, yf - 1f, zf - 1f);
        float n111 = Grad(Hash(xi + 1, yi + 1, zi + 1), xf - 1f, yf - 1f, zf - 1f);

        float x00 = Mathf.Lerp(n000, n100, u), x10 = Mathf.Lerp(n010, n110, u);
        float x01 = Mathf.Lerp(n001, n101, u), x11 = Mathf.Lerp(n011, n111, u);

        float result = Mathf.Lerp(Mathf.Lerp(x00, x10, v), Mathf.Lerp(x01, x11, v), w);
        return Mathf.Clamp01(result * 0.5f + 0.5f);
    }

    // ------------------------------------------------------------------- assets ---

    static Texture2D WritePng(Color32[] pixels, int w, int h, string path, bool isNormalMap, bool readable, bool linear = false)
    {
        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.SetPixels32(pixels);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = isNormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = !isNormalMap && !linear;
            importer.isReadable = readable;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 4;
            importer.mipmapEnabled = true;
            // The albedo is sampled on the CPU by the smoking system; block compression
            // would quantise those reads, so keep it exact.
            importer.textureCompression = readable
                ? TextureImporterCompression.Uncompressed
                : TextureImporterCompression.Compressed;
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static Material BuildMaterial(Texture2D albedo, Texture2D normal, Texture2D detail, Texture2D mask, Texture2D damage)
    {
        Shader shader = Shader.Find("BioGears/LungTissue");
        if (shader == null)
        {
            Debug.LogError("[LungTissueTextureBaker] BioGears/LungTissue shader not found - make sure Assets/LungTissue.shader compiled (check the Console for shader errors).");
            return null;
        }

        Material mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, MaterialPath);
        }
        mat.shader = shader;

        mat.SetTexture("_BaseMap", albedo);
        // White, because LungMeshDeformer reads _BaseColor as its baseline tint and
        // multiplies the stain over it; anything else would double-tint the tissue.
        mat.SetColor("_BaseColor", Color.white);

        mat.SetTexture("_BumpMap", normal);
        mat.SetFloat("_BumpScale", BumpScale);
        mat.EnableKeyword("_NORMALMAP");

        // Triplanar detail: projected in OBJECT space by the custom shader, so its
        // resolution is decoupled from the cylindrical unwrap. A 512px UV map can only
        // resolve ~26mm features on this organ; this carries sub-millimetre ones.
        mat.SetTexture("_DetailNormal", detail);
        mat.SetFloat("_DetailScale", 0.35f);
        mat.SetFloat("_DetailStrength", 0.55f);
        mat.SetFloat("_TriplanarSharpness", 4f);

        // Gloss and occlusion now vary across the surface instead of being one constant.
        mat.SetTexture("_MaskMap", mask);
        mat.SetFloat("_SmoothnessMin", 0.16f);   // fissures: dull
        mat.SetFloat("_SmoothnessMax", 0.46f);   // exposed pleura: moist
        mat.SetFloat("_OcclusionStrength", 0.75f);
        mat.SetFloat("_Metallic", 0f);

        // Subsurface. Deliberately restrained - the goal is flesh, not a glowing jelly.
        mat.SetColor("_TranslucencyColor", new Color(0.62f, 0.14f, 0.11f, 1f));
        mat.SetFloat("_TranslucencyStrength", 0.85f);
        mat.SetFloat("_TranslucencyDistortion", 0.35f);
        mat.SetFloat("_TranslucencyPower", 4f);
        mat.SetFloat("_DiffuseWrap", 0.35f);

        // --- smoking damage -------------------------------------------------------
        // Driven at runtime by a single _Damage01 float, so dragging the slider costs
        // nothing and the transition is genuinely continuous. Previously this repainted
        // 262,144 pixels on the CPU every frame of the drag.
        mat.SetTexture("_DamageMask", damage);
        mat.SetFloat("_Damage01", 0f);
        mat.SetColor("_PigmentColor", new Color(0.075f, 0.070f, 0.068f, 1f));  // anthracotic carbon
        mat.SetColor("_ScarColor", new Color(0.760f, 0.716f, 0.640f, 1f));     // pale fibrotic tissue
        mat.SetColor("_InflamedColor", new Color(0.600f, 0.240f, 0.220f, 1f)); // irritated, congested

        // URP/Lit has no true subsurface scattering (that is an HDRP feature). The
        // closest honest approximation here is the very dim warm emission
        // LungMeshDeformer.ApplyBreathingMaterialResponse already writes each frame
        // (~0.01-0.04) - enabling the keyword lets it lift shadowed tissue slightly
        // toward red, the way light bleeding through flesh does. It is far too dim to
        // read as glow.
        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;

        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return mat;
    }

    static void AssignToLung(Material mat)
    {
        if (mat == null)
            return;

        LungMeshDeformer[] deformers = Object.FindObjectsByType<LungMeshDeformer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (deformers.Length == 0)
        {
            Debug.LogWarning("[LungTissueTextureBaker] No LungMeshDeformer in the open scene - material was created but not assigned. Assign " + MaterialPath + " to the lung renderer manually.");
            return;
        }

        int assigned = 0;
        foreach (Renderer r in deformers[0].GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || r.gameObject.name == "LungSootOverlay")
                continue;

            Material[] mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
                mats[i] = mat;
            r.sharedMaterials = mats;
            assigned++;
        }

        if (assigned > 0)
        {
            EditorUtility.SetDirty(deformers[0]);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(deformers[0].gameObject.scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(deformers[0].gameObject.scene);
        }

        Debug.Log($"[LungTissueTextureBaker] Baked lung tissue and assigned to {assigned} renderer(s). " +
                  $"Albedo {AlbedoSize}px (readable, feeds the smoking stain), normal {NormalSize}px, mask {NormalSize}px, " +
                  $"detail {DetailSize}px projected TRIPLANAR in object space. UV-mapped noise capped at " +
                  $"{MaxAlbedoFreq}/{MaxNormalFreq} to stay below the aliasing limit; fine detail comes from the triplanar path.");
    }
}
