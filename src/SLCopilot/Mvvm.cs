using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace SLCopilot;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        RaisePropertyChanged(name);
        return true;
    }

    protected void RaisePropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>同步命令；只应在 UI 线程执行。异常写入 ActivityLog，不抛给 WPF（进程内未处理异常会影响 Visual Studio）。</summary>
public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public RelayCommand(Action execute) : this(_ => execute()) { }

    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        try { execute(parameter); }
        catch (Exception ex) { ActivityLog.LogError("SLCopilot", $"Command failed: {ex}"); }
    }
}

/// <summary>
/// 异步命令：在 JoinableTaskFactory 上启动，异常写入 ActivityLog 而不抛给 WPF；并发互斥由视图模型自行负责。
/// </summary>
public sealed class AsyncCommand(JoinableTaskFactory joinableTaskFactory, string name, Func<object?, Task> execute) : ICommand
{
    public AsyncCommand(JoinableTaskFactory joinableTaskFactory, string name, Func<Task> execute)
        : this(joinableTaskFactory, name, _ => execute()) { }

    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        joinableTaskFactory.RunAsync(async () =>
        {
            try { await execute(parameter); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ActivityLog.LogError("SLCopilot", $"Command {name} failed: {ex}"); }
        }).FileAndForget($"SLCopilot/command/{name}");
    }
}
