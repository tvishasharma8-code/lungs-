using UnityEngine;

[RequireComponent(typeof(MeshFilter))]
public class LungMeshDeformer : MonoBehaviour
{
    Mesh mesh;
    Vector3[] originalVertices;
    Vector3[] displacedVertices;

    [Header("BioGears Integration")]
    [Tooltip("Reference to the BioGears respiratory system. If null, will search on this GameObject and parents.")]
    public SimulatedBioGearsRespiratory bioGearsSystem;

    [Header("Deformation Settings")]
    [Tooltip("Maximum expansion distance in mesh units at full tidal volume")]
    public float maxExpansion = 5f;

    [Tooltip("Separate expansion multipliers for each axis (allows asymmetric breathing)")]
    public Vector3 expansionScale = new Vector3(1f, 0.8f, 1.2f);

    Vector3[] directions;

    void Start()
    {
        MeshFilter mf = GetComponent<MeshFilter>();

        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogError("LungMeshDeformer: No mesh found!");
            return;
        }

        // Find BioGears system if not assigned
        if (bioGearsSystem == null)
        {
            bioGearsSystem = GetComponent<SimulatedBioGearsRespiratory>();
            if (bioGearsSystem == null)
                bioGearsSystem = GetComponentInParent<SimulatedBioGearsRespiratory>();
            if (bioGearsSystem == null)
            {
                Debug.LogError("LungMeshDeformer: No BioGears respiratory system found! Add SimulatedBioGearsRespiratory component.");
                return;
            }
        }

        mesh = Instantiate(mf.sharedMesh);
        mf.mesh = mesh;
        mesh.MarkDynamic();

        originalVertices = mesh.vertices;
        displacedVertices = new Vector3[originalVertices.Length];
        directions = new Vector3[originalVertices.Length];

        Vector3 center = mesh.bounds.center;

        for (int i = 0; i < originalVertices.Length; i++)
            directions[i] = (originalVertices[i] - center).normalized;

        Debug.Log($"LungMeshDeformer: Initialized with {originalVertices.Length} vertices, connected to BioGears");
    }

    void Update()
    {
        if (mesh == null || originalVertices == null || bioGearsSystem == null) return;
        if (!bioGearsSystem.IsRunning) return;

        // Get expansion factor from BioGears (0 = exhaled, 1 = fully inhaled)
        float expansion = bioGearsSystem.ExpansionFactor * maxExpansion;

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
        mesh.RecalculateBounds();
    }
}
