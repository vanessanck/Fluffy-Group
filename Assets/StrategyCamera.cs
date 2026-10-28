using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class StrategyCamera : MonoBehaviour
{
    public float panSpeed = 15f;
    public float zoomSpeed = 2f;
    public float minHeight = 5f;
    public float maxHeight = 30f;

    void Start()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        // Look down at the middle of the map
        transform.rotation = Quaternion.Euler(55f, 0f, 0f);
        HexMap map = FindFirstObjectByType<HexMap>();
        if (map != null)
            transform.position = map.MapCenter() + new Vector3(0f, 18f, -12.6f);
    }

    void Update()
    {
        Vector2 move = Vector2.zero;
        float scroll = 0f;

#if ENABLE_INPUT_SYSTEM
        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (kb != null)
        {
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed)    move.y += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed)  move.y -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move.x += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  move.x -= 1f;
        }
        if (mouse != null)
            scroll = Mathf.Clamp(mouse.scroll.ReadValue().y, -1f, 1f);
#else
        move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        scroll = Mathf.Clamp(Input.mouseScrollDelta.y, -1f, 1f);
#endif

        // Move the camera
        transform.position += new Vector3(move.x, 0f, move.y) * panSpeed * Time.deltaTime;

        // Zoom in and out
        if (scroll != 0f)
        {
            Vector3 p = transform.position + transform.forward * scroll * zoomSpeed;
            if (p.y >= minHeight && p.y <= maxHeight) transform.position = p;
        }
    }
}