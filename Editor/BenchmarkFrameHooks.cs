using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine.LowLevel;

namespace FUKA.AvatarBenchmark.Editor
{
    internal sealed class BenchmarkFrameHooks : IDisposable
    {
        private sealed class BeginMarker { }
        private sealed class EndMarker { }
        private sealed class TextureBeginMarker { }
        private sealed class TextureEndMarker { }
        private bool disposed;

        public BenchmarkFrameHooks(Action begin, Action end, Action beginTextures, Action endTextures)
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            Remove(ref loop);
            bool first = Insert(ref loop, typeof(UnityEngine.PlayerLoop.EarlyUpdate),
                new PlayerLoopSystem { type = typeof(BeginMarker), updateDelegate = () => begin() }, true);
            bool last = Insert(ref loop, typeof(UnityEngine.PlayerLoop.PostLateUpdate),
                new PlayerLoopSystem { type = typeof(EndMarker), updateDelegate = () => end() }, false);
            bool textures = InsertAround(ref loop, typeof(UnityEngine.PlayerLoop.PostLateUpdate.UpdateCustomRenderTextures),
                new PlayerLoopSystem { type = typeof(TextureBeginMarker), updateDelegate = () => beginTextures() },
                new PlayerLoopSystem { type = typeof(TextureEndMarker), updateDelegate = () => endTextures() });
            if (!first || !last || !textures) throw new InvalidOperationException("Unityのフレーム計測位置を登録できません。");
            PlayerLoop.SetPlayerLoop(loop);
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        private void OnPlayMode(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode) Dispose();
        }

        private static bool Insert(ref PlayerLoopSystem node, Type parent, PlayerLoopSystem entry, bool first)
        {
            if (node.type == parent)
            {
                var children = new List<PlayerLoopSystem>(node.subSystemList ?? Array.Empty<PlayerLoopSystem>());
                children.Insert(first ? 0 : children.Count, entry);
                node.subSystemList = children.ToArray();
                return true;
            }
            if (node.subSystemList == null) return false;
            for (int i = 0; i < node.subSystemList.Length; i++)
                if (Insert(ref node.subSystemList[i], parent, entry, first)) return true;
            return false;
        }

        private static bool InsertAround(ref PlayerLoopSystem node, Type target, PlayerLoopSystem begin, PlayerLoopSystem end)
        {
            if (node.subSystemList == null) return false;
            for (int i = 0; i < node.subSystemList.Length; i++)
            {
                if (node.subSystemList[i].type == target)
                {
                    var children = new List<PlayerLoopSystem>(node.subSystemList);
                    children.Insert(i, begin); children.Insert(i + 2, end);
                    node.subSystemList = children.ToArray(); return true;
                }
                if (InsertAround(ref node.subSystemList[i], target, begin, end)) return true;
            }
            return false;
        }

        private static void Remove(ref PlayerLoopSystem node)
        {
            if (node.subSystemList == null) return;
            var children = new List<PlayerLoopSystem>();
            foreach (var child in node.subSystemList)
            {
                if (child.type == typeof(BeginMarker) || child.type == typeof(EndMarker) ||
                    child.type == typeof(TextureBeginMarker) || child.type == typeof(TextureEndMarker)) continue;
                var value = child; Remove(ref value); children.Add(value);
            }
            node.subSystemList = children.ToArray();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            Remove(ref loop);
            PlayerLoop.SetPlayerLoop(loop);
        }
    }
}
