using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BuildGame
{
    public static void Build()
    {
        Directory.CreateDirectory("Assets/Scenes");
        var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        var camera=new GameObject("Camera").AddComponent<Camera>();
        camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(0.063f,0.082f,0.118f);
        camera.gameObject.AddComponent<AudioListener>();
        EditorSceneManager.SaveScene(scene,"Assets/Scenes/OrangeRush.unity");
        EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene("Assets/Scenes/OrangeRush.unity",true)};
        PlayerSettings.companyName="Orange Club";
        PlayerSettings.productName="Orange Rush";
        PlayerSettings.bundleVersion=OrangeRush.GameRules.Version;
        PlayerSettings.defaultScreenWidth=1440; PlayerSettings.defaultScreenHeight=900;
        PlayerSettings.fullScreenMode=FullScreenMode.Windowed;
        PlayerSettings.resizableWindow=true; PlayerSettings.runInBackground=true;
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone,ScriptingImplementation.Mono2x);
        AssetDatabase.SaveAssets();
        var output=Environment.GetEnvironmentVariable("ORANGE_BUILD_PATH");
        if(string.IsNullOrEmpty(output)) output="Build/OrangeRush.exe";
        var options=Environment.GetEnvironmentVariable("ORANGE_DEVELOPMENT_BUILD")=="1"?BuildOptions.Development:BuildOptions.None;
        var result=BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes=new[]{"Assets/Scenes/OrangeRush.unity"},locationPathName=output,target=BuildTarget.StandaloneWindows64,options=options });
        if(result.summary.result!=BuildResult.Succeeded) throw new Exception("Build failed: "+result.summary.result);
        Debug.Log("ORANGE_BUILD_SUCCESS "+result.summary.outputPath);
    }
}
