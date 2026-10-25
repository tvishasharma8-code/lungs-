using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// Reads a LungVision Patient Package mesh (mesh.bin, written by the backend's
/// pipeline/lung_mesh.py) and converts it from DICOM patient coordinates to Unity's.
/// Plain C# (no UnityEngine) so it can be tested outside Unity; PatientLungLoader turns
/// the result into a UnityEngine.Mesh.
///
/// Coordinates. The file is in DICOM patient coordinates (LPS, millimetres):
///   x = toward the patient's Left, y = Posterior, z = Superior.
/// The lung faces the user, so in Unity:
///   +X = patient Left   (the patient's left appears on the viewer's right)
///   +Y = Superior       (head end up)
///   +Z = Posterior      (the back faces away from the viewer)
/// That is (x, y, z)_LPS -> (x, z, y)_Unity. Swapping two axes mirrors handedness, so
/// every triangle's winding is reversed too, keeping normals pointing outward.
/// </summary>
public sealed class LungPackageMesh
{
    public const string Magic = "LVMESH01";
    public const int SupportedVersion = 1;

    /// <summary>Unity-axis positions in millimetres, xyz interleaved.</summary>
    public float[] Positions;
    public List<SubMesh> SubMeshes = new List<SubMesh>();

    public sealed class SubMesh
    {
        public string Name;     // e.g. "lung_lower_lobe_right", "trachea"
        public int[] Indices;   // triangles, winding already converted for Unity
    }

    public int VertexCount => Positions.Length / 3;

    /// <summary>Converts one point from DICOM patient coordinates (LPS mm) to Unity axes (mm).</summary>
    public static void LpsToUnity(float x, float y, float z, out float ux, out float uy, out float uz)
    {
        ux = x;
        uy = z;
        uz = y;
    }

    public static LungPackageMesh Read(byte[] data)
    {
        using (var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8))
            return Read(reader, data.Length);
    }

    public static LungPackageMesh Read(BinaryReader r, long length)
    {
        string magic = Encoding.ASCII.GetString(r.ReadBytes(8));
        if (magic != Magic)
            throw new InvalidDataException($"Not a LungVision mesh (magic '{magic}').");
        uint version = r.ReadUInt32();
        if (version != SupportedVersion)
            throw new InvalidDataException($"Unsupported mesh version {version} (this app reads {SupportedVersion}).");

        uint vertexCount = r.ReadUInt32();
        uint subCount = r.ReadUInt32();
        if (subCount > 64)
            throw new InvalidDataException($"Implausible sub-mesh count {subCount}.");

        var names = new string[subCount];
        var counts = new uint[subCount];
        long totalIndices = 0;
        for (int i = 0; i < subCount; i++)
        {
            int nameLength = r.ReadByte();
            names[i] = Encoding.UTF8.GetString(r.ReadBytes(nameLength));
            counts[i] = r.ReadUInt32();
            if (counts[i] % 3 != 0)
                throw new InvalidDataException($"Sub-mesh '{names[i]}' index count {counts[i]} is not whole triangles.");
            totalIndices += counts[i];
        }

        long expected = r.BaseStream.Position + vertexCount * 12L + totalIndices * 4L;
        if (expected != length)
            throw new InvalidDataException($"Mesh file is {length} bytes, header describes {expected}.");

        var mesh = new LungPackageMesh { Positions = new float[vertexCount * 3] };
        for (int v = 0; v < vertexCount; v++)
        {
            float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
            LpsToUnity(x, y, z, out mesh.Positions[v * 3], out mesh.Positions[v * 3 + 1], out mesh.Positions[v * 3 + 2]);
        }

        for (int s = 0; s < subCount; s++)
        {
            var indices = new int[counts[s]];
            for (int i = 0; i < indices.Length; i += 3)
            {
                uint a = r.ReadUInt32(), b = r.ReadUInt32(), c = r.ReadUInt32();
                if (a >= vertexCount || b >= vertexCount || c >= vertexCount)
                    throw new InvalidDataException($"Sub-mesh '{names[s]}' references a vertex out of range.");
                // Reversed winding: the LPS -> Unity axis swap mirrors handedness.
                indices[i] = (int)a;
                indices[i + 1] = (int)c;
                indices[i + 2] = (int)b;
            }
            mesh.SubMeshes.Add(new SubMesh { Name = names[s], Indices = indices });
        }

        return mesh;
    }

    /// <summary>Axis-aligned bounds of the positions (Unity axes, mm).</summary>
    public void GetBounds(out float[] min, out float[] max)
    {
        min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
        max = new[] { float.MinValue, float.MinValue, float.MinValue };
        for (int i = 0; i < Positions.Length; i += 3)
            for (int k = 0; k < 3; k++)
            {
                float v = Positions[i + k];
                if (v < min[k]) min[k] = v;
                if (v > max[k]) max[k] = v;
            }
    }

    /// <summary>
    /// Sum of signed tetrahedron volumes. Positive = triangles wound so their (numeric)
    /// cross-product normals point outward, which is what Unity treats as front faces.
    /// </summary>
    public double SignedVolume()
    {
        double total = 0;
        foreach (var sub in SubMeshes)
        {
            int[] t = sub.Indices;
            for (int i = 0; i < t.Length; i += 3)
            {
                int a = t[i] * 3, b = t[i + 1] * 3, c = t[i + 2] * 3;
                double ax = Positions[a], ay = Positions[a + 1], az = Positions[a + 2];
                double bx = Positions[b], by = Positions[b + 1], bz = Positions[b + 2];
                double cx = Positions[c], cy = Positions[c + 1], cz = Positions[c + 2];
                total += ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx);
            }
        }
        return total / 6.0;
    }
}
