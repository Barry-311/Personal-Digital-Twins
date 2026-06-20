using System;
using System.Runtime.InteropServices;

namespace UnityTcp.Editor.Native
{
    /// <summary>
    /// Low-level P/Invoke declarations for the NativeWindowBridge streaming plugin.
    /// Mirrors NativeWindowBridge.h C ABI exactly.
    /// </summary>
    internal static class NativeWindowBridgeAPI
    {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
        private const string DllName = "NativeWindowBridge";

        [DllImport(DllName)]
        internal static extern int NWB_StartServer(int httpPort);

        [DllImport(DllName)]
        internal static extern void NWB_StopServer();

        [DllImport(DllName)]
        internal static extern int NWB_IsRunning();

        [DllImport(DllName)]
        internal static extern int NWB_GetBoundPort();

        [DllImport(DllName, CharSet = CharSet.Ansi)]
        internal static extern int NWB_UpdateWindowList(string windowsJson);

        [DllImport(DllName)]
        internal static extern void NWB_SetUnityPid(int pid);

        [DllImport(DllName)]
        internal static extern int NWB_GetWindowListJson(byte[] buffer, int bufferCapacity, out int outBytes);

        [DllImport(DllName)]
        internal static extern int NWB_GetStreamStatusJson(byte[] buffer, int bufferCapacity, out int outBytes);

        // Offscreen rendering mode: C# pushes frames and polls input events.
        [DllImport(DllName)]
        internal static extern int NWB_StartOffscreenCapture(int fps, int width, int height);

        /// <summary>Stops offscreen/OS capture on the native side (resets m_OffscreenMode).</summary>
        [DllImport(DllName)]
        internal static extern void NWB_StopCapture();

        [DllImport(DllName)]
        internal static extern int NWB_PushFrame(IntPtr bgraData, int width, int height, int stride);

        [DllImport(DllName)]
        internal static extern int NWB_GetPendingInput(byte[] buffer, int bufferSize);

        [DllImport(DllName)]
        internal static extern int NWB_GetPendingOffscreenRequest(
            byte[] windowTypeBuf, int bufSize,
            out int outFps, out int outWidth, out int outHeight);

        [DllImport(DllName)]
        internal static extern int NWB_GetPendingCompositeRequest(
            byte[] jsonBuf, int bufSize,
            out int outFps, out int outWidth, out int outHeight);

        [DllImport(DllName)]
        internal static extern int NWB_IsOffscreenActive();

        // Check for pending resize request from frontend (via /stream/resize).
        // Returns 1 if pending (and clears it), 0 otherwise.
        [DllImport(DllName)]
        internal static extern int NWB_GetPendingResize(out int outWidth, out int outHeight);

        // Query the active offscreen capture window type (survives domain reload).
        // Returns bytes written (excluding null), or 0 if no active offscreen.
        [DllImport(DllName)]
        internal static extern int NWB_GetActiveOffscreenWindowType(byte[] buffer, int bufferSize);

        // Send a UTF-8 JSON message to the browser via the WebRTC DataChannel.
        [DllImport(DllName)]
        internal static extern int NWB_SendDataChannelMessage(byte[] jsonUtf8, int length);

        // Log callback: routes C++ log messages to Unity Console.
        internal delegate void NWB_LogCallbackDelegate(int level, string message);

        [DllImport(DllName)]
        internal static extern void NWB_SetLogCallback(NWB_LogCallbackDelegate callback);
#endif
    }
}
