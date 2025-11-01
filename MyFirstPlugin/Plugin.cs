using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneLoader;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log;

    // Path to bundles folder relative to BepInEx plugins folder
    private const string BundlesFolder = "bundles";
    
    // Keep references to loaded bundles to prevent garbage collection
    private static object loadedBundle = null;
    
    // Async bundle loading state (internal so MonoBehaviour can access)
    internal static object pendingAsyncOperation = null;
    internal static Type pendingAssetBundleType = null;

    public override void Load()
    {
        Log = base.Log;
        Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        Log.LogInfo("Press F8 to load a scene from a .bundle file in the /bundles folder!");
        
        // Register the MonoBehaviour type for IL2CPP
        ClassInjector.RegisterTypeInIl2Cpp<SceneLoaderMonoBehaviour>();
        
        // Create a GameObject and add the MonoBehaviour component
        var go = new GameObject("SceneLoader");
        UnityEngine.Object.DontDestroyOnLoad(go);
        var component = go.AddComponent<SceneLoaderMonoBehaviour>();
    }

    public static void LoadSceneFromBundle()
    {

        try
        {
            // Get the bundles folder path (relative to BepInEx plugins folder)
            string bundlesPath = Path.Combine(Paths.PluginPath, BundlesFolder);
            
            if (!Directory.Exists(bundlesPath))
            {
                Log.LogWarning($"Bundles folder not found at: {bundlesPath}");
                Log.LogInfo($"Please create a '{BundlesFolder}' folder in your BepInEx/plugins directory and place .bundle files there.");
                return;
            }

            // Find all .bundle files in the bundles folder
            string[] bundleFiles = Directory.GetFiles(bundlesPath, "*.bundle");
            
            if (bundleFiles.Length == 0)
            {
                Log.LogWarning($"No .bundle files found in: {bundlesPath}");
                Log.LogInfo($"Please place .bundle files in the '{BundlesFolder}' folder.");
                return;
            }

            // Try to find stadium.bundle first, otherwise use the first bundle found
            string fullBundlePath = null;
            foreach (string bundleFile in bundleFiles)
            {
                if (Path.GetFileName(bundleFile).Equals("stadium.bundle", StringComparison.OrdinalIgnoreCase))
                {
                    fullBundlePath = bundleFile;
                    break;
                }
            }
            
            // If stadium.bundle not found, use the first bundle
            if (fullBundlePath == null)
            {
                fullBundlePath = bundleFiles[0];
                Log.LogInfo($"stadium.bundle not found, loading: {Path.GetFileName(fullBundlePath)}");
            }
            else if (bundleFiles.Length > 1)
            {
                Log.LogInfo($"Found {bundleFiles.Length} bundle files. Loading stadium.bundle");
            }

            Log.LogInfo($"Loading AssetBundle from: {fullBundlePath}");

            // AssetBundle is in a forwarded assembly, so we need to load it at runtime
            // Get the AssetBundle type from the forwarded assembly
            Type assetBundleType = Type.GetType("UnityEngine.AssetBundle, UnityEngine.AssetBundleModule");
            if (assetBundleType == null)
            {
                // Fallback: try to find it in any loaded assembly
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    assetBundleType = assembly.GetType("UnityEngine.AssetBundle");
                    if (assetBundleType != null) break;
                }
            }

            if (assetBundleType == null)
            {
                Log.LogError("Could not find AssetBundle type! Make sure UnityEngine.AssetBundleModule is available.");
                return;
            }

            // Use LoadFromFileAsync for IL2CPP compatibility
            var methods = assetBundleType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            MethodInfo loadFromFileAsyncMethod = null;
            MethodInfo loadFromFileMethod = null;
            
            // Try to find LoadFromFileAsync first (better for IL2CPP)
            foreach (var method in methods)
            {
                if (method.Name == "LoadFromFileAsync")
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                    {
                        loadFromFileAsyncMethod = method;
                        break;
                    }
                }
            }
            
            // Also look for synchronous LoadFromFile as fallback
            foreach (var method in methods)
            {
                if (method.Name == "LoadFromFile")
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                    {
                        loadFromFileMethod = method;
                        break;
                    }
                }
            }

            // Use LoadFromFileAsync - need to handle it properly with coroutine
            if (loadFromFileAsyncMethod == null)
            {
                Log.LogError("LoadFromFileAsync method not found!");
                return;
            }

            Log.LogInfo("Attempting to load bundle using LoadFromFileAsync...");
            
            // Invoke LoadFromFileAsync and store the async operation
            try
            {
                pendingAsyncOperation = loadFromFileAsyncMethod.Invoke(null, new object[] { fullBundlePath });
                pendingAssetBundleType = assetBundleType;
                
                if (pendingAsyncOperation == null)
                {
                    Log.LogError("LoadFromFileAsync returned null!");
                    pendingAssetBundleType = null;
                    return;
                }
                
                Log.LogInfo("LoadFromFileAsync started, waiting for completion...");
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to invoke LoadFromFileAsync: {ex.Message}");
                pendingAsyncOperation = null;
                pendingAssetBundleType = null;
            }
        }
        catch (Exception ex)
        {
            Log.LogError($"Error loading scene from bundle: {ex}");
            Log.LogError($"Stack trace: {ex.StackTrace}");
        }
    }

    internal static void ProcessLoadedBundle(object bundleObj, Type assetBundleType)
    {
        try
        {
            if (bundleObj == null)
            {
                Log.LogError("Bundle object is null!");
                return;
            }

            Log.LogInfo("AssetBundle loaded successfully using LoadFromFileAsync!");

            // Store the bundle reference to prevent garbage collection
            if (loadedBundle != null)
            {
                try
                {
                    MethodInfo unloadMethod = assetBundleType.GetMethod("Unload", new[] { typeof(bool) });
                    unloadMethod?.Invoke(loadedBundle, new object[] { false });
                    Log.LogInfo("Unloaded previous bundle");
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"Failed to unload previous bundle: {ex.Message}");
                }
            }
            loadedBundle = bundleObj;

            // Get GetAllScenePaths method
            MethodInfo getAllScenePathsMethod = assetBundleType.GetMethod("GetAllScenePaths");
            if (getAllScenePathsMethod == null)
            {
                Log.LogError("Could not find GetAllScenePaths method!");
                return;
            }

            // Get all scene paths from the bundle
            object scenePathsObj = getAllScenePathsMethod.Invoke(bundleObj, null);
            string[] scenePaths = null;
            
            Log.LogInfo($"ScenePathsObj type: {scenePathsObj?.GetType()?.FullName ?? "null"}");
            
            // Try to cast directly first
            scenePaths = scenePathsObj as string[];
            
            if (scenePaths == null && scenePathsObj != null)
            {
                // Try using Il2CppSystem.Array conversion
                Type arrayType = scenePathsObj.GetType();
                Log.LogInfo($"Array type: {arrayType.FullName}, IsArray: {arrayType.IsArray}");
                
                // Try to get Length property
                var lengthProperty = arrayType.GetProperty("Length");
                if (lengthProperty != null)
                {
                    int length = (int)lengthProperty.GetValue(scenePathsObj);
                    Log.LogInfo($"Array length: {length}");
                    
                    if (length > 0)
                    {
                        scenePaths = new string[length];
                        
                        // Try different methods to access array elements
                        var getMethod = arrayType.GetMethod("GetValue", new[] { typeof(int) });
                        var indexer = arrayType.GetProperty("Item", new[] { typeof(int) });
                        
                        for (int i = 0; i < length; i++)
                        {
                            object item = null;
                            string itemValue = null;
                            
                            // Try indexer first
                            if (indexer != null)
                            {
                                try
                                {
                                    item = indexer.GetValue(scenePathsObj, new object[] { i });
                                    if (item != null)
                                    {
                                        Log.LogInfo($"  Got item[{i}] via indexer, type: {item.GetType().FullName}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log.LogWarning($"  Indexer failed for [{i}]: {ex.Message}");
                                }
                            }
                            
                            // Fallback to GetValue
                            if (item == null && getMethod != null)
                            {
                                try
                                {
                                    item = getMethod.Invoke(scenePathsObj, new object[] { i });
                                    if (item != null)
                                    {
                                        Log.LogInfo($"  Got item[{i}] via GetValue, type: {item.GetType().FullName}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log.LogWarning($"  GetValue failed for [{i}]: {ex.Message}");
                                }
                            }
                            
                            if (item != null)
                            {
                                // Try to get string value - handle Il2Cpp strings specially
                                Type itemType = item.GetType();
                                
                                // Check if it's an Il2CppSystem.String
                                if (itemType.FullName?.Contains("Il2CppSystem.String") == true)
                                {
                                    // Try to get the underlying string pointer or use ToString
                                    try
                                    {
                                        var stringPtrProperty = itemType.GetProperty("StringPtr", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                        if (stringPtrProperty != null)
                                        {
                                            var stringPtr = stringPtrProperty.GetValue(item);
                                            // Use Marshal to read the string
                                            if (stringPtr != null && stringPtr is IntPtr ptr && ptr != IntPtr.Zero)
                                            {
                                                itemValue = Marshal.PtrToStringAnsi(ptr);
                                                if (string.IsNullOrEmpty(itemValue))
                                                {
                                                    itemValue = Marshal.PtrToStringUni(ptr);
                                                }
                                            }
                                        }
                                    }
                                    catch { }
                                }
                                
                                // Fallback to ToString
                                if (string.IsNullOrEmpty(itemValue))
                                {
                                    itemValue = item.ToString();
                                }
                                
                                scenePaths[i] = itemValue ?? string.Empty;
                                Log.LogInfo($"  Scene path[{i}]: '{scenePaths[i]}' (raw: '{itemValue ?? "null"}', type: {itemType.FullName})");
                            }
                            else
                            {
                                scenePaths[i] = string.Empty;
                                Log.LogWarning($"  Failed to get scene path at index {i} - item is null");
                            }
                        }
                    }
                }
                else
                {
                    Log.LogWarning($"Could not extract array elements. Array type: {arrayType.FullName}");
                }
            }
            
            if (scenePaths == null || scenePaths.Length == 0)
            {
                Log.LogWarning("No scenes found in the AssetBundle!");
                MethodInfo unloadMethod = assetBundleType.GetMethod("Unload", new[] { typeof(bool) });
                unloadMethod?.Invoke(bundleObj, new object[] { false });
                return;
            }

            Log.LogInfo($"Found {scenePaths.Length} scene(s) in bundle:");

            // Log all available scenes and find "stadium" scene
            string targetScenePath = null;
            string sceneName = "stadium";
            
            foreach (string scenePath in scenePaths)
            {
                string sceneNameInList = Path.GetFileNameWithoutExtension(scenePath);
                Log.LogInfo($"  - {sceneNameInList} (path: {scenePath})");
                
                // Look for "stadium" scene specifically
                if (sceneNameInList.Equals("stadium", StringComparison.OrdinalIgnoreCase))
                {
                    targetScenePath = scenePath;
                    sceneName = sceneNameInList;
                }
            }
            
            // If stadium not found, use first scene
            if (targetScenePath == null)
            {
                targetScenePath = scenePaths[0];
                sceneName = Path.GetFileNameWithoutExtension(targetScenePath);
                Log.LogInfo($"stadium scene not found, using first scene: {sceneName}");
            }

            Log.LogInfo($"Attempting to load scene: {sceneName} (path: {targetScenePath})");

            // Load all assets from the bundle
            try
            {
                Log.LogInfo("Loading all assets from bundle...");
                MethodInfo loadAllAssetsMethod = assetBundleType.GetMethod("LoadAllAssets", new Type[0]);
                if (loadAllAssetsMethod != null)
                {
                    object allAssets = loadAllAssetsMethod.Invoke(bundleObj, null);
                    if (allAssets != null)
                    {
                        Type assetsArrayType = allAssets.GetType();
                        var lengthProperty = assetsArrayType.GetProperty("Length");
                        if (lengthProperty != null)
                        {
                            int assetCount = (int)lengthProperty.GetValue(allAssets);
                            Log.LogInfo($"Loaded {assetCount} assets from bundle");
                        }
                        else
                        {
                            Log.LogInfo("Loaded all assets from bundle");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Failed to preload assets: {ex.Message}");
            }

            // Try loading by scene name first (Unity can find it in loaded bundles)
            try
            {
                Log.LogInfo($"Trying to load scene by name: {sceneName}");
                SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
                Log.LogInfo("Scene loading initiated by name (async)!");
            }
            catch (Exception nameEx)
            {
                Log.LogWarning($"Failed to load by name: {nameEx.Message}, trying full path...");
                try
                {
                    Log.LogInfo($"Trying to load scene by full path: {targetScenePath}");
                    SceneManager.LoadSceneAsync(targetScenePath, LoadSceneMode.Additive);
                    Log.LogInfo("Scene loading initiated by path (async)!");
                }
                catch (Exception pathEx)
                {
                    Log.LogError($"Failed to load scene by path: {pathEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogError($"Error processing loaded bundle: {ex}");
            Log.LogError($"Stack trace: {ex.StackTrace}");
        }
    }
}

// MonoBehaviour helper to check for F8 key press and poll async bundle loading
public class SceneLoaderMonoBehaviour : MonoBehaviour
{
    private float lastCheckTime = 0f;
    private const float CheckInterval = 0.1f; // Check every 0.1 seconds to avoid too frequent checks
    private bool wasPressed = false;

    static SceneLoaderMonoBehaviour()
    {
        ClassInjector.RegisterTypeInIl2Cpp<SceneLoaderMonoBehaviour>();
    }

    private void Update()
    {
        // Check for pending async bundle loading
        if (Plugin.pendingAsyncOperation != null && Plugin.pendingAssetBundleType != null)
        {
            try
            {
                var isDoneProperty = Plugin.pendingAsyncOperation.GetType().GetProperty("isDone");
                var resultProperty = Plugin.pendingAsyncOperation.GetType().GetProperty("assetBundle");
                
                if (isDoneProperty != null && resultProperty != null)
                {
                    bool isDone = (bool)isDoneProperty.GetValue(Plugin.pendingAsyncOperation);
                    
                    if (isDone)
                    {
                        object bundleObj = resultProperty.GetValue(Plugin.pendingAsyncOperation);
                        Type assetBundleType = Plugin.pendingAssetBundleType;
                        
                        // Clear pending state
                        Plugin.pendingAsyncOperation = null;
                        Plugin.pendingAssetBundleType = null;
                        
                        // Process the loaded bundle
                        Plugin.ProcessLoadedBundle(bundleObj, assetBundleType);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Error checking async bundle loading: {ex.Message}");
                Plugin.pendingAsyncOperation = null;
                Plugin.pendingAssetBundleType = null;
            }
        }

        // Throttle key checking to avoid checking every frame
        float currentTime = Time.time;
        if (currentTime - lastCheckTime < CheckInterval)
        {
            return;
        }
        lastCheckTime = currentTime;

        // Check for F8 key press using Windows API (F8 = 0x77)
        bool isPressed = WindowsInput.IsKeyPressed(0x77); // VK_F8
        
        // Only trigger on key down (wasn't pressed before, but is pressed now)
        if (isPressed && !wasPressed)
        {
            Plugin.LoadSceneFromBundle();
            wasPressed = true;
        }
        else if (!isPressed)
        {
            wasPressed = false;
        }
    }
}

// Windows API helper for keyboard input
public static class WindowsInput
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public static bool IsKeyPressed(int vKey)
    {
        // Check if key is pressed (bit 15 is set)
        return (GetAsyncKeyState(vKey) & 0x8000) != 0;
    }
}
