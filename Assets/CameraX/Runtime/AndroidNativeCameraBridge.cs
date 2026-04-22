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
        /// 단일-카메라 특수 기기(LENS_FACING 이 null 인 산업용 패드 등) 용 우회 모드.
        /// startPreview 호출 전에 활성화하면 해당 기기에서 워밍업 재시작 없이
        /// 첫 세션부터 프레임이 흐른다.
        /// </summary>
        public void SetSingleCameraWorkaround(bool enable)
            => Call("setSingleCameraWorkaround", enable);

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
        }
    }
}