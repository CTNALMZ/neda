namespace Neda.CommandProcessor
{
    public class ActivityLoggerBase
    {
        public void Log(string message)
        {
            Console.WriteLine($"[ActivityLogger] {message}");
        }
    }
}