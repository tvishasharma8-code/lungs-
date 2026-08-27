using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Removes CT slice terracing from the lung mesh by smoothing vertex POSITIONS.
///
/// Why this and not a material change: LungSurfaceDiagnostic measured the mesh and found
/// it sits on a voxel lattice - local X spaced 0.874, local Z spaced 0.695, and local Y
/// (the slice-stacking axis) spaced 3.416, occupying only 2.3% of its coordinate range.
/// That is an anisotropic CT volume with roughly 0.7-0.9mm in-plane resolution and
/// ~3.4mm slices, meshed with marching cubes. The banding is geometry.
///
/// Why not just recalculate normals: the same diagnostic found normals already evenly
/// distributed, i.e. the FBX ships smoothed normals. Shading is smooth while positions
/// still step, which is precisely what makes the terraces read as contour bands.
/// Re-averaging normals over stepped positions cannot help - the positions must move.
///
/// TAUBIN SMOOTHING (lambda/mu), not plain Laplacian. A pure Laplacian pass shrinks a
/// closed surface a little every iteration; over the ~30 iterations needed to erase a
/// 3.4-unit step that would visibly deflate the lungs and destroy anatomy. Taubin
/// alternates a positive (shrinking) pass with a slightly larger negative (inflating)
/// one, so low-frequency shape - the lobes, the hilum, the silhouette - is preserved
/// while high-frequency stepping is removed.
///
/// The original FBX is never modified. A separate smoothed mesh asset is written and
/// assigned to the MeshFilter, so reverting means dropping the original mesh back in.
/// </summary>
public static class LungMeshSmoother
{
    // Taubin's classic pair. |Mu| must exceed Lambda for the volume-preserving property.
    const float Lambda = 0.5f;
    const float Mu = -0.53f;

    // Sized from the measured geometry rather than picked by feel. For a uniform-weight
    // Laplacian the smoothing radius grows as sqrt(iterations * lambda) * edgeLength.
    // With the in-plane edge at ~0.78 units, 50 iterations gives ~3.9 units - comfortably
    // past the 3.42-unit slice step that has to be erased, while still only ~4% of the
    // ~100-unit scale of a lobe, so anatomy is preserved. (30 iterations reached only
    // 3.04 units and would have left the terracing partly visible.)
    const int Iterations = 50;

    const string OutputPath = "Assets/Settings/LungTissue/LungModel_Smoothed.asset";

    [MenuItem("BioGears/Fix Lung Mesh (Smooth CT Terracing)")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[LungMeshSmoother] Exit Play mode first.");
            return;
        }

        LungMeshDeformer[] deformers = Object.FindObjectsByType<LungMeshDeformer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (deformers.Length == 0)
        {
            Debug.LogError("[LungMeshSmoother] No LungMeshDeformer in the open scene.");
            return;
        }

        MeshFilter mf = deformers[0].GetComponentInChildren<MeshFilter>(true);
        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogError("[LungMeshSmoother] No MeshFilter/mesh found under the lung.");
            return;
        }

        Mesh source = mf.sharedMesh;
        if (!source.isReadable)
        {
            Debug.LogError("[LungMeshSmoother] Mesh is not readable - enable Read/Write in the FBX import settings.");
            return;
        }

        try
        {
            Mesh smoothed = Smooth(source);
            if (smoothed == null)
                return;

            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(OutputPath);
            if (existing != null)
            {
                // Overwrite in place so the MeshFilter reference survives a re-run.
                EditorUtility.CopySerialized(smoothed, existing);
                Object.DestroyImmediate(smoothed);
                smoothed = existing;
                EditorUtility.SetDirty(smoothed);
            }
            else
            {
                AssetDatabase.CreateAsset(smoothed, OutputPath);
            }

            AssetDatabase.SaveAssets();

            mf.sharedMesh = smoothed;
            EditorUtility.SetDirty(mf);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(mf.gameObject.scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(mf.gameObject.scene);

            Debug.Log($"[LungMeshSmoother] Done. {Iterations} Taubin iterations applied to " +
                      $"{smoothed.vertexCount:N0} vertices. Smoothed mesh saved to {OutputPath} and assigned. " +
                      "The original FBX is untouched - to revert, drop 'Segmentation' back into the Mesh Filter.");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    static Mesh Smooth(Mesh source)
    {
        Vector3[] verts = source.vertices;
        int[] tris = source.triangles;
        int n = verts.Length;

        // ---- adjacency in CSR form -------------------------------------------------
        // One List<int> per vertex would mean 730k allocations; compressed sparse row
        // keeps the whole neighbour graph in two flat arrays.
        EditorUtility.DisplayProgressBar("Smoothing lung mesh", "Building adjacency...", 0.05f);

        int[] degree = new int[n];
        for (int i = 0; i < tris.Length; i += 3)
        {
            degree[tris[i]] += 2;
            degree[tris[i + 1]] += 2;
            degree[tris[i + 2]] += 2;
        }

        int[] offset = new int[n + 1];
        for (int i = 0; i < n; i++)
            offset[i + 1] = offset[i] + degree[i];

        int[] cursor = new int[n];
        System.Array.Copy(offset, cursor, n);
        int[] neighbours = new int[offset[n]];

        for (int i = 0; i < tris.Length; i += 3)
        {
            int a = tris[i], b = tris[i + 1], c = tris[i + 2];
            neighbours[cursor[a]++] = b; neighbours[cursor[a]++] = c;
            neighbours[cursor[b]++] = a; neighbours[cursor[b]++] = c;
            neighbours[cursor[c]++] = a; neighbours[cursor[c]++] = b;
        }

        // ---- Taubin passes ---------------------------------------------------------
        Vector3[] a1 = verts;
        Vector3[] a2 = new Vector3[n];

        for (int iter = 0; iter < Iterations * 2; iter++)
        {
            if ((iter & 1) == 0)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Smoothing lung mesh",
                        $"Taubin pass {iter / 2 + 1} / {Iterations}", 0.1f + 0.8f * iter / (Iterations * 2f)))
                {
                    Debug.LogWarning("[LungMeshSmoother] Cancelled - no changes written.");
                    return null;
                }
            }

            // Even iterations shrink with Lambda, odd ones inflate with Mu.
            float factor = (iter & 1) == 0 ? Lambda : Mu;

            for (int v = 0; v < n; v++)
            {
                int start = offset[v], end = offset[v + 1];
                if (end <= start) { a2[v] = a1[v]; continue; }

                Vector3 sum = Vector3.zero;
                for (int k = start; k < end; k++)
                    sum += a1[neighbours[k]];

                // Uniform-weight Laplacian: move toward the average of the neighbours.
                Vector3 laplacian = sum / (end - start) - a1[v];
                a2[v] = a1[v] + laplacian * factor;
            }

            Vector3[] swap = a1; a1 = a2; a2 = swap;
        }

        // ---- write out -------------------------------------------------------------
        EditorUtility.DisplayProgressBar("Smoothing lung mesh", "Rebuilding mesh...", 0.95f);

        Mesh result = new Mesh();
        result.name = source.name + "_Smoothed";
        // 730k vertices needs 32-bit indices.
        result.indexFormat = IndexFormat.UInt32;
        result.vertices = a1;
        result.triangles = tris;

        // Preserve UVs if the asset has them. If not, LungMeshDeformer generates a
        // cylindrical unwrap at runtime, so the smoking stain still works either way.
        Vector2[] uv = source.uv;
        if (uv != null && uv.Length == n)
            result.uv = uv;

        // Positions moved, so the shipped normals no longer match the surface. These are
        // recalculated from the *smoothed* geometry, which is the whole point: now the
        // smooth normals and smooth positions finally agree.
        result.RecalculateNormals();
        result.RecalculateTangents();
        result.RecalculateBounds();

        return result;
    }
}
