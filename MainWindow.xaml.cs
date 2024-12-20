using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Forms;

namespace FolderSelectorApp
{
    public class DirectoryStats
    {
        public int TotalFiles { get; set; }
        public int TotalDirectories { get; set; }
        public long TotalSize { get; set; }
        public Dictionary<string, int> ExtensionStats { get; set; } = new Dictionary<string, int>();
        public DateTime ScanStartTime { get; set; }
        public TimeSpan ScanDuration { get; set; }
    }

    public class UniversalCommand : ICommand
    {
        private readonly Func<object, Task> _executeAsync;
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;
        private bool _isExecuting;

        public UniversalCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public UniversalCommand(Func<object, Task> executeAsync, Func<object, bool> canExecute = null)
        {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

        public async void Execute(object parameter)
        {
            if (!CanExecute(parameter)) return;
            _isExecuting = true;
            try
            {
                if (_executeAsync != null)
                {
                    await _executeAsync(parameter);
                }
                else
                {
                    _execute(parameter);
                }
            }
            finally
            {
                _isExecuting = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public static class Utilities
    {
        private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB" };
        private static readonly Dictionary<string, HashSet<string>> CategoryExtensions = new Dictionary<string, HashSet<string>>
        {
            { "IMG", new HashSet<string> { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".webp" } },
            { "DOC", new HashSet<string> { ".doc", ".docx", ".pdf", ".txt", ".rtf", ".xlsx", ".xls", ".pptx", ".ppt" } },
            { "ARC", new HashSet<string> { ".zip", ".rar", ".7z", ".tar", ".gz" } }
        };

        public static string FormatSize(long bytes) =>
            bytes <= 0 ? "0 B" :
            $"{bytes / Math.Pow(1024, Math.Min((int)Math.Log(bytes, 1024), SizeUnits.Length - 1)):F2} {SizeUnits[Math.Min((int)Math.Log(bytes, 1024), SizeUnits.Length - 1)]}";

        public static string GetFileCategory(string extension) =>
            CategoryExtensions.FirstOrDefault(c => c.Value.Contains(extension.ToLower())).Key ?? "FILE";

        public static string GetFileAttributes(FileInfo file)
        {
            var attributes = new List<string>();
            if (file.Attributes.HasFlag(FileAttributes.Hidden)) attributes.Add("скрытый");
            if (file.Attributes.HasFlag(FileAttributes.ReadOnly)) attributes.Add("только чтение");
            if (file.Attributes.HasFlag(FileAttributes.System)) attributes.Add("системный");
            if (file.CreationTime > DateTime.Now.AddHours(-24)) attributes.Add("новый");
            return attributes.Count > 0 ? $"[{string.Join(", ", attributes)}]" : string.Empty;
        }
    }

    public class MainViewModel : INotifyPropertyChanged
    {
        private string _directoryContent = string.Empty;
        private bool _isLoading;
        private double _progress;
        private string _statusMessage = "Готов к работе";
        private CancellationTokenSource _cts;
        private string _selectedPath;
        private DirectoryStats _currentStats;
        private StringBuilder _contentBuilder;

        public string DirectoryContent
        {
            get => _directoryContent;
            set => SetProperty(ref _directoryContent, value, nameof(DirectoryContent), nameof(CanSave));
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value, nameof(IsLoading), nameof(IsNotLoading), nameof(CanSave));
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public double Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, value);
        }

        public bool IsNotLoading => !IsLoading;
        public bool CanSave => !IsLoading && !string.IsNullOrEmpty(DirectoryContent);

        public ICommand SelectFolderCommand { get; }
        public ICommand SaveToFileCommand { get; }
        public ICommand CancelCommand { get; }

        public MainViewModel()
        {
            SelectFolderCommand = new UniversalCommand(SelectFolderAsync);
            SaveToFileCommand = new UniversalCommand(SaveToFileAsync, _ => CanSave);
            CancelCommand = new UniversalCommand(_ => { _cts?.Cancel(); return Task.CompletedTask; }, _ => IsLoading);
        }

        private void ScanDirectoryRecursive(string path, string indent, bool isLast, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var dirInfo = new DirectoryInfo(path);
                _contentBuilder.AppendLine($"{indent}{(isLast ? "└── " : "├── ")}{dirInfo.Name}/");
                _currentStats.TotalDirectories++;

                var entries = dirInfo.GetFileSystemInfos();
                var newIndent = indent + (isLast ? "    " : "│   ");

                for (int i = 0; i < entries.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = entries[i];
                    var isLastEntry = i == entries.Length - 1;

                    if (entry is FileInfo file)
                    {
                        ProcessFile(file, newIndent, isLastEntry);
                    }
                    else if (entry is DirectoryInfo subDir)
                    {
                        ScanDirectoryRecursive(subDir.FullName, newIndent, isLastEntry, ct);
                    }

                    UpdateProgress(i + 1, entries.Length);
                }
            }
            catch (UnauthorizedAccessException)
            {
                _contentBuilder.AppendLine($"{indent}{(isLast ? "└── " : "├── ")}[Доступ запрещен]");
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _contentBuilder.AppendLine($"{indent}{(isLast ? "└── " : "├── ")}[Ошибка: {ex.Message}]");
            }
        }

        private void ProcessFile(FileInfo file, string indent, bool isLast)
        {
            _currentStats.TotalFiles++;
            _currentStats.TotalSize += file.Length;

            var ext = file.Extension.ToLower();
            _currentStats.ExtensionStats[ext] = _currentStats.ExtensionStats.TryGetValue(ext, out var count) ? count + 1 : 1;

            var fileSize = Utilities.FormatSize(file.Length);
            var category = Utilities.GetFileCategory(ext);
            var attributes = Utilities.GetFileAttributes(file);
            var fileEntry = $"{file.Name} [{fileSize}] [{ext}] [{category}] {attributes}";

            _contentBuilder.AppendLine($"{indent}{(isLast ? "└── " : "├── ")}{fileEntry}");
        }

        private void AppendScanSummary()
        {
            var summary = new StringBuilder()
                .AppendLine($"=== Отчет о сканировании ===")
                .AppendLine($"Путь: {_selectedPath}")
                .AppendLine($"Время начала: {_currentStats.ScanStartTime:yyyy-MM-dd HH:mm:ss}")
                .AppendLine($"Длительность: {_currentStats.ScanDuration.TotalSeconds:F1} сек.")
                .AppendLine($"Всего файлов: {_currentStats.TotalFiles:N0}")
                .AppendLine($"Всего папок: {_currentStats.TotalDirectories:N0}")
                .AppendLine($"Общий размер: {Utilities.FormatSize(_currentStats.TotalSize)}")
                .AppendLine("Распределение по типам файлов:")
                .AppendLine(string.Join(Environment.NewLine, _currentStats.ExtensionStats
                    .OrderByDescending(x => x.Value)
                    .Select(x => $"{x.Key}: {x.Value:N0}")))
                .AppendLine("=== Структура директории ===");

            _contentBuilder.Insert(0, summary.ToString());
        }

        private async Task SelectFolderAsync(object _)
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "Выберите папку для просмотра файлов и папок",
                ShowNewFolderButton = false
            })
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    _selectedPath = dialog.SelectedPath;
                    await StartScanningAsync();
                }
            }
        }

        private async Task StartScanningAsync()
        {
            IsLoading = true;
            Progress = 0;
            _contentBuilder = new StringBuilder();
            _currentStats = new DirectoryStats
            {
                ScanStartTime = DateTime.Now,
                ExtensionStats = new Dictionary<string, int>()
            };

            StatusMessage = "Сканирование...";

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            try
            {
                await Task.Run(() => ScanDirectoryRecursive(_selectedPath, "", true, _cts.Token), _cts.Token);
                _currentStats.ScanDuration = DateTime.Now - _currentStats.ScanStartTime;
                AppendScanSummary();
                DirectoryContent = _contentBuilder.ToString();
                StatusMessage = "Сканирование завершено";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Операция отменена";
                DirectoryContent = "Сканирование было отменено пользователем.";
            }
            catch (Exception ex)
            {
                ShowMessage($"Ошибка при сканировании директории: {ex.Message}", "Ошибка", MessageBoxImage.Error);
                StatusMessage = "Произошла ошибка";
            }
            finally
            {
                IsLoading = false;
                Progress = 100;
            }
        }

        private async Task SaveToFileAsync(object _)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt",
                DefaultExt = "txt",
                AddExtension = true,
                FileName = $"DirectoryStructure_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    await Task.Run(() => File.WriteAllText(dialog.FileName, DirectoryContent, Encoding.UTF8));

                    ShowMessage("Файл успешно сохранен.", "Успех", MessageBoxImage.Information);
                    StatusMessage = "Файл сохранен";
                }
                catch (Exception ex)
                {
                    ShowMessage($"Ошибка при сохранении файла: {ex.Message}", "Ошибка", MessageBoxImage.Error);
                    StatusMessage = "Ошибка при сохранении";
                }
            }
        }

        private void UpdateProgress(int current, int total)
        {
            if (total == 0) return;
            var progress = (double)current / total * 100;
            App.Current.Dispatcher.InvokeAsync(() =>
            {
                Progress = Math.Min(progress, 99.9);
                StatusMessage = $"Прогресс: {Progress:F1}%";
            });
        }

        private void ShowMessage(string message, string caption, MessageBoxImage icon) =>
            App.Current.Dispatcher.InvokeAsync(() => System.Windows.MessageBox.Show(message, caption, MessageBoxButton.OK, icon));

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        protected bool SetProperty<T>(ref T field, T value, params string[] propertyNames)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            foreach (var name in propertyNames) OnPropertyChanged(name);
            return true;
        }
    }

    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }
    }
}