using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CameraX
{
    /// <summary>
    /// JNI 얇은 래퍼. NativeCameraPlugin.kt 와 1:1 매칭.
    /// 모든 public 메서드는 스레드-세이프하다고 가정하지 말 것 (Unity 메인 스레드에서 호출).
    /// </summary>
    public sealed class AndroidNativeCameraBridge : IDisposable
    {
        private const string PluginClass = "work.ctrl3d.camerax.NativeCameraPlugin";

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaClass _plugin;
#endif

        public bool IsAvailable =>
#if UNITY_ANDROID && !UNITY_EDITOR
            _plugin != null;
#else
            false;
#endif

        public AndroidNativeCameraBridge()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { _plugin = new AndroidJavaClass(PluginClass); }
            catch (Exception e)
            {
                Debug.LogError($"[NativeCamera] Plugin not found: {e.Message}");
                _plugin = null;
            }
#endif
        }

        public int StartPreview(int width, int height, bool useFront)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return _plugin?.CallStatic<int>("startPreview", width, height, useFront) ?? 0;
#else
            return 0;
#endif
        }

        public void StopPreview()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _plugin?.CallStatic("stopPreview");
#endif
        }

        // ─── Native OES Renderer bridge ─────────────────────

#if UNITY_ANDROID && !UNITY_EDITOR
        [DllImport("oes_renderer")]
        private static extern IntPtr GetOesRenderEventFunc();

        [DllImport("oes_renderer")]
        private static extern void OesRenderer_SetTextures(int oesTexId, int rtNativeTex,
            int width, int height);

        [DllImport("oes_renderer")]
        private static extern void OesRenderer_SetBridge(IntPtr bridgeObj);

        [DllImport("oes_renderer")]
        private static extern void OesRenderer_Release();

        [DllImport("oes_renderer")]
        private static extern void OesRenderer_GetStats(
            out int framesUpdated, out int framesSkipped,
            out int errorCount, out int renderEvents);
#endif

        /// <summary>렌더 스레드에서 실행할 OES 업데이트 콜백 함수 포인터.</summary>
        public IntPtr RenderEventFunc
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return GetOesRenderEventFunc();
#else
                return IntPtr.Zero;
#endif
            }
        }

        /// <summary>네이티브 플러그인에 OES 텍스처 ID, RT native ptr, 크기를 전달.</summary>
        public void SetNativeTextures(int oesTexId, int rtNativePtr, int width, int height)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            OesRenderer_SetTextures(oesTexId, rtNativePtr, width, height);
#endif
        }

        /// <summary>
        /// 네이티브 플러그인에 Java UnityTextureBridge 객체를 전달.
        /// 성공 시 true, session 또는 bridge가 아직 null이면 false.
        /// </summary>
        public bool SetNativeBridge()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                // NativeCameraPlugin.session (private static field)
                var session = _plugin?.GetStatic<AndroidJavaObject>("session");
                if (session == null)
                {
                    return false;
                }

                // CameraXSession.bridge (private field)
                var bridge = session.Get<AndroidJavaObject>("bridge");
                if (bridge == null)
                {
                    return false;
                }

                var rawBridge = bridge.GetRawObject();
                OesRenderer_SetBridge(rawBridge);
                Debug.Log("[NativeCamera] Native bridge object set successfully.");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[NativeCamera] Failed to set native bridge: {e.Message}");
                return false;
            }
#else
            return false;
#endif
        }

        /// <summary>네이티브 플러그인의 bridge 참조 해제.</summary>
        public void ReleaseNativeBridge()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { OesRenderer_Release(); }
            catch (Exception e) { Debug.LogWarning($"[NativeCamera] ReleaseNative failed: {e.Message}"); }
#endif
        }

        /// <summary>
        /// 네이티브 플러그인이 집계한 프레임 업데이트 통계.
        /// 프레임 freeze 여부 판단에 사용.
        /// </summary>
        public struct RenderStats
        {
            public int FramesUpdated;   // update() → true 횟수 (실제 카메라 프레임 수신)
            public int FramesSkipped;   // update() → false 횟수 (대기 프레임 없음)
            public int ErrorCount;      // JNI/GL 오류 누적
            public int RenderEvents;    // OnRenderEvent 호출 횟수

            public override string ToString() =>
                $"frames={FramesUpdated}, skipped={FramesSkipped}, errors={ErrorCount}, events={RenderEvents}";
        }

        public RenderStats GetStats()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            OesRenderer_GetStats(out var u, out var s, out var e, out var r);
            return new RenderStats { FramesUpdated = u, FramesSkipped = s, ErrorCount = e, RenderEvents = r };
#else
            return default;
#endif
        }

        // ─── Manual Controls ────────────────────────────────
        public void SetAutoExposure(bool enabled)
            => Call("setAutoExposure", enabled);

        public void SetIso(int iso)
            => Call("setIso", iso);

        public void SetExposureTimeNs(long nanos)
            => Call("setExposureTimeNs", nanos);

        public void SetFocusDistance(float diopter)
            => Call("setFocusDistance", diopter);

        /// <summary>한번-샷 오토포커스 트리거. 수동 포커스 세팅 이후 AF 복귀 용도.</summary>
        public void SetAutoFocus()
            => Call("setAutoFocus");

        /// <summary>플래시(토치) 라이트 on/off. 후면 카메라에서만 동작하는 기기가 많음.</summary>
        public void SetTorch(bool enabled)
            => Call("setTorchEnabled", enabled);

        /// <summary>줌 비율. 1.0 = 기본, > 1.0 = 망원. 지원 범위는 기기별 상이.</summary>
        public void SetZoomRatio(float ratio)
            => Call("setZoomRatio", ratio);

        /// <summary>
        /// AE 노출 보정. <paramref name="index"/> 는 <see cref="CameraCapabilities.exposureCompensationMin"/>
        /// ~ <see cref="CameraCapabilities.exposureCompensationMax"/> 범위의 정수 스텝.
        /// 실 EV 값은 <c>index * exposureCompensationStep</c>. AE 가 켜져 있어야 의미가 있음.
        /// </summary>
        public void SetExposureCompensation(int index)
            => Call("setExposureCompensation", index);

        /// <summary>
        /// 단일-카메라 특수 기기(LENS_FACING 이 null 인 산업용 패드 등) 용 우회 모드.
        /// startPreview 호출 전에 활성화하면 해당 기기에서 워밍업 재시작 없이
        /// 첫 세션부터 프레임이 흐른다.
        /// </summary>
        public void SetSingleCameraWorkaround(bool enable)
            => Call("setSingleCameraWorkaround", enable);

        /// <summary>
        /// 단일-카메라 기기에서 CameraX 초기화 시 LENS_FACING_BACK 검증 재시도(~6 초)를
        /// 건너뛰기 위해, 플러그인보다 먼저 <c>ProcessCameraProvider.configureInstance</c>를
        /// 호출하여 <c>setAvailableCamerasLimiter</c>를 설정합니다.
        /// <para><c>StartPreview</c> 호출 전에 한 번만 실행하면 됩니다.
        /// 이미 구성된 경우 예외를 잡아 무시합니다.</para>
        /// </summary>
        public static void PreConfigureCameraXForSingleCamera(bool useFront)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var camera2Config = new AndroidJavaClass("androidx.camera.camera2.Camera2Config");
                using var defaultConfig = camera2Config.CallStatic<AndroidJavaObject>("defaultConfig");

                using var builder = new AndroidJavaClass("androidx.camera.core.CameraXConfig$Builder")
                    .CallStatic<AndroidJavaObject>("fromConfig", defaultConfig);

                // 사용할 카메라 방향만 허용 → CameraValidator 가 반대쪽 카메라를 찾지 않음
                using var selectorClass = new AndroidJavaClass("androidx.camera.core.CameraSelector");
                using var selector = selectorClass.GetStatic<AndroidJavaObject>(
                    useFront ? "DEFAULT_FRONT_CAMERA" : "DEFAULT_BACK_CAMERA");
                builder.Call<AndroidJavaObject>("setAvailableCamerasLimiter", selector);

                // 플러그인(ensureConfigured)과 동일한 executor/log 설정
                using var executors = new AndroidJavaClass("java.util.concurrent.Executors");
                using var executor = executors.CallStatic<AndroidJavaObject>("newSingleThreadExecutor");
                builder.Call<AndroidJavaObject>("setCameraExecutor", (AndroidJavaObject)executor);
                builder.Call<AndroidJavaObject>("setMinimumLoggingLevel", 4); // Log.INFO

                using var config = builder.Call<AndroidJavaObject>("build");

                using var provider = new AndroidJavaClass(
                    "androidx.camera.lifecycle.ProcessCameraProvider");
                provider.CallStatic("configureInstance", config);

                Debug.Log($"[NativeCamera] Pre-configured CameraX (useFront={useFront}, limiter applied)");
            }
            catch (Exception e)
            {
                // 이미 구성됐거나 다른 이유로 실패 → 로그만 남기고 진행
                Debug.LogWarning($"[NativeCamera] PreConfigureCameraX skipped: {e.Message}");
            }
#endif
        }

        /// <summary>
        /// <c>ProcessCameraProvider.getInstance()</c> 를 미리 호출하여
        /// CameraX 내부 초기화를 백그라운드에서 선행합니다.
        /// SurfaceTexture 나 카메라 세션은 생성하지 않으므로
        /// GL 컨텍스트 / 라이프사이클 문제가 없습니다.
        /// <para><c>PreConfigureCameraXForSingleCamera</c> 호출 후 바로 실행하면
        /// 카메라 페이지 진입 시 provider 가 이미 캐시되어 즉시 사용 가능합니다.</para>
        /// </summary>
        public static void WarmUpCameraProvider()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var provider = new AndroidJavaClass(
                    "androidx.camera.lifecycle.ProcessCameraProvider");
                // getInstance() 는 ListenableFuture 를 반환하며, 내부적으로
                // CameraX 초기화를 비동기로 시작한다. 결과는 캐시되므로
                // 이후 Java 쪽 CameraXSession.start() 에서 즉시 완료된다.
                using var future = provider.CallStatic<AndroidJavaObject>("getInstance", activity);
                Debug.Log("[NativeCamera] ProcessCameraProvider.getInstance() warm-up triggered.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NativeCamera] WarmUpCameraProvider skipped: {e.Message}");
            }
#endif
        }

        /// <summary>
        /// UnitySendMessage 콜백(OnPhotoSaved / OnPhotoError) 이 전달될 GameObject 이름.
        /// NativeCameraController 가 내부 숨김 GameObject 를 만들어 연결함.
        /// </summary>
        public void SetCallbackObjectName(string name)
            => Call("setCallbackObjectName", name);

        public void TakePhoto(string savePath)
            => Call("takePhoto", savePath);

        public CameraCapabilities GetCapabilities()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var json = _plugin?.CallStatic<string>("getCapabilitiesJson") ?? "{}";
            return JsonUtility.FromJson<CameraCapabilities>(json) ?? new CameraCapabilities();
#else
            return new CameraCapabilities();
#endif
        }

        private void Call(string method, params object[] args)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _plugin?.CallStatic(method, args);
#endif
        }

        public void Dispose()
        {
            ReleaseNativeBridge();
            StopPreview();
#if UNITY_ANDROID && !UNITY_EDITOR
            _plugin?.Dispose();
            _plugin = null;
#endif
        }

        [Serializable]
        public class CameraCapabilities
        {
            public int isoMin;
            public int isoMax;
            public long exposureMinNs;
            public long exposureMaxNs;
            public float minFocusDiopter;
            public int hardwareLevel; // 0=LIMITED 1=FULL 2=LEGACY 3=LEVEL_3

            // AE 노출 보정 (CONTROL_AE_COMPENSATION_RANGE / _STEP)
            public int exposureCompensationMin;
            public int exposureCompensationMax;
            public float exposureCompensationStep; // 한 스텝당 EV (예: 1/3 ≈ 0.333)
        }
    }
}