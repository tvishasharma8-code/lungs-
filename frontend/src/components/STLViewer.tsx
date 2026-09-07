import { useEffect, useRef, useState } from "react";
import * as THREE from "three";
import { STLLoader } from "three/examples/jsm/loaders/STLLoader.js";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";

interface Props {
  stlUrl: string;
}

export default function STLViewer({ stlUrl }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const controlsRef = useRef<OrbitControls | null>(null);
  const cameraRef = useRef<THREE.PerspectiveCamera | null>(null);
  const defaultView = useRef<{ pos: THREE.Vector3; target: THREE.Vector3 } | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const scene = new THREE.Scene();
    scene.background = null;

    const camera = new THREE.PerspectiveCamera(
      45,
      container.clientWidth / container.clientHeight,
      0.1,
      5000
    );
    cameraRef.current = camera;

    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.setSize(container.clientWidth, container.clientHeight);
    container.appendChild(renderer.domElement);

    const key = new THREE.DirectionalLight(0xffffff, 1.6);
    key.position.set(1, 1, 1);
    scene.add(key);
    const fill = new THREE.DirectionalLight(0x8b5cf6, 0.6);
    fill.position.set(-1, -0.5, -1);
    scene.add(fill);
    scene.add(new THREE.AmbientLight(0x404060, 0.9));

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.dampingFactor = 0.08;
    controlsRef.current = controls;

    let mesh: THREE.Mesh | null = null;
    const loader = new STLLoader();

    loader.load(
      stlUrl,
      (geometry) => {
        geometry.computeVertexNormals();
        geometry.center();

        const material = new THREE.MeshStandardMaterial({
          color: 0xd88c7a,
          metalness: 0.05,
          roughness: 0.55,
          flatShading: false,
        });
        mesh = new THREE.Mesh(geometry, material);
        mesh.rotation.x = -Math.PI / 2;
        scene.add(mesh);

        geometry.computeBoundingSphere();
        const sphere = geometry.boundingSphere;
        const radius = sphere ? sphere.radius : 100;
        const dist = radius * 2.6;
        camera.position.set(dist * 0.6, dist * 0.3, dist * 0.8);
        camera.near = radius / 100;
        camera.far = radius * 20;
        camera.updateProjectionMatrix();
        controls.target.set(0, 0, 0);
        controls.update();

        defaultView.current = {
          pos: camera.position.clone(),
          target: controls.target.clone(),
        };

        setLoading(false);
      },
      undefined,
      (err) => {
        console.error(err);
        setError("Could not load the generated STL file.");
        setLoading(false);
      }
    );

    let frameId: number;
    const animate = () => {
      frameId = requestAnimationFrame(animate);
      controls.update();
      renderer.render(scene, camera);
    };
    animate();

    const handleResize = () => {
      if (!container) return;
      camera.aspect = container.clientWidth / container.clientHeight;
      camera.updateProjectionMatrix();
      renderer.setSize(container.clientWidth, container.clientHeight);
    };
    const resizeObserver = new ResizeObserver(handleResize);
    resizeObserver.observe(container);

    return () => {
      cancelAnimationFrame(frameId);
      resizeObserver.disconnect();
      controls.dispose();
      renderer.dispose();
      mesh?.geometry.dispose();
      if (mesh?.material) (mesh.material as THREE.Material).dispose();
      container.removeChild(renderer.domElement);
    };
  }, [stlUrl]);

  const resetView = () => {
    const controls = controlsRef.current;
    const camera = cameraRef.current;
    const dv = defaultView.current;
    if (controls && camera && dv) {
      camera.position.copy(dv.pos);
      controls.target.copy(dv.target);
      controls.update();
    }
  };

  return (
    <div className="relative h-full w-full">
      <div ref={containerRef} className="h-full w-full" />
      {loading && (
        <div className="absolute inset-0 flex items-center justify-center text-sm text-slate-500">
          Loading mesh…
        </div>
      )}
      {error && (
        <div className="absolute inset-0 flex items-center justify-center px-6 text-center text-sm text-red-400">
          {error}
        </div>
      )}
      {!loading && !error && (
        <button
          type="button"
          onClick={resetView}
          className="absolute bottom-3 right-3 rounded-md border border-white/10 bg-base-900/80 px-2.5 py-1.5 text-xs text-slate-300 backdrop-blur hover:bg-base-800"
        >
          Reset view
        </button>
      )}
    </div>
  );
}
