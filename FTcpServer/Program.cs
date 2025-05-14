using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Collections.Concurrent;
using System;

class Program
{
    private const int Port = 9000;
    private const string FilesDirectory = "ServerFiles";
    private static readonly ConcurrentDictionary<string, ClientInfo> ConnectedClients = new();
    private static FileSystemWatcher _fileWatcher;
    private static readonly object LogLock = new object();
    private const string LogFile = "server.log";

    public class ClientInfo
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public NetworkStream Stream { get; init; } // Основной поток
        public TcpClient Client { get; init; }
        public NetworkStream NotificationStream { get; set; } // Новое поле для уведомлений
    }

    static async Task Main()
    {
        InitializeLogger();
        Directory.CreateDirectory(FilesDirectory);
        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();
        Logger($"Сервер запущен на порту {Port}");
        InitializeFileWatcher();

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = HandleClientAsync(client);
        }
    }

    private static void InitializeLogger()
    {
        Logger($"\n=== Новая сессия {DateTime.Now:dd.MM.yyyy HH:mm} ===");
    }

    private static void Logger(string message, bool isError = false)
    {
        var logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{(isError ? "ERROR" : "INFO")}] {message}";
        Console.WriteLine(logEntry);
        lock (LogLock)
            File.AppendAllText(LogFile, logEntry + Environment.NewLine);
    }

    private static void InitializeFileWatcher()
    {
        _fileWatcher = new FileSystemWatcher(FilesDirectory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _fileWatcher.Deleted += (s, e) => BroadcastCommand($"DELETE|{e.Name}");
        _fileWatcher.Renamed += (s, e) => BroadcastCommand($"RENAME|{e.OldName}|{e.Name}");
        _fileWatcher.Error += (s, e) => Logger($"Ошибка FileSystemWatcher: {e.GetException().Message}", true);
    }

    private static async Task HandleClientAsync(TcpClient client)
    {
        NetworkStream stream = null;
        ClientInfo clientInfo = null;

        try
        {
            stream = client.GetStream();
            var buffer = new byte[4096];

            // Чтение первой команды
            var bytesRead = await stream.ReadAsync(buffer);
            if (bytesRead == 0) return;

            var initialCommand = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var parts = initialCommand.Split('|');

            if (parts[0] == "NOTIFICATION_CHANNEL")
            {
                clientInfo = new ClientInfo
                {
                    Stream = stream,
                    Client = client,
                    NotificationStream = stream
                };

                // Добавление клиента в список
                ConnectedClients.TryAdd(clientInfo.Id, clientInfo);
                Logger($"Новое подключение: {client.Client.RemoteEndPoint} | ID: {clientInfo.Id}");

                // Отправка ID клиенту
                await SendResponseAsync(stream, $"CLIENT_ID|{clientInfo.Id}");

                // Рассылка уведомлений:
                // 1. Всем существующим клиентам о новом подключении (кроме себя)
                foreach (var existingClient in ConnectedClients.Values)
                {
                    if (existingClient.Id != clientInfo.Id)
                    {
                        await SendResponseAsync(existingClient.NotificationStream, $"CLIENT_CONNECTED|{clientInfo.Id}");
                    }
                }

                // 2. Новому клиенту о существующих (кроме себя)
                foreach (var existingClient in ConnectedClients.Values)
                {
                    if (existingClient.Id != clientInfo.Id)
                    {
                        await SendResponseAsync(stream, $"CLIENT_CONNECTED|{existingClient.Id}");
                    }
                }

                // Обработка P2P-команд
                while (client.Connected)
                {
                    bytesRead = await stream.ReadAsync(buffer);
                    if (bytesRead == 0) break;

                    var command = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    parts = command.Split('|');

                    switch (parts[0])
                    {
                        case "SEND_TO_CLIENT":
                            // parts = ["SEND_TO_CLIENT", recipientId, fileName, fileSize]
                            var recipientId = parts[1];
                            var fileName = parts[2];
                            var fileSize = long.Parse(parts[3]);
                            // clientInfo.Id — это ваш senderId
                            _ = Task.Run(() =>
                                HandleP2PTransfer(clientInfo.Id, recipientId, fileName, fileSize));
                            break;
                    }
                }
            }
            else
            {
                await ProcessTemporaryCommand(stream, initialCommand);
            }
        }
        catch (Exception ex)
        {
            Logger($"Ошибка: {ex.Message}", true);
        }
        finally
        {
            if (clientInfo != null)
            {
                ConnectedClients.TryRemove(clientInfo.Id, out _);
                BroadcastCommand($"CLIENT_DISCONNECTED|{clientInfo.Id}");
            }
            client.Dispose();
        }
    }

    // Обработка временных команд
    private static async Task ProcessTemporaryCommand(NetworkStream stream, string command)
    {
        var parts = command.Split('|');
        try
        {
            switch (parts[0])
            {
                case "UPLOAD":
                case "FORCE_UPLOAD":
                    await HandleUploadAsync(stream, parts, parts[0] == "FORCE_UPLOAD");
                    break;
                case "DOWNLOAD":
                    if (parts.Length > 1)
                        await HandleDownloadAsync(stream, parts[1]);
                    break;
                case "LIST":
                    await HandleListAsync(stream);
                    break;
                default:
                    await SendResponseAsync(stream, "UNKNOWN_COMMAND");
                    break;
            }
        }
        finally
        {
            stream.Close();
        }
    }

    private static async Task HandleUploadAsync(NetworkStream stream, string[] parts, bool forceOverwrite)
    {
        string fileName = parts[1];
        string filePath = Path.Combine(FilesDirectory, fileName);
        long fileSize = long.Parse(parts[2]);

        if (File.Exists(filePath))
        {
            if (forceOverwrite)
            {
                File.Delete(filePath);
            }
            else
            {
                await SendResponseAsync(stream, "FILE_EXISTS");
                return;
            }
        }

        await SendResponseAsync(stream, "HEADER_ACK");

        using (var fs = new FileStream(filePath, FileMode.Create))
        {
            long totalReceived = 0;
            var buffer = new byte[8192];

            while (totalReceived < fileSize)
            {
                var bytesRead = await stream.ReadAsync(buffer);
                await fs.WriteAsync(buffer, 0, bytesRead);
                totalReceived += bytesRead;
            }
        }

        await SendResponseAsync(stream, "ACK");
        BroadcastCommand($"REFRESH|");
    }

    private static async Task HandleDownloadAsync(NetworkStream stream, string fileName)
    {
        string filePath = Path.Combine(FilesDirectory, fileName);
        if (!File.Exists(filePath)) return;

        var fileInfo = new FileInfo(filePath);
        await SendResponseAsync(stream, $"SIZE|{fileInfo.Length}");

        using (var fs = File.OpenRead(filePath))
        {
            var buffer = new byte[8192];
            int bytesRead;
            while ((bytesRead = await fs.ReadAsync(buffer)) > 0)
                await stream.WriteAsync(buffer, 0, bytesRead);
        }

        await SendResponseAsync(stream, "ACK");
    }

    private static async Task HandleListAsync(NetworkStream stream)
    {
        try
        {
            var files = Directory.GetFiles(FilesDirectory)
                .Select(p => new FileInfo(p))
                .Select(f => $"{f.Name},{f.Length},{f.Extension},{f.LastWriteTime:yyyy-MM-dd HH:mm:ss}");

            var response = files.Any()
                ? string.Join("|", files) + "|ACK"
                : "ACK";

            var bytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            Logger($"Ошибка списка: {ex.Message}", true);
        }
    }

    private static async Task SendResponseAsync(NetworkStream stream, string response)
    {
        try
        {
            // Добавляем разделитель '\n' для чёткого разделения команд
            var bytes = Encoding.UTF8.GetBytes(response + "\n");
            await stream.WriteAsync(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            Logger($"Ошибка отправки: {ex.Message}", true);
        }
    }

    private static void BroadcastCommand(string command)
    {
        var commandBytes = Encoding.UTF8.GetBytes(command + "\n");
        foreach (var client in ConnectedClients.Values.ToList())
        {
            if (client.NotificationStream != null && client.NotificationStream.CanWrite)
            {
                try
                {
                    client.NotificationStream.WriteAsync(commandBytes, 0, commandBytes.Length);
                }
                catch { /* Игнорируем ошибки */ }
            }
        }
    }

    static async Task HandleP2PTransfer(string senderId, string recipientId, string fileName, long fileSize)
    {
        // 1) Получаем ClientInfo, а из него — поток уведомлений
        if (!ConnectedClients.TryGetValue(senderId, out var senderInfo) ||
            !ConnectedClients.TryGetValue(recipientId, out var recipientInfo))
        {
            Logger($"P2P: клиент не найден {senderId} или {recipientId}", true);
            return;
        }
        var senderStream = senderInfo.NotificationStream;
        var recipientStream = recipientInfo.NotificationStream;

        // 2) Стартуем listener и получаем порт
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // 3) Шлём порт обеим сторонам по управлению
        await SendLineAsync(senderStream, $"DATA_PORT|{port}");
        await SendLineAsync(recipientStream, $"DATA_PORT|{port}");

        // 4) Уведомляем получателя о входящем файле
        await SendLineAsync(recipientStream,
                            $"INCOMING_FILE|{senderId}|{fileName}|{fileSize}");

        // 5) Ждём ровно два data-соединения
        var conn1 = await listener.AcceptTcpClientAsync();
        var conn2 = await listener.AcceptTcpClientAsync();

        // 6) Читаем clientId без StreamReader
        var ns1 = conn1.GetStream();
        var ns2 = conn2.GetStream();
        var id1 = await ReadClientIdAsync(ns1);
        var id2 = await ReadClientIdAsync(ns2);

        if ((id1 != senderId || id2 != recipientId) && (id1 != recipientId || id2 != senderId))
        {
            Logger($"P2P: получены ID: [{id1}] и [{id2}], ожидались: [{senderId}] и [{recipientId}]", true);
        }

        // 7) Разбираем, кто sender, кто recipient
        TcpClient dataSender, dataReceiver;
        if (id1 == senderId && id2 == recipientId)
        {
            dataSender = conn1;
            dataReceiver = conn2;
        }
        else if (id2 == senderId && id1 == recipientId)
        {
            dataSender = conn2;
            dataReceiver = conn1;
        }
        else
        {
            Logger("P2P: неверные clientId на data-соединении", true);
            listener.Stop();
            return;
        }

        try
        {
            // 7) Relay
            var sendNs = dataSender.GetStream();
            var recvNs = dataReceiver.GetStream();

            // 1. читаем clientId
            var id = await ReadClientIdAsync(sendNs);

            // 2. после ReadClientIdAsync — начинаем передачу вручную, НЕ CopyToAsync
            var buffer = new byte[8192];
            int read;
            while ((read = await sendNs.ReadAsync(buffer)) > 0)
            {
                await recvNs.WriteAsync(buffer, 0, read);
            }
            // Гарантируем отправку всех данных
            await recvNs.FlushAsync();

            // Уведомляем получателя о завершении отправки
            dataSender.Client.Shutdown(SocketShutdown.Send);

            // Ждём подтверждения (ACK) от получателя
            var ackBuffer = new byte[3];
            int bytesRead = await recvNs.ReadAsync(ackBuffer);
            if (bytesRead == 0 || Encoding.UTF8.GetString(ackBuffer, 0, bytesRead) != "ACK")
            {
                Logger("Подтверждение не получено", true);
            }

            // Отправляем уведомления об успешной передаче
            await SendLineAsync(senderStream, "TRANSFER_COMPLETE");
            await SendLineAsync(recipientStream, "TRANSFER_COMPLETE");
            dataSender.Close();
            dataReceiver.Close();
            listener.Stop();
        }
        catch (Exception ex)
        {
            Logger($"Ошибка P2P-релеи: {ex.Message}", true);
        }
    }

    // Вспомогательная функция
    static Task SendLineAsync(NetworkStream s, string line)
    {
        var buf = Encoding.UTF8.GetBytes(line + "\n");
        return s.WriteAsync(buf, 0, buf.Length);
    }

    private static async Task<string> ReadClientIdAsync(NetworkStream ns)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            int n = await ns.ReadAsync(buf, 0, 1);
            if (n == 0) throw new IOException("Соединение закрыто до получения ID");
            char c = (char)buf[0];
            if (c == '\n') break;
            if (c != '\r') sb.Append(c);
        }
        return sb.ToString();
    }
}