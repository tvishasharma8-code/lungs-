using UnityEngine;
using System.Collections.Generic;

public class LungMeshDeformer : MonoBehaviour
{
    Mesh mesh;
    Vector3[] originalVertices;
    Vector3[] displacedVertices;
    Mesh overlayMesh;
    GameObject overlayObject;
    Renderer overlayRenderer;
    Renderer[] targetRenderers;
    Material[] lungMaterials;
    float lastAppliedSmokingYears = -1f;
    Texture2D dynamicStainTexture;
    Texture2D overlayTexture;
    Color[] healthyPixels;
    Color[] workingPixels;
    Color[] sourcePixels;
    float[] stainField;
    float[] speckField;
    float[] airwayField;
    float[] apexField;
    float[] peripheralField;
    float[] asymmetryField;
    float[] sootCoreField;
    // Fractal mottling and thin reticular lines. Large smooth Perlin blobs alone read
    // as a shadow cast onto the lung; carbon deposits in a real smoker's lung are
    // fractal and follow the septal/lymphatic network, which is what these add.
    float[] fbmField;
    float[] ridgeField;
    int stainWidth;
    int stainHeight;
    float stainSeed;
    float baselineSmoothness = 0.35f;
    float baselineMetallic = 0f;
    Color baselineTint = Color.white;
    int primaryMaterialIndex = -1;
    static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int CutoffId = Shader.PropertyToID("_Cutoff");
    static readonly int SurfaceTypeId = Shader.PropertyToID("_SurfaceType");

    [Header("BioGears Integration")]
    [Tooltip("Reference to the BioGears respiratory system. If null, will search on this GameObject and parents.")]
    public BioGearsNativeRespiratory bioGearsSystem;

    [Header("Deformation Settings")]
    [Tooltip("Maximum expansion distance in mesh units at full tidal volume")]
    public float maxExpansion = 5f;

    [Tooltip("Separate expansion multipliers for each axis (allows asymmetric breathing)")]
    public Vector3 expansionScale = new Vector3(1f, 0.8f, 1.2f);

    [Tooltip("Flips the vertical direction used to unwrap UVs for the smoking stain. Does not affect the breathing motion.")]
    public bool invertBreathingAxis = false;

    [Tooltip("Run the breathing deformation in the vertex shader instead of on the CPU. Identical motion, but avoids rewriting and re-uploading every vertex each frame. Falls back to the CPU path automatically if the material does not support it.")]
    public bool useGpuBreathing = true;

    [Tooltip("Cigarettes smoked per day. Combined with smokingYears into pack-years, the standard cumulative-exposure measure.")]
    public float cigarettesPerDay = 20f;

    [Tooltip("Pack-years at which the visualisation reaches its most severe stage. Beyond this the appearance stops changing.")]
    public float packYearsAtMaxDamage = 40f;

    [Tooltip("Tidal volume (mL) treated as normal. Inflation depth scales with the tidal volume BioGears reports, relative to this - so asthma/COPD/exercise visibly change how far the lung inflates. Set to 0 for a fixed amplitude.")]
    public float referenceTidalVolume = 500f;

    [Header("Smoking Color Visualization")]
    [Tooltip("If enabled, applies smoking visuals to renderers on this object and all children")]
    public bool applyToChildRenderers = true;

    [Tooltip("Optional explicit renderers to target. If set, these override automatic search.")]
    public Renderer[] explicitTargetRenderers;

    [Tooltip("Search the scene for renderers whose names or material names look like lung meshes if no local renderers are found.")]
    public bool searchSceneForLungRenderers = true;

    [Tooltip("How long the patient has been smoking in years")]
    public float smokingYears = 0f;

    [Tooltip("Smoking years at which lungs reach the darkest configured color")]
    public float maxSmokingYears = 40f;

    [Tooltip("Healthy lung color")]
    public Color healthyLungColor = new Color(0.95f, 0.55f, 0.60f);

    [Tooltip("Long-term smoker deep stain color")]
    public Color smokerLungColor = new Color(0.03f, 0.03f, 0.03f);

    [Tooltip("Patch color seen in moderate smoking before areas go fully dark")]
    public Color midStainColor = new Color(0.23f, 0.17f, 0.13f);

    [Tooltip("Fibrotic/yellow-brown color in chronically irritated tissue")]
    public Color fibroticColor = new Color(0.36f, 0.31f, 0.21f);

    [Tooltip("Controls how quickly color darkens as smoking years increase")]
    public AnimationCurve smokingColorCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Tooltip("Runtime texture resolution for stain details")]
    [Range(128, 1024)]
    public int stainTextureResolution = 512;

    [Tooltip("Main patch scale")]
    [Range(1f, 12f)]
    public float primaryPatchScale = 4.2f;

    [Tooltip("Secondary patch breakup scale")]
    [Range(6f, 40f)]
    public float secondaryPatchScale = 18f;

    [Tooltip("Extra exposure multiplier if you want stronger effects per smoking year")]
    [Range(0.5f, 2f)]
    public float exposureMultiplier = 1f;

    [Tooltip("How much lung moisture/gloss reduces with chronic smoking")]
    [Range(0f, 1f)]
    public float chronicDryness = 0.55f;

    [Tooltip("Flip the vertical anatomy mapping if the stain appears on the wrong half of the mesh")]
    public bool invertVerticalAnatomy = true;

    [Tooltip("Show a high-contrast debug view of the smoke mask instead of the realistic stain blend")]
    public bool debugMaskPreview = false;

    [Tooltip("Render a separate soot overlay on top of the lungs instead of tinting the base material")]
    public bool useOverlayMesh = false;

    [Tooltip("Optional explicit target mesh renderer for the overlay. If empty, the first lung-like renderer is used.")]
    public Renderer overlayTargetRenderer;

    [Tooltip("How far the overlay mesh expands to avoid z-fighting")]
    [Range(1.0f, 1.03f)]
    public float overlayScale = 1.008f;

    [Tooltip("Opacity of the soot overlay at full smoking damage")]
    [Range(0.05f, 1f)]
    public float overlayOpacity = 0.92f;

    [Tooltip("Log the renderer and material names targeted by the smoking effect")]
    public bool logTargetRenderers = true;

    [Tooltip("Debug healthy tissue color used when debugMaskPreview is enabled")]
    public Color debugHealthyColor = new Color(0.95f, 0.82f, 0.88f);

    [Tooltip("Debug damaged tissue color used when debugMaskPreview is enabled")]
    public Color debugDamagedColor = new Color(0.02f, 0.02f, 0.02f);

    [Tooltip("Make the overlay more visible in the Scene view for tuning")]
    public bool overlayDebugVisible = false;

    Vector3[] directions;

    // Precomputed once so Update() never has to call mesh.RecalculateBounds().
    Bounds deformedBounds;

    // True when the assigned material exposes the GPU breathing parameters, in which
    // case Update() only has to set a single float per frame.
    bool gpuBreathingActive;
    // True when the material exposes _Damage01, i.e. smoking is rendered in the shader
    // rather than painted into a texture on the CPU.
    bool gpuDamageActive;
    static readonly int Damage01Id = Shader.PropertyToID("_Damage01");
    static readonly int BreathExpansionId = Shader.PropertyToID("_BreathExpansion");
    static readonly int BreathCentreId = Shader.PropertyToID("_BreathCentre");
    static readonly int BreathAxisScaleId = Shader.PropertyToID("_BreathAxisScale");

    void Start()
    {
        MeshFilter mf = GetComponent<MeshFilter>();
        stainSeed = Random.Range(0f, 1000f);

        CacheTargetMaterials();

        if (lungMaterials != null && lungMaterials.Length > 0)
        {
            for (int i = 0; i < lungMaterials.Length; i++)
            {
                Material mat = lungMaterials[i];
                if (mat == null) continue;

                bool hasBaseTexture = mat.HasProperty("_BaseMap") || mat.HasProperty("_MainTex");
                if (!hasBaseTexture) continue;

                primaryMaterialIndex = i;

                if (mat.HasProperty("_Smoothness"))
                    baselineSmoothness = mat.GetFloat("_Smoothness");
                if (mat.HasProperty("_Metallic"))
                    baselineMetallic = mat.GetFloat("_Metallic");
                if (mat.HasProperty("_BaseColor"))
                    baselineTint = mat.GetColor("_BaseColor");
                else if (mat.HasProperty("_Color"))
                    baselineTint = mat.GetColor("_Color");

                break;
            }

            if (primaryMaterialIndex < 0)
                primaryMaterialIndex = 0;
        }
        else
        {
            Debug.LogWarning("LungMeshDeformer: No compatible renderer materials found for smoking color visualization.");
        }

        // Find BioGears system if not assigned
        if (bioGearsSystem == null)
        {
            bioGearsSystem = GetComponent<BioGearsNativeRespiratory>();
            if (bioGearsSystem == null)
                bioGearsSystem = GetComponentInParent<BioGearsNativeRespiratory>();
            if (bioGearsSystem == null)
            {
                Debug.LogError("LungMeshDeformer: No BioGears respiratory system found! Add a BioGearsNativeRespiratory component.");
                return;
            }
        }

        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogWarning("LungMeshDeformer: No local MeshFilter mesh found, deformation disabled. Smoking visuals can still run on child renderers.");
        }
        else
        {
            mesh = Instantiate(mf.sharedMesh);
            mf.mesh = mesh;
            mesh.MarkDynamic();

            originalVertices = mesh.vertices;
            displacedVertices = new Vector3[originalVertices.Length];
            directions = new Vector3[originalVertices.Length];

            Vector3 center = mesh.bounds.center;

            // Original breathing deformation, restored verbatim: every vertex is pushed
            // straight out from the mesh centre, scaled per-axis by expansionScale in
            // Update(). Deliberately NOT anatomically weighted - the weighted variants
            // tried previously looked worse in the headset, so this is the version that
            // stays.
            for (int i = 0; i < originalVertices.Length; i++)
                directions[i] = (originalVertices[i] - center).normalized;

            // The axis below is used ONLY to unwrap UVs for the smoking stain (see
            // EnsureStainUVs); it no longer influences the breathing motion at all.
            Vector3 localUp = transform.InverseTransformDirection(Vector3.up);
            if (localUp.sqrMagnitude < 1e-8f)
                localUp = Vector3.up;
            localUp.Normalize();
            if (invertBreathingAxis)
                localUp = -localUp;

            // Extent of the mesh along that axis, for the base->apex gradient.
            float minH = float.MaxValue;
            float maxH = float.MinValue;
            for (int i = 0; i < originalVertices.Length; i++)
            {
                float d = Vector3.Dot(originalVertices[i], localUp);
                if (d < minH) minH = d;
                if (d > maxH) maxH = d;
            }

            // Largest possible outward displacement across a full breath, used as a
            // fixed bounds so Update() can skip RecalculateBounds entirely.
            float maxAxisScale = Mathf.Max(Mathf.Abs(expansionScale.x), Mathf.Max(Mathf.Abs(expansionScale.y), Mathf.Abs(expansionScale.z)));
            deformedBounds = mesh.bounds;
            deformedBounds.Expand(2f * maxExpansion * Mathf.Max(1f, maxAxisScale));
            mesh.bounds = deformedBounds;

            EnsureStainUVs(localUp, minH, maxH, center);

            Debug.Log($"LungMeshDeformer: Deformation initialized with {originalVertices.Length} vertices, connected to BioGears");
        }

        DetectShaderDamageSupport();

        // Only build the CPU stain pipeline when the shader cannot do the job. It
        // allocates several 512x512 buffers and takes over _BaseMap, so running it
        // alongside the shader path would both waste memory and erase the baked albedo.
        if (!gpuDamageActive)
            InitializeSmokingTexture();

        SetupGpuBreathing();

        if (useOverlayMesh)
            InitializeOverlayMesh();

        if (!gpuDamageActive)
        {
            ApplySmokingColor(true);
            ApplyBreathingMaterialResponse();
        }

        LogRuntimeMaterialState();
    }

    /// <summary>
    /// Dumps what the material actually looks like AFTER Start has finished touching it.
    /// The lung renders correctly in the Game view before Play and differently after, so
    /// something in this component's startup is changing the material - this reports the
    /// values that could do it, rather than guessing which one.
    /// </summary>
    void LogRuntimeMaterialState()
    {
        if (lungMaterials == null || lungMaterials.Length == 0) return;

        Material mat = lungMaterials[0];
        if (mat == null) return;

        string baseMap = mat.HasProperty(BaseMapId)
            ? (mat.GetTexture(BaseMapId) != null ? mat.GetTexture(BaseMapId).name : "NULL  <-- albedo lost")
            : "(no _BaseMap)";

        string tint = mat.HasProperty(BaseColorId) ? mat.GetColor(BaseColorId).ToString("F3") : "(none)";
        string emis = mat.HasProperty("_EmissionColor") ? mat.GetColor("_EmissionColor").ToString("F3") : "(none)";
        string metal = mat.HasProperty("_Metallic") ? mat.GetFloat("_Metallic").ToString("F3") : "(none)";
        string dmg = mat.HasProperty(Damage01Id) ? mat.GetFloat(Damage01Id).ToString("F3") : "(none)";

        Debug.Log(
            $"[LungMeshDeformer] RUNTIME MATERIAL STATE
" +
            $"  shader        : {mat.shader.name}
" +
            $"  _BaseMap      : {baseMap}
" +
            $"  _BaseColor    : {tint}
" +
            $"  _EmissionColor: {emis}
" +
            $"  _Metallic     : {metal}
" +
            $"  _Damage01     : {dmg}   (smokingYears {smokingYears:F1}, cigs/day {cigarettesPerDay:F0}, pack-years {PackYears:F1})
" +
            $"  gpuDamage     : {gpuDamageActive}   gpuBreathing: {gpuBreathingActive}");
    }

    void Update()
    {
        if (bioGearsSystem == null) return;
        if (!bioGearsSystem.IsRunning) return;

        if (mesh != null && originalVertices != null)
        {
            // ExpansionFactor is a NORMALISED 0-1 breathing waveform, so on its own it
            // looks identical whether the patient is moving 160 mL or 1600 mL per
            // breath. That is why asthma / COPD / exercise appeared to do nothing:
            // BioGears was computing them correctly, but the only value the lung read
            // discarded the magnitude and kept just the rhythm.
            //
            // Scaling by the tidal volume BioGears reports restores the missing link.
            // This reads the resulting respiratory state - not the slider - so the
            // intended UI -> BioGears -> state -> visual chain is preserved, and the
            // breathing motion itself (uniform radial, smoothstep waveform) is
            // untouched: this only affects how far each breath travels.
            float amplitude = maxExpansion;
            if (referenceTidalVolume > 1f)
            {
                float tidal = bioGearsSystem.TidalVolume;
                if (tidal > 0f)
                    amplitude *= Mathf.Clamp(tidal / referenceTidalVolume, 0.3f, 2f);
            }

            float expansion = bioGearsSystem.ExpansionFactor * amplitude;

            if (gpuBreathingActive)
            {
                // One float per frame. The vertex shader does the identical
                // normalize(pos - centre) * axisScale * expansion, so the motion is
                // unchanged - but the 730k-iteration loop and, more importantly, the
                // full vertex-buffer re-upload below are both gone. That upload was
                // ~8.8 MB per frame; on a Quest it was the single most expensive thing
                // this component did.
                for (int i = 0; i < lungMaterials.Length; i++)
                {
                    if (lungMaterials[i] != null)
                        lungMaterials[i].SetFloat(BreathExpansionId, expansion);
                }
            }
            else
            {
                for (int i = 0; i < originalVertices.Length; i++)
                {
                    Vector3 dir = directions[i];
                    // Apply per-axis scaling for realistic asymmetric expansion
                    Vector3 scaledDir = new Vector3(
                        dir.x * expansionScale.x,
                        dir.y * expansionScale.y,
                        dir.z * expansionScale.z
                    );
                    displacedVertices[i] = originalVertices[i] + scaledDir * expansion;
                }

                mesh.vertices = displacedVertices;
                // RecalculateBounds() walks all 700k+ vertices a second time every frame
                // purely to produce a box we can derive analytically: displacement is
                // bounded by maxExpansion * expansionScale, so a one-off inflated bounds
                // (computed in Start) stays correct for every breathing phase and keeps
                // culling working. Cheap win on a Quest frame budget.
                mesh.bounds = deformedBounds;
            }
        }

        if (!gpuDamageActive)
        {
            ApplySmokingColor();
            ApplyBreathingMaterialResponse();
        }

        if (useOverlayMesh)
            UpdateOverlayMesh();
    }

    /// <summary>
    /// The smoking stain is painted into a texture, which is only visible if the mesh
    /// has usable UVs. Meshes produced by CT segmentation (marching cubes) generally
    /// carry no UV map at all - every vertex then samples the same texel, so the whole
    /// organ shifts by a single flat colour, which is exactly why the effect read as
    /// "a shadow cast onto the lung" instead of pigment in the tissue.
    ///
    /// This unwraps the lung cylindrically around its true superior-inferior axis,
    /// which also makes the existing apex / airway / peripheral biases anatomically
    /// meaningful: v now genuinely runs base-to-apex and u runs around the organ.
    /// Runs once at startup and leaves an existing UV map untouched.
    /// </summary>
    void EnsureStainUVs(Vector3 localUp, float minH, float maxH, Vector3 center)
    {
        Vector2[] existing = mesh.uv;
        bool hasUsableUVs = false;

        if (existing != null && existing.Length == originalVertices.Length)
        {
            // A UV set that is present but entirely degenerate (all-zero, as exported
            // by most segmentation tools) is no better than none at all.
            for (int i = 0; i < existing.Length; i += Mathf.Max(1, existing.Length / 512))
            {
                if (existing[i].sqrMagnitude > 1e-8f) { hasUsableUVs = true; break; }
            }
        }

        if (hasUsableUVs)
        {
            if (logTargetRenderers)
                Debug.Log("LungMeshDeformer: Mesh already has UVs, keeping them for the smoking stain.");
            return;
        }

        // Build an orthonormal frame around the breathing axis so the angular sweep is
        // stable regardless of how the model happens to be oriented.
        Vector3 axis = localUp.normalized;
        Vector3 refVec = Mathf.Abs(Vector3.Dot(axis, Vector3.right)) < 0.9f ? Vector3.right : Vector3.forward;
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(axis, refVec));
        Vector3 bitangent = Vector3.Cross(axis, tangent);

        float invRange = 1f / Mathf.Max(1e-4f, maxH - minH);
        Vector2[] uvs = new Vector2[originalVertices.Length];

        for (int i = 0; i < originalVertices.Length; i++)
        {
            Vector3 rel = originalVertices[i] - center;
            float u = Mathf.Atan2(Vector3.Dot(rel, bitangent), Vector3.Dot(rel, tangent)) / (2f * Mathf.PI) + 0.5f;
            float v = Mathf.Clamp01((Vector3.Dot(originalVertices[i], axis) - minH) * invRange);
            uvs[i] = new Vector2(u, v);
        }

        mesh.uv = uvs;
        Debug.Log($"LungMeshDeformer: Mesh had no usable UVs - generated a cylindrical unwrap for {uvs.Length} vertices so the smoking stain varies across the tissue.");
    }

    /// <summary>
    /// Decides whether breathing runs in the vertex shader or on the CPU, and if on the
    /// GPU, uploads the two constants the shader needs (they never change at runtime).
    ///
    /// The CPU path is kept as an automatic fallback: the smoking overlay, the simulated
    /// fallback material, or any material that is not BioGears/LungTissue will not have
    /// these properties, and silently doing nothing would look like the lung had stopped
    /// breathing.
    /// </summary>
    /// <summary>
    /// Must run BEFORE InitializeSmokingTexture: that method replaces _BaseMap with its
    /// own CPU-painted stain texture, which would overwrite the baked tissue albedo the
    /// shader damage model renders on top of.
    /// </summary>
    void DetectShaderDamageSupport()
    {
        // Smoking damage is independent of breathing: a material may support one and
        // not the other, so it is detected separately.
        gpuDamageActive = false;
        if (lungMaterials != null)
        {
            bool allSupport = lungMaterials.Length > 0;
            foreach (Material m in lungMaterials)
                if (m == null || !m.HasProperty(Damage01Id)) { allSupport = false; break; }

            if (allSupport)
            {
                gpuDamageActive = true;
                PushDamageToShader();
                Debug.Log($"LungMeshDeformer: Shader-based smoking damage active (pack-years {PackYears:F1} -> severity {DamageSeverity01:F2}). " +
                          "The per-drag 512x512 CPU texture repaint is skipped entirely.");
            }
        }
    }

    void SetupGpuBreathing()
    {
        gpuBreathingActive = false;


        if (!useGpuBreathing || mesh == null || lungMaterials == null || lungMaterials.Length == 0)
            return;

        // Every targeted material must support it - a mix would tear the mesh apart,
        // with some submeshes inflating and others static.
        foreach (Material mat in lungMaterials)
        {
            if (mat == null || !mat.HasProperty(BreathExpansionId))
            {
                Debug.Log("LungMeshDeformer: A target material has no _BreathExpansion property, using the CPU breathing path.");
                return;
            }
        }

        // The shader recomputes direction from the vertex position, so it needs the same
        // centre the CPU path derived its directions from.
        Vector3 centre = mesh.bounds.center;
        foreach (Material mat in lungMaterials)
        {
            mat.SetVector(BreathCentreId, new Vector4(centre.x, centre.y, centre.z, 0f));
            mat.SetVector(BreathAxisScaleId, new Vector4(expansionScale.x, expansionScale.y, expansionScale.z, 0f));
            mat.SetFloat(BreathExpansionId, 0f);
        }

        gpuBreathingActive = true;

        // The scratch buffers only exist to stage CPU deformation. On this mesh they are
        // ~17.6 MB of managed memory that would otherwise sit untouched for the whole
        // session - worth reclaiming on a headset. originalVertices stays: Update() uses
        // it as its "is initialised" check.
        int vertexCount = originalVertices.Length;
        displacedVertices = null;
        directions = null;

        Debug.Log($"LungMeshDeformer: GPU breathing active - the per-frame {vertexCount:N0}-vertex CPU deformation and vertex-buffer upload are both skipped, and ~{(vertexCount * 24L * 2L) / (1024 * 1024)} MB of staging buffers released.");
    }

    void CacheTargetMaterials()
    {
        List<Material> collectedMaterials = new List<Material>();
        HashSet<Material> seen = new HashSet<Material>();
        List<string> targetNames = new List<string>();

        List<Renderer> rendererPool = new List<Renderer>();

        if (explicitTargetRenderers != null && explicitTargetRenderers.Length > 0)
        {
            rendererPool.AddRange(explicitTargetRenderers);
        }
        else
        {
            Renderer[] localRenderers = applyToChildRenderers
                ? GetComponentsInChildren<Renderer>(true)
                : GetComponents<Renderer>();

            if (localRenderers != null && localRenderers.Length > 0)
                rendererPool.AddRange(localRenderers);

            if (rendererPool.Count == 0 && searchSceneForLungRenderers)
            {
                Renderer[] allRenderers = FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < allRenderers.Length; i++)
                {
                    Renderer candidate = allRenderers[i];
                    if (candidate == null) continue;
                    if (LooksLikeLungRenderer(candidate))
                        rendererPool.Add(candidate);
                }
            }
        }

        targetRenderers = rendererPool.ToArray();

        for (int i = 0; i < targetRenderers.Length; i++)
        {
            Renderer rendererRef = targetRenderers[i];
            if (rendererRef == null) continue;

            if (logTargetRenderers)
                targetNames.Add(rendererRef.name);

            Material[] rendererMaterials = rendererRef.materials;
            for (int m = 0; m < rendererMaterials.Length; m++)
            {
                Material mat = rendererMaterials[m];
                if (mat == null || seen.Contains(mat)) continue;

                seen.Add(mat);
                collectedMaterials.Add(mat);
            }
        }

        lungMaterials = collectedMaterials.ToArray();

        if (logTargetRenderers)
        {
            Debug.Log($"LungMeshDeformer: Found {targetRenderers.Length} renderers and {lungMaterials.Length} compatible materials. Renderers = [{string.Join(", ", targetNames)}]");
            for (int i = 0; i < lungMaterials.Length; i++)
            {
                Material mat = lungMaterials[i];
                if (mat != null)
                    Debug.Log($"LungMeshDeformer: Target material {i}: {mat.name}");
            }
        }
    }

    bool LooksLikeLungRenderer(Renderer rendererRef)
    {
        if (rendererRef == null) return false;

        string rendererName = rendererRef.name.ToLowerInvariant();
        if (rendererName.Contains("lung")) return true;

        Transform current = rendererRef.transform;
        while (current != null)
        {
            string objectName = current.name.ToLowerInvariant();
            if (objectName.Contains("lung")) return true;
            current = current.parent;
        }

        Material[] rendererMaterials = rendererRef.sharedMaterials;
        for (int i = 0; i < rendererMaterials.Length; i++)
        {
            Material mat = rendererMaterials[i];
            if (mat == null) continue;

            string materialName = mat.name.ToLowerInvariant();
            if (materialName.Contains("lung")) return true;
        }

        return false;
    }

    /// <summary>
    /// Cumulative exposure in pack-years: (cigarettes per day / 20) x years smoked.
    /// This is the standard epidemiological measure, and it is why years alone is not
    /// enough - 40/day for 10 years is four times the exposure of 10/day for 10 years.
    /// </summary>
    public float PackYears => (Mathf.Max(0f, cigarettesPerDay) / 20f) * Mathf.Max(0f, smokingYears);

    /// <summary>
    /// Maps pack-years onto the 0-1 visual severity the shader consumes.
    ///
    /// Deliberately a saturating curve, NOT a linear "x% damaged per year". Real damage
    /// accumulates with diminishing visible return and varies enormously between
    /// individuals; this drives an educational visualisation, not a clinical estimate.
    /// </summary>
    public float DamageSeverity01
    {
        get
        {
            float pk = PackYears;
            if (pk <= 0.01f) return 0f;
            // Reaches ~0.95 near packYearsAtMaxDamage, then flattens.
            return Mathf.Clamp01(1f - Mathf.Exp(-3f * pk / Mathf.Max(1f, packYearsAtMaxDamage)));
        }
    }

    /// <summary>Descriptive stage for the UI. Illustrative, not diagnostic.</summary>
    public string DamageStageLabel
    {
        get
        {
            float pk = PackYears;
            if (pk <= 0.01f) return "Healthy - no smoking history";
            if (pk < 2.5f) return "Minimal cumulative exposure";
            if (pk < 5f) return "Low cumulative exposure";
            if (pk < 10f) return "Moderate cumulative exposure";
            if (pk <= 20f) return "High cumulative exposure";   // 20/day x 20yr = 20 pack-years
            return "Very high cumulative exposure";
        }
    }

    public void SetSmokingExposure(float years, float cigsPerDay)
    {
        cigarettesPerDay = Mathf.Max(0f, cigsPerDay);
        SetSmokingYears(years);
    }

    public void SetSmokingYears(float years)
    {
        smokingYears = Mathf.Max(0f, years);

        if (gpuDamageActive)
        {
            PushDamageToShader();
            return;
        }
        ApplySmokingColor(true);
    }

    void PushDamageToShader()
    {
        if (lungMaterials == null) return;

        float severity = DamageSeverity01;
        for (int i = 0; i < lungMaterials.Length; i++)
        {
            if (lungMaterials[i] != null)
                lungMaterials[i].SetFloat(Damage01Id, severity);
        }
    }

    void InitializeSmokingTexture()
    {
        if (lungMaterials == null || lungMaterials.Length == 0) return;

        Texture2D sourceTexture = null;
        Material sourceMaterial = lungMaterials[Mathf.Clamp(primaryMaterialIndex, 0, lungMaterials.Length - 1)];

        if (sourceMaterial != null)
        {
            if (sourceMaterial.HasProperty("_BaseMap"))
                sourceTexture = sourceMaterial.GetTexture("_BaseMap") as Texture2D;
            if (sourceTexture == null && sourceMaterial.HasProperty("_MainTex"))
                sourceTexture = sourceMaterial.GetTexture("_MainTex") as Texture2D;
            if (sourceTexture == null)
                sourceTexture = sourceMaterial.mainTexture as Texture2D;
        }

        stainWidth = stainTextureResolution;
        stainHeight = stainTextureResolution;

        if (sourceTexture != null && sourceTexture.isReadable)
        {
            stainWidth = Mathf.Clamp(sourceTexture.width, 128, 1024);
            stainHeight = Mathf.Clamp(sourceTexture.height, 128, 1024);
        }

        // Mipmaps matter here: this texture replaces _BaseMap at runtime, and the baked
        // tissue albedo it now carries has fine capillary speckle. Without a mip chain
        // that detail aliases into shimmering crawl in a headset, which is far more
        // distracting in stereo than on a monitor. Anisotropic filtering keeps it from
        // smearing where the surface curves away at grazing angles.
        dynamicStainTexture = new Texture2D(stainWidth, stainHeight, TextureFormat.RGBA32, true);
        dynamicStainTexture.wrapMode = TextureWrapMode.Repeat;
        dynamicStainTexture.filterMode = FilterMode.Trilinear;
        dynamicStainTexture.anisoLevel = 4;

        healthyPixels = new Color[stainWidth * stainHeight];
        workingPixels = new Color[stainWidth * stainHeight];
        sourcePixels = new Color[stainWidth * stainHeight];
        stainField = new float[stainWidth * stainHeight];
        speckField = new float[stainWidth * stainHeight];
        airwayField = new float[stainWidth * stainHeight];
        apexField = new float[stainWidth * stainHeight];
        peripheralField = new float[stainWidth * stainHeight];
        asymmetryField = new float[stainWidth * stainHeight];
        sootCoreField = new float[stainWidth * stainHeight];
        fbmField = new float[stainWidth * stainHeight];
        ridgeField = new float[stainWidth * stainHeight];

        for (int y = 0; y < stainHeight; y++)
        {
            float v = y / (float)(stainHeight - 1);

            for (int x = 0; x < stainWidth; x++)
            {
                float u = x / (float)(stainWidth - 1);
                int idx = y * stainWidth + x;

                Color baseColor = healthyLungColor;
                if (sourceTexture != null && sourceTexture.isReadable)
                    baseColor = sourceTexture.GetPixelBilinear(u, v);
                else
                    baseColor = baselineTint;

                healthyPixels[idx] = baseColor;
                sourcePixels[idx] = baseColor;
                workingPixels[idx] = baseColor;

                float n1 = Mathf.PerlinNoise(u * primaryPatchScale + stainSeed, v * primaryPatchScale + stainSeed * 0.7f);
                float n2 = Mathf.PerlinNoise(u * secondaryPatchScale - stainSeed * 0.3f, v * secondaryPatchScale + stainSeed * 1.1f);
                float shapeNoise = n1 * 0.72f + n2 * 0.28f;

                float upperLobeBias = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.20f, 0.95f, v));
                float centralBias = 1f - Mathf.Abs((u - 0.5f) * 2f);
                float apexBias = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.70f, 1f, v));
                float peripheralBias = Mathf.SmoothStep(0f, 1f, Mathf.Abs((u - 0.5f) * 2f));

                float airway = Mathf.Exp(-Mathf.Pow((u - 0.5f) / 0.16f, 2f)) * Mathf.SmoothStep(0.15f, 0.95f, v);
                float lobeAsymmetry = Mathf.PerlinNoise(u * 1.8f + stainSeed * 0.25f, v * 1.5f - stainSeed * 0.2f);
                lobeAsymmetry = Mathf.Lerp(0.75f, 1.25f, lobeAsymmetry);
                float sootCore = Mathf.PerlinNoise(u * 26f + stainSeed * 2.7f, v * 26f - stainSeed * 1.9f);
                sootCore = Mathf.Clamp01(Mathf.Pow(sootCore, 1.7f));

                float anatomicalBias = upperLobeBias * 0.28f + centralBias * 0.10f + apexBias * 0.18f + airway * 0.18f;

                stainField[idx] = Mathf.Clamp01(shapeNoise * 0.85f + anatomicalBias);

                float speck = Mathf.PerlinNoise(u * 110f + stainSeed * 4.2f, v * 110f - stainSeed * 3.7f);
                speckField[idx] = speck;

                // Multi-octave fBm: gives the stain self-similar detail at every scale
                // so it looks like material deposited in tissue rather than a soft blob.
                float fbm = 0f;
                float amplitude = 0.5f;
                float frequency = 3.1f;
                float normalizer = 0f;
                for (int octave = 0; octave < 4; octave++)
                {
                    fbm += amplitude * Mathf.PerlinNoise(u * frequency + stainSeed * 1.3f, v * frequency - stainSeed * 0.7f);
                    normalizer += amplitude;
                    frequency *= 2.07f;   // non-integer to avoid visible tiling
                    amplitude *= 0.5f;
                }
                fbmField[idx] = Mathf.Clamp01(fbm / Mathf.Max(1e-4f, normalizer));

                // Ridged noise -> thin branching lines. Anthracotic pigment collects
                // along interlobular septa and lymphatics, giving smokers' lungs their
                // characteristic dark "net" over paler tissue.
                float r = Mathf.PerlinNoise(u * 15f + stainSeed * 3.1f, v * 15f + stainSeed * 2.3f);
                float ridge = 1f - Mathf.Abs(r * 2f - 1f);
                ridgeField[idx] = Mathf.Pow(Mathf.Clamp01(ridge), 3f);
                airwayField[idx] = Mathf.Clamp01(airway);
                apexField[idx] = Mathf.Clamp01(apexBias);
                peripheralField[idx] = Mathf.Clamp01(peripheralBias);
                asymmetryField[idx] = lobeAsymmetry;
                sootCoreField[idx] = sootCore;
            }
        }

        dynamicStainTexture.SetPixels(healthyPixels);
        dynamicStainTexture.Apply();

        for (int i = 0; i < lungMaterials.Length; i++)
        {
            Material mat = lungMaterials[i];
            if (mat == null) continue;

            mat.mainTexture = dynamicStainTexture;

            if (mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", dynamicStainTexture);
            if (mat.HasProperty("_MainTex"))
                mat.SetTexture("_MainTex", dynamicStainTexture);
            if (mat.HasProperty("_BaseColorMap"))
                mat.SetTexture("_BaseColorMap", dynamicStainTexture);

            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", Color.white);
            if (mat.HasProperty("_EmissionColor"))
                mat.SetColor("_EmissionColor", new Color(0.02f, 0.0f, 0.0f));
        }
    }

    void InitializeOverlayMesh()
    {
        if (!useOverlayMesh) return;

        Renderer sourceRenderer = overlayTargetRenderer != null ? overlayTargetRenderer : FindBestOverlayRenderer();
        if (sourceRenderer == null)
        {
            if (logTargetRenderers)
                Debug.LogWarning("LungMeshDeformer: No overlay renderer found.");
            return;
        }

        Mesh sourceMesh = null;
        bool isSkinned = sourceRenderer is SkinnedMeshRenderer;

        if (isSkinned)
        {
            SkinnedMeshRenderer skinned = sourceRenderer as SkinnedMeshRenderer;
            if (skinned != null && skinned.sharedMesh != null)
            {
                sourceMesh = Instantiate(skinned.sharedMesh);
                overlayMesh = sourceMesh;
            }
        }
        else
        {
            MeshFilter sourceFilter = sourceRenderer.GetComponent<MeshFilter>();
            if (sourceFilter != null && sourceFilter.sharedMesh != null)
            {
                sourceMesh = Instantiate(sourceFilter.sharedMesh);
                overlayMesh = sourceMesh;
            }
        }

        if (sourceMesh == null)
        {
            if (logTargetRenderers)
                Debug.LogWarning($"LungMeshDeformer: Could not get a mesh from renderer {sourceRenderer.name}.");
            return;
        }

        // Resolve the shader BEFORE creating anything. Shader.Find only sees shaders
        // that actually got compiled into the player, so a name that resolves fine in
        // the Editor can come back null in a Quest build. A null shader (or a renderer
        // left with no material) produces Unity's magenta "missing shader" material -
        // and because this overlay is a full duplicate of the lung mesh, that reads as
        // a solid pink lung floating next to the real one.
        // "Standard" is deliberately NOT used as a fallback: it belongs to the Built-in
        // pipeline and renders magenta under URP even when Shader.Find does return it.
        bool usingScriptableRenderPipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null;

        Shader overlayShader = Shader.Find("Universal Render Pipeline/Unlit");
        if (overlayShader == null)
            overlayShader = Shader.Find("Universal Render Pipeline/Simple Lit");
        if (overlayShader == null && !usingScriptableRenderPipeline)
            overlayShader = Shader.Find("Unlit/Texture");

        if (overlayShader == null)
        {
            // Better to ship no soot overlay than a magenta lung. The smoking visual
            // still works: ApplySmokingColor writes dynamicStainTexture straight into
            // the lung's own material, and the overlay is only an alternate render path.
            Debug.LogWarning("LungMeshDeformer: No overlay-compatible shader available in this build, skipping the soot overlay. " +
                             "Add 'Universal Render Pipeline/Unlit' to Project Settings > Graphics > Always Included Shaders to enable it.");
            Destroy(overlayMesh);
            overlayMesh = null;
            return;
        }

        overlayObject = new GameObject("LungSootOverlay");
        overlayObject.transform.SetParent(sourceRenderer.transform, false);
        overlayObject.transform.localPosition = Vector3.zero;
        overlayObject.transform.localRotation = Quaternion.identity;
        overlayObject.transform.localScale = Vector3.one * overlayScale;

        MeshFilter overlayFilter = overlayObject.AddComponent<MeshFilter>();
        overlayFilter.sharedMesh = overlayMesh;

        overlayRenderer = overlayObject.AddComponent<MeshRenderer>();

        Material overlayMaterial = new Material(overlayShader);
        overlayMaterial.name = "LungSootOverlayMaterial";
        overlayMaterial.hideFlags = HideFlags.DontSave;

        if (overlayMaterial.HasProperty("_Surface"))
            overlayMaterial.SetFloat("_Surface", 1f);
        if (overlayMaterial.HasProperty("_SurfaceType"))
            overlayMaterial.SetFloat("_SurfaceType", 1f);
        if (overlayMaterial.HasProperty("_SrcBlend"))
            overlayMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (overlayMaterial.HasProperty("_DstBlend"))
            overlayMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (overlayMaterial.HasProperty("_ZWrite"))
            overlayMaterial.SetFloat("_ZWrite", 0f);
        if (overlayMaterial.HasProperty("_Cull"))
            overlayMaterial.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);

        overlayRenderer.sharedMaterial = overlayMaterial;
        overlayRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        overlayRenderer.receiveShadows = false;
        overlayRenderer.enabled = true;

        UpdateOverlayMaterial();

        if (logTargetRenderers)
            Debug.Log($"LungMeshDeformer: Created overlay on {sourceRenderer.name} using mesh {sourceMesh.name}");
    }

    Renderer FindBestOverlayRenderer()
    {
        if (targetRenderers != null && targetRenderers.Length > 0)
            return targetRenderers[0];

        Renderer[] allRenderers = FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < allRenderers.Length; i++)
        {
            Renderer candidate = allRenderers[i];
            if (candidate == null) continue;
            if (LooksLikeLungRenderer(candidate))
                return candidate;
        }

        return null;
    }

    void UpdateOverlayMesh()
    {
        if (!useOverlayMesh || overlayRenderer == null || overlayMesh == null) return;

        if (overlayTargetRenderer is SkinnedMeshRenderer skinned)
        {
            skinned.BakeMesh(overlayMesh);
        }

        UpdateOverlayMaterial();
    }

    void UpdateOverlayMaterial()
    {
        if (overlayRenderer == null || overlayRenderer.sharedMaterial == null || workingPixels == null || dynamicStainTexture == null) return;

        float effectiveYears = smokingYears * Mathf.Max(0.01f, exposureMultiplier);
        float t = Mathf.Clamp01(effectiveYears / Mathf.Max(0.01f, maxSmokingYears));
        float opacity = Mathf.Lerp(0.12f, overlayOpacity, Mathf.Pow(t, 0.72f));

        if (overlayTexture == null || overlayTexture.width != stainWidth || overlayTexture.height != stainHeight)
        {
            if (overlayTexture != null)
                Destroy(overlayTexture);

            overlayTexture = new Texture2D(stainWidth, stainHeight, TextureFormat.RGBA32, false);
            overlayTexture.wrapMode = TextureWrapMode.Clamp;
            overlayTexture.filterMode = FilterMode.Bilinear;
        }

        Color[] overlayPixels = new Color[workingPixels.Length];
        for (int i = 0; i < workingPixels.Length; i++)
        {
            Color c = workingPixels[i];
            float luminance = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
            float alpha = Mathf.Clamp01((1f - luminance) * opacity);
            Color sootColor = debugMaskPreview ? debugDamagedColor : Color.Lerp(midStainColor, smokerLungColor, Mathf.Clamp01(t * 1.25f));
            overlayPixels[i] = new Color(sootColor.r, sootColor.g, sootColor.b, alpha);
        }

        overlayTexture.SetPixels(overlayPixels);
        overlayTexture.Apply(false, false);

        Material mat = overlayRenderer.sharedMaterial;
        if (mat == null) return;

        mat.mainTexture = overlayTexture;

        if (mat.HasProperty(MainTexId))
            mat.SetTexture(MainTexId, overlayTexture);
        if (mat.HasProperty(BaseMapId))
            mat.SetTexture(BaseMapId, overlayTexture);
        if (mat.HasProperty(ColorId))
            mat.SetColor(ColorId, new Color(1f, 1f, 1f, 1f));
        if (mat.HasProperty(BaseColorId))
            mat.SetColor(BaseColorId, new Color(1f, 1f, 1f, 1f));
        if (mat.HasProperty(CutoffId))
            mat.SetFloat(CutoffId, 0.5f);
    }

    void ApplySmokingColor(bool force = false)
    {
        if (lungMaterials == null || lungMaterials.Length == 0 || dynamicStainTexture == null || healthyPixels == null || workingPixels == null) return;
        if (!force && Mathf.Approximately(lastAppliedSmokingYears, smokingYears)) return;

        float effectiveYears = smokingYears * Mathf.Max(0.01f, exposureMultiplier);
        float t = Mathf.Clamp01(effectiveYears / Mathf.Max(0.01f, maxSmokingYears));
        // Front-load the curve so early years are already visible: the change from 0 to
        // 5 years should read as "something is starting", not as nothing at all.
        t = Mathf.Pow(smokingColorCurve.Evaluate(t), 0.62f);

        // Disease progression: early sparse anthracotic specks -> clustered upper/apical patches -> confluent blackening.
        float patchCoverage = Mathf.Lerp(0.03f, 0.99f, Mathf.Pow(t, 1.08f));
        float patchThreshold = 1f - patchCoverage;
        // Tighter than before: soft edges blur deposits into a haze that reads as a
        // shadow, whereas real tar accumulation has discernible boundaries.
        float edgeSoftness = Mathf.Lerp(0.02f, 0.10f, t);
        float patchStrength = Mathf.Lerp(0.60f, 1f, Mathf.Pow(t, 0.84f));
        float baselineDiscoloration = Mathf.Lerp(0f, 0.20f, t);
        float fibrosisAmount = Mathf.SmoothStep(0.30f, 1f, t) * 0.52f;

        Color targetStainColor = Color.Lerp(midStainColor, smokerLungColor, Mathf.SmoothStep(0.0f, 1f, t));
        Color sootBlack = Color.Lerp(new Color(0.06f, 0.05f, 0.04f), smokerLungColor, Mathf.Clamp01(t * 1.2f));

        for (int i = 0; i < workingPixels.Length; i++)
        {
            // Fractal mottling now drives the spatial field. The smooth anatomical
            // gradients are retained (upper lobes really are affected first) but at a
            // fraction of their old weight: at full strength they formed a soft
            // luminance ramp across the whole organ, which is precisely what made the
            // effect look like a shadow projected onto the lung rather than pigment
            // deposited inside it.
            float field = Mathf.Lerp(stainField[i], fbmField[i], 0.65f);
            float verticalBias = invertVerticalAnatomy ? (1f - apexField[i]) : apexField[i];

            float anatomyBoost =
                verticalBias * 0.14f +
                airwayField[i] * 0.10f +
                peripheralField[i] * 0.04f;

            field = Mathf.Clamp01(field + anatomyBoost * t);
            field *= asymmetryField[i];

            // Confluent tar patches. Deposits have edges, so keep the transition tight.
            float patchMask = SmoothStep(patchThreshold - edgeSoftness, patchThreshold + edgeSoftness, field);

            // Fine carbon particulate - the earliest visible sign, present well before
            // any confluent blackening.
            float speckThreshold = Mathf.Lerp(0.93f, 0.62f, t);
            float speckMask = SmoothStep(speckThreshold, 1f, speckField[i]) * Mathf.Lerp(0.7f, 1f, t);

            // Anthracotic pigment tracking along interlobular septa - the dark "net"
            // over paler tissue that makes a smoker's lung instantly recognisable.
            float septalMask = ridgeField[i] * Mathf.SmoothStep(0.05f, 0.65f, t);

            float sootMask = SmoothStep(0.42f, 1f, sootCoreField[i]) * Mathf.SmoothStep(0.10f, 1f, t);
            float totalMask = Mathf.Clamp01(patchMask + speckMask * 0.85f + septalMask * 0.55f + sootMask * 0.45f);

            Color healthy = healthyPixels[i];
            Color original = sourcePixels != null && sourcePixels.Length == healthyPixels.Length ? sourcePixels[i] : healthy;

            Color fibroticTint = Color.Lerp(healthy, fibroticColor, fibrosisAmount * (airwayField[i] * 0.7f + apexField[i] * 0.3f));
            Color stained = Color.Lerp(fibroticTint, targetStainColor, Mathf.Pow(totalMask, 1.18f) * patchStrength);
            stained = Color.Lerp(stained, sootBlack, Mathf.Pow(sootMask, 1.05f) * Mathf.Lerp(0.32f, 0.82f, t));

            if (debugMaskPreview)
            {
                Color debugMaskColor = Color.Lerp(debugHealthyColor, debugDamagedColor, Mathf.Clamp01(totalMask));
                debugMaskColor = Color.Lerp(debugMaskColor, Color.black, Mathf.Clamp01(sootMask * 0.9f));
                stained = debugMaskColor;
            }

            // Keep some tissue tone in non-patch areas while globally aging the organ.
            Color agedBase = Color.Lerp(healthy, midStainColor, baselineDiscoloration);
            stained = Color.Lerp(agedBase, stained, Mathf.Lerp(0.22f, 1f, totalMask));

            // Preserve the original material look where smoke has not accumulated.
            stained = Color.Lerp(original, stained, Mathf.Clamp01(totalMask * 1.1f + t * 0.15f));

            // Dark ash buildup should only increase darkness, never brighten the base tissue.
            float originalLuma = original.r * 0.299f + original.g * 0.587f + original.b * 0.114f;
            float stainedLuma = stained.r * 0.299f + stained.g * 0.587f + stained.b * 0.114f;
            if (stainedLuma > originalLuma)
                stained = Color.Lerp(stained, original, 0.7f);

            // Slight desaturation as damage increases, without making healthy zones fully gray.
            float grayscale = stained.r * 0.299f + stained.g * 0.587f + stained.b * 0.114f;
            Color desat = new Color(grayscale, grayscale, grayscale, stained.a);
            stained = Color.Lerp(stained, desat, t * 0.26f);

            // Keep tiny islands of residual pink tissue even in advanced disease.
            float residualPinkMask = SmoothStep(0.00f, 0.05f, 1f - totalMask) * (1f - t) * 0.55f;
            stained = Color.Lerp(stained, healthy, residualPinkMask * 0.75f);

            // Small glossy bronchioles and mixed soot create the mottled smoker-lung texture.
            float highlight = Mathf.SmoothStep(0.62f, 0.95f, speckField[i]) * Mathf.Lerp(0.15f, 0.35f, 1f - t);
            stained = Color.Lerp(stained, original, highlight * 0.15f);

            workingPixels[i] = stained;
        }

        dynamicStainTexture.SetPixels(workingPixels);
        // updateMipmaps: true - the mip chain has to be rebuilt when the stain changes,
        // otherwise distant/oblique views keep showing the previous smoking level.
        dynamicStainTexture.Apply(true, false);

        lastAppliedSmokingYears = smokingYears;
    }

    void ApplyBreathingMaterialResponse()
    {
        if (lungMaterials == null || lungMaterials.Length == 0 || bioGearsSystem == null) return;

        float effectiveYears = smokingYears * Mathf.Max(0.01f, exposureMultiplier);
        float smokeProgress = Mathf.Clamp01(effectiveYears / Mathf.Max(0.01f, maxSmokingYears));
        smokeProgress = smokingColorCurve.Evaluate(smokeProgress);

        float expansion = Mathf.Clamp01(bioGearsSystem.ExpansionFactor);
        float inhaleSheen = Mathf.SmoothStep(0f, 1f, expansion);

        float smoothness = baselineSmoothness;
        smoothness -= chronicDryness * smokeProgress * 0.35f;
        smoothness += inhaleSheen * Mathf.Lerp(0.08f, 0.03f, smokeProgress);
        smoothness = Mathf.Clamp01(smoothness);

        for (int i = 0; i < lungMaterials.Length; i++)
        {
            Material mat = lungMaterials[i];
            if (mat == null) continue;

            // Only rebind when the CPU stain pipeline actually produced a texture. When
            // the shader owns smoking damage this is null, and assigning it would clear
            // _BaseMap every frame - wiping out the baked tissue albedo and leaving the
            // shader sampling its default white. That is what turned the lung bone-white
            // the moment Play was pressed, while it looked correct in edit mode.
            if (dynamicStainTexture != null)
                mat.mainTexture = dynamicStainTexture;

            if (mat.HasProperty("_Smoothness"))
                mat.SetFloat("_Smoothness", smoothness);

            if (mat.HasProperty("_Metallic"))
                mat.SetFloat("_Metallic", Mathf.Clamp01(baselineMetallic + smokeProgress * 0.03f));

            if (mat.HasProperty("_EmissionColor"))
            {
                Color emission = Color.Lerp(new Color(0.01f, 0f, 0f), new Color(0.04f, 0.015f, 0.01f), smokeProgress);
                mat.SetColor("_EmissionColor", emission);
            }
        }
    }

    float SmoothStep(float edge0, float edge1, float x)
    {
        float v = Mathf.InverseLerp(edge0, edge1, x);
        return v * v * (3f - 2f * v);
    }

    void OnDestroy()
    {
        if (dynamicStainTexture != null)
            Destroy(dynamicStainTexture);

        if (overlayTexture != null)
            Destroy(overlayTexture);

        if (overlayMesh != null)
            Destroy(overlayMesh);

        if (overlayObject != null)
            Destroy(overlayObject);

        if (lungMaterials == null || lungMaterials.Length == 0)
            return;

        if (lungMaterials != null)
        {
            HashSet<Material> destroyed = new HashSet<Material>();
            for (int i = 0; i < lungMaterials.Length; i++)
            {
                Material mat = lungMaterials[i];
                if (mat == null || destroyed.Contains(mat)) continue;
                destroyed.Add(mat);
                Destroy(mat);
            }
        }
    }
}
