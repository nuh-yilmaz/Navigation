using UnityEngine;

/// <summary>
/// Attached to a TARGET object. Does two jobs:
///
///  1. Puts THIS object on the target layer, so the gaze raycast can hit it.
///     The layer is checked first and only written if it differs, so the scene
///     is not needlessly modified every time the script runs.
///
///  2. Spawns (or reuses) a child "mask" object sized and positioned to match
///     this object's BoxCollider. The mask's own layer is left exactly as the
///     prefab defines it — the mask is decoration, not a raycast target, and
///     putting it on the target layer would let the ray hit the mask instead of
///     the object underneath.
///
/// Runs in edit mode so the mask can be previewed without pressing Play, but
/// physics (Rigidbody + gravity) is only added while actually playing, so
/// editing and saving the scene never leaves a live physics body sitting on it.
/// </summary>
[ExecuteAlways]
public class FitMask : MonoBehaviour
{
    [Header("Target Layer")]
    [Tooltip("Layer this target object should sit on. Pick exactly ONE layer — it must match the targetLayerNumber set on the Gaze component.")]
    [SerializeField] private LayerMask targetLayerMask;

    [Header("Mask")]
    [SerializeField] private GameObject maskPrefab;

    [Header("Physics")]
    [SerializeField] private bool useGravity = true;

    private void Start()
    {
        EnsureOnTargetLayer();

        if (maskPrefab == null)
        {
            Debug.LogError("FitMask: Mask Prefab is not assigned!", this);
            return;
        }

        BoxCollider parentCollider = GetOrAddBoxCollider();
        GameObject maskObj = GetOrCreateMask();

        maskObj.transform.localScale = parentCollider.size;
        maskObj.transform.SetLocalPositionAndRotation(parentCollider.center, Quaternion.identity);

        // Deliberately NOT touching maskObj.layer — see the class summary.

        if (Application.isPlaying)
        {
            SetUpPhysics();
        }
    }

    /// <summary>
    /// Puts this object on the chosen target layer if it isn't already.
    ///
    /// Only this GameObject is changed. Children are left alone: the mask must
    /// stay off the target layer, and any other child colliders are usually not
    /// meant to be raycast targets either. If a child collider IS the thing the
    /// gaze ray should hit, put that child on the target layer yourself.
    /// </summary>
    private void EnsureOnTargetLayer()
    {
        int layer = FirstLayerIn(targetLayerMask);

        if (layer < 0)
        {
            Debug.LogWarning("FitMask: no target layer selected; leaving this object's layer unchanged.", this);
            return;
        }

        if (CountLayersIn(targetLayerMask) > 1)
        {
            Debug.LogWarning($"FitMask: more than one layer selected; using the lowest ('{LayerMask.LayerToName(layer)}'). Select exactly one.", this);
        }

        if (gameObject.layer == layer) return;   // already correct — change nothing

        string previous = LayerMask.LayerToName(gameObject.layer);
        gameObject.layer = layer;

        Debug.Log($"FitMask: layer changed from '{previous}' to '{LayerMask.LayerToName(layer)}'.", gameObject);

#if UNITY_EDITOR
        // In edit mode a plain field write is not registered as a scene change,
        // so the new layer would be lost on reload without this.
        if (!Application.isPlaying)
        {
            UnityEditor.EditorUtility.SetDirty(gameObject);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
        }
#endif
    }

    private BoxCollider GetOrAddBoxCollider()
    {
        if (!TryGetComponent<BoxCollider>(out var box))
        {
            box = gameObject.AddComponent<BoxCollider>();
            Debug.Log("FitMask: no BoxCollider found on parent; added one.", gameObject);
        }
        return box;
    }

    private GameObject GetOrCreateMask()
    {
        Transform existing = transform.Find(maskPrefab.name);
        if (existing != null) return existing.gameObject;

        GameObject mask = Instantiate(maskPrefab, transform);
        mask.name = maskPrefab.name;
        return mask;
    }

    private void SetUpPhysics()
    {
        if (!TryGetComponent<Rigidbody>(out var rb))
        {
            rb = gameObject.AddComponent<Rigidbody>();
            Debug.Log("FitMask: no Rigidbody found on parent; added one for gravity.", gameObject);
        }
        rb.useGravity = useGravity;
    }

    /// <summary>Lowest set layer index in the mask, or -1 if none is set.</summary>
    private static int FirstLayerIn(LayerMask mask)
    {
        for (int i = 0; i < 32; i++)
        {
            if ((mask.value & (1 << i)) != 0) return i;
        }
        return -1;
    }

    private static int CountLayersIn(LayerMask mask)
    {
        int count = 0;
        for (int i = 0; i < 32; i++)
        {
            if ((mask.value & (1 << i)) != 0) count++;
        }
        return count;
    }
}