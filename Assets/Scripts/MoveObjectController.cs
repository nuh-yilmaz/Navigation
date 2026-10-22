using UnityEngine;

/// <summary>
/// Lets the player open/close this object (e.g. a door/drawer) by looking directly
/// at it within reachRange and pressing Interact. Uses PlayerController's shared
/// IsInteractTriggered flag rather than polling input devices itself, so the same
/// button binding is used everywhere in the project.
/// </summary>
[RequireComponent(typeof(Animator))]
public class MoveObjectController : MonoBehaviour
{
    public float reachRange = 1.8f;

    private const string AnimBoolPrefix = "isOpen_Obj_";

    private Animator anim;
    private Camera fpsCam;
    private GameObject player;
    private int rayLayerMask;

    private bool playerEntered;
    private bool showInteractMsg;
    private GUIStyle guiStyle;
    private string msg;

    private void Start()
    {
        player = GameObject.FindGameObjectWithTag("Player");

        fpsCam = Camera.main;
        if (fpsCam == null)
        {
            Debug.LogError("MoveObjectController: no camera tagged 'MainCamera' was found.");
        }

        anim = GetComponent<Animator>();
        anim.enabled = false;

        int interactRaycastLayer = LayerMask.NameToLayer("InteractRaycast");
        if (interactRaycastLayer == -1)
        {
            Debug.LogError("MoveObjectController: a layer named 'InteractRaycast' does not exist. Create it in Project Settings > Tags and Layers.");
        }
        rayLayerMask = interactRaycastLayer >= 0 ? (1 << interactRaycastLayer) : 0;

        SetupGui();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.gameObject == player) playerEntered = true;
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.gameObject == player)
        {
            playerEntered = false;
            showInteractMsg = false;
        }
    }

    private void Update()
    {
        if (!playerEntered) return;

        Vector3 rayOrigin = fpsCam.ViewportToWorldPoint(new Vector3(0.5f, 0.5f, 0f));

        if (!Physics.Raycast(rayOrigin, fpsCam.transform.forward, out RaycastHit hit, reachRange, rayLayerMask))
        {
            showInteractMsg = false;
            return;
        }

        if (!TryGetOwnMoveableObject(hit.collider, out MoveableObject moveableObject))
        {
            showInteractMsg = false;
            return;
        }

        showInteractMsg = true;
        string animBoolName = AnimBoolPrefix + moveableObject.objectNumber;
        bool isOpen = anim.GetBool(animBoolName);
        msg = GetGuiMsg(isOpen);

        if (PlayerController.IsInteractTriggered)
        {
            anim.enabled = true;
            anim.SetBool(animBoolName, !isOpen);
            msg = GetGuiMsg(!isOpen);
        }
    }

    /// <summary>
    /// True only if the raycast hit belongs to THIS object's own hierarchy (walking
    /// up through parents), so when several moveable objects are in trigger range
    /// at once, only the one actually being looked at reacts — not a neighbor that
    /// happens to also have a MoveableObject component.
    /// </summary>
    private bool TryGetOwnMoveableObject(Collider hitCollider, out MoveableObject moveable)
    {
        moveable = hitCollider.GetComponent<MoveableObject>();

        const int maxAncestorWalk = 6;
        Transform current = hitCollider.transform;
        for (int i = 0; i < maxAncestorWalk && current != null; i++)
        {
            if (current.gameObject == gameObject)
            {
                if (moveable == null) moveable = GetComponentInParent<MoveableObject>();
                return moveable != null;
            }
            current = current.parent;
        }

        return false;
    }

    #region GUI

    private void SetupGui()
    {
        guiStyle = new GUIStyle
        {
            fontSize = 16,
            fontStyle = FontStyle.Bold
        };
        guiStyle.normal.textColor = Color.white;
        msg = "Press Interact to Open";
    }

    private string GetGuiMsg(bool isOpen) => isOpen ? "Press Interact to Close" : "Press Interact to Open";

    private void OnGUI()
    {
        if (showInteractMsg)
        {
            GUI.Label(new Rect(50, Screen.height - 50, 200, 50), msg, guiStyle);
        }
    }

    #endregion
}