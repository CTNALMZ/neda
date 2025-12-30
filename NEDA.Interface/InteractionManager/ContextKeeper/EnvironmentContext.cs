// Example 1: Environment-aware configuration;
using System.Diagnostics;
using static System.Runtime.CompilerServices.RuntimeHelpers;

public class GameManager : MonoBehaviour;
{
    private void Start()
    {
        // Get API endpoint based on environment;
        string apiEndpoint = EnvironmentContext.Instance.GetVariable("API_ENDPOINT", "http://localhost:3000");

        // Check if features are enabled;
        if (EnvironmentContext.Instance.IsFeatureEnabled("debug_menu"))
        {
            ShowDebugMenu();
        }

        // Environment-specific logic;
        switch (EnvironmentContext.Instance.GetCurrentEnvironment())
        {
            case EnvironmentType.Development:
                EnableCheats();
                EnableDebugTools();
                break;

            case EnvironmentType.Production:
                EnableAnalytics();
                EnableCrashReporting();
                DisableDebugLogs();
                break;
        }
    }
}

// Example 2: Environment-specific assets;
public class AssetLoader : MonoBehaviour;
{
    public Dictionary<EnvironmentType, string> assetBundles = new Dictionary<EnvironmentType, string>
    {
        { EnvironmentType.Development, "http://localhost:8080/bundles" },
        { EnvironmentType.Staging, "https://staging-cdn.example.com/bundles" },
        { EnvironmentType.Production, "https://cdn.example.com/bundles" }
    };

    IEnumerator LoadAssets()
    {
        // Get appropriate URL for current environment;
        string bundleUrl = EnvironmentContext.Instance.GetEnvironmentValue(assetBundles);

        yield return LoadBundleFromURL(bundleUrl);
    }
}

// Example 3: Environment validation;
public class SecurityManager : MonoBehaviour;
{
    private void Awake()
    {
        // Validate environment constraints;
        if (!EnvironmentContext.Instance.IsPlatformAllowed())
        {
            Debug.LogError("This platform is not allowed in current environment!");
            Application.Quit();
        }

        // Check for required environment variables;
        string apiKey = EnvironmentContext.Instance.GetVariable("API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            Debug.LogError("API_KEY is required but not set!");
#if UNITY_EDITOR;
            UnityEditor.EditorApplication.isPlaying = false;
#else;
            Application.Quit();
#endif;
        }
    }
}

// Example 4: Runtime environment switching;
public class DebugController : MonoBehaviour;
{
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F9))
        {
            // Toggle between development and production;
            var currentEnv = EnvironmentContext.Instance.GetCurrentEnvironment();
            var newEnv = currentEnv == EnvironmentType.Development ?
                EnvironmentType.Production : EnvironmentType.Development;

            EnvironmentContext.Instance.SwitchEnvironment(newEnv);
            Debug.Log($"Switched to {newEnv} environment");
        }
    }
}

// Example 5: Scene validation;
public class SceneLoader : MonoBehaviour;
{
    public void LoadScene(string sceneName)
    {
        if (!EnvironmentContext.Instance.IsSceneAllowed(sceneName))
        {
            Debug.LogError($"Scene '{sceneName}' is not allowed in current environment");
            return;
        }

        UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
    }
}
