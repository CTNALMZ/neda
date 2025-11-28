using System;
using System.Threading.Tasks;

namespace NEDA.Core.CommandProcessor
{
    // Görev yönlendirme sistemi
    public class TaskRouter : ITaskRouter
    {
        public static async Task RouteTaskAsync(string intent, string parameters)
        {
            // Simüle edilen görev işlemi
            await Task.Delay(100);
            Console.WriteLine($"[TaskRouter] Komut işlendi: {intent} | Parametre: {parameters}");
        }
    }
}
