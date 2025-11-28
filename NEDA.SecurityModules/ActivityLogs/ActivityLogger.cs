namespace NEDA.SecurityModules.ActivityLogs
{
    public static class ActivityLogger
    {
        private static readonly string _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NEDA.SecurityModules\\ActivityLogs");
        private static readonly string _logFile = Path.Combine(_logDirectory, "activity.log");

        static ActivityLogger()
        {
            // Log klasörü yoksa oluştur
            if (!Directory.Exists(_logDirectory))
                Directory.CreateDirectory(_logDirectory);

            // Log dosyası yoksa oluştur
            if (!File.Exists(_logFile))
                File.Create(_logFile).Close();
        }

        /// <summary>
        /// Genel aktiviteleri loglar (komutlar, işlemler)
        /// </summary>
        public static void LogActivity(string message)
        {
            Log("INFO", message);
        }

        /// <summary>
        /// Güvenlik ile ilgili kritik olayları loglar
        /// </summary>
        public static void LogSecurity(string message)
        {
            Log("SECURITY", message);
        }

        /// <summary>
        /// Hata ve exception kayıtları
        /// </summary>
        public static void LogError(string message, Exception ex = null)
        {
            string fullMessage = ex != null ? $"{message} | Exception: {ex.Message}" : message;
            Log("ERROR", fullMessage);
        }

        /// <summary>
        /// Uyarı mesajları
        /// </summary>
        public static void LogWarning(string message)
        {
            Log("WARNING", message);
        }

        /// <summary>
        /// Tüm log kayıtlarını merkezi method ile yönetir
        /// </summary>
        private static void Log(string type, string message)
        {
            string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{type}] - {message}";
            Console.WriteLine(logEntry);
            try
            {
                File.AppendAllText(_logFile, logEntry + Environment.NewLine);
            }
            catch
            {
                // Eğer dosya erişimi başarısız olursa, console’a yaz
                Console.WriteLine("[LOGGER ERROR] Log yazılamadı: " + message);
            }
        }

        /// <summary>
        /// Log dosyasını temizleme veya yedekleme
        /// </summary>
        public static void ArchiveLogs()
        {
            string archiveFile = Path.Combine(_logDirectory, $"activity_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.Copy(_logFile, archiveFile);
            File.WriteAllText(_logFile, string.Empty);
            Console.WriteLine($"[LOGGER] Loglar arşivlendi: {archiveFile}");
        }
    }
}
