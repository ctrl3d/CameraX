using System.Text;
using CameraX;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

/// <summary>
/// Android 빌드 동작 검증용 테스트 러너.
/// 씬에 붙이면 Canvas/RawImage/버튼/진단 텍스트를 런타임에 생성하고
/// NativeCameraController 를 연결한다.
///
/// 자동 부트스트랩: 씬 로드 직후 기존 NativeCameraController 가 없으면
/// GameObject 를 만들어 이 스크립트를 부착한다.
/// 이미 씬에 설정이 있으면 건너뛴다.
/// </summary>
public class CameraTestRunner : MonoBehaviour
{
    [SerializeField] private bool autoStart = true;
    [SerializeField] private NativeCameraController.CameraFacing initialFacing =
        NativeCameraController.CameraFacing.Back;

    private NativeCameraController _camera;
    private Text _statusText;
    private Text _logText;
    private Button _startBtn, _stopBtn, _flipBtn, _torchBtn, _shotBtn;

    private readonly StringBuilder _sb = new StringBuilder(512);
    private readonly StringBuilder _logSb = new StringBuilder(1024);
    private bool _torchOn;
    private string _lastPhotoPath;
    private string _lastError;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoBootstrap()
    {
        if (FindObjectOfType<CameraTestRunner>() != null) return;
        if (FindObjectOfType<NativeCameraController>() != null) return;

        var go = new GameObject("CameraTestRunner");
        go.AddComponent<CameraTestRunner>();
        DontDestroyOnLoad(go);
    }

    private void Start()
    {
        BuildUI();
        SetLog($"[Init] Platform={Application.platform}, GfxAPI={SystemInfo.graphicsDeviceType}");
    }

    private void Update()
    {
        if (_camera == null || _statusText == null) return;

        _sb.Clear();
        _sb.Append("IsWarmupComplete: ").Append(_camera.IsWarmupComplete).Append('\n');
        _sb.Append("RestartCount:     ").Append(_camera.RestartCount).Append('\n');
        _sb.Append("SinceLastRestart: ").AppendFormat("{0:F1}s", _camera.TimeSinceLastRestart).Append('\n');

        var caps = _camera.Capabilities;
        if (caps != null)
        {
            _sb.Append("Caps ISO:  ").Append(caps.isoMin).Append('~').Append(caps.isoMax).Append('\n');
            _sb.Append("Caps Exp:  ").Append(caps.exposureMinNs).Append('~').Append(caps.exposureMaxNs).Append(" ns\n");
            _sb.Append("HW Level:  ").Append(caps.hardwareLevel).Append('\n');
        }

        var tex = _camera.PreviewTexture;
        _sb.Append("Preview:   ");
        _sb.Append(tex != null ? $"{tex.width}x{tex.height}" : "null").Append('\n');

        if (!string.IsNullOrEmpty(_lastPhotoPath))
            _sb.Append("LastPhoto: ").Append(_lastPhotoPath).Append('\n');
        if (!string.IsNullOrEmpty(_lastError))
            _sb.Append("LastError: ").Append(_lastError).Append('\n');

        _statusText.text = _sb.ToString();

        if (_torchBtn != null)
            _torchBtn.GetComponentInChildren<Text>().text = _torchOn ? "Torch: ON" : "Torch: OFF";
    }

    private void BuildUI()
    {
        var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight = 0.5f;

        if (FindObjectOfType<EventSystem>() == null)
        {
#if ENABLE_INPUT_SYSTEM
            var esGo = new GameObject("EventSystem",
                typeof(EventSystem),
                typeof(InputSystemUIInputModule));
#else
            var esGo = new GameObject("EventSystem",
                typeof(EventSystem),
                typeof(StandaloneInputModule));
#endif
            DontDestroyOnLoad(esGo);
        }

        var previewGo = new GameObject("CameraPreview",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(RawImage),
            typeof(AspectRatioFitter));
        previewGo.transform.SetParent(canvasGo.transform, false);
        var previewRT = (RectTransform)previewGo.transform;
        previewRT.anchorMin = new Vector2(0.05f, 0.15f);
        previewRT.anchorMax = new Vector2(0.95f, 0.85f);
        previewRT.offsetMin = Vector2.zero;
        previewRT.offsetMax = Vector2.zero;

        var fitter = previewGo.GetComponent<AspectRatioFitter>();
        fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        fitter.aspectRatio = 4f / 3f;

        var rawImage = previewGo.GetComponent<RawImage>();
        rawImage.color = Color.white;

        _camera = previewGo.AddComponent<NativeCameraController>();
        SetPrivateField(_camera, "playOnStart", autoStart);
        SetPrivateField(_camera, "facing", initialFacing);
        SetPrivateField(_camera, "diagnosticsIntervalSeconds", 2f);

        _camera.OnPhotoSaved += path =>
        {
            _lastPhotoPath = path;
            SetLog($"[Photo] Saved: {path}");
        };
        _camera.OnPhotoError += err =>
        {
            _lastError = err;
            SetLog($"[Photo] Error: {err}");
        };
        _camera.OnWarmupComplete += () => SetLog("[Warmup] Complete");

        _statusText = CreateLabel(canvasGo.transform, "Status",
            new Vector2(0f, 0.85f), new Vector2(1f, 1f),
            TextAnchor.UpperLeft, 26, Color.white);
        _statusText.rectTransform.offsetMin = new Vector2(20, 0);
        _statusText.rectTransform.offsetMax = new Vector2(-20, -10);

        _logText = CreateLabel(canvasGo.transform, "Log",
            new Vector2(0f, 0.02f), new Vector2(1f, 0.13f),
            TextAnchor.LowerLeft, 22, new Color(0.9f, 0.95f, 1f));
        _logText.rectTransform.offsetMin = new Vector2(20, 0);
        _logText.rectTransform.offsetMax = new Vector2(-20, 0);

        var btnRow = new GameObject("Buttons", typeof(RectTransform),
            typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
        btnRow.transform.SetParent(canvasGo.transform, false);
        var btnRT = (RectTransform)btnRow.transform;
        btnRT.anchorMin = new Vector2(0f, 0f);
        btnRT.anchorMax = new Vector2(1f, 0.15f);
        btnRT.offsetMin = new Vector2(10, 10);
        btnRT.offsetMax = new Vector2(-10, -10);
        var hlg = btnRow.GetComponent<HorizontalLayoutGroup>();
        hlg.spacing = 10;
        hlg.childForceExpandWidth = true;
        hlg.childForceExpandHeight = true;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;

        _startBtn = CreateButton(btnRow.transform, "Start", OnStart);
        _stopBtn = CreateButton(btnRow.transform, "Stop", OnStop);
        _flipBtn = CreateButton(btnRow.transform, "Flip", OnFlip);
        _torchBtn = CreateButton(btnRow.transform, "Torch: OFF", OnTorch);
        _shotBtn = CreateButton(btnRow.transform, "Shot", OnShot);
    }

    private static Text CreateLabel(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax,
        TextAnchor align, int fontSize, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text),
            typeof(Outline));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var text = go.GetComponent<Text>();
        text.alignment = align;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = fontSize;
        text.color = color;
        text.supportRichText = true;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;

        var outline = go.GetComponent<Outline>();
        outline.effectColor = new Color(0, 0, 0, 0.7f);
        outline.effectDistance = new Vector2(1, -1);
        return text;
    }

    private static Button CreateButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject($"Btn_{label}", typeof(RectTransform), typeof(CanvasRenderer),
            typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.color = new Color(0.15f, 0.15f, 0.15f, 0.9f);

        var btn = go.GetComponent<Button>();
        btn.onClick.AddListener(onClick);

        var textGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        textGo.transform.SetParent(go.transform, false);
        var labelRT = (RectTransform)textGo.transform;
        labelRT.anchorMin = Vector2.zero;
        labelRT.anchorMax = Vector2.one;
        labelRT.offsetMin = Vector2.zero;
        labelRT.offsetMax = Vector2.zero;
        var text = textGo.GetComponent<Text>();
        text.alignment = TextAnchor.MiddleCenter;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 28;
        text.color = Color.white;
        text.text = label;
        return btn;
    }

    private async void OnStart()
    {
        SetLog("[Action] Start");
        if (_camera != null) await _camera.StartCameraAsync();
    }

    private void OnStop()
    {
        SetLog("[Action] Stop");
        if (_camera != null) _camera.StopCamera();
    }

    private async void OnFlip()
    {
        if (_camera == null) return;
        var cur = GetPrivateField<NativeCameraController.CameraFacing>(_camera, "facing");
        var next = cur == NativeCameraController.CameraFacing.Front
            ? NativeCameraController.CameraFacing.Back
            : NativeCameraController.CameraFacing.Front;
        SetPrivateField(_camera, "facing", next);
        _camera.IsMirrored = next == NativeCameraController.CameraFacing.Front;
        SetLog($"[Action] Flip → {next}");
        _camera.StopCamera();
        await _camera.StartCameraAsync();
    }

    private void OnTorch()
    {
        if (_camera == null) return;
        _torchOn = !_torchOn;
        _camera.SetTorch(_torchOn);
        SetLog($"[Action] Torch={_torchOn}");
    }

    private void OnShot()
    {
        if (_camera == null) return;
        SetLog("[Action] Capture");
        _camera.TakePhotoAndSave();
    }

    private void SetLog(string line)
    {
        Debug.Log($"[CameraTest] {line}");
        _logSb.Length = 0;
        _logSb.Append(System.DateTime.Now.ToString("HH:mm:ss.fff")).Append("  ").Append(line);
        if (_logText != null) _logText.text = _logSb.ToString();
    }

    private static void SetPrivateField(object target, string name, object value)
    {
        var f = target.GetType().GetField(name,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        f?.SetValue(target, value);
    }

    private static T GetPrivateField<T>(object target, string name)
    {
        var f = target.GetType().GetField(name,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        return f != null ? (T)f.GetValue(target) : default;
    }

}
