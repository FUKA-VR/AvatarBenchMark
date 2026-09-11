using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace FUKA.AvatarBenchmark.Editor
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeGpuSample
    {
        public long sequence, forwardNs, shadowNs, cameraNs;
        public int ticket, forwardBlocks, shadowBlocks, cameraBlocks, valid, dropped, flags;
    }

    internal static class NativeGpuProvider
    {
        private const string Library = "FukaAvatarBenchmarkGpu05";
        public const string TimingMethod = "d3d11-draw-boundary-flush";
        private const int CaptureApi = 1;
        private static bool apiChecked;
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int Fuka_CaptureApi();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr Fuka_GetRenderEvent();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int Fuka_Supported();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int Fuka_Pop(out NativeGpuSample sample);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Fuka_Clear();

        public static IntPtr EventPointer()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                throw new InvalidOperationException("GPU計測にはWindows Editor環境およびDirect3D 11が必要です（CPUフレームレート等の計測はそのまま利用可能です）。");
            EnsureCaptureApi();
            var pointer = Fuka_GetRenderEvent();
            if (pointer == IntPtr.Zero || Fuka_Supported() == 0) throw new InvalidOperationException("Direct3D 11 GPUタイムスタンプの初期化に失敗しました。グラフィックドライバや実行環境を確認してください。");
            return pointer;
        }
        public static bool Pop(out NativeGpuSample sample)
        {
            EnsureCaptureApi();
            return Fuka_Pop(out sample) != 0;
        }

        public static void Clear()
        {
            EnsureCaptureApi();
            Fuka_Clear();
        }

        private static void EnsureCaptureApi()
        {
            if (apiChecked) return;
            const string mismatch = "GPU計測用DLLのバージョンが一致しません。シーンを保存してUnityを終了後、同一バージョンのツール一式を配置して再起動してください。";
            try
            {
                if (Fuka_CaptureApi() != CaptureApi) throw new InvalidOperationException(mismatch);
            }
            catch (EntryPointNotFoundException ex) { throw new InvalidOperationException(mismatch, ex); }
            catch (DllNotFoundException ex)
            {
                throw new InvalidOperationException("GPU計測用DLLが見つかりません。Editor/Plugins/x86_64にDLLが存在し、Windows Editor向けのインポート設定が有効になっているか確認してください。", ex);
            }
            catch (BadImageFormatException ex)
            {
                throw new InvalidOperationException("GPU計測用DLLを正常に読み込めませんでした。Windows x86_64用の適切なDLLが配置されているか確認し、Unityを再起動してください。", ex);
            }
            apiChecked = true;
        }
    }
}
