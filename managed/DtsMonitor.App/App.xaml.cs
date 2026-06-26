using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace DtsMonitor.App;

public partial class App : Application
{
    private const string SdkDumpPattern = "DumpDemo_v1.0-*.dmp";
    private FileSystemWatcher? _sdkDumpWatcher;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartSdkDumpCleanup();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _sdkDumpWatcher?.Dispose();
        _sdkDumpWatcher = null;
        base.OnExit(e);
    }

    private void StartSdkDumpCleanup()
    {
        string appDirectory = AppContext.BaseDirectory;
        DeleteSdkDumpFiles(appDirectory);

        if (!Directory.Exists(appDirectory))
        {
            return;
        }

        _sdkDumpWatcher = new FileSystemWatcher(appDirectory, SdkDumpPattern)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        _sdkDumpWatcher.Created += OnSdkDumpCreated;
        _sdkDumpWatcher.Renamed += OnSdkDumpRenamed;
    }

    private void OnSdkDumpCreated(object sender, FileSystemEventArgs e)
    {
        ScheduleDumpDeletion(e.FullPath);
    }

    private void OnSdkDumpRenamed(object sender, RenamedEventArgs e)
    {
        ScheduleDumpDeletion(e.FullPath);
    }

    private static void DeleteSdkDumpFiles(string directory)
    {
        foreach (string filePath in Directory.GetFiles(directory, SdkDumpPattern, SearchOption.TopDirectoryOnly))
        {
            TryDeleteFile(filePath);
        }
    }

    private static void ScheduleDumpDeletion(string filePath)
    {
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (TryDeleteFile(filePath))
                {
                    return;
                }

                await Task.Delay(300).ConfigureAwait(false);
            }
        });
    }

    private static bool TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
