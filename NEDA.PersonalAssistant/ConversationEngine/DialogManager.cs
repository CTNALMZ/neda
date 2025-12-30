using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace YourNamespace.Utilities.Dialogs;
{
    /// <summary>
    /// Manages dialog windows in WPF applications;
    /// </summary>
    public interface IDialogManager;
    {
        /// <summary>
        /// Shows a dialog with the specified view model;
        /// </summary>
        Task<bool?> ShowDialogAsync<TViewModel>(TViewModel viewModel)
            where TViewModel : class;

        /// <summary>
        /// Shows a message dialog;
        /// </summary>
        Task<MessageDialogResult> ShowMessageAsync(
            string title,
            string message,
            MessageDialogButtons buttons = MessageDialogButtons.OK,
            MessageDialogType type = MessageDialogType.Information);

        /// <summary>
        /// Shows a custom dialog window;
        /// </summary>
        Task<bool?> ShowCustomDialogAsync<TView, TViewModel>(TViewModel viewModel)
            where TView : Window, new()
            where TViewModel : class;

        /// <summary>
        /// Shows a dialog with specific owner window;
        /// </summary>
        Task<bool?> ShowDialogWithOwnerAsync<TViewModel>(
            TViewModel viewModel,
            Window owner)
            where TViewModel : class;

        /// <summary>
        /// Shows a dialog with specific startup location;
        /// </summary>
        Task<bool?> ShowDialogWithLocationAsync<TViewModel>(
            TViewModel viewModel,
            WindowStartupLocation startupLocation)
            where TViewModel : class;
    }

    /// <summary>
    /// Implementation of dialog manager for WPF applications;
    /// </summary>
    public class DialogManager : IDialogManager;
    {
        #region Private Fields;

        private readonly IServiceProvider _serviceProvider;
        private readonly Dictionary<Type, Type> _dialogMappings;
        private readonly List<Window> _openDialogs;
        private readonly object _lockObject = new object();

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new instance of DialogManager;
        /// </summary>
        /// <param name="serviceProvider">Service provider for dependency injection</param>
        public DialogManager(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider ??
                throw new ArgumentNullException(nameof(serviceProvider));

            _dialogMappings = new Dictionary<Type, Type>();
            _openDialogs = new List<Window>();

            RegisterDefaultDialogs();
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Shows a dialog with the specified view model;
        /// </summary>
        public async Task<bool?> ShowDialogAsync<TViewModel>(TViewModel viewModel)
            where TViewModel : class;
        {
            if (viewModel == null)
                throw new ArgumentNullException(nameof(viewModel));

            return await ShowDialogInternalAsync(viewModel);
        }

        /// <summary>
        /// Shows a message dialog;
        /// </summary>
        public async Task<MessageDialogResult> ShowMessageAsync(
            string title,
            string message,
            MessageDialogButtons buttons = MessageDialogButtons.OK,
            MessageDialogType type = MessageDialogType.Information)
        {
            if (string.IsNullOrEmpty(title))
                throw new ArgumentNullException(nameof(title));

            if (string.IsNullOrEmpty(message))
                throw new ArgumentNullException(nameof(message));

            var messageViewModel = new MessageDialogViewModel;
            {
                Title = title,
                Message = message,
                DialogType = type,
                Buttons = buttons;
            };

            var dialogResult = await ShowDialogAsync(messageViewModel);

            return messageViewModel.Result;
        }

        /// <summary>
        /// Shows a custom dialog window;
        /// </summary>
        public async Task<bool?> ShowCustomDialogAsync<TView, TViewModel>(TViewModel viewModel)
            where TView : Window, new()
            where TViewModel : class;
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dialog = new TView();
                dialog.DataContext = viewModel;

                ConfigureDialogWindow(dialog);

                return dialog.ShowDialog();
            }, DispatcherPriority.Normal);
        }

        /// <summary>
        /// Shows a dialog with specific owner window;
        /// </summary>
        public async Task<bool?> ShowDialogWithOwnerAsync<TViewModel>(
            TViewModel viewModel,
            Window owner)
            where TViewModel : class;
        {
            if (owner == null)
                throw new ArgumentNullException(nameof(owner));

            return await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                if (_dialogMappings.TryGetValue(typeof(TViewModel), out var windowType))
                {
                    var dialog = (Window)Activator.CreateInstance(windowType);
                    dialog.DataContext = viewModel;
                    dialog.Owner = owner;
                    dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;

                    RegisterDialog(dialog);

                    try
                    {
                        return dialog.ShowDialog();
                    }
                    finally
                    {
                        UnregisterDialog(dialog);
                    }
                }

                throw new InvalidOperationException(
                    $"No dialog window registered for view model type: {typeof(TViewModel).Name}");
            }, DispatcherPriority.Normal);
        }

        /// <summary>
        /// Shows a dialog with specific startup location;
        /// </summary>
        public async Task<bool?> ShowDialogWithLocationAsync<TViewModel>(
            TViewModel viewModel,
            WindowStartupLocation startupLocation)
            where TViewModel : class;
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_dialogMappings.TryGetValue(typeof(TViewModel), out var windowType))
                {
                    var dialog = (Window)Activator.CreateInstance(windowType);
                    dialog.DataContext = viewModel;
                    dialog.WindowStartupLocation = startupLocation;

                    ConfigureDialogWindow(dialog);

                    RegisterDialog(dialog);

                    try
                    {
                        return dialog.ShowDialog();
                    }
                    finally
                    {
                        UnregisterDialog(dialog);
                    }
                }

                throw new InvalidOperationException(
                    $"No dialog window registered for view model type: {typeof(TViewModel).Name}");
            }, DispatcherPriority.Normal);
        }

        /// <summary>
        /// Registers a mapping between a view model and a dialog window;
        /// </summary>
        public void RegisterDialog<TViewModel, TWindow>()
            where TViewModel : class;
            where TWindow : Window;
        {
            var viewModelType = typeof(TViewModel);
            var windowType = typeof(TWindow);

            lock (_lockObject)
            {
                if (_dialogMappings.ContainsKey(viewModelType))
                {
                    _dialogMappings[viewModelType] = windowType;
                }
                else;
                {
                    _dialogMappings.Add(viewModelType, windowType);
                }
            }
        }

        /// <summary>
        /// Closes all open dialogs;
        /// </summary>
        public void CloseAllDialogs()
        {
            lock (_lockObject)
            {
                foreach (var dialog in _openDialogs.ToList())
                {
                    try
                    {
                        if (dialog.IsVisible)
                        {
                            dialog.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log error but continue closing other dialogs;
                        Console.WriteLine($"Error closing dialog: {ex.Message}");
                    }
                }

                _openDialogs.Clear();
            }
        }

        /// <summary>
        /// Gets the count of currently open dialogs;
        /// </summary>
        public int GetOpenDialogCount()
        {
            lock (_lockObject)
            {
                return _openDialogs.Count(d => d.IsVisible);
            }
        }

        /// <summary>
        /// Shows a dialog with custom size;
        /// </summary>
        public async Task<bool?> ShowDialogWithSizeAsync<TViewModel>(
            TViewModel viewModel,
            double width,
            double height,
            bool canResize = false)
            where TViewModel : class;
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_dialogMappings.TryGetValue(typeof(TViewModel), out var windowType))
                {
                    var dialog = (Window)Activator.CreateInstance(windowType);
                    dialog.DataContext = viewModel;

                    dialog.Width = width;
                    dialog.Height = height;
                    dialog.ResizeMode = canResize ? ResizeMode.CanResize : ResizeMode.NoResize;

                    ConfigureDialogWindow(dialog);

                    RegisterDialog(dialog);

                    try
                    {
                        return dialog.ShowDialog();
                    }
                    finally
                    {
                        UnregisterDialog(dialog);
                    }
                }

                throw new InvalidOperationException(
                    $"No dialog window registered for view model type: {typeof(TViewModel).Name}");
            }, DispatcherPriority.Normal);
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Internal method to show dialog;
        /// </summary>
        private async Task<bool?> ShowDialogInternalAsync<TViewModel>(TViewModel viewModel)
            where TViewModel : class;
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_dialogMappings.TryGetValue(typeof(TViewModel), out var windowType))
                {
                    var dialog = (Window)Activator.CreateInstance(windowType);
                    dialog.DataContext = viewModel;

                    ConfigureDialogWindow(dialog);

                    RegisterDialog(dialog);

                    try
                    {
                        return dialog.ShowDialog();
                    }
                    finally
                    {
                        UnregisterDialog(dialog);
                    }
                }

                throw new InvalidOperationException(
                    $"No dialog window registered for view model type: {typeof(TViewModel).Name}");
            }, DispatcherPriority.Normal);
        }

        /// <summary>
        /// Configures common dialog window properties;
        /// </summary>
        private void ConfigureDialogWindow(Window dialog)
        {
            if (dialog == null) return;

            // Set default properties;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = false;

            // Apply dialog service if available;
            if (dialog.DataContext is IDialogAware dialogAware)
            {
                dialogAware.RequestClose += result =>
                {
                    dialog.DialogResult = result;
                    dialog.Close();
                };
            }
        }

        /// <summary>
        /// Registers a dialog in the open dialogs list;
        /// </summary>
        private void RegisterDialog(Window dialog)
        {
            lock (_lockObject)
            {
                if (!_openDialogs.Contains(dialog))
                {
                    _openDialogs.Add(dialog);
                    dialog.Closed += Dialog_Closed;
                }
            }
        }

        /// <summary>
        /// Unregisters a dialog from the open dialogs list;
        /// </summary>
        private void UnregisterDialog(Window dialog)
        {
            lock (_lockObject)
            {
                if (_openDialogs.Contains(dialog))
                {
                    dialog.Closed -= Dialog_Closed;
                    _openDialogs.Remove(dialog);
                }
            }
        }

        /// <summary>
        /// Handles dialog closed event;
        /// </summary>
        private void Dialog_Closed(object sender, EventArgs e)
        {
            if (sender is Window dialog)
            {
                UnregisterDialog(dialog);
            }
        }

        /// <summary>
        /// Registers default dialogs;
        /// </summary>
        private void RegisterDefaultDialogs()
        {
            // Register message dialog;
            RegisterDialog<MessageDialogViewModel, MessageDialogWindow>();

            // You can register other default dialogs here;
            // RegisterDialog<ConfirmationDialogViewModel, ConfirmationDialogWindow>();
            // RegisterDialog<ProgressDialogViewModel, ProgressDialogWindow>();
        }

        #endregion;

        #region Static Methods;

        /// <summary>
        /// Shows a simple message box using the dialog manager;
        /// </summary>
        public static async Task<MessageDialogResult> ShowSimpleMessageAsync(
            string title,
            string message,
            MessageDialogButtons buttons = MessageDialogButtons.OK,
            MessageDialogType type = MessageDialogType.Information)
        {
            return await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                var serviceProvider = CreateDefaultServiceProvider();
                var dialogManager = serviceProvider.GetRequiredService<IDialogManager>();

                return await dialogManager.ShowMessageAsync(title, message, buttons, type);
            }, DispatcherPriority.Normal);
        }

        /// <summary>
        /// Creates a default service provider for the dialog manager;
        /// </summary>
        private static IServiceProvider CreateDefaultServiceProvider()
        {
            var services = new ServiceCollection();

            services.AddSingleton<IDialogManager, DialogManager>();

            // Register dialog windows;
            services.AddTransient<MessageDialogWindow>();

            return services.BuildServiceProvider();
        }

        #endregion;
    }

    #region Dialog Interfaces;

    /// <summary>
    /// Interface for view models that are aware of dialog closing;
    /// </summary>
    public interface IDialogAware;
    {
        /// <summary>
        /// Event triggered when dialog requests to close;
        /// </summary>
        event Action<bool?> RequestClose;

        /// <summary>
        /// Called when dialog is closing;
        /// </summary>
        void OnDialogClosing();

        /// <summary>
        /// Called when dialog is closed;
        /// </summary>
        void OnDialogClosed();
    }

    /// <summary>
    /// Base class for dialog view models;
    /// </summary>
    public abstract class DialogViewModelBase : IDialogAware;
    {
        public event Action<bool?> RequestClose;

        /// <summary>
        /// Title of the dialog;
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Result of the dialog;
        /// </summary>
        public bool? DialogResult { get; protected set; }

        /// <summary>
        /// Closes the dialog with the specified result;
        /// </summary>
        public virtual void CloseDialog(bool? dialogResult = null)
        {
            DialogResult = dialogResult;
            RequestClose?.Invoke(dialogResult);
        }

        /// <summary>
        /// Called when dialog is closing;
        /// </summary>
        public virtual void OnDialogClosing()
        {
            // Can be overridden in derived classes;
        }

        /// <summary>
        /// Called when dialog is closed;
        /// </summary>
        public virtual void OnDialogClosed()
        {
            // Can be overridden in derived classes;
        }
    }

    #endregion;

    #region Message Dialog Implementation;

    /// <summary>
    /// Message dialog view model;
    /// </summary>
    public class MessageDialogViewModel : DialogViewModelBase;
    {
        private string _message;
        private MessageDialogType _dialogType;
        private MessageDialogButtons _buttons;
        private MessageDialogResult _result;

        public string Message;
        {
            get => _message;
            set => _message = value;
        }

        public MessageDialogType DialogType;
        {
            get => _dialogType;
            set => _dialogType = value;
        }

        public MessageDialogButtons Buttons;
        {
            get => _buttons;
            set => _buttons = value;
        }

        public MessageDialogResult Result;
        {
            get => _result;
            set => _result = value;
        }

        /// <summary>
        /// Executes a button command;
        /// </summary>
        public void ExecuteButton(MessageDialogResult buttonResult)
        {
            Result = buttonResult;
            CloseDialog(buttonResult == MessageDialogResult.OK ||
                       buttonResult == MessageDialogResult.Yes);
        }
    }

    /// <summary>
    /// Message dialog result;
    /// </summary>
    public enum MessageDialogResult;
    {
        None = 0,
        OK = 1,
        Cancel = 2,
        Yes = 3,
        No = 4,
        Retry = 5,
        Ignore = 6;
    }

    /// <summary>
    /// Message dialog buttons;
    /// </summary>
    public enum MessageDialogButtons;
    {
        OK = 0,
        OKCancel = 1,
        YesNo = 2,
        YesNoCancel = 3,
        RetryCancel = 4;
    }

    /// <summary>
    /// Message dialog type;
    /// </summary>
    public enum MessageDialogType;
    {
        Information = 0,
        Warning = 1,
        Error = 2,
        Question = 3,
        Success = 4;
    }

    #endregion;

    #region Dialog Windows;

    /// <summary>
    /// Message dialog window (XAML would be defined separately)
    /// </summary>
    public partial class MessageDialogWindow : Window;
    {
        public MessageDialogWindow()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            // This would be auto-generated from XAML;
            // For code-only example, we create minimal window;
            Width = 400;
            Height = 250;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;

            var grid = new Grid();
            Content = grid;
        }
    }

    /// <summary>
    /// Base dialog window class;
    /// </summary>
    public class BaseDialogWindow : Window;
    {
        public BaseDialogWindow()
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
        }

        /// <summary>
        /// Sets dialog result and closes the window;
        /// </summary>
        public void CloseWithResult(bool? result)
        {
            DialogResult = result;
            Close();
        }
    }

    #endregion;

    #region Extension Methods;

    /// <summary>
    /// Extension methods for dialog manager;
    /// </summary>
    public static class DialogManagerExtensions;
    {
        /// <summary>
        /// Shows an information dialog;
        /// </summary>
        public static async Task ShowInformationAsync(
            this IDialogManager dialogManager,
            string title,
            string message)
        {
            await dialogManager.ShowMessageAsync(
                title,
                message,
                MessageDialogButtons.OK,
                MessageDialogType.Information);
        }

        /// <summary>
        /// Shows a warning dialog;
        /// </summary>
        public static async Task ShowWarningAsync(
            this IDialogManager dialogManager,
            string title,
            string message)
        {
            await dialogManager.ShowMessageAsync(
                title,
                message,
                MessageDialogButtons.OK,
                MessageDialogType.Warning);
        }

        /// <summary>
        /// Shows an error dialog;
        /// </summary>
        public static async Task ShowErrorAsync(
            this IDialogManager dialogManager,
            string title,
            string message)
        {
            await dialogManager.ShowMessageAsync(
                title,
                message,
                MessageDialogButtons.OK,
                MessageDialogType.Error);
        }

        /// <summary>
        /// Shows a confirmation dialog;
        /// </summary>
        public static async Task<bool> ShowConfirmationAsync(
            this IDialogManager dialogManager,
            string title,
            string message)
        {
            var result = await dialogManager.ShowMessageAsync(
                title,
                message,
                MessageDialogButtons.YesNo,
                MessageDialogType.Question);

            return result == MessageDialogResult.Yes;
        }

        /// <summary>
        /// Shows a dialog and waits for result;
        /// </summary>
        public static async Task<TResult> ShowDialogWithResultAsync<TViewModel, TResult>(
            this IDialogManager dialogManager,
            TViewModel viewModel,
            Func<TViewModel, TResult> resultSelector)
            where TViewModel : class;
        {
            var dialogResult = await dialogManager.ShowDialogAsync(viewModel);

            if (dialogResult == true)
            {
                return resultSelector(viewModel);
            }

            return default;
        }
    }

    #endregion;

    #region Dialog Service Registration;

    /// <summary>
    /// Service collection extensions for dialog manager;
    /// </summary>
    public static class DialogServiceCollectionExtensions;
    {
        /// <summary>
        /// Adds dialog manager services to the service collection;
        /// </summary>
        public static IServiceCollection AddDialogManager(this IServiceCollection services)
        {
            services.AddSingleton<IDialogManager, DialogManager>();

            // Register common dialog view models;
            services.AddTransient<MessageDialogViewModel>();

            return services;
        }

        /// <summary>
        /// Registers a dialog mapping;
        /// </summary>
        public static IServiceCollection RegisterDialog<TViewModel, TWindow>(
            this IServiceCollection services)
            where TViewModel : class;
            where TWindow : Window;
        {
            services.AddTransient<TWindow>();

            // This would typically be done in the application startup;
            // by getting the DialogManager and calling RegisterDialog;

            return services;
        }
    }

    #endregion;

    #region Usage Examples;

    /// <summary>
    /// Example usage of DialogManager;
    /// </summary>
    public static class DialogManagerExamples;
    {
        /// <summary>
        /// Example view model for a custom dialog;
        /// </summary>
        public class UserInputDialogViewModel : DialogViewModelBase;
        {
            private string _userInput;

            public string UserInput;
            {
                get => _userInput;
                set => _userInput = value;
            }

            public void Save()
            {
                if (string.IsNullOrWhiteSpace(UserInput))
                {
                    // Show validation error;
                    return;
                }

                CloseDialog(true);
            }

            public void Cancel()
            {
                CloseDialog(false);
            }
        }

        /// <summary>
        /// Example of using the dialog manager;
        /// </summary>
        public static async Task ExampleUsage()
        {
            // Create service provider (typically done in App.xaml.cs)
            var services = new ServiceCollection();
            services.AddDialogManager();
            var serviceProvider = services.BuildServiceProvider();

            // Get dialog manager;
            var dialogManager = serviceProvider.GetRequiredService<IDialogManager>();

            // Example 1: Show simple message;
            await dialogManager.ShowInformationAsync(
                "Information",
                "Operation completed successfully!");

            // Example 2: Show confirmation dialog;
            var shouldProceed = await dialogManager.ShowConfirmationAsync(
                "Confirmation",
                "Are you sure you want to delete this item?");

            if (shouldProceed)
            {
                // Perform delete operation;
            }

            // Example 3: Show error dialog;
            try
            {
                // Some operation that might throw;
            }
            catch (Exception ex)
            {
                await dialogManager.ShowErrorAsync(
                    "Error",
                    $"An error occurred: {ex.Message}");
            }

            // Example 4: Show custom dialog;
            var userInputViewModel = new UserInputDialogViewModel;
            {
                Title = "Enter your name",
                UserInput = ""
            };

            var result = await dialogManager.ShowDialogAsync(userInputViewModel);

            if (result == true)
            {
                var userName = userInputViewModel.UserInput;
                // Use the user input;
            }

            // Example 5: Using extension method for result;
            var userInputResult = await dialogManager.ShowDialogWithResultAsync(
                userInputViewModel,
                vm => vm.UserInput);

            if (!string.IsNullOrEmpty(userInputResult))
            {
                // Process result;
            }
        }
    }

    #endregion;
}
