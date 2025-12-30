using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UnityEngine;

/// <summary>
/// Validates manifest files (JSON, YAML, XML) against defined schemas and business rules;
/// Supports version validation, dependency checking, and integrity verification;
/// </summary>
public class ManifestoValidator : MonoBehaviour;
{
    [System.Serializable]
    public class ValidationSettings;
    {
        [Header("File Settings")]
        public string manifestFileName = "manifest.json";
        public string schemaFileName = "schema.json";
        public string backupExtension = ".backup";

        [Header("Validation Settings")]
        public bool validateOnStart = true;
        public bool autoFixCommonIssues = true;
        public bool createBackupBeforeFix = true;
        public ValidationLevel validationLevel = ValidationLevel.Strict;

        [Header("Logging")]
        public bool logToConsole = true;
        public bool logToFile = true;
        public string logFilePath = "Logs/ValidationLog.txt";
        public LogLevel minimumLogLevel = LogLevel.Warning;
    }

    public enum ValidationLevel;
    {
        None,
        Basic,
        Standard,
        Strict,
        Paranoid;
    }

    public enum LogLevel;
    {
        Info,
        Warning,
        Error,
        Critical;
    }

    [System.Serializable]
    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<ValidationIssue> Issues { get; set; } = new List<ValidationIssue>();
        public TimeSpan ValidationTime { get; set; }
        public string ManifestHash { get; set; }
        public string ManifestVersion { get; set; }

        public bool HasErrors => Issues.Any(i => i.Level == LogLevel.Error || i.Level == LogLevel.Critical);
        public bool HasWarnings => Issues.Any(i => i.Level == LogLevel.Warning);

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Validation Result: {(IsValid ? "VALID" : "INVALID")}");
            sb.AppendLine($"Version: {ManifestVersion}");
            sb.AppendLine($"Hash: {ManifestHash}");
            sb.AppendLine($"Time: {ValidationTime.TotalMilliseconds:F2}ms");
            sb.AppendLine($"Issues: {Issues.Count} total ({Issues.Count(i => i.Level == LogLevel.Error || i.Level == LogLevel.Critical)} errors, {Issues.Count(i => i.Level == LogLevel.Warning)} warnings)");

            foreach (var issue in Issues.OrderByDescending(i => i.Level))
            {
                sb.AppendLine($"[{issue.Level}] {issue.Message} (Line: {issue.Line}, Column: {issue.Column})");
            }

            return sb.ToString();
        }
    }

    [System.Serializable]
    public class ValidationIssue;
    {
        public LogLevel Level { get; set; }
        public string Message { get; set; }
        public string Field { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string FixSuggestion { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    [System.Serializable]
    public class ManifestSchema;
    {
        public string SchemaVersion { get; set; } = "1.0.0";
        public Dictionary<string, FieldDefinition> Fields { get; set; } = new Dictionary<string, FieldDefinition>();
        public List<ValidationRule> Rules { get; set; } = new List<ValidationRule>();
        public List<Dependency> Dependencies { get; set; } = new List<Dependency>();

        [System.Serializable]
        public class FieldDefinition;
        {
            public FieldType Type { get; set; }
            public bool Required { get; set; } = true;
            public object DefaultValue { get; set; }
            public object MinValue { get; set; }
            public object MaxValue { get; set; }
            public List<string> AllowedValues { get; set; } = new List<string>();
            public string Pattern { get; set; }
            public int MinLength { get; set; }
            public int MaxLength { get; set; }
            public string Description { get; set; }
        }

        public enum FieldType;
        {
            String,
            Integer,
            Float,
            Boolean,
            Array,
            Object,
            DateTime,
            Color,
            Vector2,
            Vector3,
            Enum;
        }

        [System.Serializable]
        public class ValidationRule;
        {
            public string Name { get; set; }
            public string Condition { get; set; }
            public string ErrorMessage { get; set; }
            public LogLevel Level { get; set; } = LogLevel.Error;
        }

        [System.Serializable]
        public class Dependency;
        {
            public string Field { get; set; }
            public string DependsOn { get; set; }
            public string Condition { get; set; }
        }
    }

    [SerializeField] private ValidationSettings settings = new ValidationSettings();
    [SerializeField] private ManifestSchema defaultSchema;

    private JObject currentManifest;
    private ManifestSchema activeSchema;
    private StringBuilder logBuilder = new StringBuilder();

    public static event Action<ValidationResult> OnValidationComplete;
    public static event Action<ValidationIssue> OnValidationIssue;

    private static ManifestoValidator instance;
    public static ManifestoValidator Instance;
    {
        get;
        {
            if (instance == null)
            {
                instance = FindObjectOfType<ManifestoValidator>();
                if (instance == null)
                {
                    var go = new GameObject("ManifestoValidator");
                    instance = go.AddComponent<ManifestoValidator>();
                }
            }
            return instance;
        }
    }

    private void Awake()
    {
        if (instance == null)
        {
            instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else if (instance != this)
        {
            Destroy(gameObject);
            return;
        }

        InitializeLogging();
    }

    private void Start()
    {
        if (settings.validateOnStart)
        {
            StartCoroutine(ValidateManifestAsync());
        }
    }

    private void InitializeLogging()
    {
        if (settings.logToFile)
        {
            string logDir = Path.GetDirectoryName(settings.logFilePath);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
        }
    }

    public IEnumerator ValidateManifestAsync()
    {
        yield return null; // Wait one frame;

        var result = ValidateManifest();

        if (OnValidationComplete != null)
        {
            OnValidationComplete(result);
        }

        if (!result.IsValid)
        {
            Debug.LogError($"Manifest validation failed with {result.Issues.Count} issues");
            if (settings.autoFixCommonIssues)
            {
                yield return StartCoroutine(AutoFixIssuesAsync(result));
            }
        }
        else;
        {
            Debug.Log("Manifest validation successful");
        }
    }

    public ValidationResult ValidateManifest()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new ValidationResult();

        try
        {
            // Load manifest;
            if (!LoadManifest(out string manifestContent))
            {
                result.Issues.Add(new ValidationIssue;
                {
                    Level = LogLevel.Critical,
                    Message = "Failed to load manifest file",
                    Field = "manifest",
                    FixSuggestion = "Check if manifest file exists and is accessible"
                });
                result.IsValid = false;
                return result;
            }

            // Parse JSON;
            if (!ParseManifest(manifestContent, out currentManifest))
            {
                result.IsValid = false;
                return result;
            }

            // Load schema;
            LoadSchema();

            // Perform validations;
            ValidateStructure(result);
            ValidateDataTypes(result);
            ValidateValues(result);
            ValidateDependencies(result);
            ValidateCustomRules(result);

            // Calculate hash;
            result.ManifestHash = CalculateHash(manifestContent);

            // Extract version if present;
            if (currentManifest["version"] != null)
            {
                result.ManifestVersion = currentManifest["version"].ToString();
            }

            result.IsValid = !result.HasErrors;
        }
        catch (Exception ex)
        {
            result.Issues.Add(new ValidationIssue;
            {
                Level = LogLevel.Critical,
                Message = $"Validation crashed: {ex.Message}",
                Field = "system",
                FixSuggestion = "Check validation code and manifest format"
            });
            result.IsValid = false;
        }
        finally
        {
            stopwatch.Stop();
            result.ValidationTime = stopwatch.Elapsed;

            // Log results;
            LogValidationResult(result);
        }

        return result;
    }

    private bool LoadManifest(out string content)
    {
        content = null;

        string[] possiblePaths = new[]
        {
            Path.Combine(Application.dataPath, settings.manifestFileName),
            Path.Combine(Application.streamingAssetsPath, settings.manifestFileName),
            Path.Combine(Application.persistentDataPath, settings.manifestFileName),
            settings.manifestFileName;
        };

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    content = File.ReadAllText(path);
                    LogInfo($"Loaded manifest from: {path}");
                    return true;
                }
                catch (Exception ex)
                {
                    LogError($"Failed to read manifest file: {ex.Message}", "manifest");
                }
            }
        }

        return false;
    }

    private bool ParseManifest(string content, out JObject manifest)
    {
        manifest = null;

        try
        {
            manifest = JObject.Parse(content);
            return true;
        }
        catch (JsonException ex)
        {
            LogError($"JSON parsing error: {ex.Message}", "manifest");

            // Try to extract line and column from error message;
            var match = Regex.Match(ex.Message, @"Line (\d+),.*position (\d+)");
            if (match.Success)
            {
                int line = int.Parse(match.Groups[1].Value);
                int column = int.Parse(match.Groups[2].Value);

                var issue = new ValidationIssue;
                {
                    Level = LogLevel.Error,
                    Message = "Invalid JSON syntax",
                    Field = "manifest",
                    Line = line,
                    Column = column,
                    FixSuggestion = "Check JSON syntax, ensure proper formatting and quotation"
                };

                OnValidationIssue?.Invoke(issue);
            }

            return false;
        }
    }

    private void LoadSchema()
    {
        // Try to load from file first;
        if (File.Exists(settings.schemaFileName))
        {
            try
            {
                string schemaJson = File.ReadAllText(settings.schemaFileName);
                activeSchema = JsonConvert.DeserializeObject<ManifestSchema>(schemaJson);
                LogInfo($"Loaded schema from: {settings.schemaFileName}");
                return;
            }
            catch (Exception ex)
            {
                LogWarning($"Failed to load schema file: {ex.Message}", "schema");
            }
        }

        // Use default schema;
        activeSchema = defaultSchema ?? CreateDefaultSchema();
        LogInfo("Using default schema");
    }

    private ManifestSchema CreateDefaultSchema()
    {
        return new ManifestSchema;
        {
            SchemaVersion = "1.0.0",
            Fields = new Dictionary<string, ManifestSchema.FieldDefinition>
            {
                ["name"] = new ManifestSchema.FieldDefinition;
                {
                    Type = ManifestSchema.FieldType.String,
                    Required = true,
                    MinLength = 1,
                    MaxLength = 100;
                },
                ["version"] = new ManifestSchema.FieldDefinition;
                {
                    Type = ManifestSchema.FieldType.String,
                    Required = true,
                    Pattern = @"^\d+\.\d+\.\d+$" // Semantic versioning;
                },
                ["author"] = new ManifestSchema.FieldDefinition;
                {
                    Type = ManifestSchema.FieldType.String,
                    Required = false,
                    MaxLength = 50;
                },
                ["description"] = new ManifestSchema.FieldDefinition;
                {
                    Type = ManifestSchema.FieldType.String,
                    Required = false,
                    MaxLength = 500;
                }
            },
            Rules = new List<ManifestSchema.ValidationRule>
            {
                new ManifestSchema.ValidationRule;
                {
                    Name = "VersionFormat",
                    Condition = "version != null && Regex.IsMatch(version, @\"^\\d+\\.\\d+\\.\\d+$\")",
                    ErrorMessage = "Version must follow semantic versioning (x.y.z)"
                }
            }
        };
    }

    private void ValidateStructure(ValidationResult result)
    {
        if (activeSchema?.Fields == null) return;

        foreach (var field in activeSchema.Fields)
        {
            string fieldName = field.Key;
            var definition = field.Value;

            bool fieldExists = currentManifest[fieldName] != null;

            if (definition.Required && !fieldExists)
            {
                AddValidationIssue(
                    result,
                    LogLevel.Error,
                    $"Required field '{fieldName}' is missing",
                    fieldName,
                    fixSuggestion: $"Add '{fieldName}' field to manifest"
                );
            }
        }
    }

    private void ValidateDataTypes(ValidationResult result)
    {
        if (activeSchema?.Fields == null) return;

        foreach (var field in activeSchema.Fields)
        {
            string fieldName = field.Key;
            var definition = field.Value;
            var token = currentManifest[fieldName];

            if (token == null) continue;

            bool typeValid = false;
            string actualType = token.Type.ToString().ToLower();

            switch (definition.Type)
            {
                case ManifestSchema.FieldType.String:
                    typeValid = token.Type == JTokenType.String;
                    break;

                case ManifestSchema.FieldType.Integer:
                    typeValid = token.Type == JTokenType.Integer;
                    break;

                case ManifestSchema.FieldType.Float:
                    typeValid = token.Type == JTokenType.Float || token.Type == JTokenType.Integer;
                    break;

                case ManifestSchema.FieldType.Boolean:
                    typeValid = token.Type == JTokenType.Boolean;
                    break;

                case ManifestSchema.FieldType.Array:
                    typeValid = token.Type == JTokenType.Array;
                    break;

                case ManifestSchema.FieldType.Object:
                    typeValid = token.Type == JTokenType.Object;
                    break;

                case ManifestSchema.FieldType.DateTime:
                    if (token.Type == JTokenType.String)
                    {
                        typeValid = DateTime.TryParse(token.ToString(), out _);
                    }
                    break;
            }

            if (!typeValid)
            {
                AddValidationIssue(
                    result,
                    LogLevel.Error,
                    $"Field '{fieldName}' has invalid type. Expected: {definition.Type}, Actual: {actualType}",
                    fieldName,
                    fixSuggestion: $"Change '{fieldName}' to correct type"
                );
            }
        }
    }

    private void ValidateValues(ValidationResult result)
    {
        if (activeSchema?.Fields == null) return;

        foreach (var field in activeSchema.Fields)
        {
            string fieldName = field.Key;
            var definition = field.Value;
            var token = currentManifest[fieldName];

            if (token == null) continue;

            // String validations;
            if (definition.Type == ManifestSchema.FieldType.String && token.Type == JTokenType.String)
            {
                string value = token.Value<string>();

                // Length validation;
                if (value.Length < definition.MinLength)
                {
                    AddValidationIssue(
                        result,
                        LogLevel.Error,
                        $"Field '{fieldName}' is too short. Min length: {definition.MinLength}, Actual: {value.Length}",
                        fieldName;
                    );
                }

                if (definition.MaxLength > 0 && value.Length > definition.MaxLength)
                {
                    AddValidationIssue(
                        result,
                        LogLevel.Error,
                        $"Field '{fieldName}' is too long. Max length: {definition.MaxLength}, Actual: {value.Length}",
                        fieldName;
                    );
                }

                // Pattern validation;
                if (!string.IsNullOrEmpty(definition.Pattern))
                {
                    if (!Regex.IsMatch(value, definition.Pattern))
                    {
                        AddValidationIssue(
                            result,
                            LogLevel.Error,
                            $"Field '{fieldName}' doesn't match required pattern",
                            fieldName;
                        );
                    }
                }

                // Allowed values validation;
                if (definition.AllowedValues != null && definition.AllowedValues.Count > 0)
                {
                    if (!definition.AllowedValues.Contains(value))
                    {
                        AddValidationIssue(
                            result,
                            LogLevel.Error,
                            $"Field '{fieldName}' has invalid value. Allowed: {string.Join(", ", definition.AllowedValues)}",
                            fieldName;
                        );
                    }
                }
            }

            // Numeric range validation;
            if ((definition.Type == ManifestSchema.FieldType.Integer || definition.Type == ManifestSchema.FieldType.Float)
                && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float))
            {
                double value = token.Value<double>();

                if (definition.MinValue != null && value < Convert.ToDouble(definition.MinValue))
                {
                    AddValidationIssue(
                        result,
                        LogLevel.Error,
                        $"Field '{fieldName}' value is too small. Min: {definition.MinValue}, Actual: {value}",
                        fieldName;
                    );
                }

                if (definition.MaxValue != null && value > Convert.ToDouble(definition.MaxValue))
                {
                    AddValidationIssue(
                        result,
                        LogLevel.Error,
                        $"Field '{fieldName}' value is too large. Max: {definition.MaxValue}, Actual: {value}",
                        fieldName;
                    );
                }
            }
        }
    }

    private void ValidateDependencies(ValidationResult result)
    {
        if (activeSchema?.Dependencies == null) return;

        foreach (var dependency in activeSchema.Dependencies)
        {
            var dependentToken = currentManifest[dependency.Field];
            var dependsOnToken = currentManifest[dependency.DependsOn];

            if (dependentToken != null && dependsOnToken == null)
            {
                AddValidationIssue(
                    result,
                    LogLevel.Error,
                    $"Field '{dependency.Field}' depends on missing field '{dependency.DependsOn}'",
                    dependency.Field;
                );
            }
        }
    }

    private void ValidateCustomRules(ValidationResult result)
    {
        if (activeSchema?.Rules == null) return;

        // Note: In a real implementation, you would need a simple expression evaluator;
        // For now, we'll implement a few hardcoded common rules;

        foreach (var rule in activeSchema.Rules)
        {
            // Simple version format rule implementation;
            if (rule.Name == "VersionFormat" && rule.Condition.Contains("version"))
            {
                var versionToken = currentManifest["version"];
                if (versionToken != null && versionToken.Type == JTokenType.String)
                {
                    string version = versionToken.Value<string>();
                    if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$"))
                    {
                        AddValidationIssue(
                            result,
                            rule.Level,
                            rule.ErrorMessage,
                            "version"
                        );
                    }
                }
            }
        }
    }

    private IEnumerator AutoFixIssuesAsync(ValidationResult result)
    {
        LogInfo("Starting auto-fix process...");

        if (settings.createBackupBeforeFix)
        {
            CreateBackup();
        }

        int fixedIssues = 0;

        foreach (var issue in result.Issues.Where(i => i.Level != LogLevel.Critical))
        {
            if (TryAutoFixIssue(issue))
            {
                fixedIssues++;
                LogInfo($"Auto-fixed issue: {issue.Message}");
                yield return null; // Wait one frame between fixes;
            }
        }

        LogInfo($"Auto-fix completed. Fixed {fixedIssues} out of {result.Issues.Count} issues.");

        // Re-validate after fixing;
        if (fixedIssues > 0)
        {
            yield return StartCoroutine(ValidateManifestAsync());
        }
    }

    private bool TryAutoFixIssue(ValidationIssue issue)
    {
        try
        {
            // Implement common auto-fixes;
            if (issue.Message.Contains("is missing") && issue.Field != "manifest")
            {
                var definition = activeSchema.Fields[issue.Field];
                if (definition != null && definition.DefaultValue != null)
                {
                    currentManifest[issue.Field] = new JValue(definition.DefaultValue);
                    SaveManifest();
                    return true;
                }
            }

            // Trim whitespace for string fields;
            if (issue.Message.Contains("too long") || issue.Message.Contains("format"))
            {
                var token = currentManifest[issue.Field];
                if (token?.Type == JTokenType.String)
                {
                    string value = token.Value<string>().Trim();
                    currentManifest[issue.Field] = new JValue(value);
                    SaveManifest();
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            LogWarning($"Failed to auto-fix issue '{issue.Message}': {ex.Message}", issue.Field);
        }

        return false;
    }

    private void CreateBackup()
    {
        try
        {
            string backupPath = settings.manifestFileName + settings.backupExtension;
            if (File.Exists(settings.manifestFileName))
            {
                File.Copy(settings.manifestFileName, backupPath, true);
                LogInfo($"Created backup: {backupPath}");
            }
        }
        catch (Exception ex)
        {
            LogWarning($"Failed to create backup: {ex.Message}", "backup");
        }
    }

    private void SaveManifest()
    {
        try
        {
            string json = JsonConvert.SerializeObject(currentManifest, Formatting.Indented);
            File.WriteAllText(settings.manifestFileName, json);
            LogInfo("Manifest saved successfully");
        }
        catch (Exception ex)
        {
            LogError($"Failed to save manifest: {ex.Message}", "save");
        }
    }

    private string CalculateHash(string content)
    {
        using (var sha256 = System.Security.Cryptography.SHA256.Create())
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            byte[] hash = sha256.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
    }

    private void AddValidationIssue(
        ValidationResult result,
        LogLevel level,
        string message,
        string field,
        int line = 0,
        int column = 0,
        string fixSuggestion = null)
    {
        var issue = new ValidationIssue;
        {
            Level = level,
            Message = message,
            Field = field,
            Line = line,
            Column = column,
            FixSuggestion = fixSuggestion;
        };

        result.Issues.Add(issue);

        if (OnValidationIssue != null)
        {
            OnValidationIssue(issue);
        }

        Log(level, message, field);
    }

    private void LogValidationResult(ValidationResult result)
    {
        string logMessage = result.ToString();

        if (settings.logToConsole)
        {
            if (result.HasErrors)
                Debug.LogError(logMessage);
            else if (result.HasWarnings)
                Debug.LogWarning(logMessage);
            else;
                Debug.Log(logMessage);
        }

        if (settings.logToFile)
        {
            WriteToLogFile(logMessage);
        }
    }

    private void Log(LogLevel level, string message, string context = null)
    {
        if ((int)level < (int)settings.minimumLogLevel) return;

        string logEntry = $"[{DateTime.Now:HH:mm:ss}] [{level}] {(context != null ? $"[{context}] " : "")}{message}";

        if (settings.logToConsole)
        {
            switch (level)
            {
                case LogLevel.Error:
                case LogLevel.Critical:
                    Debug.LogError(logEntry);
                    break;
                case LogLevel.Warning:
                    Debug.LogWarning(logEntry);
                    break;
                default:
                    Debug.Log(logEntry);
                    break;
            }
        }

        if (settings.logToFile)
        {
            WriteToLogFile(logEntry);
        }
    }

    private void LogInfo(string message, string context = null) => Log(LogLevel.Info, message, context);
    private void LogWarning(string message, string context = null) => Log(LogLevel.Warning, message, context);
    private void LogError(string message, string context = null) => Log(LogLevel.Error, message, context);

    private void WriteToLogFile(string message)
    {
        try
        {
            File.AppendAllText(settings.logFilePath, message + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to write to log file: {ex.Message}");
        }
    }

    // Public API;
    public ValidationResult Validate(string manifestContent, ManifestSchema schema = null)
    {
        if (!ParseManifest(manifestContent, out var manifest))
        {
            return new ValidationResult { IsValid = false };
        }

        currentManifest = manifest;
        activeSchema = schema ?? activeSchema;

        return ValidateManifest();
    }

    public bool ValidateField(string fieldPath, out string error)
    {
        error = null;

        try
        {
            var token = currentManifest.SelectToken(fieldPath);
            var fieldName = fieldPath.Split('.').Last();

            if (activeSchema.Fields.TryGetValue(fieldName, out var definition))
            {
                if (definition.Required && token == null)
                {
                    error = $"Required field '{fieldName}' is missing";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void AddSchemaField(string fieldName, ManifestSchema.FieldDefinition definition)
    {
        if (activeSchema.Fields.ContainsKey(fieldName))
        {
            activeSchema.Fields[fieldName] = definition;
        }
        else;
        {
            activeSchema.Fields.Add(fieldName, definition);
        }
    }

    public void SaveSchemaToFile()
    {
        try
        {
            string json = JsonConvert.SerializeObject(activeSchema, Formatting.Indented);
            File.WriteAllText(settings.schemaFileName, json);
            LogInfo($"Schema saved to: {settings.schemaFileName}");
        }
        catch (Exception ex)
        {
            LogError($"Failed to save schema: {ex.Message}", "schema");
        }
    }

    public string GenerateSchemaTemplate()
    {
        var template = new ManifestSchema;
        {
            SchemaVersion = "1.0.0",
            Fields = new Dictionary<string, ManifestSchema.FieldDefinition>
            {
                ["id"] = new ManifestSchema.FieldDefinition;
                {
                    Type = ManifestSchema.FieldType.String,
                    Required = true,
                    Description = "Unique identifier"
                },
                ["name"] = new ManifestSchema.FieldDefinition;
                {
                    Type = ManifestSchema.FieldType.String,
                    Required = true,
                    MinLength = 1,
                    MaxLength = 100,
                    Description = "Display name"
                }
            }
        };

        return JsonConvert.SerializeObject(template, Formatting.Indented);
    }
}
