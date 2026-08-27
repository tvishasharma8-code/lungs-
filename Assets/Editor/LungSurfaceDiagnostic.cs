using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Answers one question with measurements rather than opinion: is the repetitive
/// horizontal banding on the lung coming from the MESH or from the MATERIAL?
///
/// The hypothesis under test is CT slice terracing. This mesh is a segmentation
/// ("Segmentation", 730k vertices, marching-cubes output). CT volumes are anisotropic -
/// in-plane resolution is well under a millimetre while slice spacing is several
/// millimetres - so an isosurface extracted from one is stepped like a contour model,
/// with terraces perpendicular to the slice axis. Those would appear as regular bands,
/// and no material change could ever remove them.
///
/// PERFORMANCE NOTE: an earlier version of this file hung the Editor for an hour on
/// `new HashSet&lt;Vector3&gt;(verts)`. Unity's Vector3.GetHashCode XORs three float bit
/// patterns with small shifts, which loses most of its entropy when coordinates are
/// quantised to a grid - precisely the case for segmentation output. The set degenerated
/// into collision chains. Everything here is now strictly O(n) over primitive types,
/// runs in a couple of seconds, and is cancelable.
/// </summary>
public static class LungSurfaceDiagnostic
{
    [MenuItem("BioGears/Diagnose Lung Surface")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[LungSurfaceDiagnostic] Exit Play mode first - the diagnostic reads the shared mesh asset.");
            return;
        }

        if (!TryGetLungMesh(out Mesh mesh))
            return;

        Vector3[] verts = mesh.vertices;
        Vector3[] normals = mesh.normals;

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("========== LUNG SURFACE DIAGNOSTIC ==========");
        sb.AppendLine($"mesh        : {mesh.name}");
        sb.AppendLine($"vertices    : {verts.Length:N0}");
        sb.AppendLine($"triangles   : {mesh.triangles.Length / 3:N0}");
        sb.AppendLine($"has normals : {(normals != null && normals.Length == verts.Length)}");
        sb.AppendLine($"bounds size : {mesh.bounds.size}");
        sb.AppendLine();

        // ---- 1. Coordinate quantisation, per local axis ----------------------------
        // Marching cubes over a voxel grid leaves vertices sitting on, or interpolated
        // between, discrete grid planes. Counting how many distinct values each axis
        // actually takes exposes that directly: an axis stepped at slice spacing has
        // far fewer distinct values than a freely-varying one.
        sb.AppendLine("--- 1. COORDINATE QUANTISATION (distinct values per axis) ---");
        string[] axisNames = { "local X", "local Y", "local Z" };
        for (int a = 0; a < 3; a++)
            ReportDistinctValues(verts, a, mesh.bounds, axisNames[a], sb);
        sb.AppendLine();

        // ---- 2. Positional periodicity --------------------------------------------
        sb.AppendLine("--- 2. POSITIONAL PERIODICITY (is the geometry stepped?) ---");
        for (int a = 0; a < 3; a++)
            AnalyzeAxis(verts, a, mesh.bounds, axisNames[a], sb);
        sb.AppendLine();

        // ---- 3. Normal clustering - the decisive test ------------------------------
        // On a stepped surface the geometry is mostly flat "treads" (normals parallel to
        // the slice axis) and vertical "risers" (perpendicular), so |dot(n, axis)| piles
        // up near 1 and near 0. A smooth organic surface spreads normals evenly.
        sb.AppendLine("--- 3. NORMAL CLUSTERING (terraced surfaces spike near 0 and 1) ---");
        if (normals != null && normals.Length == verts.Length)
        {
            for (int a = 0; a < 3; a++)
            {
                Vector3 axis = a == 0 ? Vector3.right : (a == 1 ? Vector3.up : Vector3.forward);
                AnalyzeNormals(normals, axis, axisNames[a], sb);
            }
        }
        else
        {
            sb.AppendLine("   (mesh has no normals)");
        }

        sb.AppendLine("=============================================");
        Debug.Log(sb.ToString());
    }

    static bool TryGetLungMesh(out Mesh mesh)
    {
        mesh = null;

        LungMeshDeformer[] deformers = Object.FindObjectsByType<LungMeshDeformer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (deformers.Length == 0)
        {
            Debug.LogError("[LungSurfaceDiagnostic] No LungMeshDeformer in the open scene.");
            return false;
        }

        MeshFilter mf = deformers[0].GetComponentInChildren<MeshFilter>(true);
        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogError("[LungSurfaceDiagnostic] No MeshFilter/mesh found under the lung.");
            return false;
        }

        if (!mf.sharedMesh.isReadable)
        {
            Debug.LogError("[LungSurfaceDiagnostic] Mesh is not readable - enable Read/Write in the FBX import settings.");
            return false;
        }

        mesh = mf.sharedMesh;
        return true;
    }

    /// <summary>
    /// Counts distinct quantised values along an axis using a plain bool array indexed
    /// by bucket - no hashing, no collisions, O(n).
    /// </summary>
    static void ReportDistinctValues(Vector3[] verts, int axis, Bounds bounds, string label, StringBuilder sb)
    {
        const int Buckets = 20000;
        bool[] seen = new bool[Buckets];
        float min = bounds.min[axis];
        float extent = Mathf.Max(1e-6f, bounds.size[axis]);

        for (int i = 0; i < verts.Length; i++)
        {
            int b = Mathf.Clamp((int)((verts[i][axis] - min) / extent * (Buckets - 1)), 0, Buckets - 1);
            seen[b] = true;
        }

        int distinct = 0;
        for (int i = 0; i < Buckets; i++) if (seen[i]) distinct++;

        float occupancy = 100f * distinct / Buckets;
        string verdict = occupancy < 25f ? "HEAVILY QUANTISED -> discrete planes" : "continuously varying";
        sb.AppendLine($"   {label}: {distinct:N0} / {Buckets:N0} buckets occupied ({occupancy:F1}%)  -> {verdict}");
    }

    /// <summary>
    /// Histogram positions along one axis, then autocorrelate to find any dominant
    /// spacing. Terracing produces a sharp peak; smooth geometry does not.
    /// </summary>
    static void AnalyzeAxis(Vector3[] verts, int axis, Bounds bounds, string label, StringBuilder sb)
    {
        const int Bins = 1200;
        float min = bounds.min[axis];
        float extent = Mathf.Max(1e-6f, bounds.size[axis]);

        float[] hist = new float[Bins];
        for (int i = 0; i < verts.Length; i++)
        {
            int b = Mathf.Clamp((int)((verts[i][axis] - min) / extent * (Bins - 1)), 0, Bins - 1);
            hist[b] += 1f;
        }

        float mean = 0f;
        for (int i = 0; i < Bins; i++) mean += hist[i];
        mean /= Bins;

        float variance = 0f;
        for (int i = 0; i < Bins; i++)
        {
            hist[i] -= mean;
            variance += hist[i] * hist[i];
        }

        if (variance < 1e-6f)
        {
            sb.AppendLine($"   {label}: flat distribution, no periodicity");
            return;
        }

        float bestScore = 0f;
        int bestLag = 0;
        for (int lag = 2; lag <= 60; lag++)
        {
            float sum = 0f;
            for (int i = 0; i + lag < Bins; i++)
                sum += hist[i] * hist[i + lag];

            float score = sum / variance;
            if (score > bestScore) { bestScore = score; bestLag = lag; }
        }

        float spacing = bestLag / (float)(Bins - 1) * extent;
        string verdict = bestScore > 0.45f ? "STRONGLY PERIODIC -> terracing"
                       : bestScore > 0.25f ? "moderately periodic"
                       : "no meaningful periodicity";
        sb.AppendLine($"   {label}: autocorr {bestScore:F3} at lag {bestLag} (spacing {spacing:F5} model units) -> {verdict}");
    }

    static void AnalyzeNormals(Vector3[] normals, Vector3 axis, string label, StringBuilder sb)
    {
        int[] buckets = new int[10];
        for (int i = 0; i < normals.Length; i++)
        {
            float d = Mathf.Abs(normals[i].x * axis.x + normals[i].y * axis.y + normals[i].z * axis.z);
            buckets[Mathf.Clamp((int)(d * 10f), 0, 9)]++;
        }

        float total = Mathf.Max(1, normals.Length);
        float perpendicular = buckets[0] / total * 100f;
        float parallel = buckets[9] / total * 100f;

        StringBuilder bars = new StringBuilder();
        for (int i = 0; i < 10; i++)
            bars.Append($"{(buckets[i] / total * 100f):F0} ");

        // A uniform distribution of directions gives ~10% per bucket.
        string verdict = (parallel > 22f || perpendicular > 22f)
            ? "CLUSTERED -> flat treads / risers, i.e. stepped geometry"
            : "evenly spread -> organic surface";

        sb.AppendLine($"   {label}: |dot| distribution %: [{bars.ToString().Trim()}]");
        sb.AppendLine($"       perpendicular {perpendicular:F1}%  parallel {parallel:F1}%  -> {verdict}");
    }

    // ------------------------------------------------------------------------------

    /// <summary>
    /// Separate menu item because it is the heaviest check. Counts edges used by exactly
    /// one triangle - open boundaries, i.e. holes - which is the likely explanation for
    /// the black regions at the lung bases: seeing through the shell into an unlit
    /// interior rather than a dark material.
    /// </summary>
    [MenuItem("BioGears/Diagnose - Count Open Boundaries")]
    public static void CountBoundaries()
    {
        if (!TryGetLungMesh(out Mesh mesh))
            return;

        int[] tris = mesh.triangles;
        // long keys hash cleanly, unlike Vector3 - this is the safe way to do it.
        Dictionary<long, int> edges = new Dictionary<long, int>(tris.Length);

        try
        {
            for (int i = 0; i < tris.Length; i += 3)
            {
                if ((i & 0x3FFFF) == 0)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Counting open boundaries",
                            $"{i / 3:N0} / {tris.Length / 3:N0} triangles", (float)i / tris.Length))
                    {
                        Debug.LogWarning("[LungSurfaceDiagnostic] Cancelled.");
                        return;
                    }
                }

                AddEdge(edges, tris[i], tris[i + 1]);
                AddEdge(edges, tris[i + 1], tris[i + 2]);
                AddEdge(edges, tris[i + 2], tris[i]);
            }

            int boundary = 0;
            foreach (KeyValuePair<long, int> kv in edges)
                if (kv.Value == 1) boundary++;

            Debug.Log($"[LungSurfaceDiagnostic] Open boundary edges: {boundary:N0} of {edges.Count:N0} unique edges.\n" +
                      (boundary == 0
                        ? "Mesh is closed - the black areas are NOT holes."
                        : "Mesh is OPEN. The dark regions are almost certainly the unlit interior showing " +
                          "through unclosed geometry, i.e. the segmentation was cut off at the scan boundary."));
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    static void AddEdge(Dictionary<long, int> map, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        map.TryGetValue(key, out int c);
        map[key] = c + 1;
    }

    // ------------------------------------------------------------------------------

    /// <summary>
    /// The A/B test: strip every surface effect - no normal map, no detail map, flat
    /// matte colour - so whatever banding remains cannot be blamed on the material.
    /// </summary>
    [MenuItem("BioGears/Diagnose - Apply Flat Test Material")]
    public static void ApplyFlatMaterial()
    {
        LungMeshDeformer[] deformers = Object.FindObjectsByType<LungMeshDeformer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (deformers.Length == 0)
        {
            Debug.LogError("[LungSurfaceDiagnostic] No LungMeshDeformer in the open scene.");
            return;
        }

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) { Debug.LogError("URP/Lit not found."); return; }

        const string path = "Assets/Settings/LungTissue/LungFlatTest.mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = shader;
        mat.SetTexture("_BaseMap", null);
        mat.SetTexture("_BumpMap", null);
        mat.SetTexture("_DetailNormalMap", null);
        mat.DisableKeyword("_NORMALMAP");
        mat.DisableKeyword("_DETAIL_MULX2");
        mat.SetColor("_BaseColor", new Color(0.78f, 0.52f, 0.49f));
        mat.SetFloat("_Smoothness", 0.05f);   // near-matte: removes specular banding cues
        mat.SetFloat("_Metallic", 0f);
        EditorUtility.SetDirty(mat);

        foreach (Renderer r in deformers[0].GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || r.gameObject.name == "LungSootOverlay") continue;
            Material[] mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++) mats[i] = mat;
            r.sharedMaterials = mats;
        }

        AssetDatabase.SaveAssets();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(deformers[0].gameObject.scene);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(deformers[0].gameObject.scene);

        Debug.Log("[LungSurfaceDiagnostic] Flat matte material applied - no textures, no normal map, near-zero smoothness. " +
                  "If horizontal banding is STILL visible now, it is geometry, not material.");
    }
}
