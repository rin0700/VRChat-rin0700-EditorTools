#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
///選択したGameObjectのパスを Root/Parent/Child 形式でコピーします。
///複数のGameObjectを選択した場合は、Hierarchy上の順番で1行ずつ出力します。
///Hierarchyの右クリックメニューから実行できます。
///Windows/Linuxでは Ctrl + Shift + P、macOSでは Cmd + Shift + P で実行できます。
///Editor専用のため、アプリケーションのビルドには含まれません。
/// </summary>

namespace Rin0700.Tools
{
public static class HierarchyPathCopier
{
    private const string MenuPath = "GameObject/Copy Hierarchy Path %#p";

    [MenuItem(MenuPath, false, 49)]
    private static void CopySelectedHierarchyPaths()
    {
        GameObject[] selectedObjects = Selection.gameObjects;
        if (selectedObjects == null || selectedObjects.Length == 0)
        {
            return;
        }

        Array.Sort(selectedObjects, CompareHierarchyOrder);

        var paths = new string[selectedObjects.Length];
        for (int i = 0; i < selectedObjects.Length; i++)
        {
            paths[i] = GetHierarchyPath(selectedObjects[i].transform);
        }

        string clipboardText = string.Join("\n", paths);
        EditorGUIUtility.systemCopyBuffer = clipboardText;

        Debug.Log(
            selectedObjects.Length == 1
                ? $"Copied hierarchy path: {clipboardText}"
                : $"Copied {selectedObjects.Length} hierarchy paths.\n{clipboardText}");
    }

    [MenuItem(MenuPath, true)]
    private static bool ValidateCopySelectedHierarchyPaths()
    {
        return Selection.gameObjects != null && Selection.gameObjects.Length > 0;
    }

    private static string GetHierarchyPath(Transform target)
    {
        var names = new Stack<string>();

        for (Transform current = target; current != null; current = current.parent)
        {
            names.Push(current.name);
        }

        var path = new StringBuilder();
        while (names.Count > 0)
        {
            if (path.Length > 0)
            {
                path.Append('/');
            }

            path.Append(names.Pop());
        }

        return path.ToString();
    }

    private static int CompareHierarchyOrder(GameObject left, GameObject right)
    {
        int sceneOrder = left.scene.handle.CompareTo(right.scene.handle);
        if (sceneOrder != 0)
        {
            return sceneOrder;
        }

        List<int> leftIndices = GetSiblingIndexPath(left.transform);
        List<int> rightIndices = GetSiblingIndexPath(right.transform);
        int commonLength = Math.Min(leftIndices.Count, rightIndices.Count);

        for (int i = 0; i < commonLength; i++)
        {
            int indexOrder = leftIndices[i].CompareTo(rightIndices[i]);
            if (indexOrder != 0)
            {
                return indexOrder;
            }
        }

        return leftIndices.Count.CompareTo(rightIndices.Count);
    }

    private static List<int> GetSiblingIndexPath(Transform target)
    {
        var indices = new List<int>();

        for (Transform current = target; current != null; current = current.parent)
        {
            indices.Add(current.GetSiblingIndex());
        }

        indices.Reverse();
        return indices;
    }
}
}
#endif
