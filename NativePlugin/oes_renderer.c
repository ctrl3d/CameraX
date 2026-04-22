/*  oes_renderer.c  –  Render-thread OES texture updater for Unity
 *
 *  Unity의 GL.IssuePluginEvent 를 통해 **렌더 스레드**에서
 *  SurfaceTexture.updateTexImage() 를 호출하고,
 *  OES 외부 텍스처를 일반 FBO / RenderTexture 로 blit 한다.
 *
 *  핵심: Java 플러그인이 메인 스레드에서 생성한 SurfaceTexture 를
 *  렌더 스레드의 GL 컨텍스트로 재연결(detach/attach)하여
 *  GL 컨텍스트 불일치 문제를 해결한다.
 *
 *  빌드 :  NDK clang  (arm64-v8a / armeabi-v7a)
 */

#include <stdint.h>
#include <stdatomic.h>
#include <string.h>
#include <jni.h>

#include <GLES2/gl2.h>
#include <GLES2/gl2ext.h>
#include <android/log.h>

/* ── Unity Plugin Interface ────────────────────────────────── */
#include "IUnityInterface.h"
#include "IUnityGraphics.h"

/* GL_TEXTURE_EXTERNAL_OES  =  0x8D65  (=36197) */
#ifndef GL_TEXTURE_EXTERNAL_OES
#define GL_TEXTURE_EXTERNAL_OES 0x8D65
#endif

#define TAG "OesRenderer"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO,  TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN,  TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, TAG, __VA_ARGS__)

/* ── Shared state ──────────────────────────────────────────── */
static JavaVM  *g_jvm         = NULL;
static jobject  g_bridgeObj   = NULL;   /* global ref to UnityTextureBridge */
static jmethodID g_updateMid  = NULL;
static int      g_rtWidth     = 0;
static int      g_rtHeight    = 0;
static GLuint   g_rtNativeTex = 0;

/* OES texture currently attached on the render thread */
static GLuint   g_oesTexId    = 0;

/* SurfaceTexture reattach support */
static jmethodID g_getSurfaceTextureMid = NULL;
static jmethodID g_detachMid  = NULL;
static jmethodID g_attachMid  = NULL;
static atomic_int g_needsReattach = 0;
static int      g_reattachAttempts = 0;
#define MAX_REATTACH_ATTEMPTS 10

/* OES → RT blit GL resources */
static GLuint g_fbo       = 0;
static GLuint g_program   = 0;
static GLuint g_vbo       = 0;
static int    g_locTex    = -1;
static int    g_glInitialized = 0;

/* Render thread JNI attach state — attach once, never detach */
static int    g_renderThreadAttached = 0;

/* Stats */
static atomic_int g_framesUpdated = 0;   /* update() returned true */
static atomic_int g_framesSkipped = 0;   /* update() returned false (no new frame) */
static atomic_int g_errorCount    = 0;
static atomic_int g_renderEvents  = 0;   /* total OnRenderEvent calls */

/* One-shot first-frame log flag */
static int g_loggedFirstUpdate = 0;

/* ── JNI_OnLoad — capture JavaVM ───────────────────────────── */
JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM *vm, void *reserved)
{
    (void)reserved;
    g_jvm = vm;
    LOGI("JNI_OnLoad - JavaVM captured");
    return JNI_VERSION_1_6;
}

/* ── Helpers: log Java exceptions and GL errors ────────────── */

static void log_java_exception(JNIEnv *env, const char *where)
{
    if (!(*env)->ExceptionCheck(env)) return;

    jthrowable ex = (*env)->ExceptionOccurred(env);
    (*env)->ExceptionClear(env);

    if (ex == NULL)
    {
        LOGE("[%s] exception cleared but jthrowable was NULL", where);
        atomic_fetch_add(&g_errorCount, 1);
        return;
    }

    jclass exClass = (*env)->GetObjectClass(env, ex);
    jmethodID toStringMid = exClass != NULL
        ? (*env)->GetMethodID(env, exClass, "toString", "()Ljava/lang/String;")
        : NULL;

    if (toStringMid != NULL)
    {
        jstring jmsg = (jstring)(*env)->CallObjectMethod(env, ex, toStringMid);
        if (!(*env)->ExceptionCheck(env) && jmsg != NULL)
        {
            const char *cmsg = (*env)->GetStringUTFChars(env, jmsg, NULL);
            LOGE("[%s] java: %s", where, cmsg ? cmsg : "(null)");
            if (cmsg) (*env)->ReleaseStringUTFChars(env, jmsg, cmsg);
            (*env)->DeleteLocalRef(env, jmsg);
        }
        else
        {
            (*env)->ExceptionClear(env);
            LOGE("[%s] exception.toString() itself failed", where);
        }
    }
    else
    {
        LOGE("[%s] could not resolve toString on exception", where);
    }

    if (exClass) (*env)->DeleteLocalRef(env, exClass);
    (*env)->DeleteLocalRef(env, ex);
    atomic_fetch_add(&g_errorCount, 1);
}

static int check_gl_error(const char *where)
{
    int hadError = 0;
    GLenum err;
    while ((err = glGetError()) != GL_NO_ERROR)
    {
        LOGE("[%s] glError 0x%04x", where, err);
        hadError = 1;
        atomic_fetch_add(&g_errorCount, 1);
    }
    return hadError;
}

/* ── GL helpers ────────────────────────────────────────────── */

static const char *VS_SRC =
    "attribute vec2 aPos;\n"
    "varying   vec2 vUV;\n"
    "void main() {\n"
    "  vUV = aPos * 0.5 + 0.5;\n"
    "  gl_Position = vec4(aPos, 0.0, 1.0);\n"
    "}\n";

static const char *FS_SRC =
    "#extension GL_OES_EGL_image_external : require\n"
    "precision mediump float;\n"
    "varying vec2 vUV;\n"
    "uniform samplerExternalOES uTex;\n"
    "void main() {\n"
    "  vec2 uv = vec2(vUV.x, 1.0 - vUV.y);\n"
    /* Force opaque alpha — camera buffers occasionally carry alpha=0
     * which makes RawImage render transparent for that frame (flicker). */
    "  gl_FragColor = vec4(texture2D(uTex, uv).rgb, 1.0);\n"
    "}\n";

static GLuint compile_shader(GLenum type, const char *src)
{
    GLuint s = glCreateShader(type);
    glShaderSource(s, 1, &src, NULL);
    glCompileShader(s);

    GLint ok = 0;
    glGetShaderiv(s, GL_COMPILE_STATUS, &ok);
    if (!ok)
    {
        char log[1024] = {0};
        glGetShaderInfoLog(s, sizeof(log) - 1, NULL, log);
        LOGE("shader compile failed (type=0x%x): %s", type, log);
    }
    return s;
}

static void init_gl_resources(void)
{
    if (g_glInitialized) return;

    glGenFramebuffers(1, &g_fbo);

    GLuint vs = compile_shader(GL_VERTEX_SHADER, VS_SRC);
    GLuint fs = compile_shader(GL_FRAGMENT_SHADER, FS_SRC);
    g_program = glCreateProgram();
    glAttachShader(g_program, vs);
    glAttachShader(g_program, fs);
    glBindAttribLocation(g_program, 0, "aPos");
    glLinkProgram(g_program);

    GLint linkOk = 0;
    glGetProgramiv(g_program, GL_LINK_STATUS, &linkOk);
    if (!linkOk)
    {
        char log[1024] = {0};
        glGetProgramInfoLog(g_program, sizeof(log) - 1, NULL, log);
        LOGE("program link failed: %s", log);
    }

    glDeleteShader(vs);
    glDeleteShader(fs);

    g_locTex = glGetUniformLocation(g_program, "uTex");

    static const float quad[] = {
        -1, -1,   1, -1,   -1, 1,
         1, -1,   1,  1,   -1, 1
    };
    glGenBuffers(1, &g_vbo);
    glBindBuffer(GL_ARRAY_BUFFER, g_vbo);
    glBufferData(GL_ARRAY_BUFFER, sizeof(quad), quad, GL_STATIC_DRAW);
    glBindBuffer(GL_ARRAY_BUFFER, 0);

    check_gl_error("init_gl_resources");
    g_glInitialized = 1;
    LOGI("GL resources initialized (fbo=%u, prog=%u, vbo=%u, locTex=%d)",
         g_fbo, g_program, g_vbo, g_locTex);
}

/* ── Helper: get JNIEnv on render thread (attach once) ─────── */

static JNIEnv* get_render_thread_env(void)
{
    if (g_jvm == NULL) return NULL;

    JNIEnv *env = NULL;
    if ((*g_jvm)->GetEnv(g_jvm, (void**)&env, JNI_VERSION_1_6) == JNI_OK)
        return env;

    /* Not yet attached — attach and keep attached */
    if ((*g_jvm)->AttachCurrentThread(g_jvm, &env, NULL) != 0)
    {
        LOGE("AttachCurrentThread failed on render thread");
        return NULL;
    }

    g_renderThreadAttached = 1;
    LOGI("render thread attached to JVM");
    return env;
}

/* ── Reattach SurfaceTexture to render thread GL context ─────
 *
 *  반환: 1 = 성공, 0 = 실패(다음 렌더 이벤트에 재시도)
 */
static int reattach_surface_texture(JNIEnv *env)
{
    if (g_bridgeObj == NULL || g_getSurfaceTextureMid == NULL)
    {
        LOGW("reattach: bridge or method IDs missing");
        return 0;
    }
    if (g_detachMid == NULL || g_attachMid == NULL)
    {
        LOGW("reattach: SurfaceTexture method IDs missing");
        return 0;
    }

    jobject surfaceTex = (*env)->CallObjectMethod(env, g_bridgeObj,
                                                  g_getSurfaceTextureMid);
    if ((*env)->ExceptionCheck(env))
    {
        log_java_exception(env, "reattach:getSurfaceTexture");
        return 0;
    }
    if (surfaceTex == NULL)
    {
        LOGW("reattach: SurfaceTexture is null");
        return 0;
    }

    /* 1. Detach from the old (main thread) GL context.
     *    메인 스레드에 애초에 GL 컨텍스트가 없었을 가능성이 있어
     *    실패하더라도 진행. 실패시 내용은 로그로만 기록. */
    (*env)->CallVoidMethod(env, surfaceTex, g_detachMid);
    if ((*env)->ExceptionCheck(env))
    {
        log_java_exception(env, "reattach:detachFromGLContext (non-fatal)");
    }

    /* 2. Create a new OES texture on the render thread */
    while (glGetError() != GL_NO_ERROR) { /* flush pending errors */ }

    GLuint newTex = 0;
    glGenTextures(1, &newTex);
    if (newTex == 0 || check_gl_error("reattach:glGenTextures"))
    {
        LOGE("reattach: glGenTextures failed (newTex=%u)", newTex);
        (*env)->DeleteLocalRef(env, surfaceTex);
        return 0;
    }

    glBindTexture(GL_TEXTURE_EXTERNAL_OES, newTex);
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    if (check_gl_error("reattach:texParameteri"))
    {
        glDeleteTextures(1, &newTex);
        (*env)->DeleteLocalRef(env, surfaceTex);
        return 0;
    }

    /* 3. Attach SurfaceTexture to the new texture on the render thread context */
    (*env)->CallVoidMethod(env, surfaceTex, g_attachMid, (jint)newTex);
    if ((*env)->ExceptionCheck(env))
    {
        log_java_exception(env, "reattach:attachToGLContext");
        glDeleteTextures(1, &newTex);
        (*env)->DeleteLocalRef(env, surfaceTex);
        return 0;
    }

    /* Success — update the OES texture ID */
    if (g_oesTexId != 0 && g_oesTexId != newTex)
    {
        /* previous render-thread tex exists (e.g. previous session leftover) */
        GLuint old = g_oesTexId;
        glDeleteTextures(1, &old);
    }
    g_oesTexId = newTex;

    (*env)->DeleteLocalRef(env, surfaceTex);
    LOGI("SurfaceTexture attached to render thread (newTex=%u)", newTex);
    return 1;
}

/* ── Render-thread callback (called via GL.IssuePluginEvent) ─ */

static void UNITY_INTERFACE_API OnRenderEvent(int eventID)
{
    (void)eventID;
    atomic_fetch_add(&g_renderEvents, 1);

    if (g_jvm == NULL || g_bridgeObj == NULL || g_updateMid == NULL) return;
    if (g_rtNativeTex == 0) return;

    JNIEnv *env = get_render_thread_env();
    if (env == NULL) return;

    /* Reattach SurfaceTexture to render thread GL context if needed */
    if (atomic_load(&g_needsReattach))
    {
        if (g_reattachAttempts >= MAX_REATTACH_ATTEMPTS)
        {
            /* 연속 실패 — 이벤트 당 spam 막고 한번만 포기 로그 */
            if (g_reattachAttempts == MAX_REATTACH_ATTEMPTS)
            {
                LOGE("reattach gave up after %d attempts", MAX_REATTACH_ATTEMPTS);
                g_reattachAttempts++;   /* prevent re-logging */
            }
            return;
        }
        g_reattachAttempts++;
        if (reattach_surface_texture(env))
        {
            atomic_store(&g_needsReattach, 0);
            g_reattachAttempts = 0;
        }
        else
        {
            LOGW("reattach attempt %d/%d failed, will retry next event",
                 g_reattachAttempts, MAX_REATTACH_ATTEMPTS);
            return;   /* skip this frame */
        }
    }

    if (g_oesTexId == 0) return;

    /* updateTexImage() on the render thread's GL context.
     * 반환값: true = 새 프레임 획득, false = 대기 프레임 없음. */
    jboolean newFrame = (*env)->CallBooleanMethod(env, g_bridgeObj, g_updateMid);
    if ((*env)->ExceptionCheck(env))
    {
        log_java_exception(env, "update");
        /* update() 자체가 예외를 던지면 SurfaceTexture 가 나쁜 상태 →
         * 다음 프레임에 재부착을 시도하도록 플래그를 올린다. */
        atomic_store(&g_needsReattach, 1);
        g_reattachAttempts = 0;
        return;
    }

    if (newFrame)
    {
        int n = atomic_fetch_add(&g_framesUpdated, 1) + 1;
        if (!g_loggedFirstUpdate)
        {
            g_loggedFirstUpdate = 1;
            LOGI("first frame received (frameCount=%d)", n);
        }
        /* 이후는 조용히 — 스팸 방지 */
    }
    else
    {
        atomic_fetch_add(&g_framesSkipped, 1);
    }

    /* OES → RenderTexture blit */
    init_gl_resources();
    if (!g_glInitialized) return;

    /* Save GL state */
    GLint prevFBO, prevProg, prevVBO, prevViewport[4], prevActiveTex;
    GLint prevTexBindExt = 0;
    GLint prevScissor, prevDepth, prevBlend, prevCull;
    glGetIntegerv(GL_FRAMEBUFFER_BINDING, &prevFBO);
    glGetIntegerv(GL_CURRENT_PROGRAM, &prevProg);
    glGetIntegerv(GL_ARRAY_BUFFER_BINDING, &prevVBO);
    glGetIntegerv(GL_VIEWPORT, prevViewport);
    glGetIntegerv(GL_ACTIVE_TEXTURE, &prevActiveTex);
    prevScissor = glIsEnabled(GL_SCISSOR_TEST);
    prevDepth   = glIsEnabled(GL_DEPTH_TEST);
    prevBlend   = glIsEnabled(GL_BLEND);
    prevCull    = glIsEnabled(GL_CULL_FACE);

    /* Bind FBO → RT */
    glBindFramebuffer(GL_FRAMEBUFFER, g_fbo);
    glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0,
                           GL_TEXTURE_2D, g_rtNativeTex, 0);

    GLenum fbStatus = glCheckFramebufferStatus(GL_FRAMEBUFFER);
    if (fbStatus != GL_FRAMEBUFFER_COMPLETE)
    {
        LOGE("FBO incomplete: 0x%04x (rtTex=%u, %dx%d)",
             fbStatus, g_rtNativeTex, g_rtWidth, g_rtHeight);
        glBindFramebuffer(GL_FRAMEBUFFER, prevFBO);
        atomic_fetch_add(&g_errorCount, 1);
        return;
    }

    glViewport(0, 0, g_rtWidth, g_rtHeight);
    glDisable(GL_DEPTH_TEST);
    glDisable(GL_BLEND);
    glDisable(GL_CULL_FACE);
    glDisable(GL_SCISSOR_TEST);

    /* Draw fullscreen quad sampling OES texture */
    glUseProgram(g_program);

    glActiveTexture(GL_TEXTURE0);
    glGetIntegerv(0x8D67 /* GL_TEXTURE_BINDING_EXTERNAL_OES */, &prevTexBindExt);
    glBindTexture(GL_TEXTURE_EXTERNAL_OES, g_oesTexId);
    glUniform1i(g_locTex, 0);

    glBindBuffer(GL_ARRAY_BUFFER, g_vbo);
    glEnableVertexAttribArray(0);
    glVertexAttribPointer(0, 2, GL_FLOAT, GL_FALSE, 0, 0);
    glDrawArrays(GL_TRIANGLES, 0, 6);
    glDisableVertexAttribArray(0);

    /* Restore GL state */
    glBindTexture(GL_TEXTURE_EXTERNAL_OES, (GLuint)prevTexBindExt);
    glBindBuffer(GL_ARRAY_BUFFER, prevVBO);
    glBindFramebuffer(GL_FRAMEBUFFER, prevFBO);
    glUseProgram(prevProg);
    glViewport(prevViewport[0], prevViewport[1], prevViewport[2], prevViewport[3]);
    glActiveTexture(prevActiveTex);
    if (prevScissor) glEnable(GL_SCISSOR_TEST);
    if (prevDepth)   glEnable(GL_DEPTH_TEST);
    if (prevBlend)   glEnable(GL_BLEND);
    if (prevCull)    glEnable(GL_CULL_FACE);

    check_gl_error("OnRenderEvent:blit");
}

/* ── Exports (called from C# via P/Invoke) ────────────────── */

UnityRenderingEvent UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
    GetOesRenderEventFunc(void)
{
    return OnRenderEvent;
}

/*
 * OesRenderer_SetBridge — C# 에서 Java bridge 객체의 raw JNI pointer 를 전달.
 * getSurfaceTexture / detachFromGLContext / attachToGLContext 메서드 ID 도 캐시.
 */
void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
    OesRenderer_SetBridge(intptr_t rawBridgePtr)
{
    if (g_jvm == NULL) { LOGE("SetBridge: JavaVM is null"); return; }

    JNIEnv *env = NULL;
    int attached = 0;
    if ((*g_jvm)->GetEnv(g_jvm, (void**)&env, JNI_VERSION_1_6) != JNI_OK)
    {
        if ((*g_jvm)->AttachCurrentThread(g_jvm, &env, NULL) != 0)
        {
            LOGE("SetBridge: AttachCurrentThread failed");
            return;
        }
        attached = 1;
    }

    /* release old ref */
    if (g_bridgeObj != NULL)
    {
        (*env)->DeleteGlobalRef(env, g_bridgeObj);
        g_bridgeObj = NULL;
        g_updateMid = NULL;
        g_getSurfaceTextureMid = NULL;
    }

    if (rawBridgePtr != 0)
    {
        jobject localRef = (jobject)(void*)rawBridgePtr;
        g_bridgeObj = (*env)->NewGlobalRef(env, localRef);
        jclass cls  = (*env)->GetObjectClass(env, g_bridgeObj);
        g_updateMid = (*env)->GetMethodID(env, cls, "update", "()Z");
        if (g_updateMid == NULL)
        {
            log_java_exception(env, "SetBridge:getMethodID(update)");
        }
        g_getSurfaceTextureMid = (*env)->GetMethodID(env, cls,
            "getSurfaceTexture", "()Landroid/graphics/SurfaceTexture;");
        if (g_getSurfaceTextureMid == NULL)
        {
            log_java_exception(env, "SetBridge:getMethodID(getSurfaceTexture)");
        }

        /* Cache SurfaceTexture class methods for detach/attach */
        jclass stClass = (*env)->FindClass(env, "android/graphics/SurfaceTexture");
        if (stClass != NULL)
        {
            g_detachMid = (*env)->GetMethodID(env, stClass,
                "detachFromGLContext", "()V");
            g_attachMid = (*env)->GetMethodID(env, stClass,
                "attachToGLContext", "(I)V");
            (*env)->DeleteLocalRef(env, stClass);
        }
        else
        {
            log_java_exception(env, "SetBridge:FindClass(SurfaceTexture)");
        }

        (*env)->DeleteLocalRef(env, cls);

        /* Signal the render thread to reattach on next OnRenderEvent */
        atomic_store(&g_needsReattach, 1);
        g_reattachAttempts = 0;

        /* reset stats + first-frame log for new session */
        atomic_store(&g_framesUpdated, 0);
        atomic_store(&g_framesSkipped, 0);
        atomic_store(&g_errorCount, 0);
        atomic_store(&g_renderEvents, 0);
        g_loggedFirstUpdate = 0;

        LOGI("SetBridge: cached methodIDs (update=%p, getST=%p, detach=%p, attach=%p)",
             (void*)g_updateMid, (void*)g_getSurfaceTextureMid,
             (void*)g_detachMid, (void*)g_attachMid);
    }
    else
    {
        LOGI("SetBridge: cleared (rawBridgePtr=0)");
    }

    if (attached)
        (*g_jvm)->DetachCurrentThread(g_jvm);
}

void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
    OesRenderer_SetTextures(int oesTexId, int rtNativeTex,
                            int width, int height)
{
    /* oesTexId from Java is just an initial value — will be replaced
       by the render thread after reattach. Not stored. */
    (void)oesTexId;
    g_rtNativeTex = (GLuint)rtNativeTex;
    g_rtWidth     = width;
    g_rtHeight    = height;
    LOGI("SetTextures: rtTex=%u, size=%dx%d", g_rtNativeTex, g_rtWidth, g_rtHeight);
}

void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
    OesRenderer_Release(void)
{
    if (g_jvm == NULL) return;

    JNIEnv *env = NULL;
    int attached = 0;
    if ((*g_jvm)->GetEnv(g_jvm, (void**)&env, JNI_VERSION_1_6) != JNI_OK)
    {
        if ((*g_jvm)->AttachCurrentThread(g_jvm, &env, NULL) != 0) return;
        attached = 1;
    }

    if (g_bridgeObj != NULL)
    {
        (*env)->DeleteGlobalRef(env, g_bridgeObj);
        g_bridgeObj = NULL;
        g_updateMid = NULL;
        g_getSurfaceTextureMid = NULL;
    }

    /* 렌더 스레드에서 만든 OES 텍스처는 렌더 스레드가 아닌 곳에서
     * 삭제하면 위험하므로 여기선 ID만 0으로 비워둔다.
     * 다음 세션 시작시 새 텍스처를 만든다. */
    g_oesTexId    = 0;
    g_rtNativeTex = 0;
    atomic_store(&g_needsReattach, 0);
    g_reattachAttempts = 0;

    if (attached)
        (*g_jvm)->DetachCurrentThread(g_jvm);

    LOGI("Release: bridge cleared");
}

/* ── Diagnostics export ────────────────────────────────────── */

void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
    OesRenderer_GetStats(int *framesUpdated, int *framesSkipped,
                         int *errorCount, int *renderEvents)
{
    if (framesUpdated) *framesUpdated = atomic_load(&g_framesUpdated);
    if (framesSkipped) *framesSkipped = atomic_load(&g_framesSkipped);
    if (errorCount)    *errorCount    = atomic_load(&g_errorCount);
    if (renderEvents)  *renderEvents  = atomic_load(&g_renderEvents);
}

/* ── Unity plugin lifecycle ────────────────────────────────── */
static IUnityInterfaces *s_unityInterfaces = NULL;
static IUnityGraphics   *s_graphics        = NULL;

static void UNITY_INTERFACE_API OnGfxDeviceEvent(UnityGfxDeviceEventType eventType)
{
    if (eventType == kUnityGfxDeviceEventShutdown)
    {
        g_glInitialized = 0;
        g_fbo = g_program = g_vbo = 0;
        g_renderThreadAttached = 0;
        g_oesTexId = 0;
        LOGI("GfxDeviceEventShutdown — GL state cleared");
    }
}

void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
    UnityPluginLoad(IUnityInterfaces *unityInterfaces)
{
    s_unityInterfaces = unityInterfaces;
    s_graphics = UNITY_GET_INTERFACE(unityInterfaces, IUnityGraphics);
    if (s_graphics)
        s_graphics->RegisterDeviceEventCallback(OnGfxDeviceEvent);
    LOGI("UnityPluginLoad");
}

void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
    UnityPluginUnload(void)
{
    if (s_graphics)
        s_graphics->UnregisterDeviceEventCallback(OnGfxDeviceEvent);
    LOGI("UnityPluginUnload");
}
