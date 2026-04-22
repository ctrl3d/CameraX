using CameraX;
using UnityEngine;
using UnityEngine.UI;

public class PhotoShoot : MonoBehaviour
{
    [Header("Controller")]
    [SerializeField] private NativeCameraController nativeCameraController;

    [Header("UI (Optional)")]
    [SerializeField] private Button startButton;
    [SerializeField] private Button stopButton;
    [SerializeField] private Button captureButton;

    private void Start()
    {
        if (startButton != null) startButton.onClick.AddListener(OnStartCamera);
        if (stopButton != null) stopButton.onClick.AddListener(OnStopCamera);
        if (captureButton != null) captureButton.onClick.AddListener(OnCapture);
    }

    public async void OnStartCamera()
    {
        if (nativeCameraController == null)
        {
            Debug.LogError("[PhotoShoot] NativeCameraController 가 할당되지 않았습니다.");
            return;
        }
        await nativeCameraController.StartCameraAsync();
    }

    public void OnStopCamera()
    {
        if (nativeCameraController != null) nativeCameraController.StopCamera();
    }

    public void OnCapture()
    {
        if (nativeCameraController != null) nativeCameraController.TakePhotoAndSave();
    }

    public void SetIso(float iso)
    {
        if (nativeCameraController != null) nativeCameraController.SetIso((int)iso);
    }

    public void SetShutterSpeed(float seconds)
    {
        if (nativeCameraController != null) nativeCameraController.SetShutterSpeedSeconds(seconds);
    }

    public void SetAutoExposure(bool enabled)
    {
        if (nativeCameraController != null) nativeCameraController.SetAutoExposure(enabled);
    }

    public void SetFocusDistance(float diopter)
    {
        if (nativeCameraController != null) nativeCameraController.SetFocusDistance(diopter);
    }

    private void OnDestroy()
    {
        if (startButton != null) startButton.onClick.RemoveListener(OnStartCamera);
        if (stopButton != null) stopButton.onClick.RemoveListener(OnStopCamera);
        if (captureButton != null) captureButton.onClick.RemoveListener(OnCapture);
    }
}
