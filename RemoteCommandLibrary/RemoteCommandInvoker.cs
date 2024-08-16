using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RemoteCommandLibrary
{
    public class RemoteCommandInvoker
    {
        public class RemoteCommandResult
        {
            public string ComputerName { get; set; }
            public int ExitCode { get; set; }
            public string StandardOutput { get; set; }
            public string StandardError { get; set; }
        }

        private readonly string logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        private readonly List<string> _logBuffer = new List<string>();
        private const int LogBufferSize = 100;

        public RemoteCommandInvoker()
        {
            Directory.CreateDirectory(logDirectory); // Ensure the Logs directory exists
        }

        private void Log(string message)
        {
            string userName = WindowsIdentity.GetCurrent().Name.Replace("\\", "_");
            string timestamp = DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");
            string logFileName = $"{userName}_{timestamp.Replace(":", "-")}.log";

            string logFilePath = Path.Combine(logDirectory, logFileName);
            _logBuffer.Add($"{timestamp} - {message}{Environment.NewLine}");

            if (_logBuffer.Count >= LogBufferSize)
            {
                FlushLogBuffer(logFilePath);
            }
        }

        private void FlushLogBuffer(string logFilePath)
        {
            File.AppendAllLines(logFilePath, _logBuffer);
            _logBuffer.Clear();
        }

        private async Task<bool> IsRemoteComputerOnlineAsync(string computerName)
        {
            try
            {
                using (var ping = new Ping())
                {
                    PingReply reply = await Task.Run(() => ping.Send(computerName)).ConfigureAwait(false);  // Adjust for .NET 4.6.1
                    bool isOnline = reply.Status == IPStatus.Success;
                    Log($"Remote computer {computerName} is {(isOnline ? "online" : "offline")}.");
                    return isOnline;
                }
            }
            catch (Exception ex)
            {
                Log($"Error checking online status of {computerName}: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> IsUserInRoleAsync(string computerName, string roleName = "Administrator")
        {
            try
            {
                using (var runspace = CreateAndOpenRunspace())
                {
                    using (var pipeline = runspace.CreatePipeline())
                    {
                        string roleCheckScript = @"
                            $user = [System.Security.Principal.WindowsIdentity]::GetCurrent()
                            $principal = New-Object System.Security.Principal.WindowsPrincipal($user)
                            $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
                        ";
                        pipeline.Commands.AddScript(roleCheckScript);
                        var roleCheckResult = await Task.Run(() => pipeline.Invoke()).ConfigureAwait(false);
                        bool isInRole = roleCheckResult.Count > 0 && (bool)roleCheckResult[0].BaseObject;

                        Log($"User is {(isInRole ? "" : "not ")}in the {roleName} role on {computerName}.");
                        return isInRole;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error checking user role on {computerName}: {ex.Message}");
                return false;
            }
        }

        private Runspace CreateAndOpenRunspace()
        {
            var runspace = RunspaceFactory.CreateRunspace();
            runspace.Open();
            return runspace;
        }

        private void CloseRunspace(Runspace runspace)
        {
            if (runspace != null && runspace.RunspaceStateInfo.State == RunspaceState.Opened)
            {
                runspace.Close();
            }
        }

        private RemoteCommandResult HandleCommandError(string computerName, Exception ex, string customMessage = "")
        {
            var errorMessage = string.IsNullOrEmpty(customMessage) ? ex.Message : $"{customMessage}: {ex.Message}";
            Log($"Error on {computerName}: {errorMessage}");

            return new RemoteCommandResult
            {
                ComputerName = computerName,
                ExitCode = -1,
                StandardOutput = "",
                StandardError = errorMessage
            };
        }

        private async Task<RemoteCommandResult> ExecutePowerShellScriptWithRunspaceAsync(Runspace runspace, string computerName, string scriptBlock, object[] arguments, CancellationToken cancellationToken = default)
        {
            try
            {
                using (var pipeline = runspace.CreatePipeline())
                {
                    var command = new Command("Invoke-Command");
                    command.Parameters.Add("ComputerName", computerName);
                    command.Parameters.Add("ScriptBlock", ScriptBlock.Create(scriptBlock));
                    command.Parameters.Add("ArgumentList", arguments);

                    pipeline.Commands.Add(command);

                    Collection<PSObject> results = await Task.Run(() => pipeline.Invoke(), cancellationToken).ConfigureAwait(false);

                    if (results.Count > 0)
                    {
                        string jsonResult = results[0].BaseObject.ToString();
                        var result = JsonConvert.DeserializeObject<RemoteCommandResult>(jsonResult);
                        result.ComputerName = computerName;
                        Log($"Command executed successfully on {computerName}. Exit Code: {result.ExitCode}");
                        return result;
                    }
                    else
                    {
                        Log($"No result returned from the remote command on {computerName}.");
                        return new RemoteCommandResult
                        {
                            ComputerName = computerName,
                            ExitCode = -1,
                            StandardOutput = "",
                            StandardError = "No result returned from the remote command."
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                return HandleCommandError(computerName, ex, "General error");
            }
        }

        private async Task<RemoteCommandResult> ExecutePowerShellScriptAsync(string computerName, string scriptBlock, object[] arguments, CancellationToken cancellationToken = default)
        {
            if (!await IsRemoteComputerOnlineAsync(computerName).ConfigureAwait(false))
            {
                return new RemoteCommandResult
                {
                    ComputerName = computerName,
                    ExitCode = -1,
                    StandardOutput = "",
                    StandardError = $"Remote computer {computerName} is offline."
                };
            }

            if (!await IsUserInRoleAsync(computerName).ConfigureAwait(false))
            {
                Log($"Execution denied. User is not an administrator on {computerName}.");
                return new RemoteCommandResult
                {
                    ComputerName = computerName,
                    ExitCode = -1,
                    StandardOutput = "",
                    StandardError = "User is not an administrator on the remote machine."
                };
            }

            using (var runspace = CreateAndOpenRunspace())
            {
                return await ExecutePowerShellScriptWithRunspaceAsync(runspace, computerName, scriptBlock, arguments, cancellationToken).ConfigureAwait(false);
            }
        }

        // Invocation Methods

        public async Task<RemoteCommandResult> InvokeMsiRemoteCommandAsync(string computerName, string msiPath, string[] msiArguments, CancellationToken cancellationToken = default)
        {
            string argumentString = $"/i \"{msiPath}\" {string.Join(" ", msiArguments)}";
            string scriptBlock = GetProcessScriptBlock();
            return await ExecutePowerShellScriptAsync(computerName, scriptBlock, new object[] { "msiexec.exe", argumentString }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<RemoteCommandResult> InvokeExeRemoteCommandAsync(string computerName, string exePath, string[] exeArguments, CancellationToken cancellationToken = default)
        {
            string argumentString = string.Join(" ", exeArguments);
            string scriptBlock = GetProcessScriptBlock();
            return await ExecutePowerShellScriptAsync(computerName, scriptBlock, new object[] { exePath, argumentString }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<RemoteCommandResult> InvokeMsuRemoteCommandAsync(string computerName, string msuPath, CancellationToken cancellationToken = default)
        {
            string argumentString = $"/quiet /norestart \"{msuPath}\"";
            string scriptBlock = GetProcessScriptBlock();
            return await ExecutePowerShellScriptAsync(computerName, scriptBlock, new object[] { "wusa.exe", argumentString }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<RemoteCommandResult> InvokeMspRemoteCommandAsync(string computerName, string mspPath, CancellationToken cancellationToken = default)
        {
            string argumentString = $"/p \"{mspPath}\" /quiet /norestart";
            string scriptBlock = GetProcessScriptBlock();
            return await ExecutePowerShellScriptAsync(computerName, scriptBlock, new object[] { "msiexec.exe", argumentString }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<RemoteCommandResult> InvokeBatchRemoteCommandAsync(string computerName, string batchPath, string[] batchArguments, CancellationToken cancellationToken = default)
        {
            string argumentString = string.Join(" ", batchArguments);
            string scriptBlock = GetProcessScriptBlock();
            return await ExecutePowerShellScriptAsync(computerName, scriptBlock, new object[] { batchPath, argumentString }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<RemoteCommandResult> InvokeRegRemoteCommandAsync(string computerName, string regFilePath, CancellationToken cancellationToken = default)
        {
            string argumentString = $"/s \"{regFilePath}\"";
            string scriptBlock = GetProcessScriptBlock();
            return await ExecutePowerShellScriptAsync(computerName, scriptBlock, new object[] { "regedit.exe", argumentString }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<RemoteCommandResult> InvokeRegForAllUsersRemoteCommandAsync(string computerName, string regFilePath, CancellationToken cancellationToken = default)
        {
            string scriptBlock = $@"
                param ([string]$RegFilePath)
                try {{
                    $userProfiles = Get-ChildItem 'HKU:\' | Where-Object {{ $_.Name -notmatch '_Classes$' -and $_.Name -ne '.DEFAULT' }}
                    foreach ($userProfile in $userProfiles) {{
                        $userHive = $userProfile.PSChildName
                        reg.exe load HKU\$userHive ""$($env:SystemRoot)\System32\config\software""
                        reg.exe import $RegFilePath /reg:64
                        reg.exe unload HKU\$userHive
                    }}
                    $result = [PSCustomObject]@{{
                        ExitCode = 0
                        StandardOutput = 'Successfully imported the registry file for all user profiles.'
                        StandardError = ''
                    }}
                    $result | ConvertTo-Json
                }} catch {{
                    $result = [PSCustomObject]@{{
                        ExitCode = -1
                        StandardOutput = ''
                        StandardError = $_.Exception.Message
                    }}
                    $result | ConvertTo-Json
                }}
            ";
            return await ExecutePowerShellScriptAsync(computerName, scriptBlock, new object[] { regFilePath }, cancellationToken).ConfigureAwait(false);
        }

        // File and Directory Operations

        public async Task<RemoteCommandResult> CopyFileToRemoteComputerAsync(string computerName, string sourceFilePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            if (!await IsRemoteComputerOnlineAsync(computerName).ConfigureAwait(false))
            {
                return new RemoteCommandResult
                {
                    ComputerName = computerName,
                    ExitCode = -1,
                    StandardOutput = "",
                    StandardError = $"Remote computer {computerName} is offline."
                };
            }

            try
            {
                string remotePath = $@"\\{computerName}\{destinationPath[0]}${destinationPath.Substring(2)}";
                await Task.Run(() => File.Copy(sourceFilePath, remotePath, true), cancellationToken).ConfigureAwait(false);
                Log($"File copied to {remotePath} on {computerName}.");
                return new RemoteCommandResult
                {
                    ComputerName = computerName,
                    ExitCode = 0,
                    StandardOutput = $"File copied successfully to {remotePath}.",
                    StandardError = ""
                };
            }
            catch (Exception ex)
            {
                Log($"Error copying file to {computerName}: {ex.Message}");
                return new RemoteCommandResult
                {
                    ComputerName = computerName,
                    ExitCode = -1,
                    StandardOutput = "",
                    StandardError = $"Error copying file: {ex.Message}"
                };
            }
        }

        public async Task<RemoteCommandResult> CopyFilesToRemoteComputerAsync(string computerName, List<string> sourceFilePaths, string destinationPath, CancellationToken cancellationToken = default)
        {
            if (!await IsRemoteComputerOnlineAsync(computerName).ConfigureAwait(false))
            {
                return new RemoteCommandResult
                {
                    ComputerName = computerName,
                    ExitCode = -1,
                    StandardOutput = "",
                    StandardError = $"Remote computer {computerName} is offline."
                };
            }

            try
            {
                foreach (var sourceFilePath in sourceFilePaths)
                {
                    string remotePath = $@"\\{computerName}\{destinationPath[0]}${destinationPath.Substring(2)}\{Path.GetFileName(sourceFilePath)}";
                    await Task.Run(() => File.Copy(sourceFilePath, remotePath, true), cancellationToken).ConfigureAwait(false);
                    Log($"File copied to {remotePath} on {computerName}.");
                }
                return new RemoteCommandResult
                {
                    ComputerName = computerName,
                    ExitCode = 0,
                    StandardOutput = "All files copied successfully.",
                    StandardError = ""
                };
            }
            catch (Exception ex)
            {
                Log($"Error copying files to {computerName}: {ex.Message}");
                return new RemoteCommandResult
                {
                    ComputerName = computerName,
                    ExitCode = -1,
                    StandardOutput = "",
                    StandardError = $"Error copying files: {ex.Message}"
                };
            }
        }

        public async Task<RemoteCommandResult> CopyDirectoryToRemoteComputerAsync(string computerName, string sourceDirectoryPath, string destinationPath, CancellationToken cancellationToken = default)
        {
            if (!await IsRemoteComputerOnlineAsync(computerName).ConfigureAwait(false))
            {
                return new RemoteCommandResult
                {
                    ComputerName = computerName,
                    ExitCode = -1,
                    StandardOutput = "",
                    StandardError = $"Remote computer {computerName} is offline."
                };
            }

            try
            {
                string remotePath = $@"\\{computerName}\{destinationPath[0]}${destinationPath.Substring(2)}";
                await Task.Run(() => CopyDirectory(sourceDirectoryPath, remotePath), cancellationToken).ConfigureAwait(false);
                Log($"Directory copied to {remotePath} on {computerName}.");
                return new RemoteCommandResult
                {
                    ComputerName = computerName,
                    ExitCode = 0,
                    StandardOutput = $"Directory copied successfully to {remotePath}.",
                    StandardError = ""
                };
            }
            catch (Exception ex)
            {
                Log($"Error copying directory to {computerName}: {ex.Message}");
                return new RemoteCommandResult
                {
                    ComputerName = computerName,
                    ExitCode = -1,
                    StandardOutput = "",
                    StandardError = $"Error copying directory: {ex.Message}"
                };
            }
        }

        private void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string targetFilePath = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, targetFilePath, true);
            }
            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                string targetSubDirPath = Path.Combine(destDir, Path.GetFileName(subDir));
                CopyDirectory(subDir, targetSubDirPath);
            }
        }

        public async Task<RemoteCommandResult> RemoveDirectoryFromRemoteComputerAsync(string computerName, string directoryPath, CancellationToken cancellationToken = default)
        {
            if (!await IsRemoteComputerOnlineAsync(computerName).ConfigureAwait(false))
            {
                return new RemoteCommandResult
                {
                    ComputerName = computerName,
                    ExitCode = -1,
                    StandardOutput = "",
                    StandardError = $"Remote computer {computerName} is offline."
                };
            }

            try
            {
                string remotePath = $@"\\{computerName}\{directoryPath[0]}${directoryPath.Substring(2)}";
                if (Directory.Exists(remotePath))
                {
                    await Task.Run(() => Directory.Delete(remotePath, true), cancellationToken).ConfigureAwait(false);
                    Log($"Directory {remotePath} deleted from {computerName}.");
                    return new RemoteCommandResult
                    {
                        ComputerName = computerName,
                        ExitCode = 0,
                        StandardOutput = $"Directory {remotePath} deleted successfully.",
                        StandardError = ""
                    };
                }
                else
                {
                    Log($"Directory {remotePath} not found on {computerName}.");
                    return new RemoteCommandResult
                    {
                        ComputerName = computerName,
                        ExitCode = -1,
                        StandardOutput = "",
                        StandardError = $"Directory {remotePath} not found."
                    };
                }
            }
            catch (Exception ex)
            {
                Log($"Error deleting directory from {computerName}: {ex.Message}");
                return new RemoteCommandResult
                {
                    ComputerName = computerName,
                    ExitCode = -1,
                    StandardOutput = "",
                    StandardError = $"Error deleting directory: {ex.Message}"
                };
            }
        }
        private string GetProcessScriptBlock()
        {
            return @"
        param (
            [string]$ScriptPath,
            [string]$Arguments
        )
        try {
            $startInfo = New-Object System.Diagnostics.ProcessStartInfo
            $startInfo.FileName = $ScriptPath
            $startInfo.Arguments = $Arguments
            $startInfo.RedirectStandardError = $true
            $startInfo.RedirectStandardOutput = $true
            $startInfo.UseShellExecute = $false
            $startInfo.CreateNoWindow = $true

            $process = New-Object System.Diagnostics.Process
            $process.StartInfo = $startInfo
            $process.Start() | Out-Null
            $process.WaitForExit()

            $output = $process.StandardOutput.ReadToEnd()
            $error = $process.StandardError.ReadToEnd()

            $result = [PSCustomObject]@{
                ExitCode = $process.ExitCode
                StandardOutput = $output
                StandardError = $error
            }
            $result | ConvertTo-Json
        } catch {
            $result = [PSCustomObject]@{
                ExitCode = -1
                StandardOutput = ''
                StandardError = $_.Exception.Message
            }
            $result | ConvertTo-Json
        }
    ";
        }

        public async Task<List<RemoteCommandResult>> ExecuteOnMultipleComputersAsync(IEnumerable<string> computerNames, string scriptBlock, object[] arguments, int maxDegreeOfParallelism = 4, CancellationToken cancellationToken = default)
        {
            var results = new ConcurrentBag<RemoteCommandResult>();
            var tasks = new List<Task>();

            using (var semaphore = new SemaphoreSlim(maxDegreeOfParallelism))
            {
                foreach (var computerName in computerNames)
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await ExecutePowerShellScriptAsync(computerName, scriptBlock, arguments, cancellationToken).ConfigureAwait(false);
                            results.Add(result);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, cancellationToken);
                    tasks.Add(task);
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            return results.ToList();
        }

        // Ensure to call FlushLogBuffer at the end of operations or in Dispose to make sure all logs are written
    }

    public static class TaskExtensions
    {
        public static async Task<T> TimeoutAfter<T>(this Task<T> task, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var delayTask = Task.Delay(timeout, timeoutCts.Token);
                if (await Task.WhenAny(task, delayTask).ConfigureAwait(false) == task)
                {
                    timeoutCts.Cancel();  // Cancel the timeout task
                    return await task.ConfigureAwait(false);     // Return the completed task result
                }
                else
                {
                    throw new TimeoutException("The operation has timed out.");
                }
            }
        }
    }
}
