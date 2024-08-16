# RemoteCommandLibrary

## Overview

`RemoteCommandLibrary` is a .NET 4.6.1 class library designed to facilitate remote execution of commands, file operations, and registry modifications on remote computers in a Windows environment. It uses PowerShell and asynchronous programming to ensure that operations are executed efficiently and without blocking the main application thread, making it suitable for use in applications like WPF that require a responsive UI.

## Features

- Execute remote commands, including MSI, EXE, MSU, MSP, batch files, and registry files.
- Copy files and directories to remote computers.
- Delete directories from remote computers.
- Check if a remote computer is online and whether the current user has administrative privileges on the remote machine.
- Provides detailed logs for every operation, which are saved in a `Logs` directory.

## Installation

To use this library, simply add a reference to the `RemoteCommandLibrary` project in your .NET 4.6.1 application.

## Usage

### Initialization

First, create an instance of the `RemoteCommandInvoker` class:

```csharp
var invoker = new RemoteCommandInvoker();
```

### Examples of Function Usage

#### 1. **Invoke an MSI Installer on a Remote Computer**

This function installs an MSI package on a remote computer.

```csharp
var result = await invoker.InvokeMsiRemoteCommandAsync("RemotePC01", @"C:\path\to\installer.msi", new string[] { "/quiet" });
Console.WriteLine(result.StandardOutput);
```

#### 2. **Invoke an EXE on a Remote Computer**

This function runs an EXE file on a remote computer.

```csharp
var result = await invoker.InvokeExeRemoteCommandAsync("RemotePC01", @"C:\path\to\program.exe", new string[] { "/arg1", "/arg2" });
Console.WriteLine(result.StandardOutput);
```

#### 3. **Install an MSU Update on a Remote Computer**

This function installs an MSU update package on a remote computer.

```csharp
var result = await invoker.InvokeMsuRemoteCommandAsync("RemotePC01", @"C:\path\to\update.msu");
Console.WriteLine(result.StandardOutput);
```

#### 4. **Apply an MSP Patch on a Remote Computer**

This function applies an MSP patch on a remote computer.

```csharp
var result = await invoker.InvokeMspRemoteCommandAsync("RemotePC01", @"C:\path\to\patch.msp");
Console.WriteLine(result.StandardOutput);
```

#### 5. **Run a Batch File on a Remote Computer**

This function executes a batch file on a remote computer.

```csharp
var result = await invoker.InvokeBatchRemoteCommandAsync("RemotePC01", @"C:\path\to\script.bat", new string[] { "/arg1", "/arg2" });
Console.WriteLine(result.StandardOutput);
```

#### 6. **Import a Registry File on a Remote Computer**

This function imports a `.reg` file into the registry of a remote computer.

```csharp
var result = await invoker.InvokeRegRemoteCommandAsync("RemotePC01", @"C:\path\to\settings.reg");
Console.WriteLine(result.StandardOutput);
```

#### 7. **Import a Registry File for All Users on a Remote Computer**

This function imports a `.reg` file into the registry of all user profiles on a remote computer.

```csharp
var result = await invoker.InvokeRegForAllUsersRemoteCommandAsync("RemotePC01", @"C:\path\to\settings.reg");
Console.WriteLine(result.StandardOutput);
```

#### 8. **Copy a File to a Remote Computer**

This function copies a single file to a specified path on a remote computer.

```csharp
var result = await invoker.CopyFileToRemoteComputerAsync("RemotePC01", @"C:\local\file.txt", @"C$\RemoteFolder\file.txt");
Console.WriteLine(result.StandardOutput);
```

#### 9. **Copy Multiple Files to a Remote Computer**

This function copies multiple files to a specified path on a remote computer.

```csharp
var files = new List<string> { @"C:\local\file1.txt", @"C:\local\file2.txt" };
var result = await invoker.CopyFilesToRemoteComputerAsync("RemotePC01", files, @"C$\RemoteFolder");
Console.WriteLine(result.StandardOutput);
```

#### 10. **Copy a Directory to a Remote Computer**

This function copies a directory and all its contents to a specified path on a remote computer.

```csharp
var result = await invoker.CopyDirectoryToRemoteComputerAsync("RemotePC01", @"C:\local\folder", @"C$\RemoteFolder");
Console.WriteLine(result.StandardOutput);
```

#### 11. **Remove a Directory from a Remote Computer**

This function deletes a directory and all its contents from a specified path on a remote computer.

```csharp
var result = await invoker.RemoveDirectoryFromRemoteComputerAsync("RemotePC01", @"C$\RemoteFolder");
Console.WriteLine(result.StandardOutput);
```

#### 12. **Execute a Command on Multiple Remote Computers**

This function runs a specified PowerShell script block on multiple remote computers concurrently.

```csharp
var computerNames = new List<string> { "RemotePC01", "RemotePC02", "RemotePC03" };
var scriptBlock = @"
    param ($message)
    Write-Output ""$message from $($env:COMPUTERNAME)""
";
var results = await invoker.ExecuteOnMultipleComputersAsync(computerNames, scriptBlock, new object[] { "Hello World" });

foreach (var result in results)
{
    Console.WriteLine($"{result.ComputerName}: {result.StandardOutput}");
}
```

## Logging

All operations performed by this library are logged to a `Logs` directory located in the same folder as the application's executable. The log file name includes the current user's name and a timestamp. This helps in tracing back any issues or verifying operations.

## Conclusion

The `RemoteCommandLibrary` is a powerful tool for performing remote operations on Windows computers. It abstracts the complexities of PowerShell remoting and asynchronous operations, allowing you to focus on your application's logic while handling remote tasks efficiently and reliably.
