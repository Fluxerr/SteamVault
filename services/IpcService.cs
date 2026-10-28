using System;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;

namespace SteamVault.Services
{
    public static class IpcService
    {
        private const string PipeName = "SteamVault_IPC_Pipe";

        // Event raised when a message is received from another instance
        public static event Action<string>? OnMessageReceived;

        /// <summary>
        /// Starts the Named Pipe server to listen for messages from other instances.
        /// Should only be called by the primary (first) instance.
        /// </summary>
        public static void StartServer()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                        await server.WaitForConnectionAsync();

                        using var reader = new StreamReader(server);
                        var message = await reader.ReadToEndAsync();
                        
                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            OnMessageReceived?.Invoke(message);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"IPC Server Error: {ex.Message}");
                    }
                }
            });
        }

        /// <summary>
        /// Sends a message to the primary instance via Named Pipe.
        /// Should be called by the secondary instance before shutting down.
        /// </summary>
        public static bool SendMessageToPrimaryInstance(string message, int timeoutMs = 2000)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                client.Connect(timeoutMs);

                using var writer = new StreamWriter(client);
                writer.AutoFlush = true;
                writer.Write(message);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"IPC Client Error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Registers the steamvault:// URL protocol handler in the Windows Registry.
        /// </summary>
        public static void RegisterProtocolHandler()
        {
            try
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) return;

                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\steamvault");
                key.SetValue("", "URL:SteamVault Protocol");
                key.SetValue("URL Protocol", "");

                using var defaultIcon = key.CreateSubKey("DefaultIcon");
                defaultIcon.SetValue("", $"\"{exePath}\",0");

                using var command = key.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{exePath}\" \"%1\"");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to register protocol handler: {ex.Message}");
            }
        }
    }
}
