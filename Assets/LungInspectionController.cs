using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Lets the user rotate and zoom the lungs in VR while guaranteeing the model never
/// moves from where the scene placed it.
///
/// WHY A PIVOT OBJECT: the lung FBX's own pivot sits nowhere near the mesh (measured
/// earlier - the transform origin is far outside the geometry). Rotating the lung
/// directly would swing it around that offset origin like a ball on a string. Instead
/// the scene setup parents the lung under a pivot placed at its *visual* centre, and
/// this component rotates and scales that pivot. The mesh therefore spins in place.
///
/// WHY XRSimpleInteractable AND NOT XRGrabInteractable: XRGrabInteractable exists to
/// move things - it drives position by design, needs a Rigidbody, and would let the
/// user drag the lungs across the room. XRSimpleInteractable only reports select
/// enter/exit, so translation is not merely disabled, it is never implemented. The
/// position is additionally re-asserted every frame as a hard guarantee.
///
/// WHY ZOOM SCALES RATHER THAN DOLLYING THE RIG: moving the XR Origin toward the lung
/// is the textbook "camera gets closer" approach, but here it would drag the user away
/// from the parameter console, which is anchored beside the lung at a fixed world
/// position - zoom in and the panel ends up behind your shoulder. Artificial locomotion
/// is also a common comfort problem on Quest. Scaling the pivot achieves the same
/// apparent result ("lungs appear larger") while leaving both the lung's position and
/// the user's position untouched. Set zoomMode to MoveRig if you want the dolly instead.
/// </summary>
[DisallowMultipleComponent]
public class LungInspectionController : MonoBehaviour
{
    public enum ZoomMode
    {
        /// <summary>Scale the lung about its visual centre. Position never changes.</summary>
        ScaleModel,
        /// <summary>Dolly the XR Origin along its forward axis. Moves the user, not the lung.</summary>
        MoveRig,
    }

    [Header("Wiring (filled in by BioGears > Add VR Lung Inspection)")]
    [Tooltip("Pivot placed at the lung's visual centre. This is what rotates and scales - never the lung itself.")]
    public Transform pivot;

    [Tooltip("Interactable used only for select enter/exit. Deliberately not a grab interactable.")]
    public XRBaseInteractable interactable;

    [Tooltip("Optional. Only used when zoomMode is MoveRig.")]
    public Transform xrOrigin;

    [Header("Rotation")]
    [Tooltip("How closely the lung follows the controller's twist. 1 = one-to-one.")]
    [Range(0.2f, 3f)] public float rotationSensitivity = 1f;

    [Tooltip("Higher settles faster. Damping is what removes tracking jitter from the controller.")]
    [Range(1f, 30f)] public float rotationDamping = 14f;

    [Header("Zoom")]
    public ZoomMode zoomMode = ZoomMode.ScaleModel;

    [Tooltip("Thumbstick push per second, as a fraction of the zoom range.")]
    [Range(0.1f, 4f)] public float zoomSpeed = 1.2f;

    [Range(1f, 30f)] public float zoomDamping = 10f;

    [Tooltip("Smallest the lung may become, relative to its authored size.")]
    [Range(0.2f, 1f)] public float minZoom = 0.55f;

    [Tooltip("Largest the lung may become. Capped so it cannot swallow the camera.")]
    [Range(1f, 6f)] public float maxZoom = 2.75f;

    [Tooltip("Ignore small thumbstick values so a resting thumb does not drift the zoom.")]
    [Range(0f, 0.5f)] public float stickDeadzone = 0.25f;

    [Header("Editor testing (no headset)")]
    [Tooltip("Right-drag rotates and the mouse wheel zooms while playing in the Editor. Has no effect in a build.")]
    public bool enableMouseFallback = true;

    [Tooltip("Zoom change per mouse-wheel detent. The default crosses the full zoom range in about 15 notches.")]
    [Range(0.02f, 0.5f)] public float mouseZoomPerNotch = 0.15f;

    [Tooltip("Degrees of rotation per pixel of right-drag.")]
    [Range(0.05f, 1f)] public float mouseRotateSensitivity = 0.25f;

    // --- anchoring -------------------------------------------------------------
    // Captured once at startup and re-applied every frame. This is the promise that
    // the lungs never move, enforced rather than assumed.
    Vector3 anchoredPosition;
    Vector3 baseScale;
    bool anchored;

    // --- rotation state --------------------------------------------------------
    Quaternion targetRotation;
    Transform activeInteractor;
    Quaternion lastInteractorRotation;
    bool isGrabbed;

    // --- zoom state ------------------------------------------------------------
    float targetZoom = 1f;
    float currentZoom = 1f;
    float baseRigDistance;
    Vector3 rigStartPosition;

    InputAction thumbstickAction;

    void Awake()
    {
        if (pivot == null)
            pivot = transform;

        anchoredPosition = pivot.position;
        baseScale = pivot.localScale;
        targetRotation = pivot.rotation;
        anchored = true;

        if (xrOrigin != null)
        {
            rigStartPosition = xrOrigin.position;
            baseRigDistance = Vector3.Distance(rigStartPosition, anchoredPosition);
        }

        // Bound in code rather than via a scene reference: the rig has a single
        // RightHandController and no thumbstick action wired to it, so relying on
        // inspector wiring would silently do nothing. This matches the binding the
        // project's own XRI action asset uses for the right thumbstick.
        thumbstickAction = new InputAction(
            "LungZoom",
            InputActionType.Value,
            "<XRController>{RightHand}/{Primary2DAxis}",
            expectedControlType: "Vector2");
        thumbstickAction.AddBinding("<XRController>{LeftHand}/{Primary2DAxis}");
    }

    void OnEnable()
    {
        thumbstickAction?.Enable();

        if (interactable != null)
        {
            interactable.selectEntered.AddListener(OnSelectEntered);
            interactable.selectExited.AddListener(OnSelectExited);
        }
    }

    void OnDisable()
    {
        thumbstickAction?.Disable();

        if (interactable != null)
        {
            interactable.selectEntered.RemoveListener(OnSelectEntered);
            interactable.selectExited.RemoveListener(OnSelectExited);
        }
    }

    void OnDestroy()
    {
        thumbstickAction?.Dispose();
    }

    void OnSelectEntered(SelectEnterEventArgs args)
    {
        Transform t = args.interactorObject?.transform;
        if (t == null) return;

        activeInteractor = t;
        lastInteractorRotation = t.rotation;
        isGrabbed = true;
    }

    void OnSelectExited(SelectExitEventArgs args)
    {
        isGrabbed = false;
        activeInteractor = null;
    }

    void Update()
    {
        if (!anchored || pivot == null)
            return;

        AccumulateRotation();
        AccumulateZoom();
        ApplyDamped();
        EnforceAnchor();
    }

    /// <summary>
    /// Turns the controller's own twist into lung rotation. Only the DELTA between
    /// frames is used, so the lung keeps whatever orientation it had when grabbed
    /// instead of snapping to match the controller.
    /// </summary>
    void AccumulateRotation()
    {
        if (isGrabbed && activeInteractor != null)
        {
            Quaternion current = activeInteractor.rotation;
            Quaternion delta = current * Quaternion.Inverse(lastInteractorRotation);

            if (!Mathf.Approximately(rotationSensitivity, 1f))
            {
                // Scale the delta by slerping from identity, so sensitivity works on
                // the angle rather than on the raw quaternion components.
                delta = Quaternion.Slerp(Quaternion.identity, delta, rotationSensitivity);
            }

            targetRotation = delta * targetRotation;
            lastInteractorRotation = current;
        }

#if UNITY_EDITOR
        if (enableMouseFallback && Mouse.current != null && Mouse.current.rightButton.isPressed)
        {
            Vector2 d = Mouse.current.delta.ReadValue();
            Transform cam = Camera.main != null ? Camera.main.transform : null;
            Vector3 up = cam != null ? cam.up : Vector3.up;
            Vector3 right = cam != null ? cam.right : Vector3.right;

            // Rotate about the CAMERA's axes, not world axes, so dragging right always
            // spins the lung right from wherever you happen to be looking.
            targetRotation = Quaternion.AngleAxis(-d.x * mouseRotateSensitivity, up)
                           * Quaternion.AngleAxis(d.y * mouseRotateSensitivity, right)
                           * targetRotation;
        }
#endif
    }

    void AccumulateZoom()
    {
        // Thumbstick is a RATE - held down, it should keep zooming - so it scales with
        // delta time.
        float rate = 0f;

        if (thumbstickAction != null && thumbstickAction.enabled)
        {
            float y = thumbstickAction.ReadValue<Vector2>().y;
            if (Mathf.Abs(y) > stickDeadzone)
            {
                // Rescale past the deadzone so response starts at zero, not at a jump.
                rate = Mathf.Sign(y) * (Mathf.Abs(y) - stickDeadzone) / (1f - stickDeadzone);
            }
        }

        if (Mathf.Abs(rate) > 0.0001f)
            targetZoom += rate * zoomSpeed * Time.deltaTime;

#if UNITY_EDITOR
        // The wheel is an IMPULSE, not a rate: it reports a one-frame spike. Scaling it
        // by delta time (as the thumbstick correctly is) made a single notch move the
        // zoom by about 2%, which reads as the wheel not working at all.
        if (enableMouseFallback && Mouse.current != null)
        {
            float raw = Mouse.current.scroll.ReadValue().y;

            if (Mathf.Abs(raw) > 0.01f)
            {
                // The Input System is inconsistent here: on Windows some versions report
                // the raw OS value of 120 per detent, others normalise it to 1.0. The
                // previous code assumed 120 and divided - so on a build that reports 1.0
                // every notch became 0.008 and was thrown away by the threshold, which
                // is exactly "the wheel does nothing". Detect the convention instead.
                float notches = Mathf.Abs(raw) >= 10f ? raw / 120f : raw;
                targetZoom += notches * mouseZoomPerNotch;
            }
        }

        // Keyboard alternative, because scroll behaviour varies by device and driver
        // (trackpads in particular report continuous values, not detents).
        if (enableMouseFallback && Keyboard.current != null)
        {
            float keys = 0f;
            if (Keyboard.current.equalsKey.isPressed || Keyboard.current.numpadPlusKey.isPressed) keys += 1f;
            if (Keyboard.current.minusKey.isPressed || Keyboard.current.numpadMinusKey.isPressed) keys -= 1f;
            if (Mathf.Abs(keys) > 0.001f)
                targetZoom += keys * zoomSpeed * Time.deltaTime;
        }
#endif

        targetZoom = Mathf.Clamp(targetZoom, minZoom, maxZoom);
    }

    void ApplyDamped()
    {
        // Exponential smoothing, frame-rate independent. This is what keeps controller
        // tracking noise from showing up as jitter in the model.
        pivot.rotation = Quaternion.Slerp(
            pivot.rotation, targetRotation, 1f - Mathf.Exp(-rotationDamping * Time.deltaTime));

        currentZoom = Mathf.Lerp(currentZoom, targetZoom, 1f - Mathf.Exp(-zoomDamping * Time.deltaTime));

        if (zoomMode == ZoomMode.ScaleModel)
        {
            pivot.localScale = baseScale * currentZoom;
        }
        else if (xrOrigin != null)
        {
            // Dolly along the line from the rig's start to the lung, so "in" always
            // means toward the subject regardless of which way the user is facing.
            Vector3 toLung = anchoredPosition - rigStartPosition;
            if (toLung.sqrMagnitude > 0.0001f)
            {
                float travel = baseRigDistance * (1f - 1f / Mathf.Max(0.01f, currentZoom));
                xrOrigin.position = rigStartPosition + toLung.normalized * travel;
            }
        }
    }

    /// <summary>
    /// The hard guarantee. Rotation and scale cannot translate a transform, but an
    /// errant script, a stray grab interactable, or a future edit could - so the
    /// anchored position is simply re-asserted every frame rather than trusted.
    /// </summary>
    void EnforceAnchor()
    {
        if (pivot.position != anchoredPosition)
            pivot.position = anchoredPosition;
    }

    /// <summary>Returns the lung to its starting orientation and zoom.</summary>
    public void ResetView()
    {
        targetRotation = Quaternion.identity;
        targetZoom = 1f;
    }
}
