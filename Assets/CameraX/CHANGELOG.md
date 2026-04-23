# Changelog

모든 변경사항은 [Keep a Changelog](https://keepachangelog.com/ko/1.1.0/) 형식과
[Semantic Versioning](https://semver.org/lang/ko/) 을 따른다.

## [1.2.0] - 2026-04-23

### Added
- **프리웜(Prewarm) 2단계 초기화** — `PrewarmAsync()` 메서드 추가.
  카메라 페이지 진입 전에 권한 요청 → Bridge 생성 → GL 텍스처 생성 → Java Bridge 폴링을
  미리 수행하여 `StartCameraAsync()` 호출 시 UI 바인딩만으로 즉시 완료 (~10ms).
- `IsPrewarmed` 읽기 전용 프로퍼티 — 프리웜 완료 상태를 외부에서 확인 가능.
- `SetExternalPrewarmState(AndroidNativeCameraBridge, int)` — 별도 MonoBehaviour
  (프리웜 헬퍼)가 수행한 프리웜 결과를 주입하여 빠른 경로로 전환.

### Changed
- `StartCameraAsync()` — 프리웜 완료 상태이면 빠른 경로(UI 바인딩만)로 분기.
  비프리웜 경로에서도 이전 세션의 잔여 OES 텍스처를 자동 정리하여
  "abandoned SurfaceTexture" 에러 방지.
- `StopCamera()` — 프리웜만 된 상태(`_isPlaying=false`)에서 호출해도
  CameraX 세션을 올바르게 정리하도록 수정.
- `OnApplicationPause(true)` — 프리웜 상태에서 백그라운드 전환 시
  `StopPreview()` + `ReleaseNativeBridge()` 로 CameraX 세션 명시 정리.
  재생 중이었는지 여부를 `_wasPlayingBeforePause` 로 기록하여 resume 시
  불필요한 재시작 방지.
- `OnDestroy()` — `StopCamera()` 내부에서 프리웜 정리가 처리되므로 중복 플래그
  리셋 제거.

### Internal
- `ResetWatchdogState()` 헬퍼 추출 — 워치독/워밍업 초기화 코드 중복 제거.
- `CleanupPrewarm()` 헬퍼 — 프리웜 실패 시 OES 텍스처·세션 정리.

## [1.1.0] - 2026-04-22

### Added
- `TriggerAutoFocus()` — 수동 포커스 이후 AF 복귀용 원-샷 트리거.
- `SetTorch(bool)` — 플래시/토치 라이트 on/off.
- `SetZoomRatio(float)` — 줌 제어.
- `singleCameraWorkaround` 인스펙터 필드 — LENS_FACING null 인 산업용 기기 우회.
- Photo 콜백 이벤트 — `OnPhotoSaved(string path)`, `OnPhotoError(string error)`.
  내부적으로 숨김 GameObject + `UnitySendMessage` 로 Kotlin 콜백 라우팅.
- `TakePhotoAndSave()` 가 저장 경로를 반환하도록 변경 (기존 호출자 호환).

### Required
- AAR 을 최신 버전으로 재빌드 필요 (새 Kotlin API 포함).
  [ctrl3d/NativeCameraPlugin](https://github.com/ctrl3d/NativeCameraPlugin) 참조.

## [1.0.0] - 2026-04-22

### Added
- 최초 릴리스.
- Android CameraX 기반 네이티브 카메라 프리뷰.
- OES 외부 텍스처 → RenderTexture zero-copy blit 파이프라인.
- 수동 제어 API: ISO / 셔터 / 포커스 / AE.
- `TakePhotoAndSave` — persistentDataPath 에 JPG 저장.
- 프레임 stall 감지 워치독 + in-place 재시작 (RT 유지).
- 워밍업 상태: `IsWarmupComplete` 속성 + `OnWarmupComplete` 이벤트.
- `OesRenderer_GetStats` 네이티브 통계 export.
