// Example 1: Basic key storage;
using NEDA.HardwareSecurity.BiometricLocks;

public class APIManager : MonoBehaviour;
{
    private void Start()
    {
        // Store API keys securely;
        KeyStorage.Instance.StoreKey(
            "OpenAI_API_Key",
            "sk-...",
            category: "API",
            type: KeyType.APIKey,
            expires: DateTime.Now.AddDays(90)
        );

        // Retrieve when needed;
        string apiKey = KeyStorage.Instance.RetrieveKey("OpenAI_API_Key");

        if (!string.IsNullOrEmpty(apiKey))
        {
            InitializeOpenAI(apiKey);
        }
    }
}

// Example 2: User credentials;
public class AuthManager : MonoBehaviour;
{
    public void SaveCredentials(string username, string password)
    {
        // Store username (less sensitive)
        PlayerPrefs.SetString("username", username);

        // Store password securely;
        KeyStorage.Instance.StoreKey(
            "user_password",
            password,
            category: "Credentials",
            type: KeyType.Password,
            accessLevel: KeyAccessLevel.Secret;
        );
    }

    public bool ValidateCredentials(string username, string password)
    {
        string storedUsername = PlayerPrefs.GetString("username");
        string storedPassword = KeyStorage.Instance.RetrieveKey("user_password");

        return username == storedUsername && password == storedPassword;
    }
}

// Example 3: Secure configuration;
public class GameConfig : MonoBehaviour;
{
    [System.Serializable]
    public class SecureConfig;
    {
        public string analyticsKey;
        public string adsKey;
        public string serverUrl;
        public bool enableTelemetry
    }

    private void SaveSecureConfig(SecureConfig config)
    {
        string json = JsonUtility.ToJson(config);
        KeyStorage.Instance.StoreKey(
            "game_config",
            json,
            category: "Configuration",
            type: KeyType.Custom,
            expires: null // Never expires;
        );
    }

    private SecureConfig LoadSecureConfig()
    {
        string json = KeyStorage.Instance.RetrieveKey("game_config");

        if (!string.IsNullOrEmpty(json))
        {
            return JsonUtility.FromJson<SecureConfig>(json);
        }

        return null;
    }
}

// Example 4: Crypto wallet integration;
public class CryptoWallet : MonoBehaviour;
{
    public void StoreWallet(string walletAddress, string privateKey)
    {
        // Store wallet address publicly;
        PlayerPrefs.SetString("wallet_address", walletAddress);

        // Store private key with maximum security;
        KeyStorage.Instance.StoreKey(
            $"wallet_privkey_{walletAddress}",
            privateKey,
            category: "Crypto",
            type: KeyType.AsymmetricKey,
            accessLevel: KeyAccessLevel.TopSecret,
            expires: DateTime.Now.AddYears(10) // Long-term storage;
        );
    }

    public string SignTransaction(string transactionData)
    {
        string address = PlayerPrefs.GetString("wallet_address");
        string privateKey = KeyStorage.Instance.RetrieveKey($"wallet_privkey_{address}");

        if (!string.IsNullOrEmpty(privateKey))
        {
            // Sign transaction with private key;
            return SignWithPrivateKey(transactionData, privateKey);
        }

        return null;
    }
}
