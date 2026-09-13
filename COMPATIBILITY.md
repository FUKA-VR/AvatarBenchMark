# 計測対象と制約

本ツールは、PC向けアバターをUnity EditorとAv3Emulatorで動かした際の比較に使います。実際に再生・描画されている状態の負荷を記録するため、部品の有効状態、カメラからの見え方、接触や動作などの条件によって結果が変わります。

CPU値はメインスレッドの更新時間です。部品ごとの処理時間や、全スレッドのCPU使用時間を合計する機能はありません。

## 部品ごとの扱い

| 対象 | 計測時の扱い |
|---|---|
| MeshRenderer・SkinnedMeshRenderer・シェーダー・ライト | 計測環境で実際に発生する描画と影生成をGPU値に含めます。 |
| Camera・RenderTexture | 追加カメラの描画も含めます。カメラの順番、Forward・Deferred・VertexLit、深度テクスチャに対応します。 |
| CustomRenderTexture | Unityの更新処理をGPU計測に含めます。OnDemandの場合は、更新が要求されている必要があります。 |
| Animator・Animation | Av3EmulatorやUnityが実際に再生した処理を含めます。アバターのビルド後パラメータで状態を設定します。 |
| PhysBone・VRC Constraint・Unity Constraint | SDK・Av3Emulator上で動作する処理を含めます。掴む・振るなどの入力は自動では行いません。 |
| Rigidbody・Joint・Collider | Unityの物理演算に従って動作します。Fixed・Hinge・Spring・Character・Configurable Jointを含みます。静止、スリープ、Kinematic、接触の有無によって処理が変わります。 |
| Cloth | UnityのClothシミュレーションを動かし、更新・変形後の描画による影響を記録します。無効状態や画面外での停止設定などは、元の設定に従います。 |
| ParticleSystem・TrailRenderer・LineRenderer | 実際に更新・描画された処理を含めます。発生していない粒子などを強制的に動かす処理はありません。 |
| Contact・VRCRaycast | SDK・Av3Emulatorで処理される範囲が対象です。接触相手やレイの対象がなければ、その状況での計測になります。 |
| AudioSource | 再生状態を保ちます。音声DSPや空間音響処理の総CPU負荷は、現在のCPU値だけでは評価できません。 |
| FinalIK・VRCStation・VR向けの追跡やアバター制御 | VRChatクライアントや入力機器に依存する動作の再現は保証していません。動いていることをプレビューで確認してください。 |

ビルド後の部品は、計測のために一律削除しません。SDKやビルド拡張による変換・削除は反映されます。物理演算の更新間隔、衝突レイヤー、影品質などのプロジェクト設定は変更しません。計測開始前の部品の有効状態や物理設定は、レポートJSONの動作記録にも保存します。

## Camera・RenderTextureを使う場合

アバターや背景にある追加カメラは、計測中に描画されると自動でGPU計測の対象になります。計測の視点一覧への登録は不要です。視点用のカメラと、ギミックの描画先を更新するカメラは、それぞれの用途で配置してください。

ビルド時に生成されるRenderTextureも計測用Prefabへ保存し、カメラ・マテリアル・アニメーションからの参照を保ちます。複数体を配置する場合は、アバターごとにRenderTextureと必要な参照元を複製します。同じアバター内では共通の描画先を使い、通常の画像テクスチャやメッシュは共有します。

計測前にプレビューで、ギミックが起動し、意図した内容が描画されていることを確認してください。RenderTextureの大きさ・形式・人数によって必要なGPUメモリは増えますが、その実使用量は現在のテクスチャ容量表示には含まれません。

## 数値が表す範囲

- **GPU**：カメラ描画・影生成・CustomRenderTexture更新の区間時間です。重なった区間を二重に足しません。SceneView、プレビュー用カメラ、同じ更新フレームのGameView再描画は除外します。
- **CPU**：メインスレッドのPlayerLoop時間です。物理・Clothのワーカー、描画スレッド、音声DSPなどのCPU使用時間をすべて足した値ではありません。Editor側でPlayerLoopの外に回るカメラの描画準備も対象外です。
- **Draw Calls・SetPass**：背景を含むUnityのフレーム全体の回数です。カメラ別・アバター別の内訳ではなく、Editorの描画も影響し得る補助値です。
- **メモリ**：ビルド後の画像テクスチャ容量は静的な推定値です。RenderTextureを含む実使用VRAM、割り当てや解放の瞬間的な負荷は測定していません。

計測区間外から発行される独立したGPU処理は対象外です。Forward・DeferredはカメラのAfterEverythingまで、VertexLitは描画前後のコールバック間を測ります。VertexLitの描画後コールバックより後に実行される画像効果などは含められません。部品の種類だけで、任意の独自処理がすべて含まれるとは限りません。

複数体を配置すると、各アバターのライト、カメラのCulling Mask、物理衝突などが他のアバターに影響する場合があります。ライトの範囲や描画・衝突の対象は、元の設定に従います。

## VRChat上での違い

VRChatでは、プラットフォームやSafety設定、アバターの表示状態などで動作する部品が変わります。他のユーザーのCameraはロード時に無効化され、フレンドか「Show Avatar」で表示を許可している場合に保持されます。保持されたCameraはアニメーションで有効にできます。詳細は[VRChat公式の許可コンポーネント一覧](https://creators.vrchat.com/avatars/whitelisted-avatar-components/whitelisted-avatar-components/)を参照してください。

Android・Quest向けアバターでは、Cloth・Camera・Rigidbody・Jointなどに使用制限があり、PCでの計測結果をそのまま適用できません。[公式のコンテンツ制限](https://creators.vrchat.com/platforms/android/quest-content-limitations/)で対象を確認してください。SDKのEditor再生範囲については[Avatar Componentsのデバッグ](https://creators.vrchat.com/avatars/avatar-components/debugging-avatar-components/)も参考になります。

ネットワーク同期、VRでの左右眼描画、Safetyによる制限や非表示、実機の追跡入力は再現していません。結果は同じPC・同じ条件での比較に使い、VRChat内のFPSへ直接換算しないでください。
