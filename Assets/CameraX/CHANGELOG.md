# Changelog

모든 변경사항은 [Keep a Changelog](https://keepachangelog.com/ko/1.1.0/) 형식과
[Semantic Versioning](https://semver.org/lang/ko/) 을 따른다.

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
