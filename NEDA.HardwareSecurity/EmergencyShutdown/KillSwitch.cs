using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using static System.Runtime.CompilerServices.RuntimeHelpers;

public class AdvancedKillSwitch : MonoBehaviour;
{
    [System.Serializable]
    public class KillCondition;
    {
        public string name;
        public bool enabled;
        public ConditionType type;
        public float threshold;
        public float checkInterval = 1f;

        [HideInInspector] public float timer;
        [HideInInspector] public float value;
    }

    public enum ConditionType;
    {
        MemoryUsage,
        CPUUsage,
        ObjectCount,
        Custom;
    }

    [Header("Activation Settings")]
    [SerializeField] private ActivationMode activationMode = ActivationMode.HoldKey;
    [SerializeField] private KeyCode primaryKey = KeyCode.End;
    [SerializeField] private KeyCode secondaryKey = KeyCode.K;
    [SerializeField] private float activationDelay = 2f;

    [Header("Conditions")]
    [SerializeField] private List<KillCondition> conditions = new List<KillCondition>();

    [Header("Actions")]
    [SerializeField] private KillAction primaryAction = KillAction.Quit;
    [SerializeField] private string safeSceneName = "SafeScene";

    [Header("Logging")]
    [SerializeField] private bool logToFile = false;
    [SerializeField] private string logFileName = "killswitch_log.txt";

    private float activationTimer = 0f;
    private bool isActivating = false;
    private System.Diagnostics.Stopwatch runtimeStopwatch;

    public enum ActivationMode { HoldKey, KeyCombo, Sequence }
    public enum KillAction { Quit, LoadSafeScene, DisableSystems, Custom }

    public static AdvancedKillSwitch Instance { get; private set; }

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else;
        {
            Destroy(gameObject);
            return;
        }

        runtimeStopwatch = System.Diagnostics.Stopwatch.StartNew();
        InitializeConditions();
    }

    private void InitializeConditions()
    {
        // Add default conditions if empty;
        if (conditions.Count == 0)
        {
            conditions.Add(new KillCondition;
            {
                name = "High Memory",
                enabled = true,
                type = ConditionType.MemoryUsage,
                threshold = 1024 * 1024 * 1024, // 1GB;
                checkInterval = 5f;
            });
        }
    }

    private void Update()
    {
        CheckManualActivation();
        CheckConditions();
    }

    private void CheckManualActivation()
    {
        switch (activationMode)
        {
            case ActivationMode.HoldKey:
                if (Input.GetKey(primaryKey))
                {
                    activationTimer += Time.deltaTime;
                    if (activationTimer >= activationDelay && !isActivating)
                    {
                        Activate("Manual hold activation");
                    }
                }
                else;
                {
                    activationTimer = 0f;
                    isActivating = false;
                }
                break;

            case ActivationMode.KeyCombo:
                if (Input.GetKey(primaryKey) && Input.GetKeyDown(secondaryKey))
                {
                    Activate("Manual combo activation");
                }
                break;
        }
    }

    private void CheckConditions()
    {
        foreach (var condition in conditions)
        {
            if (!condition.enabled) continue;

            condition.timer += Time.deltaTime;
            if (condition.timer >= condition.checkInterval)
            {
                condition.timer = 0f;

                float currentValue = GetConditionValue(condition);
                condition.value = currentValue;

                if (currentValue >= condition.threshold)
                {
                    Activate($"Condition '{condition.name}' triggered: {currentValue} >= {condition.threshold}");
                }
            }
        }
    }

    private float GetConditionValue(KillCondition condition)
    {
        switch (condition.type)
        {
            case ConditionType.MemoryUsage:
                return System.GC.GetTotalMemory(false) / (1024f * 1024f); // MB;

            case ConditionType.CPUUsage:
                // Note: Getting actual CPU usage requires platform-specific code;
                return 0f; // Implement platform-specific CPU monitoring;

            case ConditionType.ObjectCount:
                return FindObjectsOfType<UnityEngine.Object>().Length;

            default:
                return 0f;
        }
    }

    public void Activate(string reason)
    {
        LogActivation(reason);

        switch (primaryAction)
        {
            case KillAction.Quit:
#if UNITY_EDITOR;
                UnityEditor.EditorApplication.isPlaying = false;
#else;
                Application.Quit();
#endif;
                break;

            case KillAction.LoadSafeScene:
                UnityEngine.SceneManagement.SceneManager.LoadScene(safeSceneName);
                break;

            case KillAction.DisableSystems:
                StartCoroutine(DisableSystemsGradually());
                break;
        }
    }

    private System.Collections.IEnumerator DisableSystemsGradually()
    {
        // Gradually disable systems to prevent crashes;
        Debug.LogWarning("Gradually disabling systems...");

        // 1. Stop all audio;
        AudioListener.volume = 0f;
        yield return new WaitForSeconds(0.1f);

        // 2. Stop all particle systems;
        var particles = FindObjectsOfType<ParticleSystem>();
        foreach (var ps in particles)
        {
            ps.Stop();
        }
        yield return new WaitForSeconds(0.1f);

        // 3. Disable non-essential scripts;
        var scripts = FindObjectsOfType<MonoBehaviour>();
        foreach (var script in scripts)
        {
            if (script != this && script.enabled)
            {
                script.enabled = false;
            }
        }

        Debug.LogWarning("All non-essential systems disabled");
    }

    private void LogActivation(string reason)
    {
        string logEntry = $"[{DateTime.Now}] Kill Switch Activated\n" +
                         $"Reason: {reason}\n" +
                         $"Runtime: {runtimeStopwatch.Elapsed}\n" +
                         $"Platform: {Application.platform}\n" +
                         $"Version: {Application.version}\n" +
                         "---\n";

        Debug.LogError(logEntry);

        if (logToFile)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(Application.persistentDataPath, logFileName),
                    logEntry
                );
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to write kill switch log: {e.Message}");
            }
        }
    }

    // Public API;
    public void AddCondition(KillCondition condition) => conditions.Add(condition);
    public void RemoveCondition(string name) => conditions.RemoveAll(c => c.name == name);
    public void EnableCondition(string name, bool enable)
    {
        var condition = conditions.Find(c => c.name == name);
        if (condition != null) condition.enabled = enable;
    }

    public float GetRuntimeSeconds() => (float)runtimeStopwatch.Elapsed.TotalSeconds;

    private void OnApplicationQuit()
    {
        runtimeStopwatch.Stop();
    }
}
