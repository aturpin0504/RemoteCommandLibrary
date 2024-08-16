using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace RemoteCommandLibrary
{
    public class SessionInfo
    {
        public string ComputerName { get; set; }
        public string SessionName { get; set; }
        public string UserName { get; set; }
        public string SessionId { get; set; }
        public string State { get; set; }
        public string IdleTime { get; set; }
        public string LogonTime { get; set; }

        public override string ToString()
        {
            return $"ComputerName: {ComputerName}, SessionName: {SessionName}, UserName: {UserName}, SessionId: {SessionId}, State: {State}, IdleTime: {IdleTime}, LogonTime: {LogonTime}";
        }
    }

    public class RemoteSessionChecker
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly int _maxDegreeOfParallelism;

        public RemoteSessionChecker(int maxDegreeOfParallelism = 10)
        {
            _maxDegreeOfParallelism = maxDegreeOfParallelism;
            _semaphore = new SemaphoreSlim(_maxDegreeOfParallelism);
        }

        public async Task RunQuserOnMultipleComputersAsync(
            List<string> computerNames,
            string username,
            ProgressBar progressBar,
            TextBlock progressTextBlock,
            DataGrid dataGrid,
            ObservableCollection<SessionInfo> sessionCollection)
        {
            int totalComputers = computerNames.Count;
            int completedComputers = 0;

            var tasks = new List<Task>();

            foreach (var computerName in computerNames)
            {
                await _semaphore.WaitAsync();

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        string quserOutput = await RunQuserAsync(computerName);

                        List<SessionInfo> sessions = ParseQuserOutput(quserOutput, computerName, username);

                        // Update UI (Progress and DataGrid) on the main thread
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            foreach (var session in sessions)
                            {
                                sessionCollection.Add(session);
                            }

                            completedComputers++;
                            progressTextBlock.Text = $"Processed: {completedComputers}/{totalComputers} computers";
                            progressBar.Value = (double)completedComputers / totalComputers * 100;
                        });
                    }
                    finally
                    {
                        _semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }

        public async Task<string> RunQuserAsync(string computerName, int timeoutMinutes = 0, CancellationToken cancellationToken = default)
        {
            string quserPath = @"C:\Windows\System32\quser.exe";
            var arguments = $"/server:{computerName}";

            using (var process = new Process())
            {
                process.StartInfo.FileName = quserPath;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;

                var tcs = new TaskCompletionSource<bool>();

                process.EnableRaisingEvents = true;
                process.Exited += (sender, args) => tcs.TrySetResult(true);

                using (cancellationToken.Register(() =>
                {
                    tcs.TrySetCanceled();
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }))
                {
                    process.Start();

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();

                    if (timeoutMinutes > 0)
                    {
                        var timeoutTask = Task.Delay(TimeSpan.FromMinutes(timeoutMinutes), cancellationToken);
                        if (await Task.WhenAny(tcs.Task, timeoutTask) == timeoutTask)
                        {
                            if (!process.HasExited)
                            {
                                process.Kill();
                            }
                            throw new TimeoutException("The operation has timed out.");
                        }
                    }
                    else
                    {
                        await tcs.Task;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    var output = await outputTask;
                    var error = await errorTask;

                    if (!string.IsNullOrEmpty(error))
                    {
                        throw new Exception($"Error occurred: {error}");
                    }

                    return output;
                }
            }
        }

        public List<SessionInfo> ParseQuserOutput(string quserOutput, string computerName, string username = null)
        {
            var sessions = new List<SessionInfo>();
            var lines = quserOutput.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

            // Skip the header line
            foreach (var line in lines.Skip(1))
            {
                var columns = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (columns.Length < 5)
                {
                    // Log or handle the case where a line does not have enough columns
                    Console.WriteLine($"Skipping line due to insufficient columns: {line}");
                    continue;
                }

                var sessionInfo = new SessionInfo
                {
                    ComputerName = computerName,
                    SessionName = columns.Length > 0 ? columns[0] : string.Empty,
                    UserName = columns.Length > 1 ? columns[1] : string.Empty,
                    SessionId = columns.Length > 2 ? columns[2] : string.Empty,
                    State = columns.Length > 3 ? columns[3] : string.Empty,
                    IdleTime = columns.Length > 4 ? columns[4] : string.Empty,
                    LogonTime = columns.Length > 5 ? string.Join(" ", columns.Skip(5)) : string.Empty
                };

                if (string.IsNullOrEmpty(username) || sessionInfo.UserName.Equals(username, StringComparison.OrdinalIgnoreCase))
                {
                    sessions.Add(sessionInfo);
                }
            }

            return sessions;
        }
    }
}