using System;

namespace UnityTcp.Editor.Native
{
    // P/Invoke surface for the unified NativeTcpBridge (NTB_*) C ABI.
    // One TCP listener serves both inbound commands and outbound notifications.
    internal static class NativeUnityTcpBridgeAPI
    {
#if UNITY_EDITOR_WIN || UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
        internal delegate int  StartDelegate(int requestedPort, int maxFrameBytes);
        internal delegate void StopDelegate();
        internal delegate int  IsRunningDelegate();
        internal delegate int  GetBoundPortDelegate();
        internal delegate int  GetConnectionCountDelegate();
        internal delegate int  TryDequeueCommandDelegate(out ulong outRequestId, byte[] buffer, int bufferCapacity, out int outPayloadBytes);
        internal delegate int  EnqueueResponseDelegate(ulong requestId, byte[] payload, int payloadBytes);
        internal delegate int  GetClientsJsonDelegate(byte[] buffer, int bufferCapacity, out int outBytes);
        internal delegate void SetIsCSharpAssemblyReloadingDelegate(int isReloading);
        internal delegate int  NotifyAllDelegate(byte[] payload, int payloadBytes);

        internal static StartDelegate                        NTB_Start;
        internal static StopDelegate                         NTB_Stop;
        internal static IsRunningDelegate                    NTB_IsRunning;
        internal static GetBoundPortDelegate                 NTB_GetBoundPort;
        internal static GetConnectionCountDelegate           NTB_GetConnectionCount;
        internal static TryDequeueCommandDelegate            NTB_TryDequeueCommand;
        internal static EnqueueResponseDelegate              NTB_EnqueueResponse;
        internal static GetClientsJsonDelegate               NTB_GetClientsJson;
        internal static SetIsCSharpAssemblyReloadingDelegate NTB_SetIsCSharpAssemblyReloading;
        internal static NotifyAllDelegate                    NTB_NotifyAll;
#endif
    }
}
