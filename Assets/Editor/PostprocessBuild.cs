// Assets/Editor/PostprocessBuild.cs
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using System.IO;
using System.Linq;

public static class PostprocessBuild_iOS_RemoveLd64
{
    [PostProcessBuild(int.MaxValue)]
    public static void OnPostprocessBuild(BuildTarget buildTarget, string pathToBuiltProject)
    {
        if (buildTarget != BuildTarget.iOS) return;

        // Xcode プロジェクトを開く
        string projPath = PBXProject.GetPBXProjectPath(pathToBuiltProject);
        var proj = new PBXProject();
        proj.ReadFromFile(projPath);

        // ターゲット GUID を取得
#if UNITY_2019_3_OR_NEWER
        string mainTarget = proj.GetUnityMainTargetGuid();          // "Unity-iPhone"
        string frameworkTarget = proj.GetUnityFrameworkTargetGuid(); // "UnityFramework"
#else
        string mainTarget = proj.TargetGuidByName("Unity-iPhone");
        string frameworkTarget = proj.TargetGuidByName("UnityFramework");
#endif
        string projectGuid = proj.ProjectGuid();

        // 削除したいフラグ（必要ならここに追加）
        string[] removeFlags = { "-ld64", "-ld_classic" };

        // まず通常のビルドプロパティ更新（全ビルド構成に一括適用）
        RemoveFlagsWithUpdate(proj, mainTarget, removeFlags);
        RemoveFlagsWithUpdate(proj, frameworkTarget, removeFlags);
        RemoveFlagsWithUpdate(proj, projectGuid, removeFlags);

        // 文字列一括格納のケースにも対応（保険）
        RemoveFlagsFromAnyConfigString(proj, mainTarget, removeFlags);
        RemoveFlagsFromAnyConfigString(proj, frameworkTarget, removeFlags);
        RemoveFlagsFromAnyConfigString(proj, projectGuid, removeFlags);

        // 保存
        proj.WriteToFile(projPath);
    }

    static void RemoveFlagsWithUpdate(PBXProject proj, string guid, string[] removeFlags)
    {
        proj.UpdateBuildProperty(guid, "OTHER_LDFLAGS",
            addValues: new string[] {}, 
            removeValues: removeFlags
        );
    }

    // OTHER_LDFLAGS が「配列」ではなく「単一の文字列」として入っている場合の除去
    static void RemoveFlagsFromAnyConfigString(PBXProject proj, string guid, string[] removeFlags)
    {
        var current = proj.GetBuildPropertyForAnyConfig(guid, "OTHER_LDFLAGS");
        if (string.IsNullOrEmpty(current)) return;

        // スペース区切りで安全に除去して再設定
        var kept = current
            .Split(' ')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s) && !removeFlags.Contains(s))
            .ToArray();

        var joined = string.Join(" ", kept);
        proj.SetBuildProperty(guid, "OTHER_LDFLAGS", joined);
    }
}
