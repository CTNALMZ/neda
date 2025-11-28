using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Neda.Automation
{
    public enum TaskPriority
    {
        Critical,
        High,
        Medium,
        Low
    }

    public class AutomationTask
    {
        public string TaskId { get; set; } = null!;
        public string Description { get; set; } = null!;
        public TaskPriority Priority { get; set; }
        public List<string> Dependencies { get; set; } = [];
        public Func<Task<bool>> Execute { get; set; } = null!;
        public bool IsCompleted { get; set; } = false;
        public bool IsFailed { get; set; } = false;
        public int RetryCount { get; set; } = 0;
        public int MaxRetries { get; set; } = 3;

        // Alternatif olarak constructor ile zorunlu atama
        public AutomationTask(string taskId, string description, Func<Task<bool>> execute)
        {
            TaskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
            Description = description ?? throw new ArgumentNullException(nameof(description));
            Execute = execute ?? throw new ArgumentNullException(nameof(execute));
        }

        public AutomationTask() { } // Parameterless constructor (opsiyonel)
    }

    public class TaskPlanner
    {
        private readonly Dictionary<string, AutomationTask> tasks = [];
        private readonly Queue<AutomationTask> readyQueue = new();

        // Nullable olaylar C# 10 ile uyumlu
        public event Action<AutomationTask>? OnTaskStarted;
        public event Action<AutomationTask>? OnTaskCompleted;
        public event Action<AutomationTask, Exception>? OnTaskFailed;

        public void AddTask(AutomationTask task)
        {
            if (!tasks.ContainsKey(task.TaskId))
                tasks[task.TaskId] = task;
        }

        public async Task ExecuteAllTasksAsync()
        {
            while (tasks.Values.Any(t => !t.IsCompleted && !t.IsFailed))
            {
                var executableTasks = tasks.Values
                    .Where(t => !t.IsCompleted && !t.IsFailed &&
                                t.Dependencies.All(d => tasks[d].IsCompleted))
                    .OrderBy(t => t.Priority)
                    .ToList();

                if (executableTasks.Count == 0)
                {
                    Console.WriteLine("TaskPlanner: Bekleyen bağımlılıklar var, kısa süre bekleniyor...");
                    await Task.Delay(500);
                    continue;
                }

                foreach (var task in executableTasks)
                    readyQueue.Enqueue(task);

                while (readyQueue.Count > 0)
                {
                    var currentTask = readyQueue.Dequeue();
                    await ExecuteTaskAsync(currentTask);
                }
            }
        }

        private async Task ExecuteTaskAsync(AutomationTask task)
        {
            try
            {
                OnTaskStarted?.Invoke(task);
                bool success = await task.Execute();

                if (success)
                {
                    task.IsCompleted = true;
                    OnTaskCompleted?.Invoke(task);
                }
                else
                {
                    await HandleTaskFailure(task, new Exception("Task returned failure"));
                }
            }
            catch (Exception ex)
            {
                await HandleTaskFailure(task, ex);
            }
        }

        private async Task HandleTaskFailure(AutomationTask task, Exception ex)
        {
            task.RetryCount++;
            Console.WriteLine($"TaskPlanner: Görev '{task.TaskId}' başarısız oldu. Deneme: {task.RetryCount}");
            OnTaskFailed?.Invoke(task, ex);

            if (task.RetryCount < task.MaxRetries)
            {
                await Task.Delay(1000);
                await ExecuteTaskAsync(task);
            }
            else
            {
                task.IsFailed = true;
                Console.WriteLine($"TaskPlanner: Görev '{task.TaskId}' kalıcı olarak başarısız oldu.");
            }
        }

        public void UpdateTaskPriority(string taskId, TaskPriority newPriority)
        {
            if (tasks.TryGetValue(taskId, out AutomationTask? value))
                value.Priority = newPriority;
        }

        public void CancelTask(string taskId)
        {
            if (tasks.TryGetValue(taskId, out AutomationTask? value))
            {
                value.IsFailed = true;
                Console.WriteLine($"TaskPlanner: Görev '{taskId}' iptal edildi.");
            }
        }

        public void ReportStatus()
        {
            foreach (var task in tasks.Values)
            {
                Console.WriteLine($"[{task.Priority}] {task.TaskId} - Tamamlandı: {task.IsCompleted}, Başarısız: {task.IsFailed}, Deneme: {task.RetryCount}");
            }
        }
    }
}
