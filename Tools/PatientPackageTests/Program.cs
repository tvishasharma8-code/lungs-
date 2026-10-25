using System;
using System.IO;
using System.Linq;
using System.Text.Json;

// Reads a real mesh.bin with the VR app's reader and checks that the LPS -> Unity
// conversion is anatomically right: a mirrored or flipped mapping would put the lobes
// on the wrong side or upside down even though every number still "looks" fine.
static class Program
{
    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what); if (!ok) failures++; }

    static float[] Centroid(LungPackageMesh m, string name)
    {
        var sub = m.SubMeshes.FirstOrDefault(s => s.Name == name);
        if (sub == null) return null;
        double x = 0, y = 0, z = 0;
        foreach (int i in sub.Indices) { x += m.Positions[i * 3]; y += m.Positions[i * 3 + 1]; z += m.Positions[i * 3 + 2]; }
        int n = sub.Indices.Length;
        return new[] { (float)(x / n), (float)(y / n), (float)(z / n) };
    }

    static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("usage: dotnet run -- <folder with mesh.bin + meta.json>"); return 2; }
        string dir = args[0];
        var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "meta.json"))).RootElement;

        Console.WriteLine("Read:");
        var mesh = LungPackageMesh.Read(File.ReadAllBytes(Path.Combine(dir, "mesh.bin")));
        Check(mesh.VertexCount == meta.GetProperty("vertices").GetInt32(), $"vertex count matches meta ({mesh.VertexCount})");
        int tris = mesh.SubMeshes.Sum(s => s.Indices.Length) / 3;
        Check(tris == meta.GetProperty("triangles").GetInt32(), $"triangle count matches meta ({tris})");
        Console.WriteLine("  sub-meshes: " + string.Join(", ", mesh.SubMeshes.Select(s => $"{s.Name}={s.Indices.Length / 3}")));

        Console.WriteLine("Axis mapping (LPS x,y,z -> Unity x,z,y):");
        mesh.GetBounds(out var min, out var max);
        var bmin = meta.GetProperty("bbox_min_mm").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var bmax = meta.GetProperty("bbox_max_mm").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        bool mapped = Math.Abs(min[0] - bmin[0]) < 0.01 && Math.Abs(min[1] - bmin[2]) < 0.01 && Math.Abs(min[2] - bmin[1]) < 0.01
                   && Math.Abs(max[0] - bmax[0]) < 0.01 && Math.Abs(max[1] - bmax[2]) < 0.01 && Math.Abs(max[2] - bmax[1]) < 0.01;
        Check(mapped, $"bounds are meta's bbox with y/z swapped (Unity {min[0]:F0},{min[1]:F0},{min[2]:F0} .. {max[0]:F0},{max[1]:F0},{max[2]:F0} mm)");
        Check(mesh.SignedVolume() > 0, $"triangles wound outward for Unity (signed volume {mesh.SignedVolume() / 1e6:F2} L)");

        Console.WriteLine("Anatomy in Unity space (+X = patient left, +Y = up, +Z = back):");
        var ru = Centroid(mesh, "lung_upper_lobe_right");
        var rm = Centroid(mesh, "lung_middle_lobe_right");
        var rl = Centroid(mesh, "lung_lower_lobe_right");
        var lu = Centroid(mesh, "lung_upper_lobe_left");
        var ll = Centroid(mesh, "lung_lower_lobe_left");
        var tr = Centroid(mesh, "trachea");
        if (new[] { ru, rm, rl, lu, ll, tr }.Any(c => c == null)) { Check(false, "all six structures present"); return 1; }
        Check(ru[0] < 0 && rm[0] < 0 && rl[0] < 0, "right lobes on -X (the viewer's left, facing the patient)");
        Check(lu[0] > 0 && ll[0] > 0, "left lobes on +X");
        Check(ru[1] > rl[1] && lu[1] > ll[1], "upper lobes above lower lobes (+Y up)");
        Check(tr[1] > ru[1] && tr[1] > lu[1], "trachea highest");
        Check(Math.Abs(tr[0]) < Math.Abs(ru[0]) && Math.Abs(tr[0]) < Math.Abs(lu[0]), "trachea near the midline");
        Check(rm[2] < rl[2], "right middle lobe in front of the right lower lobe (-Z = front, toward the viewer)");

        Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
