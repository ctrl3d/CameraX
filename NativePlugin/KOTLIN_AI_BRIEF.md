# NativeCameraPlugin Kotlin 수정 지시서 (Claude 에이전트용)

당신은 `com.viv.nativecamera` Kotlin 프로젝트(AAR 소스)를 수정하는 작업을 맡았습니다.
이 프로젝트는 Unity Android 앱이 `NativeCameraPlugin-release.aar` 로 로드하는
CameraX 기반 카메라 플러그인입니다.

Unity 쪽(C# / C 플러그인)은 이미 안정 상태이고 **이 작업에서는 건드리지 않습니다**.
Kotlin 쪽의 두 가지 runtime 버그만 정확히 고치고 AAR 을 다시 빌드해 주세요.

---

## 0. 시작 전에 조사할 것

1. Kotlin 소스 루트에서 다음 파일을 먼저 읽는다.
   - `NativeCameraPlugin.kt`
   - `CameraXSession.kt`
   - `UnityTextureBridge.kt`
   - `ManualControls.kt`
   - `AndroidManifest.xml`
   - `build.gradle(.kts)` (AAR 산출 설정 확인)
2. `grep` 으로 다음 심볼이 어디서 쓰이는지 파악.
   - `ProcessCameraProvider.getInstance`
   - `CameraSelector` / `DEFAULT_BACK_CAMERA` / `DEFAULT_FRONT_CAMERA`
   - `setOnFrameAvailableListener`
   - `provideSurface` / `SurfaceRequest`
3. 현재 분기(로직) 를 이해한 뒤 아래 수정사항을 적용.

---

## 1. 절대 바꾸지 말아야 할 "계약" (Contract)

Unity C# / C 네이티브 플러그인이 **리플렉션 및 JNI 시그니처로** 접근하는 API 입니다.
하나라도 틀어지면 Unity 쪽이 silent fail 합니다.

### 패키지 / 클래스명
```
com.viv.nativecamera.NativeCameraPlugin    (object)
com.viv.nativecamera.CameraXSession         (class)
com.viv.nativecamera.UnityTextureBridge     (class)
com.viv.nativecamera.ManualControls         (class)
```

### `NativeCameraPlugin` static API (이름·시그니처 고정)
```kotlin
@JvmStatic fun startPreview(width: Int, height: Int, useFront: Boolean): Int
@JvmStatic fun stopPreview()
@JvmStatic fun updateTexture(): Boolean
@JvmStatic fun setIso(iso: Int)
@JvmStatic fun setExposureTimeNs(nanos: Long)
@JvmStatic fun setFocusDistance(diopter: Float)
@JvmStatic fun setAutoExposure(enabled: Boolean)
@JvmStatic fun takePhoto(savePath: String)
@JvmStatic fun getCapabilitiesJson(): String
```

### 리플렉션 대상 필드 (이름·타입 고정)
```kotlin
object NativeCameraPlugin {
    private var session: CameraXSession?   // Unity C# 가 이 이름으로 꺼냄
}
class CameraXSession {
    val bridge: UnityTextureBridge          // Unity C# 가 이 이름으로 꺼냄
}
```

### JNI 대상 메서드 (이름·시그니처 고정)
```kotlin
class UnityTextureBridge {
    fun getSurfaceTexture(): SurfaceTexture     // 시그니처: ()Landroid/graphics/SurfaceTexture;
    fun update(): Boolean                        // 시그니처: ()Z
}
```

이름 변경, 파라미터 변경, `@JvmStatic` 제거, 패키지 이동 — **전부 금지**.

---

## 2. 고칠 버그 #1 — 첫 `startPreview` 시 프레임이 안 옴 (3~15초 정체)

### 증상 (logcat 로그)
```
E CameraValidator : Camera LensFacing verification failed, existing cameras: [Camera@…[id=0]]
W CameraX         : CameraValidator$CameraIdListIncorrectException: Expected camera missing from device.
D CameraValidator : Verifying camera lens facing on OrderPAD_3, lensFacingInteger: null
W CameraValidator : Camera LENS_FACING_BACK verification failed
E BufferQueueProducer [SurfaceTexture-…] queueBuffer:   BufferQueue has been abandoned
E BufferQueueProducer [SurfaceTexture-…] cancelBuffer:  BufferQueue has been abandoned   (×6)
```

### 원인
- 타겟 기기(산업용 태블릿 `OrderPAD_3`)는 카메라가 1개(id=0)뿐이고
  `CameraCharacteristics.LENS_FACING` 이 `null`.
- `ProcessCameraProvider.getInstance(context)` 기본 경로가 `CameraValidator` 로
  LENS_FACING_BACK / FRONT 존재 여부를 검증하며 실패 → 내부 재시도 루프.
- 재시도 중에 Preview 의 `SurfaceRequest` 가 한 번 소비되어 BufferQueue 가 abandoned.
- 재시도가 성공해도 유효 Surface 없는 세션이라 프레임이 절대 흐르지 않음.
- Unity 쪽 워치독이 15초 후 강제 재시작해야 그제야 정상.

### 수정
`NativeCameraPlugin` 첫 진입 시 (첫 `startPreview` 호출 시점) **딱 한 번**
`ProcessCameraProvider.configureInstance(...)` 를 호출해 validator 를 우회한다.

구현 골격:
```kotlin
object NativeCameraPlugin {
    private var session: CameraXSession? = null
    private val configured = java.util.concurrent.atomic.AtomicBoolean(false)

    private fun ensureConfigured() {
        if (!configured.compareAndSet(false, true)) return

        val limiter = androidx.camera.core.CameraSelector.Builder()
            .addCameraFilter { infos ->
                // LENS_FACING 메타데이터가 null 이어도 id 존재하는 카메라를 통과시킴.
                // 해당 기기는 id="0" 하나만 있으므로 그것만 포함.
                infos.filter {
                    val id = androidx.camera.camera2.interop.Camera2CameraInfo
                        .from(it).cameraId
                    id == "0"
                }
            }
            .build()

        val config = androidx.camera.core.CameraXConfig.Builder
            .fromConfig(androidx.camera.camera2.Camera2Config.defaultConfig())
            .setAvailableCamerasLimiter(limiter)
            .setMinimumLoggingLevel(android.util.Log.WARN)
            .build()

        try {
            androidx.camera.lifecycle.ProcessCameraProvider.configureInstance(config)
        } catch (e: IllegalStateException) {
            // 이미 다른 provider 가 초기화된 드문 경우 — 로그만 남기고 진행.
            android.util.Log.w("NativeCameraPlugin",
                "CameraX already configured: ${e.message}")
        }
    }

    @JvmStatic
    fun startPreview(width: Int, height: Int, useFront: Boolean): Int {
        ensureConfigured()
        // … 기존 로직 …
    }
}
```

### 검증
수정 후 logcat 에서 `CameraValidator` / `BufferQueueProducer abandoned` 메시지가
**첫 `startPreview` 에서 더 이상 뜨지 않아야** 하고, 첫 프레임이 1초 이내에
수신되어야 한다.

---

## 3. 고칠 버그 #2 — `setOnFrameAvailableListener` 가 Unity 메인 스레드 Looper 에 묶임

### 증상
`UnityTextureBridge.createExternalTexture()` 안쪽에서:
```kotlin
surfaceTexture.setOnFrameAvailableListener { frameAvailable = true }
```
이렇게 Handler 인자 없이 호출하면 **호출 스레드의 Looper** (Unity 메인 스레드)에서
리스너가 돈다. 메인 스레드가 블록되면 콜백이 밀리거나 유실.

### 수정
전용 HandlerThread 로 리스너를 붙이고, `release()` 에서 안전히 종료.
```kotlin
class UnityTextureBridge(val width: Int, val height: Int) {
    private val cbThread = android.os.HandlerThread("oes-frame-cb").apply { start() }
    private val cbHandler = android.os.Handler(cbThread.looper)

    fun createExternalTexture(): Int {
        // … GL / SurfaceTexture 생성은 기존 로직 유지 …
        surfaceTexture.setOnFrameAvailableListener(
            { frameAvailable = true },
            cbHandler
        )
        return textureId
    }

    fun release() {
        // … 기존 SurfaceTexture.release / glDeleteTextures …
        cbThread.quitSafely()
    }
}
```

`update(): Boolean` 의 시맨틱은 그대로 유지 — volatile `frameAvailable` 플래그
하나로 동작하는 것 변함 없음.

---

## 4. 추가로 해두면 좋은 하드닝 (선택)

- `startPreview` 진입 시 `session != null` 이면 `stopPreview()` 를 먼저 호출.
- `CameraXSession.stop()` 순서: `provider.unbindAll()` → use case null 화 →
  `bridge.release()` → `provider = null`.
- `setIso`, `setExposureTimeNs`, `setFocusDistance`, `setAutoExposure` 는
  `session?.controls?.let { it.setXxx(...) }` 로 널 가드 — 카메라가 아직 안
  열린 상태에서 호출돼도 크래시하지 않게.
- `ProcessCameraProvider.getInstance(...).addListener` 안에서 `bindToLifecycle`
  은 `try / catch (IllegalArgumentException)` 으로 감싸고 실패 시 로그.

---

## 5. AndroidManifest 주의

Unity 는 최종 APK 빌드에서 manifest merge 를 한다. AAR 쪽 `<application>` 에
`android:name=".MyApp"` 같은 걸 넣으면 Unity 의 기본 Application 을 덮어써
실행 불가. **커스텀 `Application` 클래스로 해결하지 말고** 반드시
`ProcessCameraProvider.configureInstance(config)` 를 플러그인 코드에서 직접
호출하는 방식으로 해결할 것.

---

## 6. 빌드 및 산출물

1. `./gradlew :<module>:assembleRelease` (모듈명은 build.gradle 확인).
2. 산출된 `NativeCameraPlugin-release.aar` 경로를 알려 주세요 — 사용자가
   `D:\UnityProjects\AndroidCamera\Assets\Plugins\Android\NativeCameraPlugin-release.aar`
   에 덮어쓸 겁니다.
3. 변경 요약(3~5줄) 을 마지막에 출력 — 어느 파일의 어느 메서드를, 왜 고쳤는지.

---

## 7. 작업 금지사항 (Non-Goals)

- C# / C 네이티브 플러그인 수정 금지. 이 프로젝트엔 Kotlin 소스만 있음.
- Public API / 필드명 / 패키지명 변경 금지 (1 섹션 참조).
- Gradle 의존성 버전 업그레이드 금지 (호환성 리스크).
- `takePhoto` 포맷 / 포커스 알고리즘 / 캡처 파이프라인 건드리지 말 것.
- "김에 리팩토링" 금지. 이 지시서 범위만 수정.

---

## 8. 성공 기준

- 첫 `startPreview` 호출 후 **1초 이내** 첫 프레임 수신 (현재 3~15초).
- logcat 에 `CameraValidator ... verification failed` 가 안 뜬다.
- `BufferQueueProducer ... abandoned` 가 첫 세션에서 안 뜬다.
- `startPreview → stopPreview → startPreview` 반복해도 누수 없음.
- Unity 쪽 `AndroidNativeCameraBridge` / `NativeCameraController` 는 수정 없이
  그대로 동작.
