# GPUタイムスタンプDLLのビルド

GPU計測には`Editor/Plugins/x86_64/FukaAvatarBenchmarkGpu05.dll`を使用します。この手順はネイティブコードを変更するときに実行します。

ソースを変更する場合は、Visual Studioの「C++によるデスクトップ開発」とWindows SDK、Unity 2022.3のEditorが必要です。Visual Studioの「x64 Native Tools Command Prompt」を開き、このフォルダへ移動して以下を実行します。Unityのインストール先は環境に合わせて変更してください。

```bat
set "BENCH_UNITY_PLUGIN_API=C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Data\PluginAPI"
cl /nologo /std:c++17 /utf-8 /EHsc /O2 /MT /LD /I"%BENCH_UNITY_PLUGIN_API%" GpuTimestamps.cpp /Fo"%TEMP%\FukaAvatarBenchmarkGpu.obj" /link /OUT:"..\Plugins\x86_64\FukaAvatarBenchmarkGpu05.dll" /IMPLIB:"%TEMP%\FukaAvatarBenchmarkGpu.lib"
```

UnityがDLLを読み込んでいる間は置き換えられません。作業中のシーンを保存してUnityを終了してから再ビルドしてください。インポート設定はAny Platformを無効、Editorのみ有効、OSをWindows、CPUをx86_64にします。

Unity付属の`IUnityInterface.h`、`IUnityGraphics.h`、`IUnityGraphicsD3D11.h`をビルド時に参照します。これらのヘッダー自体は本ツールに同梱しません。ヘッダーの利用条件はインストール先の原文を確認してください。GPUクエリにはWindows SDKのDirect3D 11 APIを使用します。

GPU計測はレンダースレッドから非同期タイムスタンプを発行し、`DONOTFLUSH`で完了した結果だけを回収します。開始と終了の間に起きた命令供給の待ち時間も区間時間に含まれ得るため、GPU演算だけの所要時間を保証するAPIではありません。

通常描画と各ライトの影生成では、開始タイムスタンプの直前と終了タイムスタンプの直後に`ID3D11DeviceContext::Flush`を呼びます。先行する命令と計測開始を別の送信単位にし、終了マーカーが後続のCPU処理や次の送信まで保留されて区間を延ばす影響を抑えます。開始タイムスタンプの直後にはFlushしません。[Flushは非同期の命令送信](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-flush)で、GPUの完了待機や追加の描画は行いません。ただし、命令送信自体にもオーバーヘッドがあります。

カメラ全体の区間には追加のFlushを入れず、CSVの診断値として残します。この値には描画区間の間の待ち時間も含まれ、表示するGPU時間は通常描画と影生成の区間の合計です。レポートJSONの`gpuTimingMethod`には`d3d11-draw-boundary-flush`を記録します。

マネージド側は描画ごとに1〜16,777,215の一意なチケットを割り当て、開始イベント1の上位24ビットに格納します。通常描画はイベント4・5、影生成は6・7、カメラ区間は8・9、チケットの終了は10です。各チケットには通常描画とカメラの完了区間が1つずつ必要で、影生成は0個以上です。イベント2・3は使用しません。

終了したクエリは次の開始・終了イベント、または回収専用イベント11で確認します。計測停止後もイベント11をレンダースレッドに送ることで残りの結果を回収できます。`Fuka_Pop`は完了結果のキューから1件ずつ返し、チケットを使った描画フレームとの対応付けはマネージド側で行います。回収時のCPUフレームへ結果を割り当てないでください。

時計の不整合、開始・終了イベントの欠落、タイムスタンプの逆転は該当する結果だけを無効にします。GPUが処理中のクエリは結果が返るまで保留し、完了した長さ0の区間は有効です。キュー落ちの累計は診断用であり、後続の正常な結果を無効にしません。返らなかったチケットはマネージド側で欠測として数えます。`Fuka_Clear`は結果キューと破棄境界を更新するだけで、メインスレッドからDirect3Dのクエリを操作しません。

`Fuka_CaptureApi()`が返すAPI識別値は1です。構造体と関数の呼び出し形式が一致するか、他の計測APIを呼ぶ前に確認します。古いDLLが読み込まれている場合は、Unityを終了してDLLとC#コードを同じ版にそろえてください。
