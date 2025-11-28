using System;
using System.Threading.Tasks;

namespace Neda.CommandProcessor
{
    #region Yardımcı Sınıflar / Stublar

    // Basit loglama sistemi
    public class ActivityLogger
    {
        public static void Log(string message)
        {
            Console.WriteLine($"[ActivityLogger] {message}");
        }
    }

    // Basit izin kontrol sistemi
    public class PermissionChecker
    {
        public static bool HasPermission(string intent)
        {
            ArgumentNullException.ThrowIfNull(intent);
            // Örnek: tüm komutlara izin ver
            return true;
        }
    }

    #endregion

    public class CommandProcessor(TaskRouter taskRouter)
    {
        private readonly TaskRouter _taskRouter = taskRouter ?? throw new ArgumentNullException(nameof(taskRouter));
        private readonly PermissionChecker _permissionChecker = new();
        private readonly ActivityLogger _logger = new();

        // Asenkron komut işleme
        public async Task ProcessCommandAsync(string intent, string parameters = "")
        {
            try
            {
                ActivityLogger.Log($"[CommandProcessor] Komut alındı: {intent} | Parametre: {parameters}");

                // Güvenlik kontrolü
                if (!PermissionChecker.HasPermission(intent))
                {
                    ActivityLogger.Log($"[CommandProcessor] İzin yok: {intent}");
                    Console.WriteLine("Bu komutu çalıştırmak için yetkiniz yok.");
                    return;
                }

                // Komutu TaskRouter'a yönlendir
                await _taskRouter.RouteTaskAsync(intent, parameters);

                ActivityLogger.Log($"[CommandProcessor] Komut başarıyla işlendi: {intent}");
            }
            catch (Exception ex)
            {
                ActivityLogger.Log($"[CommandProcessor] Hata: {ex.Message}");
                Console.WriteLine($"Bir hata oluştu: {ex.Message}");

                // Geri alma veya retry mekanizması
                await HandleErrorAsync(intent, parameters, ex);
            }
        }

        private async Task HandleErrorAsync(string intent, string parameters, Exception ex)
        {
            ArgumentNullException.ThrowIfNull(ex);
            Console.WriteLine("[CommandProcessor] Hata yönetimi çalıştırılıyor...");

            // Basit retry örneği (1 defa)
            try
            {
                await _taskRouter.RouteTaskAsync(intent, parameters);
                ActivityLogger.Log($"[CommandProcessor] Komut retry ile başarıyla işlendi: {intent}");
            }
            catch (Exception retryEx)
            {
                ActivityLogger.Log($"[CommandProcessor] Retry başarısız: {retryEx.Message}");
                Console.WriteLine("[CommandProcessor] Komut işlenemedi, sistem yöneticisine bildirildi.");
            }
        }
    }
}
