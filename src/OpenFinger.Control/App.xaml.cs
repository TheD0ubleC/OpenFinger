using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace OpenFinger.Control;

public partial class App : Application
{
	private static Mutex? _singleInstanceMutex;
	private const string SingleInstanceMutexName = @"Local\OpenFinger.Control.SingleInstance";

	protected override void OnStartup(StartupEventArgs e)
	{
		var startupLaunch = e.Args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
		if (!TryAcquireSingleInstance())
		{
			if (!startupLaunch)
			{
				MessageBox.Show(
					"OpenFinger 已经在运行。如果窗口被隐藏到托盘，请从托盘图标恢复。",
					"OpenFinger",
					MessageBoxButton.OK,
					MessageBoxImage.Information);
			}

			Shutdown(0);
			return;
		}

		AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
		DispatcherUnhandledException += App_DispatcherUnhandledException;
		TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
		base.OnStartup(e);

		var configStore = new OpenFingerConfigStore();
		var config = configStore.Load();
		var startHidden = startupLaunch && config.Ui.Tray.StartHiddenOnWindowsStartup;
		ThemeManager.ApplyThemeMode(config.Ui.ThemeMode);

		var window = new MainWindow();
		window.ConfigureStartupMode(startHidden);
		MainWindow = window;
		window.Show();
	}

	protected override void OnExit(ExitEventArgs e)
	{
		try
		{
			_singleInstanceMutex?.ReleaseMutex();
		}
		catch
		{
		}

		_singleInstanceMutex?.Dispose();
		_singleInstanceMutex = null;
		base.OnExit(e);
	}

	private void App_DispatcherUnhandledException(object? sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
	{
		LogException("DispatcherUnhandledException", e.Exception);
		// don't mark handled so the process exits with the original failure
	}

	private void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
	{
		LogException("CurrentDomain_UnhandledException", e.ExceptionObject as Exception);
	}

	private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
	{
		LogException("TaskScheduler_UnobservedTaskException", e.Exception);
		e.SetObserved();
	}

	private static void LogException(string where, Exception? ex)
	{
		try
		{
			var path = Path.Combine(AppContext.BaseDirectory ?? ".", "openfinger_run_error.log");
			var text = $"{DateTime.Now:o} {where}: {ex}\n";
			File.AppendAllText(path, text);
		}
		catch
		{
		}
	}

	private static bool TryAcquireSingleInstance()
	{
		try
		{
			_singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
			if (createdNew)
			{
				return true;
			}

			_singleInstanceMutex.Dispose();
			_singleInstanceMutex = null;
			return false;
		}
		catch
		{
			return true;
		}
	}
}
