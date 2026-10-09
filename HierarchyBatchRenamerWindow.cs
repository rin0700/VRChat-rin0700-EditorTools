#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Rin0700.Tools
{
    /// <summary>Sceneの指定GameObjectの直下の子をプレビュー付きで一括改名します。</summary>
    public sealed class HierarchyBatchRenamerWindow : EditorWindow
    {
        private enum RenameMode { KeepOriginal, SetName, ReplaceText }
        private static readonly string[] ModeLabels = { "現在の名前を保持", "名前を一括設定", "文字列を置換" };
        private const string UndoLabel = "Rename Hierarchy Objects";
        private const int PreviewLimit = 500;

        [SerializeField] private GameObject root;
        [SerializeField] private bool includeRoot;
        [SerializeField] private bool includeInactive = true;
        [SerializeField] private RenameMode mode;
        [SerializeField] private string newName = "Object";
        [SerializeField] private string searchText = string.Empty;
        [SerializeField] private string replacementText = string.Empty;
        [SerializeField] private string prefix = string.Empty;
        [SerializeField] private string suffix = string.Empty;
        [SerializeField] private bool numbering;
        [SerializeField] private bool numberAtStart;
        [SerializeField] private int startNumber = 1;
        [SerializeField] private int numberIncrement = 1;
        [SerializeField] private int numberDigits = 3;
        [SerializeField] private string numberSeparator = "_";
        [SerializeField] private Vector2 scroll;

        private sealed class RenameEntry
        {
            public GameObject Target;
            public string Path;
            public string Before;
            public string After;
            public bool Changed => !string.Equals(Before, After, StringComparison.Ordinal);
        }

        [MenuItem("Tools/Rin0700/Hierarchy Batch Renamer")]
        private static void Open()
        {
            var window = GetWindow<HierarchyBatchRenamerWindow>();
            window.titleContent = new GUIContent("Hierarchy Renamer");
            window.minSize = new Vector2(620f, 480f);
            if (window.root == null && IsSceneObject(Selection.activeGameObject))
                window.root = Selection.activeGameObject;
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.hierarchyChanged += Repaint;
            Selection.selectionChanged += Repaint;
            Undo.undoRedoPerformed += Repaint;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= Repaint;
            Selection.selectionChanged -= Repaint;
            Undo.undoRedoPerformed -= Repaint;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Hierarchy 一括名前変更", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                root = (GameObject)EditorGUILayout.ObjectField("対象の親", root, typeof(GameObject), true);
                using (new EditorGUI.DisabledScope(!IsSceneObject(Selection.activeGameObject)))
                {
                    if (GUILayout.Button("選択中を使用", GUILayout.Width(110f)))
                        root = Selection.activeGameObject;
                }
            }

            EditorGUILayout.LabelField("対象範囲: 直下の子のみ（孫以降は対象外）", EditorStyles.miniLabel);
            includeRoot = EditorGUILayout.ToggleLeft("親自身も対象にする", includeRoot);
            includeInactive = EditorGUILayout.ToggleLeft("非アクティブのオブジェクトも対象にする", includeInactive);
            EditorGUILayout.Space();
            mode = (RenameMode)EditorGUILayout.Popup("変更方法", (int)mode, ModeLabels);
            if (mode == RenameMode.SetName)
                newName = EditorGUILayout.TextField("新しい名前", newName);
            else if (mode == RenameMode.ReplaceText)
            {
                searchText = EditorGUILayout.TextField("検索する文字列", searchText);
                replacementText = EditorGUILayout.TextField("置換後の文字列", replacementText);
                EditorGUILayout.LabelField("大文字・小文字を区別し、一致する文字列をすべて置換します。", EditorStyles.miniLabel);
            }
            prefix = EditorGUILayout.TextField("先頭に追加", prefix);
            suffix = EditorGUILayout.TextField("末尾に追加", suffix);

            numbering = EditorGUILayout.ToggleLeft("ナンバリングを追加", numbering);
            if (numbering)
            {
                numberAtStart = EditorGUILayout.Popup("番号の位置", numberAtStart ? 1 : 0,
                    new[] { "名前の末尾", "名前の先頭" }) == 1;
                startNumber = Math.Max(0, EditorGUILayout.IntField("開始番号", startNumber));
                numberIncrement = Math.Max(1, EditorGUILayout.IntField("増分", numberIncrement));
                numberDigits = EditorGUILayout.IntSlider("桁数（ゼロ埋め）", numberDigits, 1, 10);
                numberSeparator = EditorGUILayout.TextField("番号との区切り文字", numberSeparator);
                EditorGUILayout.LabelField("直下の子にHierarchyの上から順に連番を付けます。親を含める場合は親が最初です。", EditorStyles.miniLabel);
            }

            EditorGUILayout.HelpBox(
                "名前の設定・置換 → 先頭・末尾の文字追加 → ナンバリングの順に処理します。\n" +
                "名前に依存するAnimationのパスやTransform.Findなどは自動更新されません。",
                MessageType.Info);

            List<RenameEntry> entries = BuildPreview(out string error);
            int changedCount = 0;
            foreach (RenameEntry entry in entries)
                if (entry.Changed) changedCount++;

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                error = "Play Modeでは実行できません。停止してから実行してください。";
            else if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                error = "コンパイル・アセット更新の完了を待ってください。";
            if (!string.IsNullOrEmpty(error))
                EditorGUILayout.HelpBox(error, MessageType.Warning);

            EditorGUILayout.LabelField($"プレビュー: 対象 {entries.Count} 件 / 変更 {changedCount} 件", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("現在の相対パス", GUILayout.Width(240f));
                GUILayout.Label("変更後の名前");
            }
            using (var view = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = view.scrollPosition;
                int count = Math.Min(entries.Count, PreviewLimit);
                for (int i = 0; i < count; i++)
                {
                    RenameEntry entry = entries[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(new GUIContent(entry.Path, entry.Path), EditorStyles.label, GUILayout.Width(240f)))
                            EditorGUIUtility.PingObject(entry.Target);
                        GUILayout.Label(new GUIContent(entry.After, entry.After), entry.Changed ? EditorStyles.boldLabel : EditorStyles.label);
                    }
                }
                if (entries.Count > PreviewLimit)
                    EditorGUILayout.HelpBox($"表示は先頭 {PreviewLimit} 件です。実行時は対象 {entries.Count} 件すべてを処理します。", MessageType.Info);
            }

            using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(error) || changedCount == 0))
            {
                if (GUILayout.Button($"{changedCount} 件の名前変更を実行", GUILayout.Height(30f)))
                {
                    ApplyRename();
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.LabelField("Ctrl + Z / Cmd + Z で一括Undoできます。Sceneの保存は通常どおり行ってください。", EditorStyles.miniLabel);
        }

        private static bool IsSceneObject(GameObject target)
        {
            return target != null && !EditorUtility.IsPersistent(target) &&
                   target.scene.IsValid() && target.scene.isLoaded &&
                   !EditorSceneManager.IsPreviewScene(target.scene);
        }

        private List<RenameEntry> BuildPreview(out string error)
        {
            var entries = new List<RenameEntry>();
            error = null;
            if (!IsSceneObject(root))
            {
                error = "HierarchyからScene内の親オブジェクトを指定してください（Prefab編集モード・Project内のアセットは対象外）。";
                return entries;
            }
            if (mode == RenameMode.ReplaceText && string.IsNullOrEmpty(searchText))
            {
                error = "置換する検索文字列を入力してください。";
                return entries;
            }

            // Only enumerate immediate children; never traverse grandchildren.
            for (int i = includeRoot ? -1 : 0; i < root.transform.childCount; i++)
            {
                Transform current = i < 0 ? root.transform : root.transform.GetChild(i);
                if (!includeInactive && !current.gameObject.activeInHierarchy) continue;

                string before = current.gameObject.name;
                string result = before;
                if (mode == RenameMode.SetName) result = newName ?? string.Empty;
                else if (mode == RenameMode.ReplaceText) result = before.Replace(searchText, replacementText ?? string.Empty);
                result = (prefix ?? string.Empty) + result + (suffix ?? string.Empty);
                if (numbering)
                {
                    // Use long arithmetic so large start/increment values do not overflow int.
                    long number = (long)Math.Max(0, startNumber) + (long)entries.Count * Math.Max(1, numberIncrement);
                    string numberText = number.ToString("D" + Math.Max(1, Math.Min(10, numberDigits)), CultureInfo.InvariantCulture);
                    string separator = numberSeparator ?? string.Empty;
                    result = numberAtStart ? numberText + separator + result : result + separator + numberText;
                }

                if (string.IsNullOrWhiteSpace(result) || result.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                    error = "空白のみの名前や、改行・NULL文字を含む名前には変更できません。";
                if ((current.gameObject.hideFlags & HideFlags.NotEditable) != 0)
                    error = "対象に編集不可のオブジェクトが含まれています。対象の親を変更してください。";

                entries.Add(new RenameEntry
                {
                    Target = current.gameObject,
                    Path = GetRelativePath(current),
                    Before = before,
                    After = result
                });
            }
            return entries;
        }

        private string GetRelativePath(Transform target)
        {
            if (target == root.transform) return "(親) " + target.name;
            var names = new Stack<string>();
            for (Transform current = target; current != null && current != root.transform; current = current.parent)
                names.Push(current.name);
            return string.Join("/", names.ToArray());
        }

        private void ApplyRename()
        {
            // Rebuild at execution time to account for hierarchy/Undo changes since the preview.
            List<RenameEntry> entries = BuildPreview(out string error);
            if (!string.IsNullOrEmpty(error) || EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            var targets = new List<UnityEngine.Object>();
            foreach (RenameEntry entry in entries)
                if (entry.Changed) targets.Add(entry.Target);
            if (targets.Count == 0) return;

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoLabel);
            // Complete snapshots also allow synchronous rollback if applying an entry fails.
            Undo.RegisterCompleteObjectUndo(targets.ToArray(), UndoLabel);
            try
            {
                foreach (RenameEntry entry in entries)
                {
                    if (!entry.Changed) continue;
                    entry.Target.name = entry.After;
                    if (PrefabUtility.IsPartOfPrefabInstance(entry.Target))
                        PrefabUtility.RecordPrefabInstancePropertyModifications(entry.Target);
                }
                EditorSceneManager.MarkSceneDirty(root.scene);
                Undo.CollapseUndoOperations(group);
                Debug.Log($"Hierarchy Renamer: {targets.Count} 件の名前を変更しました。", root);
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(group);
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("名前変更に失敗しました", "変更をUndoしました。Consoleのエラーを確認してください。", "OK");
            }
            Repaint();
        }
    }
}
#endif
