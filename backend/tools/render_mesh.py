"""
Render lung meshes to PNG from the front (the view the VR app shows), off-screen with
VTK - for comparing surface-building settings without a headset.

Usage (from backend/):
    .venv\\Scripts\\python tools\\render_mesh.py out.png <mesh.bin | model.stl> [more ...]
Several inputs are rendered side by side, each labelled with its file's parent folder
name. Meshes are expected in DICOM patient coordinates (LPS mm) like the VR package.
"""

from __future__ import annotations

import os
import struct
import sys

import numpy as np
import vtk
from vtk.util import numpy_support


def load(path: str):
    if path.endswith(".bin"):
        d = open(path, "rb").read()
        nv, ns = struct.unpack("<II", d[12:20])
        o = 20
        for _ in range(ns):
            n = d[o]
            o += 1 + n + 4
        verts = np.frombuffer(d[o:o + nv * 12], "<f4").reshape(-1, 3)
        faces = np.frombuffer(d[o + nv * 12:], "<u4").reshape(-1, 3)
    else:
        import trimesh
        m = trimesh.load(path, force="mesh")
        verts, faces = np.asarray(m.vertices, np.float32), np.asarray(m.faces, np.int64)

    poly = vtk.vtkPolyData()
    pts = vtk.vtkPoints()
    pts.SetData(numpy_support.numpy_to_vtk(np.ascontiguousarray(verts, np.float32), deep=True))
    poly.SetPoints(pts)
    cells = np.hstack([np.full((len(faces), 1), 3, np.int64), faces.astype(np.int64)]).ravel()
    ca = vtk.vtkCellArray()
    ca.SetCells(len(faces), numpy_support.numpy_to_vtkIdTypeArray(cells, deep=True))
    poly.SetPolys(ca)
    normals = vtk.vtkPolyDataNormals()
    normals.SetInputData(poly)
    normals.SplittingOff()
    normals.ConsistencyOn()
    normals.Update()
    return normals.GetOutput()


def render(paths: list[str], out_png: str, size=(520, 560)) -> None:
    win = vtk.vtkRenderWindow()
    win.SetOffScreenRendering(1)
    win.SetSize(size[0] * len(paths), size[1])
    for i, path in enumerate(paths):
        ren = vtk.vtkRenderer()
        ren.SetViewport(i / len(paths), 0, (i + 1) / len(paths), 1)
        ren.SetBackground(0.12, 0.13, 0.15)
        actor = vtk.vtkActor()
        mapper = vtk.vtkPolyDataMapper()
        mapper.SetInputData(load(path))
        actor.SetMapper(mapper)
        prop = actor.GetProperty()
        prop.SetColor(0.80, 0.50, 0.44)
        prop.SetSpecular(0.15)
        prop.SetSpecularPower(20)
        prop.SetInterpolationToPhong()
        ren.AddActor(actor)

        label = vtk.vtkTextActor()
        label.SetInput(os.path.basename(os.path.dirname(os.path.abspath(path))))
        label.GetTextProperty().SetFontSize(18)
        label.SetPosition(10, 10)
        ren.AddActor2D(label)

        b = actor.GetBounds()
        c = [(b[0] + b[1]) / 2, (b[2] + b[3]) / 2, (b[4] + b[5]) / 2]
        cam = ren.GetActiveCamera()
        # Front view: LPS -y is anterior, +z superior; the patient's left ends up on
        # the viewer's right, as in the VR app.
        cam.SetFocalPoint(*c)
        cam.SetPosition(c[0], c[1] - 900, c[2])
        cam.SetViewUp(0, 0, 1)
        cam.SetViewAngle(22)
        ren.ResetCameraClippingRange()
        win.AddRenderer(ren)

    win.Render()
    grab = vtk.vtkWindowToImageFilter()
    grab.SetInput(win)
    grab.Update()
    writer = vtk.vtkPNGWriter()
    writer.SetFileName(out_png)
    writer.SetInputConnection(grab.GetOutputPort())
    writer.Write()


if __name__ == "__main__":
    render(sys.argv[2:], sys.argv[1])
    print("wrote", sys.argv[1])
