# CameraX for Unity (`work.ctrl3d.camerax`)

Android **CameraX** 기반 Unity 네이티브 카메라 플러그인. OES 외부 텍스처를 렌더
스레드에서 곧바로 `RenderTexture` 로 blit해서 **zero-copy 프리뷰**를 제공한다.

`WebCamTexture` 대비:
- 네이티브 OES 파이프라인으로 CPU 카피 제거
- ISO / 셔터 / 포커스 / AE 수동 제어
- 프레임 stall 자동 감지 & 세션 복구 (워치독)
- 워밍업 완료 이벤트 (`OnWarmupComplete`)

> ⚠️ **Android 전용.** Editor / iOS / Standalone 빌드에서는 `IsAvailable == false`
> 로 no-op. Unity Android Graphics API 는 **OpenGL ES 3** 여야 한다 (Vulkan 미지원).

---

## 설치

### Git URL 로 설치

`Packages/manifest.json` 의 `dependencies` 에 추가:

```json
{
  "dependencies": {
    "work.ctrl3d.camerax": "https://github.com/ctrl3d/CameraX.git?path=Assets/CameraX"
  }
}
```

또는 Unity Editor → `Window > Package Manager` → `+` → `Add package from git URL...`
에 URL 입력.

### 의존 패키지

- `com.unity.ugui` — `RawImage` / `AspectRatioFitter` 사용.
- **EDM4U (External Dependency Manager for Unity)** — CameraX Gradle artifact
  해결에 필요. 소비 프로젝트에 미리 설치되어 있어야 한다.
  `Editor/NativeCameraPluginDependencies.xml` 가 자동으로 수집됨.

---

## 빠른 사용 예

`RawImage` + `AspectRatioFitter` 가 붙은 GameObject 에 `NativeCameraController`
컴포넌트를 추가하기만 하면 끝:

```csharp
using UnityEngine;
using VIV.Components.WebCam.Native;

public class CameraHost : MonoBehaviour
{
    public NativeCameraController cam;
    public GameObject loadingSpinner;

    void Awake()
    {
        cam.OnWarmupComplete += () => loadingSpinner.SetActive(false);
    }

    // 수동 제어 예시
    public void Darker()   => cam.SetIso(100);
    public void Brighter() => cam.SetIso(1600);
    public void FreezeFast()  => cam.SetShutterSpeedSeconds(1f / 1000f);
    public void FocusNear()   => cam.SetFocusDistance(10f);   // diopter
    public void TakeShot()    => cam.TakePhotoAndSave();
}
```

### 인스펙터 주요 필드

| 필드 | 기본값 | 설명 |
|---|---|---|
| Play On Start | true | 컴포넌트 활성화 시 자동 시작 |
| Facing | Front | 전/후면 선택 |
| Is Mirrored | true | 좌우 반전 (주로 Front 용) |
| Requested Width / Height | 1600 × 1200 | 요청 해상도 |
| Frame Stall Restart Seconds | 3 | 정상 재생 중 stall 감지 기준 |
| Startup Timeout Seconds | 15 | 첫 프레임 대기 한계 |
| Warmup Min Cumulative Frames | 60 | 워밍업 완료 누적 프레임 |
| Warmup Stable Seconds | 3 | 워밍업 완료 판정용 안정 시간 |

### 공개 API (요약)

```csharp
class NativeCameraController : MonoBehaviour
{
    Texture  PreviewTexture   { get; }
    bool     IsWarmupComplete { get; }     // 안정화 여부 (latched)
    int      RestartCount     { get; }     // 누적 자동 재시작 횟수
    float    TimeSinceLastRestart { get; }

    event Action          OnWarmupComplete; // 1회 발화
    event Action<string>  OnPhotoSaved;     // 저장 경로
    event Action<string>  OnPhotoError;     // 에러 메시지

    Task StartCameraAsync();
    void StopCamera();

    // 노출/포커스
    void SetAutoExposure(bool enabled);
    void SetIso(int iso);
    void SetShutterSpeedSeconds(float seconds);
    void SetFocusDistance(float diopter);
    void TriggerAutoFocus();                // 수동 → AF 복귀

    // 기타
    void SetTorch(bool enabled);            // 플래시 라이트
    void SetZoomRatio(float ratio);         // 1.0 기본, >1 망원

    // 캡처
    string TakePhotoAndSave();              // 저장 경로 반환. 완료는 OnPhotoSaved.
}
```

---

## 내부 구조

```
Unity Scene (RawImage)
        │  RenderTexture
        ▼
┌─────────────────────────┐
│  NativeCameraController │  C# MonoBehaviour: 수명·워치독·UI 바인딩
└──────────┬──────────────┘
           │
┌──────────▼──────────────┐
│ AndroidNativeCameraBridge │  JNI/P-Invoke 래퍼
└────┬───────────┬─────────┘
     │           │
     │ JNI       │ P/Invoke
     ▼           ▼
 ┌───────┐   ┌────────────────┐
 │ .aar  │   │ liboes_renderer │  GL.IssuePluginEvent 콜백:
 │ Kotlin│   │   .so (per ABI) │   updateTexImage + OES→RT blit
 └───────┘   └────────────────┘
 CameraX · SurfaceTexture 생산자
```

### 플러그인 바이너리

| 파일 | 역할 |
|---|---|
| `Plugins/Android/NativeCameraPlugin-release.aar` | Kotlin + CameraX + UnityTextureBridge |
| `Plugins/Android/libs/arm64-v8a/liboes_renderer.so` | 렌더 스레드 OES 블리터 (ARMv8) |
| `Plugins/Android/libs/armeabi-v7a/liboes_renderer.so` | 동 ARMv7 |

---

## 제약 / 알려진 이슈

- **Graphics API**: OpenGL ES 3 필수. Vulkan 선택 시 로드 실패.
- **첫 시작 지연**: 일부 산업용 OEM 기기(예: `OrderPAD_3`)는 카메라 HAL 워밍업 과정에서
  2~3 회 자동 재시작이 발생하다가 안정됨. `OnWarmupComplete` 로 UI 마스킹 권장.
  해당 기기에서는 인스펙터의 **Single Camera Workaround** 를 켜면 Kotlin 쪽에서
  워밍업을 건너뛰는 우회 모드로 동작해 첫 프레임이 즉시 들어온다.
- **권한**: `android.permission.CAMERA` 는 AAR 의 manifest 에 이미 포함. 런타임 권한 요청은
  컨트롤러 내부에서 처리.

---

## 라이선스

MIT. 자세한 내용은 `LICENSE.md`.
