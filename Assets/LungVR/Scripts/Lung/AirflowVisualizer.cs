using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Shows breathing as air moving through a glowing airway tree:
///
///  * Air is drawn in from a wide cloud above the trachea, flows down the trachea,
///    splits at the carina into the two main bronchi, then into lobar branches (three
///    in the larger lung, two in the smaller - right and left lung anatomy) and on to
///    clusters of alveoli. On exhale it flows back out and spreads as a plume.
///  * Motion is tied to lung volume, not a timer: a particle's distance along its path
///    is (its offset) + (current inflation x travel). The air therefore moves exactly in
///    step with the breathing animation, fastest at peak flow, and reaches deeper when
///    BioGears reports a larger tidal volume. Air left in the airway at end-expiration
///    stays there, as the residual volume does.
///  * Fresh air is blue. Air that reaches the alveoli turns pink (CO2-rich) and carries
///    that colour back out. Red sparks leap from the alveoli at peak inhale: oxygen
///    crossing into the blood, fewer when SpO2 is low.
///  * The bronchial tree glows, with bands of light that travel in and out with the
///    breath.
///  * Asthma narrows the bronchi (bronchoconstriction), tints them amber and makes the
///    flow turbulent. COPD traps some air in the deep lung - it lingers, dim violet, for
///    a few breaths - and turns some alveoli into swollen, dim sacs that barely deflate
///    (emphysema).
///
/// The airway tree is derived from the lung mesh at start (no hand-placed points).
/// </summary>
[RequireComponent(typeof(ParticleSystem))]
public class AirflowVisualizer : MonoBehaviour
{
    public LungMeshDeformer lung;
    public BioGearsNativeRespiratory bio;
    [Tooltip("Material using BioGears/AirflowParticle. Assigned by the scene setup tool so the shader is included in builds.")]
    public Material particleMaterial;

    public bool showAirflow = false;

    [Header("Air")]
    public int parcelCount = 350;
    [Tooltip("World size of an air particle (metres) at the lung's starting scale.")]
    public float particleSize = 0.010f;
    [Tooltip("Stretch moving particles into streaks. Off by default: combined with the spread across the tube it reads as crackling lightning rather than air.")]
    public bool streaks = false;
    [Tooltip("How long a moving particle's streak is per unit of speed (only with streaks on).")]
    public float streakLength = 0.12f;
    [Tooltip("Turns of spiral the air makes on its way down the airway. 0 = straight flow.")]
    public float swirlTurns = 0f;
    [Tooltip("How far across the tube particles spread (0 = single file on the centre line, 1 = full width).")]
    [Range(0f, 1f)] public float tubeSpread = 0.5f;
    public Color freshAirColor = new Color(0.45f, 0.85f, 1.00f, 1f);
    public Color exhaledAirColor = new Color(1.00f, 0.40f, 0.48f, 1f);

    [Header("Flow")]
    [Tooltip("Fraction of the airway path a particle travels in one normal breath.")]
    public float baseTravel = 0.8f;
    [Tooltip("Tidal volume (mL) that gives baseTravel. Larger breaths push air deeper.")]
    public float referenceTidalVolume = 500f;
    [Tooltip("Path fraction beyond which air has reached the gas-exchange region.")]
    public float exchangeDepth = 0.85f;
    [Tooltip("Turbulence added by asthma, as a fraction of the lung's radial size.")]
    public float asthmaTurbulence = 0.02f;

    [Header("Glowing airway tree")]
    public bool showAirwayTree = true;
    public Color treeColor = new Color(0.30f, 0.85f, 1.00f, 1f);
    public Color asthmaTreeColor = new Color(1.00f, 0.55f, 0.15f, 1f);
    [Tooltip("Tree brightness between breaths, and the extra at peak flow.")]
    public float treeIdleBrightness = 0.10f;
    public float treeFlowBrightness = 0.12f;
    [Tooltip("Tube width as a fraction of the airway diameter.")]
    public float treeWidth = 0.5f;
    [Tooltip("How much full-severity asthma narrows the bronchi (the trachea is not affected).")]
    [Range(0f, 1f)] public float asthmaConstriction = 0.55f;
    [Tooltip("Light bands travelling along the tree with the breath. 0 = a smooth, even glow.")]
    public float bandsPerPath = 0f;

    [Header("Alveoli")]
    [Tooltip("Glows per branch end. 1 = a single soft glow; more = a grape-like cluster.")]
    public int alveoliPerTerminal = 1;
    [Tooltip("World size of an alveolar glow (metres) at the lung's starting scale.")]
    public float alveolusSize = 0.028f;
    [Range(0f, 1f)] public float alveolusOpacity = 0.35f;
    public Color trappedAirColor = new Color(0.55f, 0.35f, 0.85f, 1f);
    [Tooltip("Fraction of alveoli that are emphysematous at full COPD severity.")]
    [Range(0f, 1f)] public float maxEmphysemaFraction = 0.6f;

    [Header("Oxygen sparks")]
    [Tooltip("Red sparks leaping from the alveoli at peak inhale (O2 into blood). Off by default - busy.")]
    public bool showOxygenSparks = false;
    public int sparkPool = 160;
    [Tooltip("Sparks per second at peak inhale with normal SpO2.")]
    public float sparkRate = 70f;
    public float sparkLifetime = 0.8f;
    public Color oxygenColor = new Color(1.00f, 0.16f, 0.10f, 1f);

    [Header("COPD air trapping")]
    [Tooltip("Fraction of deep air trapped on each breath at full COPD severity.")]
    [Range(0f, 1f)] public float maxTrappedFraction = 0.45f;
    [Tooltip("Path depth trapped air cannot be exhaled past.")]
    public float trapDepth = 0.72f;

    [Header("Airway geometry (fractions of lung height)")]
    [Tooltip("How far below the lung apex the trachea divides.")]
    public float carinaDrop = 0.28f;
    [Tooltip("How far above the trachea opening the intake cloud starts.")]
    public float intakeHeight = 0.22f;
    public int terminalsPerSide = 14;
    [Tooltip("How far each end point is pulled from the lung surface toward the lung's centre.")]
    [Range(0f, 1f)] public float terminalInset = 0.45f;
    [Tooltip("Curve samples between consecutive branch points.")]
    public int samplesPerSegment = 6;

    // A curved airway path from the intake cloud to one alveolar cluster, in lung
    // object space. Per sample: position, base radius, and how much asthma can
    // constrict it (0 for the trachea, 1 for the bronchi).
    class AirPath
    {
        public Vector3[] points;
        public float[] radius;
        public float[] constrictable;
        public float[] cumulative;
        public float length;
        public int[] controlSample; // sample index of each branch point
        public int terminal;
        public string[] segmentKeys; // shared segments are drawn once
    }

    struct Alveolus { public Vector3 position; public int terminal; public bool emphysema; public float phase; }
    struct Spark { public Vector3 position; public Vector3 velocity; public float age; public bool alive; }

    ParticleSystem airSystem, alveoliSystem;
    ParticleSystem.Particle[] airParticles, alveoliParticles;

    List<AirPath> paths = new List<AirPath>();
    Vector3[] terminals;
    Alveolus[] alveoli;
    Spark[] sparks;
    float radialExtent, tracheaRadius, meanPathLength;
    Vector3 depthAxis;

    // Per air particle.
    float[] offset, exchange, noiseSeed, swirlAngle, spread;
    int[] pathIndex;
    bool[] decided, trapped, hasPrev;
    Vector3[] prevWorld;

    // Glowing tree.
    Transform treeRoot;
    Material treeMaterial;
    readonly List<LineRenderer> treeLines = new List<LineRenderer>();
    readonly List<(AirPath path, int from, int to)> treeSegments = new List<(AirPath, int, int)>();
    readonly List<Vector3[]> treeWorldPoints = new List<Vector3[]>();
    readonly List<float> treeMaxWidth = new List<float>(); // lung object units
    MaterialPropertyBlock block;
    float lastTreeAsthma = -1f;

    static readonly int BandCountId = Shader.PropertyToID("_BandCount");
    static readonly int FlowPhaseId = Shader.PropertyToID("_FlowPhase");
    static readonly int LineModeId = Shader.PropertyToID("_LineMode");

    System.Random rng = new System.Random(1234);
    float initialScale = 1f;
    float lastInflation;
    bool breathDeep;
    float sparkBudget;

    // Not carried across a script reload: the airway paths (a plain C# class) are lost
    // on reload while the per-particle arrays survive, so "ready" must drop with them.
    [System.NonSerialized] bool ready;
    bool started;

    void Awake() => EnsureSystems();

    void EnsureSystems()
    {
        if (airSystem == null)
            airSystem = GetComponent<ParticleSystem>();
        ConfigureSystem(airSystem, Mathf.Max(1, parcelCount) + Mathf.Max(0, sparkPool), stretched: streaks);

        // Alveoli don't move, so they render as plain billboards on their own system -
        // a stretched billboard with no velocity has no direction to stretch along.
        if (alveoliSystem == null)
        {
            Transform existing = transform.Find("Alveoli");
            GameObject alv = existing != null ? existing.gameObject : new GameObject("Alveoli");
            alv.transform.SetParent(transform, false);
            alveoliSystem = alv.GetComponent<ParticleSystem>();
            if (alveoliSystem == null)
                alveoliSystem = alv.AddComponent<ParticleSystem>();
        }
        ConfigureSystem(alveoliSystem, 1024, stretched: false);
    }

    void Start()
    {
        started = true;
        Initialize();
    }

    // Unity calls OnDisable/OnEnable around a script reload in Play mode (e.g. a script
    // saved while playing). Tear down and rebuild, rather than carry on with half the
    // state gone - which is what threw "index out of range" every frame.
    void OnEnable()
    {
        if (started && !ready)
            Initialize();
    }

    void OnDisable() => Teardown();

    /// <summary>Re-derives the airway tree from the lung's current mesh (e.g. after a patient's lung is loaded).</summary>
    public void Rebuild()
    {
        if (!started)
            return; // Start will build it
        Teardown();
        Initialize();
    }

    void Teardown()
    {
        ready = false;
        if (treeRoot != null)
            Destroy(treeRoot.gameObject);
        treeRoot = null;
        if (treeMaterial != null)
            Destroy(treeMaterial);
        treeLines.Clear();
        treeSegments.Clear();
        treeWorldPoints.Clear();
        treeMaxWidth.Clear();
        lastTreeAsthma = -1f;
        lastCopd = -1f;
        paths.Clear();
        if (airSystem != null) airSystem.Clear();
        if (alveoliSystem != null) alveoliSystem.Clear();
    }

    void ConfigureSystem(ParticleSystem ps, int max, bool stretched)
    {
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = ps.main;
        main.loop = true;
        main.playOnAwake = false;
        main.maxParticles = max;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = 1f;
        main.startSpeed = 0f;
        main.gravityModifier = 0f;

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.enabled = false; // positions are written directly every frame
        ParticleSystem.ShapeModule shape = ps.shape;
        shape.enabled = false;

        ParticleSystemRenderer r = ps.GetComponent<ParticleSystemRenderer>();
        r.renderMode = stretched ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
        if (stretched)
        {
            r.velocityScale = streakLength;
            r.lengthScale = 1.2f;
            r.cameraVelocityScale = 0f;
        }
        r.alignment = ParticleSystemRenderSpace.View;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;

        if (particleMaterial == null)
        {
            Shader shader = Shader.Find("BioGears/AirflowParticle");
            if (shader != null)
                particleMaterial = new Material(shader);
            else
                Debug.LogWarning("AirflowVisualizer: no particle material assigned and the AirflowParticle shader was not found - run BioGears > Fix VR Scene Layout.");
        }
        if (particleMaterial != null)
            r.sharedMaterial = particleMaterial;
    }

    void Initialize()
    {
        EnsureSystems();
        paths.Clear();

        if (lung == null) lung = FindFirstObjectByType<LungMeshDeformer>();
        if (bio == null) bio = FindFirstObjectByType<BioGearsNativeRespiratory>();
        if (lung == null || bio == null)
        {
            Debug.LogError("AirflowVisualizer: needs a LungMeshDeformer and a BioGearsNativeRespiratory in the scene.");
            enabled = false;
            return;
        }

        MeshFilter mf = lung.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null || !BuildAirwayTree(mf.sharedMesh))
        {
            Debug.LogError("AirflowVisualizer: could not derive the airway tree from the lung mesh.");
            enabled = false;
            return;
        }

        initialScale = Mathf.Max(1e-6f, lung.transform.lossyScale.x);

        int n = Mathf.Max(1, parcelCount);
        airParticles = new ParticleSystem.Particle[n + Mathf.Max(0, sparkPool)];
        offset = new float[n];
        exchange = new float[n];
        noiseSeed = new float[n];
        swirlAngle = new float[n];
        spread = new float[n];
        pathIndex = new int[n];
        decided = new bool[n];
        trapped = new bool[n];
        hasPrev = new bool[n];
        prevWorld = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            // Spread over the whole path plus the stretch above it, so the airway starts
            // partly filled (residual air) and the rest enters on the first breath.
            offset[i] = Mathf.Lerp(-baseTravel, 1f, Rand());
            pathIndex[i] = rng.Next(paths.Count);
            noiseSeed[i] = Rand() * 100f;
            swirlAngle[i] = Rand() * Mathf.PI * 2f;
            spread[i] = Mathf.Sqrt(Rand()); // uniform over the tube's cross-section
        }

        sparks = new Spark[Mathf.Max(0, sparkPool)];
        alveoliParticles = new ParticleSystem.Particle[alveoli.Length];
        ParticleSystem.MainModule alvMain = alveoliSystem.main;
        alvMain.maxParticles = Mathf.Max(1, alveoli.Length);

        BuildTreeLines();

        airSystem.Play();
        alveoliSystem.Play();
        ready = true;
    }

    float Rand() => (float)rng.NextDouble();

    public void SetShowAirflow(bool on) => showAirflow = on;

    void Update()
    {
        if (!ready || paths.Count == 0)
            return;

        bool visible = showAirflow && bio.IsRunning;
        if (treeRoot != null)
            treeRoot.gameObject.SetActive(visible && showAirwayTree);

        if (!visible)
        {
            if (airSystem.particleCount > 0) airSystem.SetParticles(airParticles, 0);
            if (alveoliSystem.particleCount > 0) alveoliSystem.SetParticles(alveoliParticles, 0);
            return;
        }

        Transform lungT = lung.transform;
        float dt = Mathf.Max(1e-4f, Time.deltaTime);
        float scaleRatio = lungT.lossyScale.x / initialScale;

        // ---- breath state ----
        float inflation = bio.ExpansionFactor;
        float inflationRate = (inflation - lastInflation) / dt;
        lastInflation = inflation;
        float rate = Mathf.Max(4f, bio.RespirationRate);
        // ~1 at peak flow for a normal breath, ~0 in the pause between breaths.
        float flow01 = Mathf.Clamp01(Mathf.Abs(inflationRate) * (60f / rate) * 0.3f);

        float travel = baseTravel;
        if (referenceTidalVolume > 1f && bio.TidalVolume > 0f)
            travel *= Mathf.Clamp(bio.TidalVolume / referenceTidalVolume, 0.3f, 2f);
        // How well the deep lung is ventilated, for alveolar brightness and sparks.
        float ventilation = Mathf.Clamp(travel / baseTravel, 0.3f, 1.3f);

        float asthma = lung.asthmaSeverity;
        float copd = lung.DamageSeverity01;

        // End of a breath: give trapped air a chance to escape on the next one. Worse
        // COPD holds it for more breaths.
        if (inflation > 0.5f) breathDeep = true;
        if (breathDeep && inflation < 0.05f)
        {
            breathDeep = false;
            float release = Mathf.Lerp(0.6f, 0.15f, copd);
            for (int i = 0; i < trapped.Length; i++)
            {
                if (trapped[i] && Rand() < release)
                {
                    trapped[i] = false;
                    offset[i] = trapDepth - inflation * travel; // stay put, no jump
                }
            }
        }

        // ---- air ----
        float turbulence = asthma * asthmaTurbulence * radialExtent;
        float t = Time.time;
        // A gentle lift at peak flow; any more and the air visibly flickers each breath.
        float brightness = 0.85f + 0.15f * flow01;
        int count = 0;

        for (int i = 0; i < offset.Length; i++)
        {
            float s = offset[i] + inflation * travel;

            // Fully breathed out and above the airway: becomes fresh air waiting to come
            // in on the next breath, down a randomly chosen branch.
            if (s < 0f && inflation < 0.05f && !trapped[i])
            {
                offset[i] = -travel * Rand();
                pathIndex[i] = rng.Next(paths.Count);
                exchange[i] = 0f;
                decided[i] = false;
                hasPrev[i] = false;
                continue;
            }

            if (trapped[i])
                s = Mathf.Max(s, trapDepth);

            if (s > exchangeDepth)
            {
                exchange[i] = Mathf.MoveTowards(exchange[i], 1f, dt * 2.5f);
                if (!decided[i])
                {
                    decided[i] = true;
                    trapped[i] = Rand() < copd * maxTrappedFraction;
                }
            }

            if (s <= 0f || s >= 1f)
            {
                hasPrev[i] = false;
                continue;
            }

            AirPath path = paths[pathIndex[i]];
            Evaluate(path, s, out Vector3 pos, out Vector3 tangent, out float radius, out float constrict);
            radius *= 1f - asthmaConstriction * asthma * constrict;

            // Spread across the tube, spiralling as it travels.
            float a = swirlAngle[i] + s * swirlTurns * Mathf.PI * 2f;
            Vector3 n1 = Vector3.Cross(tangent, depthAxis);
            if (n1.sqrMagnitude < 1e-6f) n1 = Vector3.Cross(tangent, Vector3.up);
            n1.Normalize();
            Vector3 n2 = Vector3.Cross(tangent, n1);
            Vector3 local = pos + (Mathf.Cos(a) * n1 + Mathf.Sin(a) * n2) * (radius * tubeSpread * spread[i]);

            if (turbulence > 0f)
            {
                float k = noiseSeed[i];
                local += new Vector3(
                    Mathf.PerlinNoise(k, t * 5f) - 0.5f,
                    Mathf.PerlinNoise(k + 17f, t * 5f) - 0.5f,
                    Mathf.PerlinNoise(k + 41f, t * 5f) - 0.5f) * (2f * turbulence);
            }

            Vector3 world = lungT.TransformPoint(local);
            Vector3 velocity = hasPrev[i] ? (world - prevWorld[i]) / dt : Vector3.zero;
            // Never fully still: a stretched billboard needs a direction.
            if (velocity.sqrMagnitude < 1e-4f)
                velocity = lungT.TransformDirection(tangent).normalized * 0.01f;
            prevWorld[i] = world;
            hasPrev[i] = true;

            Color c = Color.Lerp(freshAirColor, exhaledAirColor, exchange[i]);
            if (trapped[i]) c = Color.Lerp(c, trappedAirColor, 0.7f) * 0.7f;
            else c *= brightness;
            // Fade in at the intake cloud, out as it disperses into the alveoli.
            c.a = Mathf.SmoothStep(0f, 1f, s / 0.08f) * (1f - Mathf.SmoothStep(0f, 1f, (s - 0.93f) / 0.07f));

            // Swells slightly as it reaches the alveoli.
            float size = particleSize * scaleRatio * (1f + 0.5f * Mathf.SmoothStep(0f, 1f, (s - 0.85f) / 0.15f));

            airParticles[count++] = MakeParticle(world, velocity, c, size);
        }

        // ---- oxygen sparks ----
        float oxygenation = Mathf.Clamp01((bio.OxygenSaturation - 0.75f) / 0.23f);
        if (showOxygenSparks && inflation > 0.6f)
            sparkBudget += sparkRate * dt * oxygenation * ventilation;
        float sparkSpeed = tracheaRadius * 2.5f;

        for (int i = 0; i < sparks.Length; i++)
        {
            if (!sparks[i].alive && sparkBudget >= 1f)
            {
                int j = rng.Next(alveoli.Length);
                if (alveoli[j].emphysema) { sparkBudget -= 1f; continue; } // destroyed sacs exchange little
                sparks[i] = new Spark
                {
                    alive = true,
                    position = alveoli[j].position,
                    velocity = Random.onUnitSphere * sparkSpeed,
                };
                sparkBudget -= 1f;
            }

            if (!sparks[i].alive) continue;

            sparks[i].age += dt;
            if (sparks[i].age >= sparkLifetime) { sparks[i].alive = false; sparks[i].age = 0f; continue; }
            sparks[i].position += sparks[i].velocity * dt;

            float life = sparks[i].age / sparkLifetime;
            Color c = oxygenColor * 1.4f;
            c.a = 1f - life * life;
            airParticles[count++] = MakeParticle(
                lungT.TransformPoint(sparks[i].position),
                lungT.TransformVector(sparks[i].velocity),
                c, particleSize * 0.7f * scaleRatio);
        }
        sparkBudget = Mathf.Min(sparkBudget, 5f);

        airSystem.SetParticles(airParticles, count);

        // ---- alveoli ----
        for (int j = 0; j < alveoli.Length; j++)
        {
            Alveolus alv = alveoli[j];
            Color c;
            float size;
            if (alv.emphysema)
            {
                // Swollen, dim, barely deflating: air trapped in a destroyed sac.
                c = trappedAirColor * 0.55f;
                size = alveolusSize * 1.8f * (0.92f + 0.08f * inflation);
            }
            else
            {
                // Inflates with the breath; blue with fresh air, pink as CO2 builds.
                float puff = inflation * ventilation;
                c = Color.Lerp(exhaledAirColor, freshAirColor, inflation) * (0.25f + 0.75f * puff);
                size = alveolusSize * (0.6f + 0.6f * puff + 0.05f * Mathf.Sin(t * 2f + alv.phase));
            }
            c.a = alveolusOpacity;
            alveoliParticles[j] = MakeParticle(lungT.TransformPoint(alv.position), Vector3.zero, c, size * scaleRatio);
        }
        alveoliSystem.SetParticles(alveoliParticles, alveoli.Length);

        // ---- glowing tree ----
        if (treeRoot != null && showAirwayTree)
            UpdateTree(lungT, inflation, inflationRate, flow01, travel, asthma);
    }

    static ParticleSystem.Particle MakeParticle(Vector3 position, Vector3 velocity, Color color, float size)
    {
        ParticleSystem.Particle p = default;
        p.position = position;
        p.velocity = velocity;
        p.startColor = color;
        p.startSize = size;
        p.startLifetime = 1f;
        p.remainingLifetime = 1f;
        return p;
    }

    // ------------------------------------------------------------------ tree glow --

    void BuildTreeLines()
    {
        if (particleMaterial == null)
            return;

        treeMaterial = new Material(particleMaterial) { name = "AirwayTreeGlow" };
        treeMaterial.SetFloat(LineModeId, 1f);
        block = new MaterialPropertyBlock();

        // Not a child of the lung: LungMeshDeformer collects every renderer under the
        // lung as "lung material", and these lines would switch it off its GPU path.
        // Lines are placed in world space each frame from the lung's transform instead.
        treeRoot = new GameObject("AirwayTree").transform;
        treeRoot.SetParent(transform, false);

        HashSet<string> drawn = new HashSet<string>();
        foreach (AirPath path in paths)
        {
            // Segment 0 is the intake cloud above the trachea: air, not airway.
            for (int k = 1; k < path.controlSample.Length - 1; k++)
            {
                string key = path.segmentKeys[k];
                if (!drawn.Add(key)) continue;

                int from = path.controlSample[k];
                int to = path.controlSample[k + 1];
                LineRenderer lr = new GameObject("Airway_" + key).AddComponent<LineRenderer>();
                lr.transform.SetParent(treeRoot, false);
                lr.useWorldSpace = true;
                lr.sharedMaterial = treeMaterial;
                lr.textureMode = LineTextureMode.Stretch;
                lr.alignment = LineAlignment.View;
                lr.numCapVertices = 3;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.positionCount = to - from + 1;

                treeLines.Add(lr);
                treeSegments.Add((path, from, to));
                treeWorldPoints.Add(new Vector3[to - from + 1]);
                treeMaxWidth.Add(0f);
            }
        }
    }

    void UpdateTree(Transform lungT, float inflation, float inflationRate, float flow01, float travel, float asthma)
    {
        // Widths only change with asthma, so rebuild the curves only then.
        if (!Mathf.Approximately(asthma, lastTreeAsthma))
        {
            lastTreeAsthma = asthma;
            for (int l = 0; l < treeLines.Count; l++)
            {
                (AirPath path, int from, int to) = treeSegments[l];
                float maxW = 1e-5f;
                for (int i = from; i <= to; i++)
                    maxW = Mathf.Max(maxW, Width(path, i, asthma));

                AnimationCurve curve = new AnimationCurve();
                for (int i = from; i <= to; i++)
                {
                    float u = (path.cumulative[i] - path.cumulative[from]) / Mathf.Max(1e-5f, path.cumulative[to] - path.cumulative[from]);
                    curve.AddKey(u, Width(path, i, asthma) / maxW);
                }
                treeLines[l].widthCurve = curve;
                treeMaxWidth[l] = maxW;
            }
        }

        // World placement follows the lung, so the tree turns and zooms with it.
        float worldScale = lungT.lossyScale.x;
        for (int l = 0; l < treeLines.Count; l++)
        {
            (AirPath path, int from, int to) = treeSegments[l];
            Vector3[] world = treeWorldPoints[l];
            for (int i = from; i <= to; i++)
                world[i - from] = lungT.TransformPoint(path.points[i]);
            treeLines[l].SetPositions(world);
            treeLines[l].widthMultiplier = treeMaxWidth[l] * worldScale;
        }

        // One steady colour (amber with asthma), slightly brighter at peak flow. It used
        // to tint toward blue/pink with the breath too, which flickered.
        Color c = Color.Lerp(treeColor, asthmaTreeColor, asthma) * (treeIdleBrightness + treeFlowBrightness * flow01);
        c.a = 1f;

        // Bands sit at fixed positions along the full path, shifted by how far the air
        // has moved - so they flow continuously from the trachea into every branch.
        // bandsPerPath = 0 gives a smooth, even glow.
        bool bands = bandsPerPath > 0f;
        float spacing = meanPathLength / Mathf.Max(1f, bandsPerPath);
        float phase = inflation * travel * meanPathLength / spacing;

        for (int l = 0; l < treeLines.Count; l++)
        {
            (AirPath path, int from, int to) = treeSegments[l];
            float start = path.cumulative[from];
            float len = path.cumulative[to] - start;

            LineRenderer lr = treeLines[l];
            lr.startColor = c;
            lr.endColor = c;
            lr.GetPropertyBlock(block);
            block.SetFloat(BandCountId, bands ? len / spacing : 0f);
            block.SetFloat(FlowPhaseId, bands ? phase - start / spacing : 0f);
            lr.SetPropertyBlock(block);
        }
    }

    float Width(AirPath path, int i, float asthma) =>
        2f * path.radius[i] * treeWidth * (1f - asthmaConstriction * asthma * path.constrictable[i]);

    // ------------------------------------------------------------------ geometry --

    static void Evaluate(AirPath path, float s, out Vector3 pos, out Vector3 tangent, out float radius, out float constrict)
    {
        float d = Mathf.Clamp01(s) * path.length;
        int last = path.points.Length - 1;
        int i = 1;
        while (i < last && path.cumulative[i] < d) i++;

        float segment = path.cumulative[i] - path.cumulative[i - 1];
        float f = segment > 1e-6f ? Mathf.Clamp01((d - path.cumulative[i - 1]) / segment) : 0f;
        pos = Vector3.Lerp(path.points[i - 1], path.points[i], f);
        tangent = (path.points[i] - path.points[i - 1]).normalized;
        radius = Mathf.Lerp(path.radius[i - 1], path.radius[i], f);
        constrict = Mathf.Lerp(path.constrictable[i - 1], path.constrictable[i], f);
    }

    /// <summary>
    /// Derives the airway tree from the mesh: the trachea is the column of tissue on the
    /// central vertical axis, the lungs are the tissue either side of it along the widest
    /// horizontal direction. Branch points: intake -> trachea top -> carina -> hilum ->
    /// lobar branch -> alveolar cluster.
    /// </summary>
    bool BuildAirwayTree(Mesh mesh)
    {
        Vector3[] v = mesh.vertices;
        if (v.Length < 100)
            return false;

        Transform lungT = lung.transform;
        Vector3 up = lungT.InverseTransformDirection(Vector3.up).normalized;
        Vector3 c = mesh.bounds.center;
        Vector3 refVec = Mathf.Abs(Vector3.Dot(up, Vector3.right)) < 0.9f ? Vector3.right : Vector3.forward;
        Vector3 ax = Vector3.Cross(up, refVec).normalized;
        Vector3 ay = Vector3.Cross(up, ax);

        // A stride keeps this quick on a 730k-vertex mesh; statistics don't need every vertex.
        int stride = Mathf.Max(1, v.Length / 120000);

        // Pass 1: radial extent and the widest horizontal direction (left-right).
        float sxx = 0f, syy = 0f, sxy = 0f;
        radialExtent = 0f;
        for (int i = 0; i < v.Length; i += stride)
        {
            Vector3 rel = v[i] - c;
            float x = Vector3.Dot(rel, ax), y = Vector3.Dot(rel, ay);
            sxx += x * x; syy += y * y; sxy += x * y;
            radialExtent = Mathf.Max(radialExtent, Mathf.Sqrt(x * x + y * y));
        }
        float angle = 0.5f * Mathf.Atan2(2f * sxy, sxx - syy);
        Vector3 lateral = (Mathf.Cos(angle) * ax + Mathf.Sin(angle) * ay).normalized;
        depthAxis = Vector3.Cross(up, lateral).normalized;

        // Pass 2: trachea column, lung tissue either side, apex and base heights.
        float tracheaBand = 0.15f * radialExtent;
        float lungMinLateral = 0.30f * radialExtent;
        float tracheaTopH = float.MinValue, apexH = float.MinValue, baseH = float.MaxValue;
        for (int i = 0; i < v.Length; i += stride)
        {
            Vector3 rel = v[i] - c;
            float h = Vector3.Dot(rel, up);
            float lat = Vector3.Dot(rel, lateral);
            if ((rel - h * up).magnitude < tracheaBand) tracheaTopH = Mathf.Max(tracheaTopH, h);
            if (Mathf.Abs(lat) > lungMinLateral)
            {
                apexH = Mathf.Max(apexH, h);
                baseH = Mathf.Min(baseH, h);
            }
        }
        if (tracheaTopH == float.MinValue || apexH == float.MinValue)
            return false;
        float lungHeight = Mathf.Max(1e-4f, apexH - baseH);

        // Pass 3: trachea opening (centroid and radius) and each lung's centroid.
        Vector3 topSum = Vector3.zero, leftSum = Vector3.zero, rightSum = Vector3.zero;
        int topN = 0, leftN = 0, rightN = 0;
        List<Vector3> topVerts = new List<Vector3>();
        for (int i = 0; i < v.Length; i += stride)
        {
            Vector3 rel = v[i] - c;
            float h = Vector3.Dot(rel, up);
            float lat = Vector3.Dot(rel, lateral);
            if ((rel - h * up).magnitude < tracheaBand && h > tracheaTopH - 0.06f * lungHeight)
            {
                topSum += v[i]; topN++;
                topVerts.Add(v[i]);
            }
            if (lat < -lungMinLateral) { leftSum += v[i]; leftN++; }
            else if (lat > lungMinLateral) { rightSum += v[i]; rightN++; }
        }
        if (topN == 0 || leftN == 0 || rightN == 0)
            return false;

        Vector3 tracheaTop = topSum / topN;
        float rSum = 0f;
        foreach (Vector3 p in topVerts)
        {
            Vector3 rel = p - tracheaTop;
            rSum += (rel - Vector3.Dot(rel, up) * up).magnitude;
        }
        tracheaRadius = Mathf.Clamp(rSum / topN, 0.02f * radialExtent, 0.12f * radialExtent);

        Vector3[] sideCentre = { leftSum / leftN, rightSum / rightN };
        // The right lung is the larger one, with three lobes; the left has two.
        int largerSide = rightN >= leftN ? 1 : 0;

        float H(Vector3 p) => Vector3.Dot(p - c, up);
        float carinaH = apexH - carinaDrop * lungHeight;
        Vector3 carina = tracheaTop + up * (carinaH - H(tracheaTop));
        Vector3 intake = tracheaTop + up * (intakeHeight * lungHeight);

        List<Vector3> terminalList = new List<Vector3>();
        float r0 = tracheaRadius;

        for (int side = 0; side < 2; side++)
        {
            Vector3 centre = sideCentre[side];
            Vector3 atCarina = centre + up * (carinaH - 0.06f * lungHeight - H(centre));
            Vector3 hilum = Vector3.Lerp(carina, atCarina, 0.45f);

            float[] lobeLevels = side == largerSide ? new[] { 0.78f, 0.52f, 0.25f } : new[] { 0.70f, 0.30f };
            Vector3[] lobes = new Vector3[lobeLevels.Length];
            for (int k = 0; k < lobes.Length; k++)
            {
                float lh = baseH + lobeLevels[k] * lungHeight;
                lobes[k] = Vector3.Lerp(hilum, centre + up * (lh - H(centre)), 0.55f);
            }

            int attempts = 0, found = 0;
            while (found < terminalsPerSide && attempts < terminalsPerSide * 400)
            {
                attempts++;
                Vector3 p = v[rng.Next(v.Length)];
                Vector3 rel = p - c;
                float lat = Vector3.Dot(rel, lateral);
                float h = Vector3.Dot(rel, up);
                if ((side == 1 ? lat : -lat) < lungMinLateral) continue;
                if (h < baseH + 0.08f * lungHeight || h > apexH - 0.05f * lungHeight) continue;

                // Branch from the lobe whose level is nearest this point.
                int lobe = 0;
                float best = float.MaxValue;
                for (int k = 0; k < lobes.Length; k++)
                {
                    float dh = Mathf.Abs(H(lobes[k]) - h);
                    if (dh < best) { best = dh; lobe = k; }
                }

                Vector3 end = Vector3.Lerp(p, centre, terminalInset);
                int terminal = terminalList.Count;
                terminalList.Add(end);

                AirPath path = MakePath(
                    new[] { intake, tracheaTop, carina, hilum, lobes[lobe], end },
                    new[] { r0 * 4f, r0, r0 * 0.85f, r0 * 0.6f, r0 * 0.4f, r0 * 0.18f },
                    new[] { 0f, 0f, 0.3f, 1f, 1f, 1f });
                path.terminal = terminal;
                path.segmentKeys = new[] { "intake", "trachea", "bronchus" + side, "lobar" + side + "_" + lobe, "branch" + terminal };
                paths.Add(path);
                found++;
            }
        }

        if (paths.Count == 0)
            return false;

        terminals = terminalList.ToArray();
        meanPathLength = 0f;
        foreach (AirPath p in paths) meanPathLength += p.length;
        meanPathLength /= paths.Count;

        // Grape-like alveolar clusters around each branch end. Which sacs are
        // emphysematous follows COPD severity - see LateUpdate.
        alveoli = new Alveolus[terminals.Length * Mathf.Max(1, alveoliPerTerminal)];
        int a = 0;
        for (int tIdx = 0; tIdx < terminals.Length; tIdx++)
        {
            for (int k = 0; k < Mathf.Max(1, alveoliPerTerminal); k++)
            {
                // A single glow sits on the branch end; a cluster spreads around it.
                float clusterSize = alveoliPerTerminal > 1 ? 2.2f * r0 : 0f;
                Vector3 jitter = new Vector3(Rand() - 0.5f, Rand() - 0.5f, Rand() - 0.5f) * clusterSize;
                alveoli[a++] = new Alveolus
                {
                    position = terminals[tIdx] + jitter,
                    terminal = tIdx,
                    phase = Rand() * 6.28f,
                };
            }
        }
        emphysemaRolls = new float[alveoli.Length];
        for (int k = 0; k < emphysemaRolls.Length; k++) emphysemaRolls[k] = Rand();

        Debug.Log($"AirflowVisualizer: built an airway tree with {paths.Count} branches, {alveoli.Length} alveoli " +
                  $"({(largerSide == 1 ? "right" : "left")}-hand lung given 3 lobes; carina {carinaDrop * 100f:F0}% below the apex).");
        return true;
    }

    // One random number per alveolus, so the emphysematous set grows smoothly with COPD
    // severity (sacs with a roll below the threshold are affected) instead of reshuffling.
    float[] emphysemaRolls;
    float lastCopd = -1f;

    void LateUpdate()
    {
        if (!ready) return;
        float copd = lung.DamageSeverity01;
        if (Mathf.Approximately(copd, lastCopd)) return;
        lastCopd = copd;
        for (int j = 0; j < alveoli.Length; j++)
            alveoli[j].emphysema = emphysemaRolls[j] < copd * maxEmphysemaFraction;
    }

    AirPath MakePath(Vector3[] control, float[] radius, float[] constrictable)
    {
        int seg = Mathf.Max(1, samplesPerSegment);
        int n = (control.Length - 1) * seg + 1;
        AirPath path = new AirPath
        {
            points = new Vector3[n],
            radius = new float[n],
            constrictable = new float[n],
            cumulative = new float[n],
            controlSample = new int[control.Length],
        };

        // Catmull-Rom through the branch points, so branches curve instead of kinking.
        int idx = 0;
        for (int k = 0; k < control.Length - 1; k++)
        {
            path.controlSample[k] = idx;
            Vector3 p0 = control[Mathf.Max(0, k - 1)];
            Vector3 p1 = control[k];
            Vector3 p2 = control[k + 1];
            Vector3 p3 = control[Mathf.Min(control.Length - 1, k + 2)];
            for (int j = 0; j < seg; j++)
            {
                float t = j / (float)seg;
                path.points[idx] = CatmullRom(p0, p1, p2, p3, t);
                path.radius[idx] = Mathf.Lerp(radius[k], radius[k + 1], t);
                path.constrictable[idx] = Mathf.Lerp(constrictable[k], constrictable[k + 1], t);
                idx++;
            }
        }
        path.controlSample[control.Length - 1] = idx;
        path.points[idx] = control[control.Length - 1];
        path.radius[idx] = radius[control.Length - 1];
        path.constrictable[idx] = constrictable[control.Length - 1];

        for (int i = 1; i < n; i++)
            path.cumulative[i] = path.cumulative[i - 1] + Vector3.Distance(path.points[i - 1], path.points[i]);
        path.length = Mathf.Max(1e-5f, path.cumulative[n - 1]);
        return path;
    }

    static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    // Shows the derived airway tree in the Scene view (Play mode, object selected).
    void OnDrawGizmosSelected()
    {
        if (paths == null || lung == null) return;
        Gizmos.color = Color.cyan;
        Transform lungT = lung.transform;
        foreach (AirPath path in paths)
            for (int i = 1; i < path.points.Length; i++)
                Gizmos.DrawLine(lungT.TransformPoint(path.points[i - 1]), lungT.TransformPoint(path.points[i]));
    }
}
