using System;
using UnityEngine;

namespace FUKA.AvatarBenchmark
{
    [AddComponentMenu("")]
    public sealed class BenchmarkFramePump : MonoBehaviour
    {
        [NonSerialized] public Action tick;
        private void Update() { tick?.Invoke(); }
    }
}
