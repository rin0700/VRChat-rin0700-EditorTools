# VRChat-rin0700-EditorTools

```
HierarchyBatchRenamerWindow.cs
(Tools > Rin0700 > Hierarchy Batch Renamer)
```
Scene内の指定GameObjectの直下の子の名前を、プレビューを確認しながら一括変更。孫以降は対象外です。

- 現在の名前を保持 / 名前を一括設定 / 文字列を置換（大文字・小文字を区別）
- 先頭・末尾の文字追加を、上記の変更方法と組み合わせ可能
- ナンバリング（番号の先頭/末尾配置、開始番号、増分、1〜10桁のゼロ埋め、区切り文字）
- 対象は直下の子のみ。親自身の対象化、非アクティブの対象化を選択
- Hierarchy順に変更前の相対パスと変更後の名前を表示（最大500件。実行は全対象）
- 一括Undo / Redo、Sceneの変更記録、Prefabインスタンスの名前Override記録に対応

### 使い方

1. `HierarchyBatchRenamerWindow.cs` をUnityプロジェクトの `Assets/Editor/` などのEditorフォルダーに配置。
2. `Tools > Rin0700 > Hierarchy Batch Renamer` を開く。
3. Hierarchyの親を「対象の親」にドラッグするか、選択して「選択中を使用」を押す。
4. 対象範囲・変更方法・先頭/末尾の文字・必要に応じてナンバリングを設定し、プレビューを確認。
5. 「名前変更を実行」を押し、Sceneを保存。戻す場合は `Ctrl + Z` / `Cmd + Z`。

例: 現在の名前を保持して、先頭に `Prop_`、末尾に `_01` を指定すると、
`Chair` → `Prop_Chair_01`。一括設定では対象すべてを同じ名前にできます。
同名のオブジェクトは許可されます。

連番の例: 「名前を一括設定」で `Prop` を指定し、「ナンバリングを追加」をONにして、
末尾・開始番号 `1`・増分 `1`・桁数 `3`・区切り文字 `_` を指定すると、
`Prop_001`、`Prop_002`、`Prop_003` …になります。先頭配置では `001_Prop` …になります。
処理順は「名前の設定/置換 → 先頭/末尾の文字追加 → ナンバリング」です。
番号は直下の子にHierarchyの上から下へ割り当てます。親自身を対象に含める場合は親が最初です。
孫以降は改名・ナンバリングの対象に含めません。
対象外の親・非アクティブオブジェクトは番号を消費せず、名前が変更されない対象も番号を消費します。
桁数は最小表示幅です。例えば2桁の指定でも100は `100` と表示します。

Scene内のGameObjectが対象です。Project内のPrefabアセット・Prefab編集モード・Play Modeは対象外です。
Prefabインスタンスの変更はScene側に記録します。Prefabアセットへ自動Applyは行いません。
AnimationClipのバインドパスや `Transform.Find` など、名前・階層パスに依存する参照は自動更新されません。
現在の名前を保持するモードで文字追加・ナンバリングを再実行すると、現在の名前にもう一度追加します。

```
BatchScriptClassRenamer.cs
(Tools > Rin0700 > UdonSharp Script Renamer)
```
UdonSharp スクリプトのファイル名・クラス名・対応する Program Asset をまとめて改名


```
HierarchyPathCopier.cs
(Hierarchy の右クリック / Ctrl + Shift + P)
```
Hierarchy で選択した GameObject の階層パスをクリップボードへコピー

```
ProjectPathTool.cs
```
Project ウィンドウ下部にパス表示、コピー、直接移動、パンくずを追加

```
VrchatCacheInspectorWindow.cs
(Tools > VRChat > Cache Inspector)
```
VRChat のキャッシュ内 __data を UnityFS として読み取り、構造をレポート化
