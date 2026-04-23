using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.UI;

namespace CameraX
{
    /// <summary>
    /// Camera2/CameraX 기반 네이티브 카메라 컨트롤러.
    /// 기존 WebCamController 의 드롭인 대체. ISO / 셔터 / 포커스 수동 제어 지원.
    ///
    /// OES 외부 텍스처는 렌더 스레드의 GL 컨텍스트에서만 올바르게 처리할 수 있으므로,
    /// 네이티브 C 플러그인(liboes_renderer.so)을 통해 GL.IssuePluginEvent 로
    /// 렌더 스레드에서 updateTexImage + OES→RT blit 을 수행한다.
    /// </summary>
    [RequireComponent(typeof(RawImage), typeof(AspectRatioFitter))]
    public class NativeCameraController : MonoBehaviour
    {
        public enum CameraFacing { Back = 0, Front = 1 }

        [Header("Settings")]
        [SerializeField] private bool playOnStart = true;
        [SerializeField] private CameraFacing facing = CameraFacing.Front;

        [field: SerializeField]
        public bool IsMirrored { get; set; } = true;

        [Header("Requested Capture")]
        [SerializeField] private int requestedWidth = 1600;
        [SerializeField] private int requestedHeight = 1200;

        [Header("Saved Photo Output")]
        [SerializeField] private int savedWidth = 1600;
        [SerializeField] private int savedHeight = 1200;

        [Header("Diagnostics")]
        [Tooltip("첫 프레임을 받은 뒤 프레임이 끊기면 자동 재시작할 시간(초). 0 이면 비활성.")]
        [SerializeField] private float frameStallRestartSeconds = 3f;

        [Tooltip("첫 프레임이 끝내 안 오면 재시작할 시간(초). CameraX 재시도를 허용할 만큼 여유 있게. 0 이면 비활성.")]
        [SerializeField] private float startupTimeoutSeconds = 15f;

        [Tooltip("진단 로그를 몇 초 간격으로 남길지. 0 이면 비활성.")]
        [SerializeField] private float diagnosticsIntervalSeconds = 2f;

        [Header("Warmup 판정 기준")]
        [Tooltip("이 프레임 수 이상 받았고, 아래 조건이 충족되면 워밍업 완료로 판단.")]
        [SerializeField] private int warmupMinCumulativeFrames = 60;

        [Tooltip("마지막 재시작 이후 이 시간만큼 무-재시작 상태가 유지되면 안정으로 판단.")]
        [SerializeField] private float warmupStableSeconds = 3f;

        [Header("단일 카메라 기기 우회")]
        [Tooltip("LENS_FACING 이 null 인 산업용 패드 등에서 워밍업 재시작을 건너뛰기 위한 Kotlin 쪽 우회 모드. " +
                 "일반 기기에서는 끄세요.")]
        [SerializeField] private bool singleCameraWorkaround;

        private AndroidNativeCameraBridge _bridge;
        private RenderTexture _previewRT;
        private RawImage _rawImage;
        private AspectRatioFitter _fitter;
        private bool _isInitialized;
        private bool _isPlaying;
        private bool _isRestarting;

        // ─── Prewarm 상태 ─────────────────────────────────────
        private bool _isPrewarmed;
        private bool _isPrewarming;

        private IntPtr _renderEventFunc;
        private int _oesTexId;

        // Mirror 상태 추적 — Canvas 더티 최소화
        private float _lastAppliedScaleX = float.NaN;

        // 프레임 stall 감지용
        private int _lastObservedFrames;
        private float _lastFrameProgressTime;
        private float _playStartTime;
        private bool _everReceivedFrame;
        private float _nextDiagLogTime;

        /// <summary>WebCamCorrectedFeed 호환용 — RT로 변환해 쓰려면 이 텍스처를 Blit 대상으로.</summary>
        public Texture PreviewTexture => _previewRT;

        public AndroidNativeCameraBridge.CameraCapabilities Capabilities { get; private set; }

        // ─── Photo 콜백 ───────────────────────────────────────

        /// <summary>TakePhoto 성공 시 호출. 인자: 저장된 파일 경로.</summary>
        public event System.Action<string> OnPhotoSaved;

        /// <summary>TakePhoto 실패 시 호출. 인자: 에러 메시지.</summary>
        public event System.Action<string> OnPhotoError;

        private GameObject _callbackReceiver;

        // ─── Warmup 상태 ──────────────────────────────────────

        /// <summary>워밍업 완료 시점에 한 번 발화. UI 로딩 해제에 사용.</summary>
        public event System.Action OnWarmupComplete;

        /// <summary>워밍업이 완료되어 프레임 스트림이 안정화됐는지 (한 번 true 되면 유지).</summary>
        public bool IsWarmupComplete { get; private set; }

        /// <summary>진단용: 지금까지 카메라 자동 재시작이 몇 번 일어났는지.</summary>
        public int RestartCount => _restartCount;

        /// <summary>진단용: 마지막 재시작(또는 세션 시작) 후 경과 시간.</summary>
        public float TimeSinceLastRestart =>
            _lastRestartCompletedTime > 0f
                ? Time.realtimeSinceStartup - _lastRestartCompletedTime
                : Time.realtimeSinceStartup - _playStartTime;

        private float _lastRestartCompletedTime;

        private void Awake()
        {
            _rawImage = GetComponent<RawImage>();
            _fitter = GetComponent<AspectRatioFitter>();
        }

        private async void Start()
        {
            if (playOnStart)
                await StartCameraAsync();
        }

        /// <summary>
        /// 카메라 하드웨어를 미리 초기화합니다 (UI 바인딩 제외).
        /// 권한 요청 → Bridge 생성 → GL 텍스처 생성 → Java Bridge 대기까지 수행.
        /// PhotoShootPage 진입 전에 호출하면 StartCameraAsync()가 즉시 완료됩니다.
        /// 이미 프리웜 중이거나 카메라가 재생 중이면 무시합니다.
        /// </summary>
        public async Task PrewarmAsync()
        {
            if (_isPrewarming || _isPrewarmed || _isPlaying) return;
            _isPrewarming = true;

            try
            {
                // 1) 권한 요청
                if (!_isInitialized)
                {
                    if (!await RequestCameraPermissionAsync())
                    {
                        Debug.LogWarning("[NativeCamera] Prewarm: Permission denied");
                        return;
                    }
                    _bridge = new AndroidNativeCameraBridge();
                    _isInitialized = true;
                }

                if (!_bridge.IsAvailable)
                {
                    Debug.LogWarning("[NativeCamera] Prewarm: Plugin unavailable.");
                    return;
                }

                _bridge.SetSingleCameraWorkaround(singleCameraWorkaround);

                // 2) 콜백 수신 GameObject 준비
                EnsureCallbackReceiver();
                _bridge.SetCallbackObjectName(_callbackReceiver.name);

                // 3) GL 컨텍스트에서 OES 텍스처 생성
                var tcs = new TaskCompletionSource<int>();
                StartCoroutine(StartPreviewOnRenderThread(tcs));
                _oesTexId = await tcs.Task;

                if (_oesTexId == 0)
                {
                    Debug.LogError("[NativeCamera] Prewarm: Failed (texId=0).");
                    return;
                }

                // 4) Java Bridge 폴링 대기 (최대 2초 — 이 대기를 미리 소화)
                const int maxBridgeWaitMs = 2000;
                const int bridgePollMs = 20;
                bool bridgeReady = false;
                for (int waited = 0; waited < maxBridgeWaitMs; waited += bridgePollMs)
                {
                    if (_bridge.SetNativeBridge())
                    {
                        bridgeReady = true;
                        break;
                    }
                    await Task.Delay(bridgePollMs);
                }

                if (!bridgeReady)
                {
                    Debug.LogWarning("[NativeCamera] Prewarm: Bridge not ready within timeout.");
                    // 프리웜 실패 — StartCameraAsync에서 정상 경로로 진행
                    CleanupPrewarm();
                    return;
                }

                _isPrewarmed = true;
                Debug.Log($"[NativeCamera] Prewarm complete. oesTexId={_oesTexId}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NativeCamera] Prewarm failed: {ex}");
                CleanupPrewarm();
            }
            finally
            {
                _isPrewarming = false;
            }
        }

        /// <summary>프리웜 실패 시 정리.</summary>
        private void CleanupPrewarm()
        {
            _isPrewarmed = false;
            if (_oesTexId != 0)
            {
                _bridge?.StopPreview();
                _oesTexId = 0;
            }
        }

        /// <summary>프리웜 상태인지 외부에서 확인할 수 있는 프로퍼티.</summary>
        public bool IsPrewarmed => _isPrewarmed;

        /// <summary>
        /// 외부에서 미리 초기화된 Bridge 와 OES 텍스처 ID 를 주입합니다.
        /// 별도의 MonoBehaviour(프리웜 헬퍼)가 수행한 결과를 이 인스턴스에 넘겨,
        /// 이후 StartCameraAsync() 호출 시 빠른 경로(UI 바인딩만)로 진행하게 합니다.
        /// 콜백 수신 GameObject 는 StartCameraAsync 빠른 경로에서 자동 생성됩니다.
        /// 이미 재생 중이거나 프리웜 상태이면 무시합니다.
        /// </summary>
        public void SetExternalPrewarmState(AndroidNativeCameraBridge bridge, int oesTexId)
        {
            if (_isPlaying || _isPrewarmed || _isPrewarming) return;
            if (bridge == null || oesTexId == 0) return;

            _bridge = bridge;
            _oesTexId = oesTexId;
            _isInitialized = true;
            _isPrewarmed = true;
            Debug.Log($"[NativeCamera] External prewarm state injected. oesTexId={oesTexId}");
        }

        public async Task StartCameraAsync()
        {
            if (_isPlaying) return;

            // ── 프리웜된 상태라면 빠른 경로: UI 바인딩만 수행 ──
            if (_isPrewarmed)
            {
                _isPrewarmed = false;

                // 외부 프리웜 시 콜백 수신자가 없을 수 있으므로 보장
                EnsureCallbackReceiver();
                _bridge.SetCallbackObjectName(_callbackReceiver.name);

                // RenderTexture 생성 + UI 바인딩
                _previewRT = new RenderTexture(requestedWidth, requestedHeight, 0, RenderTextureFormat.ARGB32)
                {
                    name = "NativeCameraPreviewRT",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                _previewRT.Create();

                _rawImage.texture = _previewRT;
                _fitter.aspectRatio = (float)requestedWidth / requestedHeight;

                // 프리웜 시 이미 SetNativeBridge 완료 → 텍스처 포인터만 전달
                var rtNativePtr = (int)_previewRT.GetNativeTexturePtr();
                _bridge.SetNativeTextures(_oesTexId, rtNativePtr, requestedWidth, requestedHeight);
                _renderEventFunc = _bridge.RenderEventFunc;

                _isPlaying = true;
                ResetWatchdogState();

                Capabilities = _bridge.GetCapabilities();
                Debug.Log($"[NativeCamera] Started (prewarmed). texId={_oesTexId}, rtPtr={rtNativePtr}.");
                return;
            }

            // ── 프리웜 안 된 경우: 기존 전체 초기화 경로 ──
            if (!_isInitialized)
            {
                if (!await RequestCameraPermissionAsync())
                {
                    Debug.LogWarning("[NativeCamera] Permission denied");
                    return;
                }
                _bridge = new AndroidNativeCameraBridge();
                _isInitialized = true;
            }

            if (!_bridge.IsAvailable)
            {
                Debug.LogWarning("[NativeCamera] Plugin unavailable. (Editor or missing .aar)");
                return;
            }

            _bridge.SetSingleCameraWorkaround(singleCameraWorkaround);

            EnsureCallbackReceiver();
            _bridge.SetCallbackObjectName(_callbackReceiver.name);

            var tcs2 = new TaskCompletionSource<int>();
            StartCoroutine(StartPreviewOnRenderThread(tcs2));
            _oesTexId = await tcs2.Task;

            if (_oesTexId == 0)
            {
                Debug.LogError("[NativeCamera] Failed to start preview (texId=0).");
                return;
            }

            _previewRT = new RenderTexture(requestedWidth, requestedHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = "NativeCameraPreviewRT",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _previewRT.Create();

            _rawImage.texture = _previewRT;
            _fitter.aspectRatio = (float)requestedWidth / requestedHeight;

            const int maxBridgeWaitMs = 2000;
            const int bridgePollMs = 20;
            for (int waited = 0; waited < maxBridgeWaitMs; waited += bridgePollMs)
            {
                if (_bridge.SetNativeBridge()) break;
                await Task.Delay(bridgePollMs);
            }

            var rtPtr = (int)_previewRT.GetNativeTexturePtr();
            _bridge.SetNativeTextures(_oesTexId, rtPtr, requestedWidth, requestedHeight);
            _renderEventFunc = _bridge.RenderEventFunc;

            _isPlaying = true;
            ResetWatchdogState();

            Capabilities = _bridge.GetCapabilities();
            Debug.Log($"[NativeCamera] Started. texId={_oesTexId}, rtPtr={rtPtr}.");
        }

        /// <summary>워치독/워밍업 상태를 초기화합니다.</summary>
        private void ResetWatchdogState()
        {
            _lastObservedFrames = 0;
            _playStartTime = Time.realtimeSinceStartup;
            _lastFrameProgressTime = _playStartTime;
            _everReceivedFrame = false;
            _nextDiagLogTime = _playStartTime + diagnosticsIntervalSeconds;
            _lastRestartCompletedTime = 0f;
            _restartCount = 0;
            IsWarmupComplete = false;
        }

        public void StopCamera()
        {
            if (!_isPlaying && !_isPrewarmed) return;

            _isPlaying = false;
            _isPrewarmed = false;    // 프리웜만 된 상태에서 Stop 호출 시에도 정리
            _bridge?.StopPreview();
            _bridge?.ReleaseNativeBridge();
            ReleaseTextures();
        }

        private void ReleaseTextures()
        {
            if (_previewRT != null)
            {
                _previewRT.Release();
                Destroy(_previewRT);
                _previewRT = null;
            }
            if (_rawImage != null) _rawImage.texture = null;
        }

        private IEnumerator StartPreviewOnRenderThread(TaskCompletionSource<int> tcs)
        {
            const int maxAttempts = 5;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                yield return new WaitForEndOfFrame();
                var texId = _bridge.StartPreview(requestedWidth, requestedHeight,
                    facing == CameraFacing.Front);
                if (texId != 0)
                {
                    Debug.Log($"[NativeCamera] GL texture created (texId={texId}) on attempt {attempt + 1}");
                    tcs.SetResult(texId);
                    yield break;
                }
                Debug.LogWarning($"[NativeCamera] startPreview returned texId=0 (attempt {attempt + 1}/{maxAttempts}), retrying next frame...");
            }
            tcs.SetResult(0);
        }

        // ─── Per-frame render-thread update ──────────────────

        private void Update()
        {
            if (!_isPlaying) return;

            // Mirror 처리 — 값이 바뀐 프레임에만 쓰기 (Canvas 더티 방지)
            var scaleX = IsMirrored ? -1f : 1f;
            if (scaleX != _lastAppliedScaleX)
            {
                _rawImage.rectTransform.localScale = new Vector3(scaleX, 1f, 1f);
                _lastAppliedScaleX = scaleX;
            }

            // 렌더 스레드에서 updateTexImage + OES→RT blit 실행
            if (_renderEventFunc != IntPtr.Zero)
            {
                GL.IssuePluginEvent(_renderEventFunc, 0);
            }

            // 프레임 stall 감지 + 주기적 진단 로그
            TickWatchdog();
        }

        private void TickWatchdog()
        {
            if (_bridge == null || _isRestarting) return;

            var stats = _bridge.GetStats();
            var now = Time.realtimeSinceStartup;

            if (stats.FramesUpdated != _lastObservedFrames)
            {
                _lastObservedFrames = stats.FramesUpdated;
                _lastFrameProgressTime = now;
                _everReceivedFrame = true;
            }

            if (diagnosticsIntervalSeconds > 0f && now >= _nextDiagLogTime)
            {
                _nextDiagLogTime = now + diagnosticsIntervalSeconds;
                var age = _everReceivedFrame
                    ? now - _lastFrameProgressTime
                    : now - _playStartTime;
                var phase = _everReceivedFrame ? "stallAge" : "startupAge";
                Debug.Log($"[NativeCamera] stats: {stats} ({phase}={age:F1}s)");
            }

            if (_everReceivedFrame)
            {
                // Warmup 완료 판정 — 누적 프레임 충족 + 마지막 재시작 후 충분히 무탈
                if (!IsWarmupComplete
                    && stats.FramesUpdated >= warmupMinCumulativeFrames
                    && TimeSinceLastRestart >= warmupStableSeconds)
                {
                    IsWarmupComplete = true;
                    Debug.Log($"[NativeCamera] Warmup complete. " +
                              $"frames={stats.FramesUpdated}, restarts={_restartCount}, " +
                              $"sinceLastRestart={TimeSinceLastRestart:F1}s");
                    OnWarmupComplete?.Invoke();
                }

                // 정상 재생 중 stall → 지정 시간 경과 시 재시작
                if (frameStallRestartSeconds > 0f
                    && (now - _lastFrameProgressTime) > frameStallRestartSeconds)
                {
                    Debug.LogError($"[NativeCamera] Frame stall detected ({now - _lastFrameProgressTime:F1}s " +
                                   $"without new frames). Stats: {stats}. Restarting camera...");
                    _ = RestartCameraAsync();
                }
            }
            else
            {
                // 아직 첫 프레임을 못 받음 → CameraX 초기화 재시도 시간 고려해 넉넉히 대기
                if (startupTimeoutSeconds > 0f
                    && (now - _playStartTime) > startupTimeoutSeconds)
                {
                    Debug.LogError($"[NativeCamera] No frames after {now - _playStartTime:F1}s " +
                                   $"since start. Stats: {stats}. Restarting camera...");
                    _ = RestartCameraAsync();
                }
            }
        }

        private int _restartCount;

        /// <summary>
        /// 세션만 뜯어내고 RT/RawImage 는 살려둔 채 재시작한다.
        /// 사용자는 검은 화면 대신 "마지막 프레임이 잠깐 정지 → 재생 재개" 로 본다.
        /// 실패 시에만 전면 재시작(StopCamera+StartCameraAsync) 으로 폴백.
        /// </summary>
        private async Task RestartCameraAsync()
        {
            if (_isRestarting) return;
            _isRestarting = true;
            _restartCount++;
            var restartStart = Time.realtimeSinceStartup;
            Debug.LogWarning($"[NativeCamera] Restart #{_restartCount} — keeping RT visible.");
            try
            {
                if (_isPlaying)
                {
                    _isPlaying = false;
                    _bridge?.StopPreview();
                    _bridge?.ReleaseNativeBridge();
                    // 주의: ReleaseTextures() 를 부르지 않는다 — RT / RawImage.texture 유지.
                }

                await Task.Yield();

                if (!await RestartPreviewInPlaceAsync())
                {
                    Debug.LogWarning("[NativeCamera] In-place restart failed — falling back to full reset.");
                    ReleaseTextures();
                    await StartCameraAsync();
                }
                Debug.Log($"[NativeCamera] Restart #{_restartCount} complete in " +
                          $"{Time.realtimeSinceStartup - restartStart:F2}s");
                _lastRestartCompletedTime = Time.realtimeSinceStartup;
            }
            finally
            {
                _isRestarting = false;
            }
        }

        private async Task<bool> RestartPreviewInPlaceAsync()
        {
            if (_bridge == null || !_bridge.IsAvailable) return false;
            if (_previewRT == null) return false;

            var tcs = new TaskCompletionSource<int>();
            StartCoroutine(StartPreviewOnRenderThread(tcs));
            var texId = await tcs.Task;
            if (texId == 0) return false;

            _oesTexId = texId;
            // Java 쪽 session이 runOnUiThread로 비동기 생성되므로, 준비될 때까지 대기
            const int maxBridgeWaitMs = 2000;
            const int bridgePollMs = 20;
            for (int waited = 0; waited < maxBridgeWaitMs; waited += bridgePollMs)
            {
                if (_bridge.SetNativeBridge()) break;
                await Task.Delay(bridgePollMs);
            }
            var rtNativePtr = (int)_previewRT.GetNativeTexturePtr();
            _bridge.SetNativeTextures(_oesTexId, rtNativePtr, requestedWidth, requestedHeight);
            _renderEventFunc = _bridge.RenderEventFunc;

            _isPlaying = true;

            // 워치독 상태 리셋
            _lastObservedFrames = 0;
            _playStartTime = Time.realtimeSinceStartup;
            _lastFrameProgressTime = _playStartTime;
            _everReceivedFrame = false;
            _nextDiagLogTime = _playStartTime + diagnosticsIntervalSeconds;
            return true;
        }

        /// <summary>
        /// 앱이 백그라운드로 갔다 돌아오면 Android 카메라 세션과 GL 텍스처가
        /// 무효화되므로, 복귀 시 카메라를 완전히 재시작한다.
        /// </summary>
        private async void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus)
            {
                _isPrewarmed = false;  // 백그라운드 전환 시 프리웜 무효화
                if (_isPlaying)
                {
                    _isPlaying = false;
                    _bridge?.StopPreview();
                    _bridge?.ReleaseNativeBridge();
                    ReleaseTextures();
                    Debug.Log("[NativeCamera] Paused — camera stopped.");
                }
            }
            else
            {
                if (_isInitialized && !_isPlaying && gameObject.activeInHierarchy)
                {
                    Debug.Log("[NativeCamera] Resumed — restarting camera...");
                    await StartCameraAsync();
                }
            }
        }

        // ─── Manual Controls (public API) ────────────────────

        public void SetAutoExposure(bool enabled) => _bridge?.SetAutoExposure(enabled);
        public void SetIso(int iso) => _bridge?.SetIso(iso);
        public void SetShutterSpeedSeconds(float seconds)
            => _bridge?.SetExposureTimeNs((long)(seconds * 1_000_000_000L));
        public void SetFocusDistance(float diopter) => _bridge?.SetFocusDistance(diopter);

        /// <summary>한번-샷 오토포커스 트리거. 수동 포커스 후 AF 복귀 용도.</summary>
        public void TriggerAutoFocus() => _bridge?.SetAutoFocus();

        /// <summary>플래시(토치) 라이트. 후면 카메라 전용인 기기가 많음.</summary>
        public void SetTorch(bool enabled) => _bridge?.SetTorch(enabled);

        /// <summary>줌 비율. 1.0 = 기본 광각, > 1.0 = 망원. 기기별 지원 범위 상이.</summary>
        public void SetZoomRatio(float ratio) => _bridge?.SetZoomRatio(ratio);

        // ─── Capture ─────────────────────────────────────────

        /// <summary>
        /// 사진을 persistentDataPath 에 JPG 로 저장. 결과는 OnPhotoSaved/OnPhotoError 이벤트로.
        /// 반환: 저장 경로 (비동기 저장이므로 파일 존재는 OnPhotoSaved 때 보장).
        /// </summary>
        public string TakePhotoAndSave()
        {
            var fileName = $"IMG_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
            var path = Path.Combine(Application.persistentDataPath, fileName);
            _bridge?.TakePhoto(path);
            Debug.Log($"[NativeCamera] Saving to: {path}");
            return path;
        }

        // ─── Photo 콜백 수신 GameObject ───────────────────────

        private void EnsureCallbackReceiver()
        {
            if (_callbackReceiver != null) return;
            _callbackReceiver = new GameObject($"__NativeCameraCB_{GetInstanceID()}")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var receiver = _callbackReceiver.AddComponent<PhotoCallbackReceiver>();
            receiver.Owner = this;
        }

        private void DestroyCallbackReceiver()
        {
            if (_callbackReceiver != null)
            {
                Destroy(_callbackReceiver);
                _callbackReceiver = null;
            }
        }

        internal void InvokePhotoSaved(string path) => OnPhotoSaved?.Invoke(path);
        internal void InvokePhotoError(string error) => OnPhotoError?.Invoke(error);

        // ─── Permission (기존 WebCamController 로직 재사용) ───
        private static async Task<bool> RequestCameraPermissionAsync()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Permission.HasUserAuthorizedPermission(Permission.Camera)) return true;
            var tcs = new TaskCompletionSource<bool>();
            var cb = new PermissionCallbacks();
            cb.PermissionGranted += _ => tcs.TrySetResult(true);
            cb.PermissionDenied  += _ => tcs.TrySetResult(false);
            Permission.RequestUserPermission(Permission.Camera, cb);
            return await tcs.Task;
#else
            await Task.Yield();
            return true;
#endif
        }

        private void OnDestroy()
        {
            _isPrewarmed = false;
            StopCamera();
            DestroyCallbackReceiver();
            _bridge?.Dispose();
        }
    }

    /// <summary>
    /// Kotlin 측이 UnitySendMessage 로 부르는 콜백 수신자.
    /// 메서드 이름(OnPhotoSaved/OnPhotoError)은 Kotlin README 에 고정.
    /// </summary>
    internal sealed class PhotoCallbackReceiver : MonoBehaviour
    {
        public NativeCameraController Owner;

        // 시그니처 변경 금지 — UnitySendMessage 가 string 1개로 부름.
        public void OnPhotoSaved(string path) => Owner?.InvokePhotoSaved(path);
        public void OnPhotoError(string error) => Owner?.InvokePhotoError(error);
    }
}