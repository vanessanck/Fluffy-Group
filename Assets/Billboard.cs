using UnityEngine;

// Keeps a flat picture turned towards the camera, so a resource icon always reads the same
// no matter how the strategy camera is panned or zoomed.
public class Billboard : MonoBehaviour
{
    Camera cam;

    void LateUpdate()
    {
        if (cam == null) cam = Camera.main;
        if (cam == null) return;
        transform.rotation = cam.transform.rotation;
    }
}
