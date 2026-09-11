using System;
using UnityEngine;
using VRC.SDKBase;

namespace FUKA.AvatarBenchmark
{
    [AddComponentMenu("FUKA/ギミック負荷検証/環境・配置設定")]
    public sealed class BenchmarkEnvironment : MonoBehaviour, IEditorOnly
    {
        [Tooltip("アバターを配置する位置と向きです。複数人での計測時はリストの先頭から順に使用されます（最大10箇所）。")]
        public Transform[] avatarPins = Array.Empty<Transform>();
        [Tooltip("計測対象を撮影するカメラです。計測ウィンドウで使用するカメラを選択できます。")]
        public Camera[] viewpoints = Array.Empty<Camera>();

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.2f, 0.85f, 1f);
            foreach (var pin in avatarPins)
            {
                if (!pin) continue;
                Gizmos.DrawWireSphere(pin.position, 0.12f);
                Gizmos.DrawLine(pin.position, pin.position + pin.forward * 0.5f);
                Gizmos.DrawLine(pin.position, pin.position + Vector3.up * 1.5f);
            }
        }
    }
}
